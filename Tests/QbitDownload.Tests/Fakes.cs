using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace QbitDownload.Tests;

/// <summary>
/// HttpMessageHandler that answers requests from a supplied responder — lets tests exercise the
/// qBit-facing helpers (QbitAddMagnet / ResolveFile / ResolveDubFile) with canned responses, no network.
/// </summary>
public sealed class FakeHttpMessageHandler : HttpMessageHandler
{
    readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;

    /// <summary>Every request that passed through (assert on method/url/body).</summary>
    public List<HttpRequestMessage> Requests { get; } = new();

    public FakeHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) => _responder = responder;

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        Requests.Add(request);
        return Task.FromResult(_responder(request));
    }

    public static HttpClient Client(Func<HttpRequestMessage, HttpResponseMessage> responder, string baseAddress = "http://qbit.test")
        => new HttpClient(new FakeHttpMessageHandler(responder)) { BaseAddress = new Uri(baseAddress) };

    public static HttpResponseMessage Text(string body, HttpStatusCode code = HttpStatusCode.OK)
        => new HttpResponseMessage(code) { Content = new StringContent(body ?? "") };

    public static HttpResponseMessage Json(string json, HttpStatusCode code = HttpStatusCode.OK)
        => new HttpResponseMessage(code) { Content = new StringContent(json ?? "", Encoding.UTF8, "application/json") };
}

/// <summary>
/// Fluent router for a fake qBittorrent WebUI: map a substring of the request URL to a canned response.
/// First matching route wins; unmatched → 404 with "[]". Example:
/// <code>
/// var c = new FakeQbit()
///     .Json("/torrents/info", "[{\"save_path\":\"C:\\\\dl\",\"content_path\":\"C:\\\\dl\\\\a.mkv\"}]")
///     .Json("/torrents/files", "[{\"name\":\"a.mkv\",\"index\":0,\"size\":100}]")
///     .Build();
/// </code>
/// </summary>
public sealed class FakeQbit
{
    readonly List<(string needle, Func<HttpRequestMessage, HttpResponseMessage> resp)> _routes = new();

    public FakeQbit Route(string urlContains, Func<HttpRequestMessage, HttpResponseMessage> resp) { _routes.Add((urlContains, resp)); return this; }
    public FakeQbit Text(string urlContains, string body, HttpStatusCode code = HttpStatusCode.OK) { _routes.Add((urlContains, _ => FakeHttpMessageHandler.Text(body, code))); return this; }
    public FakeQbit Json(string urlContains, string json, HttpStatusCode code = HttpStatusCode.OK) { _routes.Add((urlContains, _ => FakeHttpMessageHandler.Json(json, code))); return this; }

    FakeHttpMessageHandler _handler;
    /// <summary>Requests captured by the built client (populated after Build()).</summary>
    public IReadOnlyList<HttpRequestMessage> Requests => _handler?.Requests ?? (IReadOnlyList<HttpRequestMessage>)Array.Empty<HttpRequestMessage>();

    public HttpClient Build(string baseAddress = "http://qbit.test")
    {
        _handler = new FakeHttpMessageHandler(req =>
        {
            string url = req.RequestUri?.ToString() ?? "";
            foreach (var (needle, resp) in _routes)
                if (url.Contains(needle, StringComparison.OrdinalIgnoreCase)) return resp(req);
            return new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("[]") };
        });
        return new HttpClient(_handler) { BaseAddress = new Uri(baseAddress) };
    }
}
