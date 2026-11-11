using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.MediaPruner.Arr;
using Jellyfin.Plugin.MediaPruner.Services;
using MediaBrowser.Controller.Collections;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Jellyfin.Plugin.MediaPruner.Tests.Infrastructure;

/// <summary>
/// Wires a <see cref="CleanupService"/> to a fake Radarr/Sonarr server and an in-memory Jellyfin library.
/// </summary>
public sealed class CleanupHarness
{
    private readonly Dictionary<(Guid User, Guid Item), UserItemData> _userData = new();
    private readonly Dictionary<Guid, List<BaseItem>> _children = new();

    public CleanupHarness()
    {
        Library.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>())).Returns((InternalItemsQuery q) => Query(q));
        Library.Setup(l => l.GetItemById(It.IsAny<Guid>())).Returns((Guid id) => AllItems().FirstOrDefault(i => i.Id == id));
        Library
            .Setup(l => l.UpdateItemAsync(It.IsAny<BaseItem>(), It.IsAny<BaseItem>(), It.IsAny<ItemUpdateType>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        Users.Setup(u => u.GetUsers()).Returns(() => UserList);
        UserData
            .Setup(u => u.GetUserData(It.IsAny<User>(), It.IsAny<BaseItem>()))
            .Returns((User u, BaseItem i) => _userData.TryGetValue((u.Id, i.Id), out var d) ? d : null);

        // BaseItem.UpdateToRepositoryAsync goes through this static.
        BaseItem.LibraryManager = Library.Object;

        Http.Json("GET", ArrUrls.Radarr + "/api/v3/tag", new List<ArrTag>());
        Http.Json("GET", ArrUrls.Radarr + "/api/v3/movie", new List<RadarrMovie>());
        Http.Json("GET", ArrUrls.Sonarr + "/api/v3/tag", new List<ArrTag>());
        Http.Json("GET", ArrUrls.Sonarr + "/api/v3/series", new List<SonarrSeries>());

        Service = new CleanupService(
            new ArrClient(new FakeHttpClientFactory(Http), NullLogger<ArrClient>.Instance),
            new HistoryStore(NullLogger<HistoryStore>.Instance),
            Library.Object,
            Users.Object,
            UserData.Object,
            Collections.Object,
            NullLogger<CleanupService>.Instance);
    }

    /// <summary>Single fake HTTP server answering for both Radarr and Sonarr (distinct hosts).</summary>
    public FakeArrServer Http { get; } = new();

    public Mock<ILibraryManager> Library { get; } = new();

    public Mock<IUserManager> Users { get; } = new();

    public Mock<IUserDataManager> UserData { get; } = new();

    public Mock<ICollectionManager> Collections { get; } = new();

    public List<User> UserList { get; } = new();

    public List<BaseItem> Items { get; } = new();

    public CleanupService Service { get; }

    public CleanupHarness WithMovies(params RadarrMovie[] movies)
    {
        Http.Json("GET", ArrUrls.Radarr + "/api/v3/movie", movies.ToList());
        return this;
    }

    public CleanupHarness WithSeries(params SonarrSeries[] series)
    {
        Http.Json("GET", ArrUrls.Sonarr + "/api/v3/series", series.ToList());
        return this;
    }

    public CleanupHarness WithRadarrTags(params ArrTag[] tags)
    {
        Http.Json("GET", ArrUrls.Radarr + "/api/v3/tag", tags.ToList());
        return this;
    }

    public CleanupHarness WithSonarrTags(params ArrTag[] tags)
    {
        Http.Json("GET", ArrUrls.Sonarr + "/api/v3/tag", tags.ToList());
        return this;
    }

    public CleanupHarness WithEpisodeFiles(int seriesId, params ArrFile[] files)
    {
        Http.Json("GET", ArrUrls.Sonarr + "/api/v3/episodefile?seriesId=" + seriesId, files.ToList());
        return this;
    }

    public User AddUser(string name)
    {
        var user = new User(name, "auth", "reset");
        UserList.Add(user);
        return user;
    }

    public Movie AddJellyfinMovie(int tmdbId, DateTime? dateCreated = null, string? imdbId = null)
    {
        var movie = new Movie { Id = Guid.NewGuid(), Name = "jf-movie-" + tmdbId, DateCreated = dateCreated ?? DateTime.UtcNow };
        if (tmdbId > 0)
        {
            movie.SetProviderId(MetadataProvider.Tmdb, tmdbId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        if (imdbId is not null)
        {
            movie.SetProviderId(MetadataProvider.Imdb, imdbId);
        }

        Items.Add(movie);
        return movie;
    }

    public Series AddJellyfinSeries(int tvdbId, DateTime? dateCreated = null)
    {
        var series = new Series { Id = Guid.NewGuid(), Name = "jf-series-" + tvdbId, DateCreated = dateCreated ?? DateTime.UtcNow };
        series.SetProviderId(MetadataProvider.Tvdb, tvdbId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Items.Add(series);
        _children[series.Id] = new List<BaseItem>();
        return series;
    }

    public Episode AddEpisode(Series series)
    {
        var episode = new Episode { Id = Guid.NewGuid(), Name = "episode" };
        _children[series.Id].Add(episode);
        return episode;
    }

    public Season AddSeason(Series series)
    {
        var season = new Season { Id = Guid.NewGuid(), Name = "season" };
        _children[series.Id].Add(season);
        return season;
    }

    public void SetUserData(User user, BaseItem item, bool favorite = false, bool played = false, DateTime? lastPlayed = null)
        => _userData[(user.Id, item.Id)] = new UserItemData
        {
            Key = item.Id.ToString("N"),
            IsFavorite = favorite,
            Played = played,
            LastPlayedDate = lastPlayed
        };

    private IEnumerable<BaseItem> AllItems() => Items.Concat(_children.Values.SelectMany(c => c));

    private List<BaseItem> Query(InternalItemsQuery q)
    {
        IEnumerable<BaseItem> source = q.AncestorIds is { Length: > 0 }
            ? q.AncestorIds.SelectMany(id => _children.TryGetValue(id, out var c) ? c : new List<BaseItem>())
            : Items;

        if (q.IncludeItemTypes is { Length: > 0 })
        {
            source = source.Where(i => q.IncludeItemTypes.Contains(i.GetBaseItemKind()));
        }

        if (q.Tags is { Length: > 0 })
        {
            source = source.Where(i => (i.Tags ?? Array.Empty<string>()).Any(t => q.Tags.Contains(t, StringComparer.OrdinalIgnoreCase)));
        }

        if (q.Name is not null)
        {
            source = source.Where(i => string.Equals(i.Name, q.Name, StringComparison.OrdinalIgnoreCase));
        }

        return source.ToList();
    }
}
