using System.Net;
using Jellyfin.Plugin.MediaPruner.Arr;
using Jellyfin.Plugin.MediaPruner.Tests.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jellyfin.Plugin.MediaPruner.Tests;

public class ArrClientTests
{
    private readonly FakeArrServer _server = new();
    private readonly ArrClient _client;

    public ArrClientTests()
    {
        _client = new ArrClient(new FakeHttpClientFactory(_server), NullLogger<ArrClient>.Instance);
    }

    [Fact]
    public async Task GetStatus_SendsApiKeyHeader_AndParsesBody()
    {
        _server.Json("GET", "http://radarr/api/v3/system/status", new { appName = "Radarr", version = "5.0" });

        var status = await _client.GetStatusAsync("http://radarr", "secret", CancellationToken.None);

        Assert.Equal("Radarr", status!.AppName);
        Assert.Equal("5.0", status.Version);
        var request = Assert.Single(_server.Requests);
        Assert.Equal("secret", Assert.Single(request.Headers.GetValues("X-Api-Key")));
    }

    [Theory]
    [InlineData("http://host/radarr")]
    [InlineData("http://host/radarr/")]
    public async Task BaseUrl_WithSubPath_IsPreserved(string baseUrl)
    {
        _server.Json("GET", "http://host/radarr/api/v3/movie", new[] { new { id = 1, title = "A" } });

        var movies = await _client.GetMoviesAsync(baseUrl, "k", CancellationToken.None);

        Assert.Equal("A", Assert.Single(movies).Title);
    }

    [Fact]
    public async Task GetMovies_DeserializesFields()
    {
        _server.On("GET", "http://radarr/api/v3/movie", _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""
                [{"id":7,"title":"Dune","year":2021,"added":"2024-01-02T03:04:05Z","tmdbId":438631,"imdbId":"tt1160419",
                  "tags":[1,2],"sizeOnDisk":123,"hasFile":true,"images":[{"coverType":"poster","remoteUrl":"http://p"}],
                  "movieFile":{"id":9,"dateAdded":"2024-02-03T00:00:00Z"}}]
                """)
        });

        var m = Assert.Single(await _client.GetMoviesAsync("http://radarr", "k", CancellationToken.None));

        Assert.Equal(7, m.Id);
        Assert.Equal(438631, m.TmdbId);
        Assert.Equal("tt1160419", m.ImdbId);
        Assert.Equal(new[] { 1, 2 }, m.Tags);
        Assert.True(m.HasFile);
        Assert.Equal(new DateTime(2024, 2, 3), m.MovieFile!.DateAdded!.Value.ToUniversalTime().Date);
    }

    [Fact]
    public async Task NullBody_ReturnsEmptyList()
    {
        _server.On("GET", "http://sonarr/api/v3/series", _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("null") });

        Assert.Empty(await _client.GetSeriesAsync("http://sonarr", "k", CancellationToken.None));
    }

    [Fact]
    public async Task GetEpisodeFiles_PassesSeriesId()
    {
        _server.Json("GET", "http://sonarr/api/v3/episodefile?seriesId=5", new[] { new { id = 1, dateAdded = "2024-01-01T00:00:00Z" } });

        var files = await _client.GetEpisodeFilesAsync("http://sonarr", "k", 5, CancellationToken.None);

        Assert.Single(files);
    }

    [Theory]
    [InlineData(true, true, "deleteFiles=true&addImportExclusion=true")]
    [InlineData(false, false, "deleteFiles=false&addImportExclusion=false")]
    public async Task DeleteMovie_BuildsQuery(bool deleteFiles, bool exclusion, string query)
    {
        _server.Status("DELETE", "http://radarr/api/v3/movie/12", HttpStatusCode.OK);

        await _client.DeleteMovieAsync("http://radarr", "k", 12, deleteFiles, exclusion, CancellationToken.None);

        Assert.Equal("?" + query, Assert.Single(_server.Requests).RequestUri!.Query);
    }

    [Fact]
    public async Task DeleteSeries_UsesImportListExclusionParameter()
    {
        _server.Status("DELETE", "http://sonarr/api/v3/series/4", HttpStatusCode.OK);

        await _client.DeleteSeriesAsync("http://sonarr", "k", 4, true, true, CancellationToken.None);

        Assert.Equal("?deleteFiles=true&addImportListExclusion=true", Assert.Single(_server.Requests).RequestUri!.Query);
    }

    [Fact]
    public async Task ErrorStatus_Throws()
    {
        _server.Status("GET", "http://radarr/api/v3/tag", HttpStatusCode.Unauthorized);

        await Assert.ThrowsAsync<HttpRequestException>(() => _client.GetTagsAsync("http://radarr", "bad", CancellationToken.None));
    }

    [Fact]
    public async Task FailedDelete_Throws()
    {
        _server.Status("DELETE", "http://radarr/api/v3/movie/1", HttpStatusCode.InternalServerError);

        await Assert.ThrowsAsync<HttpRequestException>(() => _client.DeleteMovieAsync("http://radarr", "k", 1, true, false, CancellationToken.None));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task MissingUrl_ThrowsInvalidOperation(string url)
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => _client.GetMoviesAsync(url, "k", CancellationToken.None));
        Assert.Empty(_server.Requests);
    }
}
