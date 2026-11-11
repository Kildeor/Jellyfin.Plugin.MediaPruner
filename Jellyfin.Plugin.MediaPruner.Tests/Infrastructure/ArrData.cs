using Jellyfin.Plugin.MediaPruner.Arr;

namespace Jellyfin.Plugin.MediaPruner.Tests.Infrastructure;

/// <summary>Builders for Radarr / Sonarr payloads.</summary>
public static class ArrData
{
    public static RadarrMovie Movie(int id, int addedDaysAgo, int tmdbId = 0, bool hasFile = true, long size = 1000, params int[] tags)
        => new()
        {
            Id = id,
            Title = "Movie " + id,
            Year = 2020,
            Added = DateTime.UtcNow.AddDays(-addedDaysAgo),
            TmdbId = tmdbId,
            HasFile = hasFile,
            SizeOnDisk = size,
            Tags = tags.ToList(),
            Images = [new ArrImage { CoverType = "poster", RemoteUrl = "http://img/" + id }]
        };

    public static SonarrSeries Series(int id, int addedDaysAgo, int tvdbId = 0, int episodeFiles = 1, string status = "ended", long size = 5000, params int[] tags)
        => new()
        {
            Id = id,
            Title = "Series " + id,
            Year = 2019,
            Added = DateTime.UtcNow.AddDays(-addedDaysAgo),
            TvdbId = tvdbId,
            Status = status,
            Tags = tags.ToList(),
            Statistics = new SonarrStatistics { SizeOnDisk = size, EpisodeFileCount = episodeFiles }
        };
}
