using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.MediaPruner.Arr;
using Jellyfin.Plugin.MediaPruner.Configuration;
using Jellyfin.Plugin.MediaPruner.Models;
using MediaBrowser.Controller.Collections;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MediaPruner.Services;

/// <summary>
/// Evaluates the deletion criteria and deletes items in Radarr / Sonarr.
/// </summary>
public class CleanupService
{
    private readonly ArrClient _arr;
    private readonly HistoryStore _history;
    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;
    private readonly IUserDataManager _userDataManager;
    private readonly ILogger<CleanupService> _logger;
    private readonly ICollectionManager _collectionManager;
    private readonly SemaphoreSlim _runLock = new(1, 1);
    private readonly SemaphoreSlim _syncLock = new(1, 1);

    public CleanupService(
        ArrClient arr,
        HistoryStore history,
        ILibraryManager libraryManager,
        IUserManager userManager,
        IUserDataManager userDataManager,
        ICollectionManager collectionManager,
        ILogger<CleanupService> logger)
    {
        _arr = arr;
        _history = history;
        _libraryManager = libraryManager;
        _userManager = userManager;
        _userDataManager = userDataManager;
        _collectionManager = collectionManager;
        _logger = logger;
    }

    private static PluginConfiguration Config => Plugin.Instance?.Configuration ?? new PluginConfiguration();

    // ------------------------------------------------------------------
    // Evaluation
    // ------------------------------------------------------------------

    /// <summary>
    /// Evaluates all Radarr/Sonarr items and returns those scheduled for deletion
    /// within the warning window (or all of them if <paramref name="all"/>).
    /// </summary>
    public async Task<CandidatesResult> EvaluateAsync(bool all, CancellationToken ct)
    {
        var config = Config;
        var result = new CandidatesResult { GeneratedAt = DateTime.UtcNow };
        var now = DateTime.UtcNow;
        var window = all ? int.MaxValue : Math.Max(0, config.WarningDays);
        var protectedKeys = new HashSet<string>(config.ProtectedItems ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        var protectedTags = ParseTags(config.ProtectedTags);
        var users = _userManager.GetUsers().ToList();

        if (config.RadarrEnabled)
        {
            try
            {
                var tags = await _arr.GetTagsAsync(config.RadarrUrl, config.RadarrApiKey, ct).ConfigureAwait(false);
                var tagIds = TagIds(tags, protectedTags);
                var movies = await _arr.GetMoviesAsync(config.RadarrUrl, config.RadarrApiKey, ct).ConfigureAwait(false);
                var index = BuildIndex(BaseItemKind.Movie);

                foreach (var m in movies)
                {
                    var jf = Find(index, ("tmdb", m.TmdbId > 0 ? m.TmdbId.ToString(CultureInfo.InvariantCulture) : null), ("imdb", m.ImdbId));
                    var c = new CleanupCandidate
                    {
                        Key = MakeKey(ArrKind.Radarr, m.Id),
                        Source = ArrKind.Radarr,
                        ArrId = m.Id,
                        Title = m.Title,
                        Year = m.Year,
                        SizeOnDisk = m.SizeOnDisk,
                        PosterUrl = Poster(m.Images),
                    };

                    if (!ComputeDates(c, m.Added, m.MovieFile?.DateAdded, jf, config, now, window))
                    {
                        continue;
                    }

                    if (!m.HasFile)
                    {
                        c.ExclusionReasons.Add("No downloaded file");
                    }

                    if (m.Tags.Any(tagIds.Contains))
                    {
                        c.ExclusionReasons.Add("Protected tag");
                    }

                    ApplyCommon(c, jf, users, protectedKeys, config, now);
                    result.Items.Add(c);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Error while reading Radarr");
                result.Errors.Add("Radarr: " + ex.Message);
            }
        }

        if (config.SonarrEnabled)
        {
            try
            {
                var tags = await _arr.GetTagsAsync(config.SonarrUrl, config.SonarrApiKey, ct).ConfigureAwait(false);
                var tagIds = TagIds(tags, protectedTags);
                var series = await _arr.GetSeriesAsync(config.SonarrUrl, config.SonarrApiKey, ct).ConfigureAwait(false);
                var index = BuildIndex(BaseItemKind.Series);
                var downloadDates = config.DateSource == DateSource.Download
                    ? await GetSeriesDownloadDatesAsync(config, series, ct).ConfigureAwait(false)
                    : new Dictionary<int, DateTime>();

                foreach (var s in series)
                {
                    var jf = Find(
                        index,
                        ("tvdb", s.TvdbId > 0 ? s.TvdbId.ToString(CultureInfo.InvariantCulture) : null),
                        ("tmdb", s.TmdbId > 0 ? s.TmdbId.ToString(CultureInfo.InvariantCulture) : null),
                        ("imdb", s.ImdbId));
                    var c = new CleanupCandidate
                    {
                        Key = MakeKey(ArrKind.Sonarr, s.Id),
                        Source = ArrKind.Sonarr,
                        ArrId = s.Id,
                        Title = s.Title,
                        Year = s.Year,
                        SizeOnDisk = s.Statistics?.SizeOnDisk ?? 0,
                        PosterUrl = Poster(s.Images),
                    };

                    if (!ComputeDates(c, s.Added, downloadDates.TryGetValue(s.Id, out var dl) ? dl : null, jf, config, now, window))
                    {
                        continue;
                    }

                    if ((s.Statistics?.EpisodeFileCount ?? 0) == 0)
                    {
                        c.ExclusionReasons.Add("No downloaded file");
                    }

                    if (s.Tags.Any(tagIds.Contains))
                    {
                        c.ExclusionReasons.Add("Protected tag");
                    }

                    if (config.KeepContinuingSeries && string.Equals(s.Status, "continuing", StringComparison.OrdinalIgnoreCase))
                    {
                        c.ExclusionReasons.Add("Series still airing");
                    }

                    ApplyCommon(c, jf, users, protectedKeys, config, now);
                    result.Items.Add(c);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Error while reading Sonarr");
                result.Errors.Add("Sonarr: " + ex.Message);
            }
        }

        result.Items = result.Items.OrderBy(i => i.DaysRemaining).ThenBy(i => i.Title, StringComparer.CurrentCultureIgnoreCase).ToList();
        return result;
    }

    // ------------------------------------------------------------------
    // Deletion
    // ------------------------------------------------------------------

    /// <summary>
    /// Scheduled task run: deletes eligible items when automatic deletion is enabled.
    /// </summary>
    public async Task<DeleteResult> RunScheduledAsync(IProgress<double>? progress, CancellationToken ct)
    {
        var config = Config;
        var evaluation = await EvaluateAsync(false, ct).ConfigureAwait(false);
        await SyncPendingMarkersAsync(evaluation, ct).ConfigureAwait(false);
        var eligible = evaluation.Items.Where(i => i.IsEligible).ToList();
        progress?.Report(30);

        if (!config.AutoDelete)
        {
            _logger.LogInformation(
                "Media Pruner: automatic deletion disabled, {Count} eligible item(s) awaiting manual deletion",
                eligible.Count);
            progress?.Report(100);
            return new DeleteResult();
        }

        if (config.MaxDeletionsPerRun > 0)
        {
            eligible = eligible.Take(config.MaxDeletionsPerRun).ToList();
        }

        return await DeleteCandidatesAsync(eligible, config.DryRun ? "Dry run" : "Auto", config.DryRun, progress, ct).ConfigureAwait(false);
    }

    /// <summary>Manually deletes all currently eligible items.</summary>
    public async Task<DeleteResult> DeleteAllEligibleAsync(CancellationToken ct)
    {
        var evaluation = await EvaluateAsync(false, ct).ConfigureAwait(false);
        var eligible = evaluation.Items.Where(i => i.IsEligible).ToList();
        return await DeleteCandidatesAsync(eligible, "Manual", false, null, ct).ConfigureAwait(false);
    }

    /// <summary>Manually deletes the given items (ignoring the criteria).</summary>
    public async Task<DeleteResult> DeleteByKeysAsync(IEnumerable<string> keys, CancellationToken ct)
    {
        var config = Config;
        var wanted = keys.Select(ParseKey).Where(k => k.HasValue).Select(k => k!.Value).Distinct().ToList();
        var candidates = new List<CleanupCandidate>();

        if (wanted.Any(k => k.Kind == ArrKind.Radarr))
        {
            var movies = await _arr.GetMoviesAsync(config.RadarrUrl, config.RadarrApiKey, ct).ConfigureAwait(false);
            foreach (var (_, id) in wanted.Where(k => k.Kind == ArrKind.Radarr))
            {
                var m = movies.FirstOrDefault(x => x.Id == id);
                candidates.Add(new CleanupCandidate
                {
                    Key = MakeKey(ArrKind.Radarr, id),
                    Source = ArrKind.Radarr,
                    ArrId = id,
                    Title = m?.Title ?? MakeKey(ArrKind.Radarr, id),
                    SizeOnDisk = m?.SizeOnDisk ?? 0
                });
            }
        }

        if (wanted.Any(k => k.Kind == ArrKind.Sonarr))
        {
            var series = await _arr.GetSeriesAsync(config.SonarrUrl, config.SonarrApiKey, ct).ConfigureAwait(false);
            foreach (var (_, id) in wanted.Where(k => k.Kind == ArrKind.Sonarr))
            {
                var s = series.FirstOrDefault(x => x.Id == id);
                candidates.Add(new CleanupCandidate
                {
                    Key = MakeKey(ArrKind.Sonarr, id),
                    Source = ArrKind.Sonarr,
                    ArrId = id,
                    Title = s?.Title ?? MakeKey(ArrKind.Sonarr, id),
                    SizeOnDisk = s?.Statistics?.SizeOnDisk ?? 0
                });
            }
        }

        return await DeleteCandidatesAsync(candidates, "Manual", false, null, ct).ConfigureAwait(false);
    }

    /// <summary>Protects or unprotects an item.</summary>
    public void SetProtected(string key, bool isProtected)
    {
        var plugin = Plugin.Instance ?? throw new InvalidOperationException("Plugin not initialized.");
        var set = new HashSet<string>(plugin.Configuration.ProtectedItems ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        if (isProtected)
        {
            set.Add(key);
        }
        else
        {
            set.Remove(key);
        }

        plugin.Configuration.ProtectedItems = set.OrderBy(k => k, StringComparer.Ordinal).ToArray();
        plugin.SaveConfiguration();
    }

    // ------------------------------------------------------------------
    // "Pending deletion" Jellyfin tag and collection
    // ------------------------------------------------------------------

    /// <summary>
    /// Keeps the "pending deletion" Jellyfin tag and collection in sync: items that will be deleted
    /// within the warning window (not excluded) get the tag and are in the collection, all other items do not.
    /// </summary>
    /// <param name="evaluation">Result of <see cref="EvaluateAsync"/> with <c>all = false</c>.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task SyncPendingMarkersAsync(CandidatesResult evaluation, CancellationToken ct)
    {
        var plugin = Plugin.Instance;
        if (plugin is null)
        {
            return;
        }

        var config = plugin.Configuration;

        // Do not remove anything based on an incomplete evaluation (Radarr/Sonarr unreachable).
        var complete = evaluation.Errors.Count == 0;

        var pending = new HashSet<Guid>();
        foreach (var c in evaluation.Items)
        {
            if (c.ExclusionReasons.Count == 0
                && c.DaysRemaining <= Math.Max(0, config.WarningDays)
                && Guid.TryParse(c.JellyfinId, out var id))
            {
                pending.Add(id);
            }
        }

        await _syncLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var changed = await SyncTagAsync(config, pending, complete, ct).ConfigureAwait(false);
            changed |= await SyncCollectionAsync(config, pending, complete, ct).ConfigureAwait(false);
            if (changed)
            {
                plugin.SaveConfiguration();
            }
        }
        finally
        {
            _syncLock.Release();
        }
    }

    /// <summary>Syncs the tag; returns true if the configuration must be saved.</summary>
    private async Task<bool> SyncTagAsync(PluginConfiguration config, HashSet<Guid> pending, bool complete, CancellationToken ct)
    {
        var tag = config.TagPendingItems ? (config.PendingTagName ?? string.Empty).Trim() : string.Empty;
        var previous = (config.AppliedPendingTagName ?? string.Empty).Trim();
        var wanted = tag.Length > 0 ? pending : new HashSet<Guid>();
        var added = 0;
        var removed = 0;

        // Remove the current tag (or a previously used tag name) from items that no longer need it.
        if (complete)
        {
            foreach (var name in new[] { tag, previous }.Where(n => n.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var tagged = _libraryManager.GetItemList(new InternalItemsQuery
                {
                    IncludeItemTypes = [BaseItemKind.Movie, BaseItemKind.Series],
                    Recursive = true,
                    Tags = [name]
                });

                foreach (var item in tagged)
                {
                    var keep = string.Equals(name, tag, StringComparison.OrdinalIgnoreCase) && wanted.Contains(item.Id);
                    if (!keep && await SetTagAsync(item, name, false, ct).ConfigureAwait(false))
                    {
                        removed++;
                    }
                }
            }
        }

        // Add the tag to pending items.
        foreach (var id in wanted)
        {
            var item = _libraryManager.GetItemById(id);
            if ((item is Movie || item is Series) && await SetTagAsync(item, tag, true, ct).ConfigureAwait(false))
            {
                added++;
            }
        }

        if (added > 0 || removed > 0)
        {
            _logger.LogInformation("Media Pruner: tag \"{Tag}\" added to {Added} item(s), removed from {Removed} item(s)", tag, added, removed);
        }

        if (complete && !string.Equals(previous, tag, StringComparison.Ordinal))
        {
            config.AppliedPendingTagName = tag;
            return true;
        }

        return false;
    }

    /// <summary>Syncs the collection; returns true if the configuration must be saved.</summary>
    private async Task<bool> SyncCollectionAsync(PluginConfiguration config, HashSet<Guid> pending, bool complete, CancellationToken ct)
    {
        var name = (config.PendingCollectionName ?? string.Empty).Trim();
        var enabled = config.CollectionPendingItems && name.Length > 0;

        // The collection created by the plugin (remembered by id so it survives a rename).
        BoxSet? collection = null;
        if (Guid.TryParse(config.PendingCollectionId, out var storedId))
        {
            collection = _libraryManager.GetItemById(storedId) as BoxSet;
        }

        if (!enabled)
        {
            if (collection is null || !config.PendingCollectionCreatedByPlugin)
            {
                // Nothing to delete, or a collection that already existed before the plugin used it: keep it.
                var hadState = !string.IsNullOrEmpty(config.PendingCollectionId) || config.PendingCollectionCreatedByPlugin;
                config.PendingCollectionId = string.Empty;
                config.PendingCollectionCreatedByPlugin = false;
                return hadState;
            }

            // Feature disabled: delete the plugin's collection (the media themselves are untouched).
            try
            {
                _libraryManager.DeleteItem(collection, new DeleteOptions { DeleteFileLocation = true }, true);
                _logger.LogInformation("Media Pruner: collection \"{Name}\" deleted", collection.Name);
                config.PendingCollectionId = string.Empty;
                config.PendingCollectionCreatedByPlugin = false;
                return true;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Media Pruner: unable to delete collection {Name}", collection.Name);
                return false;
            }
        }

        try
        {
            var saveConfig = false;
            if (collection is null)
            {
                // Reuse an existing collection with the same name, otherwise create it.
                collection = _libraryManager.GetItemList(new InternalItemsQuery
                {
                    IncludeItemTypes = [BaseItemKind.BoxSet],
                    Recursive = true,
                    Name = name
                }).OfType<BoxSet>().FirstOrDefault();

                if (collection is null)
                {
                    if (pending.Count == 0)
                    {
                        return false; // Nothing to show yet: do not create an empty collection.
                    }

                    collection = await _collectionManager.CreateCollectionAsync(new CollectionCreationOptions
                    {
                        Name = name,
                        IsLocked = true,
                        ItemIdList = pending.Select(id => id.ToString("N", CultureInfo.InvariantCulture)).ToArray()
                    }).ConfigureAwait(false);
                    _logger.LogInformation("Media Pruner: collection \"{Name}\" created with {Count} item(s)", name, pending.Count);
                    config.PendingCollectionId = collection.Id.ToString("N", CultureInfo.InvariantCulture);
                    config.PendingCollectionCreatedByPlugin = true;
                    return true;
                }

                config.PendingCollectionId = collection.Id.ToString("N", CultureInfo.InvariantCulture);
                config.PendingCollectionCreatedByPlugin = false;
                saveConfig = true;
            }

            if (!string.Equals(collection.Name, name, StringComparison.Ordinal))
            {
                collection.Name = name;
                await collection.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, ct).ConfigureAwait(false);
            }

            var current = collection.GetLinkedChildren().Select(i => i.Id).ToHashSet();
            var toAdd = pending.Where(id => !current.Contains(id)).ToList();
            var toRemove = complete ? current.Where(id => !pending.Contains(id)).ToList() : new List<Guid>();

            if (toAdd.Count > 0)
            {
                await _collectionManager.AddToCollectionAsync(collection.Id, toAdd).ConfigureAwait(false);
            }

            if (toRemove.Count > 0)
            {
                await _collectionManager.RemoveFromCollectionAsync(collection.Id, toRemove).ConfigureAwait(false);
            }

            if (toAdd.Count > 0 || toRemove.Count > 0)
            {
                _logger.LogInformation("Media Pruner: collection \"{Name}\": {Added} item(s) added, {Removed} removed", name, toAdd.Count, toRemove.Count);
            }

            return saveConfig;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Media Pruner: unable to update collection {Name}", name);
            return false;
        }
    }

    private async Task<bool> SetTagAsync(BaseItem item, string tag, bool present, CancellationToken ct)
    {
        var tags = item.Tags ?? Array.Empty<string>();
        var has = tags.Contains(tag, StringComparer.OrdinalIgnoreCase);
        if (has == present)
        {
            return false;
        }

        try
        {
            item.Tags = present
                ? tags.Append(tag).ToArray()
                : tags.Where(t => !string.Equals(t, tag, StringComparison.OrdinalIgnoreCase)).ToArray();
            await item.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, ct).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Media Pruner: unable to update tags of {Name}", item.Name);
            return false;
        }
    }

    private async Task<DeleteResult> DeleteCandidatesAsync(
        IReadOnlyList<CleanupCandidate> candidates,
        string mode,
        bool dryRun,
        IProgress<double>? progress,
        CancellationToken ct)
    {
        var config = Config;
        var result = new DeleteResult { DryRun = dryRun };
        if (candidates.Count == 0)
        {
            progress?.Report(100);
            return result;
        }

        await _runLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            for (var i = 0; i < candidates.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var c = candidates[i];
                var entry = new HistoryEntry
                {
                    Date = DateTime.UtcNow,
                    Key = c.Key,
                    Title = c.Title,
                    Source = c.Source,
                    SizeOnDisk = c.SizeOnDisk,
                    Mode = mode,
                };

                try
                {
                    if (dryRun)
                    {
                        _logger.LogInformation("Media Pruner [dry run]: {Title} ({Key}) would be deleted", c.Title, c.Key);
                    }
                    else if (c.Source == ArrKind.Radarr)
                    {
                        await _arr.DeleteMovieAsync(config.RadarrUrl, config.RadarrApiKey, c.ArrId, config.DeleteFiles, config.AddImportExclusion, ct).ConfigureAwait(false);
                        _logger.LogInformation("Media Pruner: movie {Title} deleted from Radarr", c.Title);
                    }
                    else
                    {
                        await _arr.DeleteSeriesAsync(config.SonarrUrl, config.SonarrApiKey, c.ArrId, config.DeleteFiles, config.AddImportExclusion, ct).ConfigureAwait(false);
                        _logger.LogInformation("Media Pruner: series {Title} deleted from Sonarr", c.Title);
                    }

                    entry.Success = true;
                    result.Deleted++;
                    result.FreedBytes += c.SizeOnDisk;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogError(ex, "Media Pruner: failed to delete {Title} ({Key})", c.Title, c.Key);
                    entry.Success = false;
                    entry.Error = ex.Message;
                    result.Failed++;
                }

                result.Entries.Add(entry);
                progress?.Report(30 + (70.0 * (i + 1) / candidates.Count));
            }
        }
        finally
        {
            _runLock.Release();
            _history.Add(result.Entries);
        }

        if (!dryRun && result.Deleted > 0 && config.RefreshLibraryAfterDelete)
        {
            _libraryManager.QueueLibraryScan();
        }

        return result;
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    public static string MakeKey(ArrKind kind, int id)
        => string.Format(CultureInfo.InvariantCulture, "{0}:{1}", kind == ArrKind.Radarr ? "radarr" : "sonarr", id);

    public static (ArrKind Kind, int Id)? ParseKey(string key)
    {
        var parts = (key ?? string.Empty).Split(':', 2);
        if (parts.Length != 2 || !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var id))
        {
            return null;
        }

        return parts[0].ToLowerInvariant() switch
        {
            "radarr" => (ArrKind.Radarr, id),
            "sonarr" => (ArrKind.Sonarr, id),
            _ => null
        };
    }

    private static HashSet<string> ParseTags(string? tags)
        => new(
            (tags ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            StringComparer.OrdinalIgnoreCase);

    private static HashSet<int> TagIds(IEnumerable<ArrTag> tags, HashSet<string> labels)
        => tags.Where(t => labels.Contains(t.Label)).Select(t => t.Id).ToHashSet();

    private static string? Poster(IEnumerable<ArrImage> images)
        => images.FirstOrDefault(i => string.Equals(i.CoverType, "poster", StringComparison.OrdinalIgnoreCase))?.RemoteUrl;

    /// <summary>
    /// For each series with files, the date of its most recently downloaded episode file
    /// (one Sonarr request per series, a few in parallel).
    /// </summary>
    private async Task<Dictionary<int, DateTime>> GetSeriesDownloadDatesAsync(PluginConfiguration config, IReadOnlyList<SonarrSeries> series, CancellationToken ct)
    {
        var result = new System.Collections.Concurrent.ConcurrentDictionary<int, DateTime>();
        await Parallel.ForEachAsync(
            series.Where(s => (s.Statistics?.EpisodeFileCount ?? 0) > 0),
            new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = ct },
            async (s, token) =>
            {
                try
                {
                    var files = await _arr.GetEpisodeFilesAsync(config.SonarrUrl, config.SonarrApiKey, s.Id, token).ConfigureAwait(false);
                    var last = files.Where(f => f.DateAdded is not null).Max(f => f.DateAdded);
                    if (last is not null)
                    {
                        result[s.Id] = last.Value;
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Falls back to the date added in Sonarr for this series.
                    _logger.LogWarning(ex, "Media Pruner: unable to read episode files of {Title}", s.Title);
                }
            }).ConfigureAwait(false);

        return new Dictionary<int, DateTime>(result);
    }

    /// <summary>Computes the dates; returns false if the item is outside the displayed window.</summary>
    private static bool ComputeDates(CleanupCandidate c, DateTime? arrAdded, DateTime? downloaded, BaseItem? jf, PluginConfiguration config, DateTime now, int window)
    {
        DateTime? added = config.DateSource switch
        {
            DateSource.Jellyfin when jf is not null => jf.DateCreated,
            DateSource.Download => downloaded,
            _ => arrAdded
        };

        // Fallback to the Radarr/Sonarr added date (item not in Jellyfin, nothing downloaded yet, ...).
        if (added is null || added.Value.Year < 1900)
        {
            added = arrAdded;
        }

        if (added is null)
        {
            return false;
        }

        c.AddedDate = DateTime.SpecifyKind(added.Value, DateTimeKind.Utc);
        c.DeletionDate = c.AddedDate.Value.AddDays(Math.Max(0, config.DaysBeforeDeletion));
        c.DaysRemaining = (int)Math.Ceiling((c.DeletionDate.Value - now).TotalDays);
        return window == int.MaxValue || c.DaysRemaining <= window;
    }

    private void ApplyCommon(
        CleanupCandidate c,
        BaseItem? jf,
        IReadOnlyList<User> users,
        HashSet<string> protectedKeys,
        PluginConfiguration config,
        DateTime now)
    {
        if (protectedKeys.Contains(c.Key))
        {
            c.IsProtected = true;
            c.ExclusionReasons.Add("Manually protected");
        }

        if (jf is not null)
        {
            c.JellyfinId = jf.Id.ToString("N", CultureInfo.InvariantCulture);
            FillUserData(c, jf, users, config);
        }

        if (config.KeepFavorites && c.IsFavorite)
        {
            c.ExclusionReasons.Add("Favorite of " + string.Join(", ", c.FavoritedBy));
        }

        if (config.OnlyWatched && !c.IsWatched)
        {
            c.ExclusionReasons.Add("Not watched yet");
        }

        if (config.KeepIfPlayedWithinDays > 0 && c.LastPlayed is not null
            && (now - c.LastPlayed.Value).TotalDays < config.KeepIfPlayedWithinDays)
        {
            c.ExclusionReasons.Add("Recently played");
        }

        c.IsEligible = c.DaysRemaining <= 0 && c.ExclusionReasons.Count == 0;
    }

    private void FillUserData(CleanupCandidate c, BaseItem item, IReadOnlyList<User> users, PluginConfiguration config)
    {
        List<BaseItem>? children = null;
        if (item is Series)
        {
            children = _libraryManager.GetItemList(new InternalItemsQuery
            {
                AncestorIds = [item.Id],
                IncludeItemTypes = [BaseItemKind.Season, BaseItemKind.Episode],
                Recursive = true,
                IsVirtualItem = false
            }).ToList();
        }

        var episodes = children?.Where(x => x is Episode).ToList();

        foreach (var user in users)
        {
            var data = _userDataManager.GetUserData(user, item);
            var favorite = data?.IsFavorite == true;
            var watched = data?.Played == true;
            var lastPlayed = data?.LastPlayedDate;

            if (children is not null)
            {
                var playedEpisodes = 0;
                foreach (var child in children)
                {
                    var cd = _userDataManager.GetUserData(user, child);
                    if (cd is null)
                    {
                        continue;
                    }

                    if (config.KeepFavoriteEpisodes && cd.IsFavorite)
                    {
                        favorite = true;
                    }

                    if (child is Episode)
                    {
                        if (cd.Played)
                        {
                            playedEpisodes++;
                        }

                        if (cd.LastPlayedDate is not null && (lastPlayed is null || cd.LastPlayedDate > lastPlayed))
                        {
                            lastPlayed = cd.LastPlayedDate;
                        }
                    }
                }

                watched = episodes!.Count > 0 && playedEpisodes == episodes.Count;
            }

            if (favorite)
            {
                c.IsFavorite = true;
                c.FavoritedBy.Add(user.Username);
            }

            if (watched)
            {
                c.IsWatched = true;
            }

            if (lastPlayed is not null && (c.LastPlayed is null || lastPlayed > c.LastPlayed))
            {
                c.LastPlayed = lastPlayed;
            }
        }
    }

    private Dictionary<string, BaseItem> BuildIndex(BaseItemKind kind)
    {
        var index = new Dictionary<string, BaseItem>(StringComparer.OrdinalIgnoreCase);
        var items = _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = [kind],
            Recursive = true,
            IsVirtualItem = false
        });

        foreach (var item in items)
        {
            Add(index, "tmdb", item.GetProviderId(MetadataProvider.Tmdb), item);
            Add(index, "imdb", item.GetProviderId(MetadataProvider.Imdb), item);
            Add(index, "tvdb", item.GetProviderId(MetadataProvider.Tvdb), item);
        }

        return index;

        static void Add(Dictionary<string, BaseItem> index, string prefix, string? id, BaseItem item)
        {
            if (!string.IsNullOrWhiteSpace(id))
            {
                index.TryAdd(prefix + ":" + id, item);
            }
        }
    }

    private static BaseItem? Find(Dictionary<string, BaseItem> index, params (string Prefix, string? Id)[] ids)
    {
        foreach (var (prefix, id) in ids)
        {
            if (!string.IsNullOrWhiteSpace(id) && index.TryGetValue(prefix + ":" + id, out var item))
            {
                return item;
            }
        }

        return null;
    }
}
