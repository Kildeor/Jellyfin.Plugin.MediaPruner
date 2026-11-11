using System.Net;
using Jellyfin.Plugin.MediaPruner.Arr;
using Jellyfin.Plugin.MediaPruner.Configuration;
using Jellyfin.Plugin.MediaPruner.Models;
using Jellyfin.Plugin.MediaPruner.Tests.Infrastructure;

namespace Jellyfin.Plugin.MediaPruner.Tests;

public class EvaluationTests
{
    private readonly CleanupHarness _h = new();

    public EvaluationTests() => TestPlugin.Reset(c => c.DaysBeforeDeletion = 90);

    private Task<CandidatesResult> Evaluate(bool all = false) => _h.Service.EvaluateAsync(all, CancellationToken.None);

    private static void Configure(Action<PluginConfiguration> configure) => TestPlugin.Reset(c =>
    {
        c.DaysBeforeDeletion = 90;
        configure(c);
    });

    // ---------------- Dates and window ----------------

    [Fact]
    public async Task OldMovie_IsEligible_WithComputedDates()
    {
        var movie = ArrData.Movie(1, addedDaysAgo: 100);
        _h.WithMovies(movie);

        var c = Assert.Single((await Evaluate()).Items);

        Assert.Equal("radarr:1", c.Key);
        Assert.Equal(ArrKind.Radarr, c.Source);
        Assert.Equal("Movie 1", c.Title);
        Assert.Equal("http://img/1", c.PosterUrl);
        Assert.Equal(-10, c.DaysRemaining);
        Assert.Equal(movie.Added!.Value.AddDays(90), c.DeletionDate!.Value, TimeSpan.FromSeconds(1));
        Assert.True(c.IsEligible);
        Assert.Empty(c.ExclusionReasons);
    }

    [Fact]
    public async Task Items_OutsideWarningWindow_AreHidden_UnlessAll()
    {
        Configure(c => c.WarningDays = 14);
        _h.WithMovies(ArrData.Movie(1, addedDaysAgo: 80), ArrData.Movie(2, addedDaysAgo: 10));

        var pending = await Evaluate();
        var c1 = Assert.Single(pending.Items);
        Assert.Equal(1, c1.ArrId);
        Assert.Equal(10, c1.DaysRemaining);
        Assert.False(c1.IsEligible);

        Assert.Equal(2, (await Evaluate(all: true)).Items.Count);
    }

    [Fact]
    public async Task Items_AreSortedByDaysRemainingThenTitle()
    {
        var b = ArrData.Movie(2, 100);
        b.Title = "Beta";
        var a = ArrData.Movie(3, 100);
        a.Title = "alpha";
        _h.WithMovies(ArrData.Movie(1, 95), b, a);

        var keys = (await Evaluate()).Items.Select(i => i.Title).ToList();

        Assert.Equal(new[] { "alpha", "Beta", "Movie 1" }, keys);
    }

    [Fact]
    public async Task MovieWithoutAddedDate_IsSkipped()
    {
        var m = ArrData.Movie(1, 100);
        m.Added = null;
        _h.WithMovies(m);

        Assert.Empty((await Evaluate(all: true)).Items);
    }

    [Fact]
    public async Task JellyfinDateSource_UsesJellyfinDateCreated()
    {
        Configure(c => c.DateSource = DateSource.Jellyfin);
        _h.WithMovies(ArrData.Movie(1, addedDaysAgo: 200, tmdbId: 10));
        _h.AddJellyfinMovie(10, dateCreated: DateTime.UtcNow.AddDays(-30));

        var c = Assert.Single((await Evaluate(all: true)).Items);

        Assert.Equal(60, c.DaysRemaining);
        Assert.False(c.IsEligible);
    }

    [Fact]
    public async Task JellyfinDateSource_FallsBackToArrDate_WhenNotInJellyfin()
    {
        Configure(c => c.DateSource = DateSource.Jellyfin);
        _h.WithMovies(ArrData.Movie(1, addedDaysAgo: 100, tmdbId: 10));

        Assert.Equal(-10, Assert.Single((await Evaluate()).Items).DaysRemaining);
    }

    [Fact]
    public async Task DownloadDateSource_UsesMovieFileDate_OrFallsBack()
    {
        Configure(c => c.DateSource = DateSource.Download);
        var downloaded = ArrData.Movie(1, addedDaysAgo: 300);
        downloaded.MovieFile = new ArrFile { Id = 1, DateAdded = DateTime.UtcNow.AddDays(-20) };
        var notDownloaded = ArrData.Movie(2, addedDaysAgo: 100, hasFile: false);
        _h.WithMovies(downloaded, notDownloaded);

        var items = (await Evaluate(all: true)).Items.ToDictionary(i => i.ArrId);

        Assert.Equal(70, items[1].DaysRemaining);
        Assert.Equal(-10, items[2].DaysRemaining);
    }

    [Fact]
    public async Task DownloadDateSource_UsesMostRecentEpisodeFile()
    {
        Configure(c => c.DateSource = DateSource.Download);
        _h.WithSeries(ArrData.Series(1, addedDaysAgo: 400), ArrData.Series(2, addedDaysAgo: 100, episodeFiles: 0), ArrData.Series(3, addedDaysAgo: 95));
        _h.WithEpisodeFiles(
            1,
            new ArrFile { Id = 1, DateAdded = DateTime.UtcNow.AddDays(-300) },
            new ArrFile { Id = 2, DateAdded = DateTime.UtcNow.AddDays(-50) },
            new ArrFile { Id = 3, DateAdded = null });
        _h.Http.Status("GET", ArrUrls.Sonarr + "/api/v3/episodefile?seriesId=3", HttpStatusCode.InternalServerError);

        var result = await Evaluate(all: true);
        var items = result.Items.ToDictionary(i => i.ArrId);

        Assert.Empty(result.Errors);
        Assert.Equal(40, items[1].DaysRemaining);
        Assert.Equal(-10, items[2].DaysRemaining); // no file: Sonarr added date
        Assert.Equal(-5, items[3].DaysRemaining); // episode files unreadable: Sonarr added date
        Assert.Empty(_h.Http.RequestsTo("GET", ArrUrls.Sonarr + "/api/v3/episodefile?seriesId=2"));
    }

    [Fact]
    public async Task ArrDateSource_DoesNotQueryEpisodeFiles()
    {
        _h.WithSeries(ArrData.Series(1, 100));

        await Evaluate();

        Assert.Empty(_h.Http.RequestsTo("GET", ArrUrls.Sonarr + "/api/v3/episodefile"));
    }

    // ---------------- Exclusions ----------------

    [Fact]
    public async Task MovieWithoutFile_IsExcluded()
    {
        _h.WithMovies(ArrData.Movie(1, 100, hasFile: false));

        var c = Assert.Single((await Evaluate()).Items);

        Assert.Contains("No downloaded file", c.ExclusionReasons);
        Assert.False(c.IsEligible);
    }

    [Fact]
    public async Task ProtectedTag_IsMatchedCaseInsensitively()
    {
        Configure(c => c.ProtectedTags = " Keep , forever ");
        _h.WithRadarrTags(new ArrTag { Id = 1, Label = "keep" }, new ArrTag { Id = 2, Label = "other" });
        _h.WithMovies(ArrData.Movie(1, 100, tags: 1), ArrData.Movie(2, 100, tags: 2));

        var items = (await Evaluate()).Items.ToDictionary(i => i.ArrId);

        Assert.Contains("Protected tag", items[1].ExclusionReasons);
        Assert.True(items[2].IsEligible);
    }

    [Fact]
    public async Task SonarrProtectedTag_ExcludesSeries()
    {
        _h.WithSonarrTags(new ArrTag { Id = 5, Label = "KEEP" });
        _h.WithSeries(ArrData.Series(1, 100, tags: 5));

        Assert.Contains("Protected tag", Assert.Single((await Evaluate()).Items).ExclusionReasons);
    }

    [Fact]
    public async Task ManuallyProtectedItem_IsExcluded()
    {
        Configure(c => c.ProtectedItems = ["RADARR:1"]);
        _h.WithMovies(ArrData.Movie(1, 100));

        var c = Assert.Single((await Evaluate()).Items);

        Assert.True(c.IsProtected);
        Assert.Contains("Manually protected", c.ExclusionReasons);
        Assert.False(c.IsEligible);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task FavoriteMovie_IsKeptOnlyWhenKeepFavorites(bool keepFavorites, bool eligible)
    {
        Configure(c => c.KeepFavorites = keepFavorites);
        _h.WithMovies(ArrData.Movie(1, 100, tmdbId: 10));
        var jf = _h.AddJellyfinMovie(10);
        var alice = _h.AddUser("alice");
        _h.AddUser("bob");
        _h.SetUserData(alice, jf, favorite: true);

        var c = Assert.Single((await Evaluate()).Items);

        Assert.True(c.IsFavorite);
        Assert.Equal(new[] { "alice" }, c.FavoritedBy);
        Assert.Equal(jf.Id.ToString("N"), c.JellyfinId);
        Assert.Equal(eligible, c.IsEligible);
        if (keepFavorites)
        {
            Assert.Contains("Favorite of alice", c.ExclusionReasons);
        }
    }

    [Fact]
    public async Task Movie_IsMatchedByImdb_WhenTmdbIsMissing()
    {
        var m = ArrData.Movie(1, 100);
        m.ImdbId = "tt0001";
        _h.WithMovies(m);
        var jf = _h.AddJellyfinMovie(0, imdbId: "tt0001");

        Assert.Equal(jf.Id.ToString("N"), Assert.Single((await Evaluate()).Items).JellyfinId);
    }

    [Fact]
    public async Task OnlyWatched_ExcludesUnwatchedItems()
    {
        Configure(c => c.OnlyWatched = true);
        _h.WithMovies(ArrData.Movie(1, 100, tmdbId: 10), ArrData.Movie(2, 100, tmdbId: 20), ArrData.Movie(3, 100));
        var watched = _h.AddJellyfinMovie(10);
        _h.AddJellyfinMovie(20);
        _h.SetUserData(_h.AddUser("alice"), watched, played: true);

        var items = (await Evaluate()).Items.ToDictionary(i => i.ArrId);

        Assert.True(items[1].IsEligible);
        Assert.Contains("Not watched yet", items[2].ExclusionReasons);
        Assert.Contains("Not watched yet", items[3].ExclusionReasons); // not in Jellyfin
    }

    [Fact]
    public async Task RecentlyPlayed_IsExcluded()
    {
        Configure(c => c.KeepIfPlayedWithinDays = 7);
        _h.WithMovies(ArrData.Movie(1, 100, tmdbId: 10), ArrData.Movie(2, 100, tmdbId: 20));
        var recent = _h.AddJellyfinMovie(10);
        var old = _h.AddJellyfinMovie(20);
        var alice = _h.AddUser("alice");
        var bob = _h.AddUser("bob");
        _h.SetUserData(alice, recent, lastPlayed: DateTime.UtcNow.AddDays(-30));
        _h.SetUserData(bob, recent, lastPlayed: DateTime.UtcNow.AddDays(-2));
        _h.SetUserData(alice, old, lastPlayed: DateTime.UtcNow.AddDays(-8));

        var items = (await Evaluate()).Items.ToDictionary(i => i.ArrId);

        Assert.Contains("Recently played", items[1].ExclusionReasons);
        Assert.Equal(DateTime.UtcNow.AddDays(-2), items[1].LastPlayed!.Value, TimeSpan.FromMinutes(1));
        Assert.True(items[2].IsEligible);
    }

    [Theory]
    [InlineData(true, "continuing", false)]
    [InlineData(true, "ended", true)]
    [InlineData(false, "continuing", true)]
    public async Task ContinuingSeries_AreKeptWhenConfigured(bool keepContinuing, string status, bool eligible)
    {
        Configure(c => c.KeepContinuingSeries = keepContinuing);
        _h.WithSeries(ArrData.Series(1, 100, status: status));

        var c = Assert.Single((await Evaluate()).Items);

        Assert.Equal(eligible, c.IsEligible);
        Assert.Equal(!eligible, c.ExclusionReasons.Contains("Series still airing"));
    }

    [Fact]
    public void KeepIfNotInJellyfin_IsEnabledByDefault()
        => Assert.True(new PluginConfiguration().KeepIfNotInJellyfin);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ItemsNotInJellyfin_AreKeptWhenConfigured(bool keepIfNotInJellyfin)
    {
        Configure(c => c.KeepIfNotInJellyfin = keepIfNotInJellyfin);
        _h.WithMovies(ArrData.Movie(1, 100, tmdbId: 10), ArrData.Movie(2, 100, tmdbId: 20));
        _h.WithSeries(ArrData.Series(1, 100, tvdbId: 77), ArrData.Series(2, 100, tvdbId: 88));
        _h.AddJellyfinMovie(10);
        _h.AddJellyfinSeries(77);

        var items = (await Evaluate()).Items.ToDictionary(i => i.Key);

        Assert.True(items["radarr:1"].IsEligible);
        Assert.True(items["sonarr:1"].IsEligible);
        foreach (var key in new[] { "radarr:2", "sonarr:2" })
        {
            Assert.Null(items[key].JellyfinId);
            Assert.Equal(!keepIfNotInJellyfin, items[key].IsEligible);
            Assert.Equal(keepIfNotInJellyfin, items[key].ExclusionReasons.Contains("Not in Jellyfin"));
        }
    }

    [Fact]
    public async Task SeriesWithoutEpisodeFiles_IsExcluded()
    {
        _h.WithSeries(ArrData.Series(1, 100, episodeFiles: 0));

        Assert.Contains("No downloaded file", Assert.Single((await Evaluate()).Items).ExclusionReasons);
    }

    // ---------------- Series user data ----------------

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task FavoriteEpisode_ProtectsSeries_OnlyWithKeepFavoriteEpisodes(bool keepEpisodes, bool eligible)
    {
        Configure(c => c.KeepFavoriteEpisodes = keepEpisodes);
        _h.WithSeries(ArrData.Series(1, 100, tvdbId: 77));
        var series = _h.AddJellyfinSeries(77);
        var episode = _h.AddEpisode(series);
        _h.SetUserData(_h.AddUser("alice"), episode, favorite: true);

        var c = Assert.Single((await Evaluate()).Items);

        Assert.Equal(!eligible, c.IsFavorite);
        Assert.Equal(eligible, c.IsEligible);
    }

    [Fact]
    public async Task FavoriteSeason_ProtectsSeries()
    {
        _h.WithSeries(ArrData.Series(1, 100, tvdbId: 77));
        var series = _h.AddJellyfinSeries(77);
        var season = _h.AddSeason(series);
        _h.AddEpisode(series);
        _h.SetUserData(_h.AddUser("alice"), season, favorite: true);

        Assert.True(Assert.Single((await Evaluate()).Items).IsFavorite);
    }

    [Fact]
    public async Task Series_IsWatched_OnlyWhenOneUserPlayedEveryEpisode()
    {
        Configure(c => c.OnlyWatched = true);
        _h.WithSeries(ArrData.Series(1, 100, tvdbId: 1), ArrData.Series(2, 100, tvdbId: 2), ArrData.Series(3, 100, tvdbId: 3));
        var alice = _h.AddUser("alice");
        var bob = _h.AddUser("bob");

        // Series 1: alice watched everything.
        var s1 = _h.AddJellyfinSeries(1);
        var e11 = _h.AddEpisode(s1);
        var e12 = _h.AddEpisode(s1);
        _h.SetUserData(alice, e11, played: true);
        _h.SetUserData(alice, e12, played: true);

        // Series 2: alice and bob each watched half.
        var s2 = _h.AddJellyfinSeries(2);
        var e21 = _h.AddEpisode(s2);
        var e22 = _h.AddEpisode(s2);
        _h.SetUserData(alice, e21, played: true);
        _h.SetUserData(bob, e22, played: true, lastPlayed: DateTime.UtcNow.AddDays(-3));

        // Series 3: no episodes in Jellyfin.
        _h.AddJellyfinSeries(3);

        var items = (await Evaluate()).Items.ToDictionary(i => i.ArrId);

        Assert.True(items[1].IsWatched);
        Assert.True(items[1].IsEligible);
        Assert.False(items[2].IsWatched);
        Assert.Equal(DateTime.UtcNow.AddDays(-3), items[2].LastPlayed!.Value, TimeSpan.FromMinutes(1));
        Assert.False(items[3].IsWatched);
    }

    // ---------------- Errors / enabled services ----------------

    [Fact]
    public async Task RadarrError_IsReported_AndSonarrIsStillEvaluated()
    {
        _h.Http.Status("GET", ArrUrls.Radarr + "/api/v3/movie", HttpStatusCode.Unauthorized);
        _h.WithSeries(ArrData.Series(1, 100));

        var result = await Evaluate();

        Assert.StartsWith("Radarr:", Assert.Single(result.Errors), StringComparison.Ordinal);
        Assert.Equal(ArrKind.Sonarr, Assert.Single(result.Items).Source);
    }

    [Fact]
    public async Task DisabledServices_AreNotQueried()
    {
        Configure(c =>
        {
            c.RadarrEnabled = false;
            c.SonarrEnabled = false;
            c.DryRun = false;
        });

        var result = await Evaluate();

        Assert.Empty(result.Items);
        Assert.Empty(result.Errors);
        Assert.False(result.DryRun);
        Assert.Empty(_h.Http.Requests);
    }
}
