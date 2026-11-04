using System;
using System.Collections.Generic;
using System.Globalization;
using Jellyfin.Plugin.MediaPruner.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.MediaPruner;

/// <summary>
/// Media Pruner plugin: automatically or manually deletes movies (Radarr) and series (Sonarr).
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    public static Plugin? Instance { get; private set; }

    public override string Name => "Media Pruner";

    public override Guid Id => Guid.Parse("6f0b1f3e-5a4c-4c1e-9d8b-2b7f2e6a9c41");

    public override string Description =>
        "Automatically or manually deletes movies and series from Radarr and Sonarr based on configurable criteria.";

    public IEnumerable<PluginPageInfo> GetPages()
    {
        return
        [
            new PluginPageInfo
            {
                Name = "MediaPruner",
                DisplayName = "Media Pruner",
                EmbeddedResourcePath = string.Format(CultureInfo.InvariantCulture, "{0}.Configuration.configPage.html", GetType().Namespace),
                EnableInMainMenu = true,
                MenuSection = "server",
                MenuIcon = "auto_delete"
            },
            new PluginPageInfo
            {
                Name = "MediaPrunerjs",
                EmbeddedResourcePath = string.Format(CultureInfo.InvariantCulture, "{0}.Configuration.configPage.js", GetType().Namespace)
            }
        ];
    }
}
