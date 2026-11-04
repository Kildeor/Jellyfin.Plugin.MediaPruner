using System;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.MediaPruner.Configuration;

/// <summary>
/// Source used for an item's added date.
/// </summary>
public enum DateSource
{
    /// <summary>Date added in Radarr / Sonarr.</summary>
    Arr = 0,

    /// <summary>Date added to the Jellyfin library.</summary>
    Jellyfin = 1,

    /// <summary>
    /// Date the file was downloaded / imported by Radarr / Sonarr
    /// (for series: the most recently downloaded episode).
    /// </summary>
    Download = 2
}

/// <summary>
/// Plugin configuration.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    // ----- Radarr -----
    public bool RadarrEnabled { get; set; }

    public string RadarrUrl { get; set; } = "http://localhost:7878";

    public string RadarrApiKey { get; set; } = string.Empty;

    // ----- Sonarr -----
    public bool SonarrEnabled { get; set; }

    public string SonarrUrl { get; set; } = "http://localhost:8989";

    public string SonarrApiKey { get; set; } = string.Empty;

    // ----- Criteria -----

    /// <summary>Delete items added more than X days ago.</summary>
    public int DaysBeforeDeletion { get; set; } = 90;

    /// <summary>Source of the added date.</summary>
    public DateSource DateSource { get; set; } = DateSource.Arr;

    /// <summary>Do not delete an item favorited by at least one user.</summary>
    public bool KeepFavorites { get; set; } = true;

    /// <summary>For series, also consider favorited seasons / episodes.</summary>
    public bool KeepFavoriteEpisodes { get; set; } = true;

    /// <summary>Only delete items fully watched by at least one user.</summary>
    public bool OnlyWatched { get; set; }

    /// <summary>Do not delete if the item was played in the last X days (0 = disabled).</summary>
    public int KeepIfPlayedWithinDays { get; set; }

    /// <summary>Do not delete series that are still airing (Sonarr status "continuing").</summary>
    public bool KeepContinuingSeries { get; set; }

    /// <summary>Radarr/Sonarr tags that protect an item (comma-separated).</summary>
    public string ProtectedTags { get; set; } = "keep";

    /// <summary>Keys of manually protected items (e.g. "radarr:12", "sonarr:4").</summary>
    public string[] ProtectedItems { get; set; } = Array.Empty<string>();

    // ----- Deletion mode -----

    /// <summary>Automatic deletion (scheduled task) or manual only.</summary>
    public bool AutoDelete { get; set; }

    /// <summary>Dry-run mode: the automatic task logs without deleting.</summary>
    public bool DryRun { get; set; } = true;

    /// <summary>Also delete the files from disk.</summary>
    public bool DeleteFiles { get; set; } = true;

    /// <summary>Add the item to the Radarr/Sonarr import exclusion list.</summary>
    public bool AddImportExclusion { get; set; }

    /// <summary>Maximum number of deletions per automatic run (0 = unlimited).</summary>
    public int MaxDeletionsPerRun { get; set; } = 10;

    /// <summary>Show items that will be deleted within the next X days.</summary>
    public int WarningDays { get; set; } = 14;

    /// <summary>Run a Jellyfin library scan after deletion.</summary>
    public bool RefreshLibraryAfterDelete { get; set; } = true;

    // ----- Jellyfin tag -----

    /// <summary>Add a Jellyfin tag to items that will be deleted within the warning window.</summary>
    public bool TagPendingItems { get; set; } = true;

    /// <summary>Name of the Jellyfin tag added to items pending deletion.</summary>
    public string PendingTagName { get; set; } = "Leaving soon";

    /// <summary>Tag name actually applied during the last sync (internal, used to clean up after a rename).</summary>
    public string AppliedPendingTagName { get; set; } = string.Empty;

    // ----- Jellyfin collection -----

    /// <summary>Keep a Jellyfin collection of the items that will be deleted within the warning window.</summary>
    public bool CollectionPendingItems { get; set; } = true;

    /// <summary>Name of the Jellyfin collection of items pending deletion.</summary>
    public string PendingCollectionName { get; set; } = "Leaving soon";

    /// <summary>Id of the collection managed by the plugin (internal).</summary>
    public string PendingCollectionId { get; set; } = string.Empty;

    /// <summary>True if the plugin created the collection itself (internal; only then is it deleted when disabled).</summary>
    public bool PendingCollectionCreatedByPlugin { get; set; }
}
