using System.Net;
using Jellyfin.Plugin.MediaPruner.Arr;
using Jellyfin.Plugin.MediaPruner.Configuration;
using Jellyfin.Plugin.MediaPruner.Services;
using Jellyfin.Plugin.MediaPruner.Tests.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Jellyfin.Plugin.MediaPruner.Tests;

public class DeletionTests
{
    private readonly CleanupHarness _h = new();

    public DeletionTests() => Configure(_ => { });

    private static void Configure(Action<PluginConfiguration> configure) => TestPlugin.Reset(c =>
    {
        c.DaysBeforeDeletion = 90;
        c.DryRun = false;
        c.AutoDelete = true;
        c.MaxDeletionsPerRun = 0;
        configure(c);
    });

    private IReadOnlyList<Models.HistoryEntry> History => new HistoryStore(NullLogger<HistoryStore>.Instance).GetAll();

    private IEnumerable<HttpRequestMessage> Deletes => _h.Http.Requests.Where(r => r.Method == HttpMethod.Delete);

    private void AcceptDeletes(params string[] urls)
    {
        foreach (var url in urls)
        {
            _h.Http.Status("DELETE", url, HttpStatusCode.OK);
        }
    }

    // ---------------- Scheduled run ----------------

    [Fact]
    public async Task Scheduled_DeletesEligibleItems_AndRecordsHistory()
    {
        _h.WithMovies(ArrData.Movie(1, 100, size: 300), ArrData.Movie(2, 100, hasFile: false), ArrData.Movie(3, 10));
        _h.WithSeries(ArrData.Series(4, 120, size: 700));
        AcceptDeletes(ArrUrls.Radarr + "/api/v3/movie/1", ArrUrls.Sonarr + "/api/v3/series/4");
        var progress = new List<double>();

        var result = await _h.Service.RunScheduledAsync(new SyncProgress(progress), CancellationToken.None);

        Assert.Equal(2, result.Deleted);
        Assert.Equal(0, result.Failed);
        Assert.Equal(1000, result.FreedBytes);
        Assert.False(result.DryRun);
        Assert.Equal(
            new[] { "/api/v3/movie/1", "/api/v3/series/4" },
            Deletes.Select(r => r.RequestUri!.AbsolutePath).OrderBy(p => p));
        Assert.Equal(100, progress[^1]);
        Assert.All(History, e => Assert.Equal("Auto", e.Mode));
        Assert.Equal(new[] { "radarr:1", "sonarr:4" }, History.Select(e => e.Key).OrderBy(k => k));
        _h.Library.Verify(l => l.QueueLibraryScan(), Times.Once);
    }

    [Fact]
    public async Task Scheduled_PassesDeleteFilesAndExclusionOptions()
    {
        Configure(c =>
        {
            c.DeleteFiles = false;
            c.AddImportExclusion = true;
            c.RefreshLibraryAfterDelete = false;
        });
        _h.WithMovies(ArrData.Movie(1, 100));
        AcceptDeletes(ArrUrls.Radarr + "/api/v3/movie/1");

        await _h.Service.RunScheduledAsync(null, CancellationToken.None);

        var delete = Assert.Single(Deletes);
        Assert.Equal("?deleteFiles=false&addImportExclusion=true", delete.RequestUri!.Query);
        Assert.Equal("radarr-key", Assert.Single(delete.Headers.GetValues("X-Api-Key")));
        _h.Library.Verify(l => l.QueueLibraryScan(), Times.Never);
    }

    [Fact]
    public async Task Scheduled_DoesNothing_WhenAutoDeleteIsDisabled()
    {
        Configure(c => c.AutoDelete = false);
        _h.WithMovies(ArrData.Movie(1, 100));

        var result = await _h.Service.RunScheduledAsync(null, CancellationToken.None);

        Assert.Equal(0, result.Deleted);
        Assert.Empty(Deletes);
        Assert.Empty(History);
    }

    [Fact]
    public async Task Scheduled_DryRun_OnlyLogsAndRecordsHistory()
    {
        Configure(c => c.DryRun = true);
        _h.WithMovies(ArrData.Movie(1, 100, size: 50));

        var result = await _h.Service.RunScheduledAsync(null, CancellationToken.None);

        Assert.True(result.DryRun);
        Assert.Equal(1, result.Deleted);
        Assert.Empty(Deletes);
        var entry = Assert.Single(History);
        Assert.Equal("Dry run", entry.Mode);
        Assert.True(entry.Success);
        _h.Library.Verify(l => l.QueueLibraryScan(), Times.Never);
    }

    [Fact]
    public async Task Scheduled_HonoursMaxDeletionsPerRun_InDeletionOrder()
    {
        Configure(c => c.MaxDeletionsPerRun = 2);
        _h.WithMovies(ArrData.Movie(1, 100), ArrData.Movie(2, 200), ArrData.Movie(3, 150));
        AcceptDeletes(ArrUrls.Radarr + "/api/v3/movie/2", ArrUrls.Radarr + "/api/v3/movie/3");

        var result = await _h.Service.RunScheduledAsync(null, CancellationToken.None);

        Assert.Equal(2, result.Deleted);
        Assert.Equal(new[] { "/api/v3/movie/2", "/api/v3/movie/3" }, Deletes.Select(r => r.RequestUri!.AbsolutePath));
    }

    [Fact]
    public async Task Scheduled_FailedDeletion_IsCountedAndRecorded()
    {
        _h.WithMovies(ArrData.Movie(1, 100), ArrData.Movie(2, 100));
        AcceptDeletes(ArrUrls.Radarr + "/api/v3/movie/2");
        _h.Http.Status("DELETE", ArrUrls.Radarr + "/api/v3/movie/1", HttpStatusCode.InternalServerError);

        var result = await _h.Service.RunScheduledAsync(null, CancellationToken.None);

        Assert.Equal(1, result.Deleted);
        Assert.Equal(1, result.Failed);
        var failed = Assert.Single(History, e => !e.Success);
        Assert.Equal("radarr:1", failed.Key);
        Assert.False(string.IsNullOrEmpty(failed.Error));
    }

    [Fact]
    public async Task Scheduled_Cancellation_StopsTheRun()
    {
        _h.WithMovies(ArrData.Movie(1, 100));
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _h.Service.RunScheduledAsync(null, cts.Token));
        Assert.Empty(Deletes);
    }

    // ---------------- Manual deletion ----------------

    [Fact]
    public async Task DeleteAllEligible_IgnoresAutoDeleteAndMaxPerRun()
    {
        Configure(c =>
        {
            c.AutoDelete = false;
            c.MaxDeletionsPerRun = 1;
        });
        _h.WithMovies(ArrData.Movie(1, 100), ArrData.Movie(2, 100), ArrData.Movie(3, 10));
        AcceptDeletes(ArrUrls.Radarr + "/api/v3/movie/1", ArrUrls.Radarr + "/api/v3/movie/2");

        var result = await _h.Service.DeleteAllEligibleAsync(CancellationToken.None);

        Assert.Equal(2, result.Deleted);
        Assert.All(History, e => Assert.Equal("Manual", e.Mode));
    }

    [Fact]
    public async Task DeleteByKeys_DeletesRequestedItems_EvenIfNotEligible()
    {
        _h.WithMovies(ArrData.Movie(1, 1, hasFile: false, size: 10));
        _h.WithSeries(ArrData.Series(4, 1, size: 20));
        AcceptDeletes(ArrUrls.Radarr + "/api/v3/movie/1", ArrUrls.Sonarr + "/api/v3/series/4", ArrUrls.Sonarr + "/api/v3/series/99");

        var result = await _h.Service.DeleteByKeysAsync(["radarr:1", "Sonarr:4", "sonarr:4", "sonarr:99", "garbage", "lidarr:1"], CancellationToken.None);

        Assert.Equal(3, result.Deleted);
        Assert.Equal(30, result.FreedBytes);
        var titles = History.ToDictionary(e => e.Key, e => e.Title);
        Assert.Equal("Movie 1", titles["radarr:1"]);
        Assert.Equal("Series 4", titles["sonarr:4"]);
        Assert.Equal("sonarr:99", titles["sonarr:99"]); // unknown in Sonarr: key used as title
        Assert.All(History, e => Assert.Equal("Manual", e.Mode));
    }

    [Fact]
    public async Task DeleteByKeys_OnlyQueriesTheNeededService()
    {
        _h.WithMovies(ArrData.Movie(1, 100));
        AcceptDeletes(ArrUrls.Radarr + "/api/v3/movie/1");

        await _h.Service.DeleteByKeysAsync(["radarr:1"], CancellationToken.None);

        Assert.DoesNotContain(_h.Http.Requests, r => r.RequestUri!.Host == "sonarr.test");
    }

    [Fact]
    public async Task DeleteByKeys_DryRun_DoesNotDelete()
    {
        Configure(c => c.DryRun = true);
        _h.WithMovies(ArrData.Movie(1, 100));

        var result = await _h.Service.DeleteByKeysAsync(["radarr:1"], CancellationToken.None);

        Assert.True(result.DryRun);
        Assert.Empty(Deletes);
        Assert.Equal("Dry run", Assert.Single(History).Mode);
    }

    [Fact]
    public async Task DeleteByKeys_NoValidKey_ReturnsEmptyResult()
    {
        var result = await _h.Service.DeleteByKeysAsync(["nope"], CancellationToken.None);

        Assert.Equal(0, result.Deleted);
        Assert.Empty(_h.Http.Requests);
        Assert.Empty(History);
    }

    // ---------------- Protection ----------------

    [Fact]
    public void SetProtected_AddsAndRemovesKeys_CaseInsensitively()
    {
        _h.Service.SetProtected("sonarr:4", true);
        _h.Service.SetProtected("radarr:12", true);
        _h.Service.SetProtected("RADARR:12", true);

        Assert.Equal(new[] { "radarr:12", "sonarr:4" }, TestPlugin.Instance.Configuration.ProtectedItems);

        _h.Service.SetProtected("Sonarr:4", false);

        Assert.Equal(new[] { "radarr:12" }, TestPlugin.Instance.Configuration.ProtectedItems);
    }

    [Fact]
    public async Task ProtectedItem_IsNotDeletedByScheduledRun()
    {
        _h.WithMovies(ArrData.Movie(1, 100));
        _h.Service.SetProtected("radarr:1", true);

        var result = await _h.Service.RunScheduledAsync(null, CancellationToken.None);

        Assert.Equal(0, result.Deleted);
        Assert.Empty(Deletes);
    }

    /// <summary>Synchronous <see cref="IProgress{T}"/> (Progress&lt;T&gt; posts to the thread pool).</summary>
    private sealed class SyncProgress(List<double> values) : IProgress<double>
    {
        public void Report(double value) => values.Add(value);
    }
}
