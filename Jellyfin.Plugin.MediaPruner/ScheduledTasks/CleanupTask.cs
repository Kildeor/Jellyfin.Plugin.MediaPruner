using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.MediaPruner.Services;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MediaPruner.ScheduledTasks;

/// <summary>
/// Scheduled cleanup task. Deletes eligible items when automatic deletion is enabled.
/// </summary>
public class CleanupTask : IScheduledTask
{
    private readonly CleanupService _cleanup;
    private readonly ILogger<CleanupTask> _logger;

    public CleanupTask(CleanupService cleanup, ILogger<CleanupTask> logger)
    {
        _cleanup = cleanup;
        _logger = logger;
    }

    public string Name => "Radarr / Sonarr cleanup";

    public string Key => "MediaPrunerCleanup";

    public string Description => "Deletes movies and series matching the Media Pruner criteria from Radarr and Sonarr.";

    public string Category => "Media Pruner";

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var result = await _cleanup.RunScheduledAsync(progress, cancellationToken).ConfigureAwait(false);
        _logger.LogInformation(
            "Media Pruner: {Deleted} deleted, {Failed} failed, {Freed} bytes freed{DryRun}",
            result.Deleted,
            result.Failed,
            result.FreedBytes,
            result.DryRun ? " (dry run)" : string.Empty);
    }

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        yield return new TaskTriggerInfo
        {
            Type = TaskTriggerInfoType.DailyTrigger,
            TimeOfDayTicks = TimeSpan.FromHours(3).Ticks
        };
    }
}
