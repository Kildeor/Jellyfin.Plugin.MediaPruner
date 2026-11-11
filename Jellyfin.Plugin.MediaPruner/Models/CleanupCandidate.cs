using System;
using System.Collections.Generic;
using Jellyfin.Plugin.MediaPruner.Arr;

namespace Jellyfin.Plugin.MediaPruner.Models;

/// <summary>
/// Radarr/Sonarr item evaluated against the deletion criteria.
/// </summary>
public class CleanupCandidate
{
    /// <summary>Unique key, e.g. "radarr:12".</summary>
    public string Key { get; set; } = string.Empty;

    public ArrKind Source { get; set; }

    public int ArrId { get; set; }

    public string Title { get; set; } = string.Empty;

    public int Year { get; set; }

    public DateTime? AddedDate { get; set; }

    public DateTime? DeletionDate { get; set; }

    /// <summary>Days remaining before deletion (zero or negative = eligible).</summary>
    public int DaysRemaining { get; set; }

    public long SizeOnDisk { get; set; }

    public string? PosterUrl { get; set; }

    public string? JellyfinId { get; set; }

    public bool IsFavorite { get; set; }

    public List<string> FavoritedBy { get; set; } = new();

    public bool IsWatched { get; set; }

    public DateTime? LastPlayed { get; set; }

    public bool IsProtected { get; set; }

    /// <summary>Reasons why the item will not be deleted automatically.</summary>
    public List<string> ExclusionReasons { get; set; } = new();

    /// <summary>The item will be deleted on the next automatic run.</summary>
    public bool IsEligible { get; set; }
}

/// <summary>Result of an evaluation.</summary>
public class CandidatesResult
{
    public DateTime GeneratedAt { get; set; }

    /// <summary>Whether dry-run mode is active (deletions are only simulated).</summary>
    public bool DryRun { get; set; }

    public List<CleanupCandidate> Items { get; set; } = new();

    public List<string> Errors { get; set; } = new();
}

/// <summary>Deletion history entry.</summary>
public class HistoryEntry
{
    public DateTime Date { get; set; }

    public string Key { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    public ArrKind Source { get; set; }

    public long SizeOnDisk { get; set; }

    /// <summary>"Auto", "Manual" or "Dry run".</summary>
    public string Mode { get; set; } = string.Empty;

    public bool Success { get; set; }

    public string? Error { get; set; }
}

/// <summary>Result of a deletion operation.</summary>
public class DeleteResult
{
    public int Deleted { get; set; }

    public int Failed { get; set; }

    public long FreedBytes { get; set; }

    public bool DryRun { get; set; }

    public List<HistoryEntry> Entries { get; set; } = new();
}
