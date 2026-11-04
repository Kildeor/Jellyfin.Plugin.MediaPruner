using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Jellyfin.Plugin.MediaPruner.Models;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MediaPruner.Services;

/// <summary>
/// Stores the deletion history in a JSON file in the plugin data folder.
/// </summary>
public class HistoryStore
{
    private const int MaxEntries = 500;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly object _lock = new();
    private readonly ILogger<HistoryStore> _logger;

    public HistoryStore(ILogger<HistoryStore> logger)
    {
        _logger = logger;
    }

    private static string? FilePath
    {
        get
        {
            var folder = Plugin.Instance?.DataFolderPath;
            return folder is null ? null : Path.Combine(folder, "history.json");
        }
    }

    public IReadOnlyList<HistoryEntry> GetAll()
    {
        lock (_lock)
        {
            return Load();
        }
    }

    public void Add(IEnumerable<HistoryEntry> entries)
    {
        lock (_lock)
        {
            var list = Load();
            list.InsertRange(0, entries.OrderByDescending(e => e.Date));
            if (list.Count > MaxEntries)
            {
                list.RemoveRange(MaxEntries, list.Count - MaxEntries);
            }

            Save(list);
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            Save(new List<HistoryEntry>());
        }
    }

    private List<HistoryEntry> Load()
    {
        var path = FilePath;
        if (path is null || !File.Exists(path))
        {
            return new List<HistoryEntry>();
        }

        try
        {
            return JsonSerializer.Deserialize<List<HistoryEntry>>(File.ReadAllText(path)) ?? new List<HistoryEntry>();
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            _logger.LogWarning(ex, "Unable to read the Media Pruner history");
            return new List<HistoryEntry>();
        }
    }

    private void Save(List<HistoryEntry> list)
    {
        var path = FilePath;
        if (path is null)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(list, JsonOptions));
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "Unable to write the Media Pruner history");
        }
    }
}
