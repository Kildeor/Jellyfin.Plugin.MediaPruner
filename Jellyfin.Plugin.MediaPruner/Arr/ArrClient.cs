using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Net;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MediaPruner.Arr;

/// <summary>
/// Minimal HTTP client for the Radarr and Sonarr v3 APIs.
/// </summary>
public class ArrClient
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<ArrClient> _logger;

    public ArrClient(IHttpClientFactory httpClientFactory, ILogger<ArrClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public Task<ArrSystemStatus?> GetStatusAsync(string baseUrl, string apiKey, CancellationToken ct)
        => GetAsync<ArrSystemStatus>(baseUrl, apiKey, "api/v3/system/status", ct);

    public async Task<IReadOnlyList<RadarrMovie>> GetMoviesAsync(string baseUrl, string apiKey, CancellationToken ct)
        => await GetAsync<List<RadarrMovie>>(baseUrl, apiKey, "api/v3/movie", ct).ConfigureAwait(false) ?? new List<RadarrMovie>();

    public async Task<IReadOnlyList<SonarrSeries>> GetSeriesAsync(string baseUrl, string apiKey, CancellationToken ct)
        => await GetAsync<List<SonarrSeries>>(baseUrl, apiKey, "api/v3/series", ct).ConfigureAwait(false) ?? new List<SonarrSeries>();

    public async Task<IReadOnlyList<ArrFile>> GetEpisodeFilesAsync(string baseUrl, string apiKey, int seriesId, CancellationToken ct)
        => await GetAsync<List<ArrFile>>(
            baseUrl,
            apiKey,
            string.Format(CultureInfo.InvariantCulture, "api/v3/episodefile?seriesId={0}", seriesId),
            ct).ConfigureAwait(false) ?? new List<ArrFile>();

    public async Task<IReadOnlyList<ArrTag>> GetTagsAsync(string baseUrl, string apiKey, CancellationToken ct)
        => await GetAsync<List<ArrTag>>(baseUrl, apiKey, "api/v3/tag", ct).ConfigureAwait(false) ?? new List<ArrTag>();

    public Task DeleteMovieAsync(string baseUrl, string apiKey, int id, bool deleteFiles, bool addExclusion, CancellationToken ct)
        => DeleteAsync(
            baseUrl,
            apiKey,
            string.Format(CultureInfo.InvariantCulture, "api/v3/movie/{0}?deleteFiles={1}&addImportExclusion={2}", id, Bool(deleteFiles), Bool(addExclusion)),
            ct);

    public Task DeleteSeriesAsync(string baseUrl, string apiKey, int id, bool deleteFiles, bool addExclusion, CancellationToken ct)
        => DeleteAsync(
            baseUrl,
            apiKey,
            string.Format(CultureInfo.InvariantCulture, "api/v3/series/{0}?deleteFiles={1}&addImportListExclusion={2}", id, Bool(deleteFiles), Bool(addExclusion)),
            ct);

    private static string Bool(bool b) => b ? "true" : "false";

    private HttpRequestMessage BuildRequest(HttpMethod method, string baseUrl, string apiKey, string path)
    {
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            throw new InvalidOperationException("URL not configured.");
        }

        var uri = new Uri(new Uri(baseUrl.TrimEnd('/') + "/"), path);
        var request = new HttpRequestMessage(method, uri);
        request.Headers.Add("X-Api-Key", apiKey);
        return request;
    }

    private async Task<T?> GetAsync<T>(string baseUrl, string apiKey, string path, CancellationToken ct)
    {
        using var request = BuildRequest(HttpMethod.Get, baseUrl, apiKey, path);
        var client = _httpClientFactory.CreateClient(NamedClient.Default);
        using var response = await client.SendAsync(request, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<T>(ct).ConfigureAwait(false);
    }

    private async Task DeleteAsync(string baseUrl, string apiKey, string path, CancellationToken ct)
    {
        using var request = BuildRequest(HttpMethod.Delete, baseUrl, apiKey, path);
        var client = _httpClientFactory.CreateClient(NamedClient.Default);
        using var response = await client.SendAsync(request, ct).ConfigureAwait(false);
        _logger.LogDebug("DELETE {Path} -> {Status}", path, response.StatusCode);
        response.EnsureSuccessStatusCode();
    }
}
