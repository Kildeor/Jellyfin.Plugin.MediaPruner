using System.Net;
using System.Text;
using System.Text.Json;

namespace Jellyfin.Plugin.MediaPruner.Tests.Infrastructure;

/// <summary>
/// In-memory HTTP handler standing in for Radarr / Sonarr.
/// Routes are matched on "METHOD absolute-uri-without-query" or "METHOD absolute-uri-with-query".
/// </summary>
public sealed class FakeArrServer : HttpMessageHandler
{
    private readonly Dictionary<string, Func<HttpRequestMessage, HttpResponseMessage>> _routes = new(StringComparer.OrdinalIgnoreCase);

    public List<HttpRequestMessage> Requests { get; } = new();

    public FakeArrServer Json(string method, string url, object body)
        => On(method, url, _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
        });

    public FakeArrServer Status(string method, string url, HttpStatusCode status)
        => On(method, url, _ => new HttpResponseMessage(status));

    public FakeArrServer On(string method, string url, Func<HttpRequestMessage, HttpResponseMessage> handler)
    {
        _routes[method + " " + url] = handler;
        return this;
    }

    public IEnumerable<HttpRequestMessage> RequestsTo(string method, string urlPrefix)
        => Requests.Where(r => r.Method.Method == method && r.RequestUri!.AbsoluteUri.StartsWith(urlPrefix, StringComparison.OrdinalIgnoreCase));

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        lock (Requests)
        {
            Requests.Add(request);
        }

        var uri = request.RequestUri!;
        var withQuery = request.Method.Method + " " + uri.AbsoluteUri;
        var withoutQuery = request.Method.Method + " " + uri.GetLeftPart(UriPartial.Path);

        if (_routes.TryGetValue(withQuery, out var handler) || _routes.TryGetValue(withoutQuery, out handler))
        {
            return Task.FromResult(handler(request));
        }

        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
    }
}

/// <summary>Minimal <see cref="IHttpClientFactory"/> returning clients bound to a handler.</summary>
public sealed class FakeHttpClientFactory : IHttpClientFactory
{
    private readonly HttpMessageHandler _handler;

    public FakeHttpClientFactory(HttpMessageHandler handler) => _handler = handler;

    public HttpClient CreateClient(string name) => new(_handler, disposeHandler: false);
}
