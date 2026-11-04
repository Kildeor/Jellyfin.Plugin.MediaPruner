using System;
using System.Collections.Generic;
using System.Net.Mime;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.MediaPruner.Arr;
using Jellyfin.Plugin.MediaPruner.Models;
using Jellyfin.Plugin.MediaPruner.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.MediaPruner.Api;

/// <summary>
/// API used by the plugin configuration page (administrators only).
/// </summary>
[ApiController]
[Route("MediaPruner")]
[Authorize(Policy = "RequiresElevation")]
[Produces(MediaTypeNames.Application.Json)]
public class MediaPrunerController : ControllerBase
{
    private readonly CleanupService _cleanup;
    private readonly HistoryStore _history;
    private readonly ArrClient _arr;

    public MediaPrunerController(CleanupService cleanup, HistoryStore history, ArrClient arr)
    {
        _cleanup = cleanup;
        _history = history;
        _arr = arr;
    }

    /// <summary>Lists items due for deletion soon (or all of them with <c>all=true</c>).</summary>
    [HttpGet("Candidates")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<CandidatesResult> GetCandidates([FromQuery] bool all = false, CancellationToken ct = default)
    {
        var result = await _cleanup.EvaluateAsync(all, ct).ConfigureAwait(false);
        if (!all)
        {
            // Keep the "pending deletion" Jellyfin tag and collection in sync with what the page shows.
            await _cleanup.SyncPendingMarkersAsync(result, ct).ConfigureAwait(false);
        }

        return result;
    }

    /// <summary>Deletes the selected items.</summary>
    [HttpPost("Delete")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public Task<DeleteResult> Delete([FromBody] DeleteRequest request, CancellationToken ct = default)
        => _cleanup.DeleteByKeysAsync(request.Keys ?? new List<string>(), ct);

    /// <summary>Immediately deletes all eligible items.</summary>
    [HttpPost("DeleteEligible")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public Task<DeleteResult> DeleteEligible(CancellationToken ct = default)
        => _cleanup.DeleteAllEligibleAsync(ct);

    /// <summary>Protects / unprotects an item.</summary>
    [HttpPost("Protect")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public ActionResult Protect([FromBody] ProtectRequest request)
    {
        if (CleanupService.ParseKey(request.Key) is null)
        {
            return BadRequest("Invalid key.");
        }

        _cleanup.SetProtected(request.Key, request.Protected);
        return NoContent();
    }

    /// <summary>Tests the connection to Radarr or Sonarr.</summary>
    [HttpPost("TestConnection")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<TestConnectionResult> TestConnection([FromBody] TestConnectionRequest request, CancellationToken ct = default)
    {
        try
        {
            var status = await _arr.GetStatusAsync(request.Url, request.ApiKey, ct).ConfigureAwait(false);
            return new TestConnectionResult
            {
                Success = true,
                Message = $"Connected to {status?.AppName ?? request.Kind.ToString()} v{status?.Version}"
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new TestConnectionResult { Success = false, Message = ex.Message };
        }
    }

    /// <summary>Deletion history.</summary>
    [HttpGet("History")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public IReadOnlyList<HistoryEntry> GetHistory() => _history.GetAll();

    /// <summary>Clears the history.</summary>
    [HttpDelete("History")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public ActionResult ClearHistory()
    {
        _history.Clear();
        return NoContent();
    }
}

public class DeleteRequest
{
    public List<string>? Keys { get; set; }
}

public class ProtectRequest
{
    public string Key { get; set; } = string.Empty;

    public bool Protected { get; set; }
}

public class TestConnectionRequest
{
    public ArrKind Kind { get; set; }

    public string Url { get; set; } = string.Empty;

    public string ApiKey { get; set; } = string.Empty;
}

public class TestConnectionResult
{
    public bool Success { get; set; }

    public string Message { get; set; } = string.Empty;
}
