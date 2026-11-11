using Jellyfin.Plugin.MediaPruner.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Model.Serialization;
using Moq;

namespace Jellyfin.Plugin.MediaPruner.Tests.Infrastructure;

/// <summary>
/// Creates the (static) <see cref="Plugin.Instance"/> once, backed by a temporary folder,
/// and lets each test start from a fresh configuration.
/// </summary>
public static class TestPlugin
{
    private static readonly object Lock = new();
    private static Plugin? _plugin;

    public static string RootPath { get; } = Path.Combine(Path.GetTempPath(), "mediapruner-tests-" + Guid.NewGuid().ToString("N"));

    public static Mock<IXmlSerializer> Serializer { get; } = new();

    public static Plugin Instance
    {
        get
        {
            lock (Lock)
            {
                if (_plugin is null)
                {
                    Directory.CreateDirectory(RootPath);
                    var paths = new Mock<IApplicationPaths>();
                    paths.SetupGet(p => p.ProgramDataPath).Returns(RootPath);
                    paths.SetupGet(p => p.DataPath).Returns(RootPath);
                    paths.SetupGet(p => p.CachePath).Returns(RootPath);
                    paths.SetupGet(p => p.TempDirectory).Returns(RootPath);
                    paths.SetupGet(p => p.PluginsPath).Returns(Path.Combine(RootPath, "plugins"));
                    paths.SetupGet(p => p.PluginConfigurationsPath).Returns(Path.Combine(RootPath, "configurations"));
                    Serializer
                        .Setup(s => s.DeserializeFromFile(It.IsAny<Type>(), It.IsAny<string>()))
                        .Returns(() => new PluginConfiguration());
                    _plugin = new Plugin(paths.Object, Serializer.Object);
                }

                return _plugin;
            }
        }
    }

    /// <summary>Resets the plugin configuration and the history file, then returns the new configuration.</summary>
    public static PluginConfiguration Reset(Action<PluginConfiguration>? configure = null)
    {
        var config = new PluginConfiguration
        {
            RadarrEnabled = true,
            RadarrUrl = ArrUrls.Radarr,
            RadarrApiKey = "radarr-key",
            SonarrEnabled = true,
            SonarrUrl = ArrUrls.Sonarr,
            SonarrApiKey = "sonarr-key",
            ProtectedTags = "keep",
            TagPendingItems = false,
            CollectionPendingItems = false,
            KeepIfNotInJellyfin = false,
        };
        configure?.Invoke(config);
        Instance.UpdateConfiguration(config);

        var history = Path.Combine(Instance.DataFolderPath, "history.json");
        if (File.Exists(history))
        {
            File.Delete(history);
        }

        return Instance.Configuration;
    }
}

public static class ArrUrls
{
    public const string Radarr = "http://radarr.test:7878";
    public const string Sonarr = "http://sonarr.test:8989";
}
