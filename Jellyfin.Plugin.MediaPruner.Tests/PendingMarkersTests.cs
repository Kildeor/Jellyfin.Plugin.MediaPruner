using Jellyfin.Plugin.MediaPruner.Configuration;
using Jellyfin.Plugin.MediaPruner.Models;
using Jellyfin.Plugin.MediaPruner.Tests.Infrastructure;
using MediaBrowser.Controller.Collections;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using Moq;

namespace Jellyfin.Plugin.MediaPruner.Tests;

public class PendingMarkersTests
{
    private const string Tag = "Leaving soon";

    private readonly CleanupHarness _h = new();

    public PendingMarkersTests() => Configure(_ => { });

    private static PluginConfiguration Config => TestPlugin.Instance.Configuration;

    private static void Configure(Action<PluginConfiguration> configure) => TestPlugin.Reset(c =>
    {
        c.WarningDays = 14;
        c.TagPendingItems = true;
        c.PendingTagName = Tag;
        configure(c);
    });

    private static CleanupCandidate Pending(BaseItem item, int daysRemaining = 5, params string[] reasons)
    {
        var c = new CleanupCandidate { JellyfinId = item.Id.ToString("N"), DaysRemaining = daysRemaining };
        c.ExclusionReasons.AddRange(reasons);
        return c;
    }

    private Task Sync(params CleanupCandidate[] items) => Sync(new CandidatesResult { Items = items.ToList() });

    private Task Sync(CandidatesResult result) => _h.Service.SyncPendingMarkersAsync(result, CancellationToken.None);

    // ---------------- Tag ----------------

    [Fact]
    public async Task Tag_IsAddedToPendingItems_Only()
    {
        var pending = _h.AddJellyfinMovie(1);
        var excluded = _h.AddJellyfinMovie(2);
        var later = _h.AddJellyfinMovie(3);

        await Sync(Pending(pending), Pending(excluded, 5, "Protected tag"), Pending(later, 30));

        Assert.Contains(Tag, pending.Tags);
        Assert.DoesNotContain(Tag, excluded.Tags ?? []);
        Assert.DoesNotContain(Tag, later.Tags ?? []);
        Assert.Equal(Tag, Config.AppliedPendingTagName);
        _h.Library.Verify(
            l => l.UpdateItemAsync(pending, It.IsAny<BaseItem>(), ItemUpdateType.MetadataEdit, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task Tag_IsNotAddedTwice_AndOtherTagsArePreserved()
    {
        var movie = _h.AddJellyfinMovie(1);
        movie.Tags = ["kids", "leaving SOON"];

        await Sync(Pending(movie));

        Assert.Equal(new[] { "kids", "leaving SOON" }, movie.Tags);
        _h.Library.Verify(
            l => l.UpdateItemAsync(It.IsAny<BaseItem>(), It.IsAny<BaseItem>(), It.IsAny<ItemUpdateType>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Tag_IsRemovedFromItemsNoLongerPending()
    {
        var stale = _h.AddJellyfinMovie(1);
        stale.Tags = ["kids", Tag];

        await Sync();

        Assert.Equal(new[] { "kids" }, stale.Tags);
    }

    [Fact]
    public async Task Tag_IsNotRemoved_WhenEvaluationHadErrors()
    {
        var stale = _h.AddJellyfinMovie(1);
        stale.Tags = [Tag];
        Config.AppliedPendingTagName = "Old name";

        await Sync(new CandidatesResult { Errors = ["Radarr: unreachable"] });

        Assert.Equal(new[] { Tag }, stale.Tags);
        Assert.Equal("Old name", Config.AppliedPendingTagName); // not committed on an incomplete run
    }

    [Fact]
    public async Task RenamedTag_OldNameIsCleanedUp()
    {
        Configure(c =>
        {
            c.PendingTagName = "Bye bye";
            c.AppliedPendingTagName = Tag;
        });
        var movie = _h.AddJellyfinMovie(1);
        movie.Tags = [Tag];

        await Sync(Pending(movie));

        Assert.Equal(new[] { "Bye bye" }, movie.Tags);
        Assert.Equal("Bye bye", Config.AppliedPendingTagName);
    }

    [Fact]
    public async Task DisabledTag_RemovesPreviouslyAppliedTag()
    {
        Configure(c =>
        {
            c.TagPendingItems = false;
            c.AppliedPendingTagName = Tag;
        });
        var movie = _h.AddJellyfinMovie(1);
        movie.Tags = [Tag];

        await Sync(Pending(movie));

        Assert.Empty(movie.Tags);
        Assert.Equal(string.Empty, Config.AppliedPendingTagName);
    }

    // ---------------- Collection ----------------

    [Fact]
    public async Task Collection_IsCreated_WithPendingItems()
    {
        Configure(c => c.CollectionPendingItems = true);
        var movie = _h.AddJellyfinMovie(1);
        var created = new BoxSet { Id = Guid.NewGuid(), Name = Tag };
        CollectionCreationOptions? options = null;
        _h.Collections
            .Setup(c => c.CreateCollectionAsync(It.IsAny<CollectionCreationOptions>()))
            .Callback((CollectionCreationOptions o) => options = o)
            .ReturnsAsync(created);

        await Sync(Pending(movie));

        Assert.NotNull(options);
        Assert.Equal(Tag, options!.Name);
        Assert.True(options.IsLocked);
        Assert.Equal(new[] { movie.Id.ToString("N") }, options.ItemIdList);
        Assert.Equal(created.Id.ToString("N"), Config.PendingCollectionId);
        Assert.True(Config.PendingCollectionCreatedByPlugin);
    }

    [Fact]
    public async Task Collection_IsNotCreated_WhenNothingIsPending()
    {
        Configure(c => c.CollectionPendingItems = true);

        await Sync();

        _h.Collections.Verify(c => c.CreateCollectionAsync(It.IsAny<CollectionCreationOptions>()), Times.Never);
        Assert.Equal(string.Empty, Config.PendingCollectionId);
    }

    [Fact]
    public async Task Collection_ExistingWithSameName_IsReused_AndKeptAfterDisable()
    {
        Configure(c => c.CollectionPendingItems = true);
        var movie = _h.AddJellyfinMovie(1);
        var existing = new BoxSet { Id = Guid.NewGuid(), Name = "leaving soon" };
        _h.Items.Add(existing);

        await Sync(Pending(movie));

        _h.Collections.Verify(c => c.CreateCollectionAsync(It.IsAny<CollectionCreationOptions>()), Times.Never);
        _h.Collections.Verify(c => c.AddToCollectionAsync(existing.Id, It.Is<IEnumerable<Guid>>(ids => ids.Single() == movie.Id)), Times.Once);
        Assert.Equal(existing.Id.ToString("N"), Config.PendingCollectionId);
        Assert.False(Config.PendingCollectionCreatedByPlugin);
        Assert.Equal(Tag, existing.Name); // renamed to the configured name

        // Turning the feature off forgets the collection but does not delete it (it was not created by the plugin).
        Config.CollectionPendingItems = false;
        await Sync(Pending(movie));

        _h.Library.Verify(l => l.DeleteItem(It.IsAny<BaseItem>(), It.IsAny<DeleteOptions>(), It.IsAny<bool>()), Times.Never);
        Assert.Equal(string.Empty, Config.PendingCollectionId);
    }

    [Fact]
    public async Task Collection_CreatedByPlugin_IsDeletedWhenDisabled()
    {
        var collection = new BoxSet { Id = Guid.NewGuid(), Name = Tag };
        _h.Items.Add(collection);
        Configure(c =>
        {
            c.CollectionPendingItems = false;
            c.PendingCollectionId = collection.Id.ToString("N");
            c.PendingCollectionCreatedByPlugin = true;
        });

        await Sync();

        _h.Library.Verify(l => l.DeleteItem(collection, It.IsAny<DeleteOptions>(), true), Times.Once);
        Assert.Equal(string.Empty, Config.PendingCollectionId);
        Assert.False(Config.PendingCollectionCreatedByPlugin);
    }

    [Fact]
    public async Task Collection_StaleItemsAreRemoved_OnlyWhenEvaluationIsComplete()
    {
        var pending = _h.AddJellyfinMovie(1);
        var stale = _h.AddJellyfinMovie(2);
        var collection = new BoxSet { Id = Guid.NewGuid(), Name = Tag };
        collection.LinkedChildren = [LinkedChild.Create(stale)];
        _h.Items.Add(collection);
        Configure(c =>
        {
            c.TagPendingItems = false;
            c.CollectionPendingItems = true;
            c.PendingCollectionId = collection.Id.ToString("N");
            c.PendingCollectionCreatedByPlugin = true;
        });

        await Sync(new CandidatesResult { Items = [Pending(pending)], Errors = ["Sonarr: down"] });

        _h.Collections.Verify(c => c.AddToCollectionAsync(collection.Id, It.Is<IEnumerable<Guid>>(ids => ids.Single() == pending.Id)), Times.Once);
        _h.Collections.Verify(c => c.RemoveFromCollectionAsync(It.IsAny<Guid>(), It.IsAny<IEnumerable<Guid>>()), Times.Never);

        await Sync(Pending(pending));

        _h.Collections.Verify(c => c.RemoveFromCollectionAsync(collection.Id, It.Is<IEnumerable<Guid>>(ids => ids.Single() == stale.Id)), Times.Once);
    }
}
