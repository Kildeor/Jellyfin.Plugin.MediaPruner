using Jellyfin.Plugin.MediaPruner.Arr;
using Jellyfin.Plugin.MediaPruner.Services;

namespace Jellyfin.Plugin.MediaPruner.Tests;

public class KeyTests
{
    [Theory]
    [InlineData(ArrKind.Radarr, 12, "radarr:12")]
    [InlineData(ArrKind.Sonarr, 4, "sonarr:4")]
    public void MakeKey_FormatsSourceAndId(ArrKind kind, int id, string expected)
        => Assert.Equal(expected, CleanupService.MakeKey(kind, id));

    [Theory]
    [InlineData("radarr:12", ArrKind.Radarr, 12)]
    [InlineData("SONARR:4", ArrKind.Sonarr, 4)]
    [InlineData("Radarr:-1", ArrKind.Radarr, -1)]
    public void ParseKey_ValidKeys(string key, ArrKind kind, int id)
    {
        var parsed = CleanupService.ParseKey(key);
        Assert.NotNull(parsed);
        Assert.Equal(kind, parsed!.Value.Kind);
        Assert.Equal(id, parsed.Value.Id);
    }

    [Theory]
    [InlineData("")]
    [InlineData("radarr")]
    [InlineData("radarr:")]
    [InlineData("radarr:abc")]
    [InlineData("lidarr:3")]
    [InlineData("radarr:1:2")]
    [InlineData(null)]
    public void ParseKey_InvalidKeys_ReturnNull(string? key)
        => Assert.Null(CleanupService.ParseKey(key!));

    [Fact]
    public void ParseKey_RoundTripsMakeKey()
    {
        var parsed = CleanupService.ParseKey(CleanupService.MakeKey(ArrKind.Sonarr, 42));
        Assert.Equal((ArrKind.Sonarr, 42), parsed);
    }
}
