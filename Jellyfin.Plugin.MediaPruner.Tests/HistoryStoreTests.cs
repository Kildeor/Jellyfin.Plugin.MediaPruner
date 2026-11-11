using Jellyfin.Plugin.MediaPruner.Models;
using Jellyfin.Plugin.MediaPruner.Services;
using Jellyfin.Plugin.MediaPruner.Tests.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jellyfin.Plugin.MediaPruner.Tests;

public class HistoryStoreTests
{
    private readonly HistoryStore _store = new(NullLogger<HistoryStore>.Instance);

    public HistoryStoreTests() => TestPlugin.Reset();

    private static string FilePath => Path.Combine(TestPlugin.Instance.DataFolderPath, "history.json");

    private static HistoryEntry Entry(string key, DateTime date) => new() { Key = key, Title = key, Date = date, Mode = "Manual", Success = true };

    [Fact]
    public void Empty_WhenNoFile()
        => Assert.Empty(_store.GetAll());

    [Fact]
    public void Add_PersistsNewestFirst_BeforeExistingEntries()
    {
        var t = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        _store.Add([Entry("old", t)]);
        _store.Add([Entry("a", t.AddDays(1)), Entry("b", t.AddDays(2))]);

        Assert.Equal(new[] { "b", "a", "old" }, _store.GetAll().Select(e => e.Key));
        Assert.True(File.Exists(FilePath));

        // A new instance reads the same file.
        Assert.Equal(3, new HistoryStore(NullLogger<HistoryStore>.Instance).GetAll().Count);
    }

    [Fact]
    public void Add_KeepsOnlyTheLast500Entries()
    {
        var t = DateTime.UtcNow;
        _store.Add(Enumerable.Range(0, 450).Select(i => Entry("first" + i, t)));
        _store.Add(Enumerable.Range(0, 100).Select(i => Entry("second" + i, t.AddMinutes(1))));

        var all = _store.GetAll();
        Assert.Equal(500, all.Count);
        Assert.Equal(100, all.Count(e => e.Key.StartsWith("second", StringComparison.Ordinal)));
        Assert.Equal(400, all.Count(e => e.Key.StartsWith("first", StringComparison.Ordinal)));
    }

    [Fact]
    public void Clear_RemovesAllEntries()
    {
        _store.Add([Entry("a", DateTime.UtcNow)]);
        _store.Clear();
        Assert.Empty(_store.GetAll());
    }

    [Fact]
    public void CorruptedFile_IsIgnored()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, "{ not json");

        Assert.Empty(_store.GetAll());

        _store.Add([Entry("a", DateTime.UtcNow)]);
        Assert.Single(_store.GetAll());
    }
}
