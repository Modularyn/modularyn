using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Modularyn.Framework.Api;
using Modularyn.Framework.Api.Contracts;
using Xunit;

namespace Modularyn.Framework.Hosting.Tests;

// TM-G02-011: a response's media type is checked at run time against its endpoint's inventory declaration, so no handler can
// answer HTML, binary or any other content its endpoint did not declare by any supported write path. The handlers here
// deliberately send every kind of type through every supported way a handler writes, over a real Kestrel server. A handler
// that casts the request's IHttpResponseFeature to the server's own body feature writes past the check, which module trust
// covers (RR-G02-019).
public sealed class ResponseMediaTypeTests
{
    private const string Undeclared = "8400";
    private const string Problem = "application/problem+json";
    // How long an aborted response may take to end; the abort itself takes well under a second.
    private static readonly TimeSpan AbortBound = TimeSpan.FromSeconds(5);
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    private static readonly ApiInventory Inventory = new(1, 1, [],
    [
        new("Json", "GET", "/json", "Anonymous", null, false, null, null, Versioned: false, Authentication: "none"),
        new("Page", "GET", "/page", "Anonymous", null, false, null, null, Versioned: false, ResponseMediaType: ApiMediaTypes.Html,
            Authentication: "none", Page: true),
        new("Download", "GET", "/download", "Anonymous", null, false, null, null, Versioned: false, ResponseMediaType: ApiMediaTypes.Binary,
            Authentication: "none", Download: new("inline", ["image/png"])),
        new("Text", "GET", "/text", "Anonymous", null, false, null, null, Versioned: false, ResponseMediaType: ApiMediaTypes.PlainText,
            Authentication: "none")
    ]);

    [Fact(DisplayName = "TM-G02-011 a response leaves only with a media type its declaration allows; every other one is refused before any byte")]
    public async Task TM_G02_011_A_response_leaves_only_with_a_media_type_its_declaration_allows()
    {
        var marker = "marker-" + Guid.NewGuid().ToString("N");
        var file = Path.Combine(Path.GetTempPath(), "modularyn-media-" + Guid.NewGuid().ToString("N") + ".html");
        await File.WriteAllTextAsync(file, "<p>" + marker + "</p>", Cancellation);
        try
        {
            using var fixture = new LifecycleFixture();
            var events = new EventCodes();
            await using var application = Build(fixture, events);
            application.UseRouting();
            foreach (var contract in Inventory.Endpoints)
                application.MapGet(contract.Route, (HttpContext context) => SendAsync(context, marker, file)).AllowAnonymous().WithApiContract(contract, Inventory);
            await application.StartAsync(Cancellation);
            using var client = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false })
            {
                BaseAddress = new Uri(Assert.Single(application.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses))
            };

            // What each endpoint declares, and what its handler then sends: the declared type with or without parameters, the
            // API's problem document and an empty redirect pass; every other type, and bytes or a file with no type, are refused.
            var allowed = new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                ["/json"] = ["json", "json-charset", "problem", "redirect"],
                ["/page"] = ["html", "html-upper-charset", "problem", "redirect"],
                ["/download"] = ["png", "problem", "redirect"],
                ["/text"] = ["text", "problem", "redirect"]
            };
            // The pipe-writer, file, caught, empty and flushed variants send a type the endpoint does not declare: JSON for the page,
            // HTML for every other endpoint.
            // An explicit start of a response with no type waits for its first bytes, so those are still refused with a problem.
            string[] sends = ["json", "json-charset", "html", "html-upper-charset", "png", "text", "problem", "redirect", "untyped", "untyped-text",
                "started-untyped", "javascript", "pipe-other", "file-other", "swallow-other", "empty-other", "flush-other"];
            var refusals = 0;
            foreach (var (path, permitted) in allowed)
                foreach (var send in sends)
                {
                    using var response = await client.GetAsync(path + "?send=" + send, Cancellation);
                    var body = await response.Content.ReadAsStringAsync(Cancellation);
                    if (permitted.Contains(send, StringComparer.Ordinal))
                    {
                        Assert.True(response.StatusCode is HttpStatusCode.OK or HttpStatusCode.Redirect, path + " " + send);
                        if (send != "redirect") Assert.Contains(marker, body, StringComparison.Ordinal);
                        continue;
                    }
                    refusals++;
                    // Refused in place of everything the handler set: a 500 problem with MAP008, its own headers, and no byte of the handler's.
                    Assert.True(response.StatusCode == HttpStatusCode.InternalServerError, path + " " + send + ": " + response.StatusCode);
                    Assert.Equal(Problem, response.Content.Headers.ContentType?.MediaType);
                    Assert.Equal(ApiCodes.Protocol, JsonDocument.Parse(body).RootElement.GetProperty("code").GetString());
                    Assert.DoesNotContain(marker, body, StringComparison.Ordinal);
                    Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
                    Assert.False(response.Content.Headers.Contains("Content-Disposition"), path + " " + send);
                    Assert.True(response.Headers.Contains(FrameworkApi.CorrelationHeader));
                }
            Assert.Equal(allowed.Sum(entry => sends.Length - entry.Value.Length), refusals);
            Assert.Equal(refusals, events.Count(Undeclared));

            // Once a flush has sent an untyped response's headers, bytes that follow cannot be refused with a problem: the
            // connection is aborted and none of them arrives. Each request reads its headers and then its body within a bound of a
            // few seconds rather than the client's 100-second timeout, and a read that ends must end in the abort, with no marker
            // received. On Linux and Windows the abort resets the connection at once, so a read that stalls fails. On macOS the
            // abort does not always reach the client after the server has refused and aborted: no byte follows the headers and
            // nothing ends the read. There a stall with an empty body is accepted; a completed body, a marker or any byte after
            // the headers still fails on every platform.
            foreach (var path in allowed.Keys)
            {
                using var bound = CancellationTokenSource.CreateLinkedTokenSource(Cancellation);
                bound.CancelAfter(AbortBound);
                using var received = new MemoryStream();
                var aborted = await Record.ExceptionAsync(async () =>
                {
                    using var response = await client.GetAsync(path + "?send=flushed-untyped", HttpCompletionOption.ResponseHeadersRead, bound.Token);
                    await using var body = await response.Content.ReadAsStreamAsync(bound.Token);
                    await body.CopyToAsync(received, bound.Token);
                });
                var stalled = bound.IsCancellationRequested && !Cancellation.IsCancellationRequested;
                Assert.False(stalled && !OperatingSystem.IsMacOS(),
                    $"{path}: the aborted response neither ended nor failed within {AbortBound.TotalSeconds} seconds: {aborted?.GetType().Name}");
                Assert.True(stalled || aborted is HttpRequestException or IOException,
                    $"{path}: reading the body must end in the abort, but it ended with {aborted?.GetType().Name ?? "the complete body"}");
                Assert.DoesNotContain(marker, Encoding.UTF8.GetString(received.ToArray()), StringComparison.Ordinal);
                Assert.Equal(0, received.Length);
            }
            Assert.Equal(refusals + allowed.Count, events.Count(Undeclared));
            await application.StopAsync(Cancellation);
        }
        finally { File.Delete(file); }
    }

    private static async Task SendAsync(HttpContext context, string marker, string file)
    {
        var response = context.Response;
        var bytes = Encoding.UTF8.GetBytes(marker);
        var other = context.Request.Path == "/page" ? ApiMediaTypes.Json : ApiMediaTypes.Html;
        switch (context.Request.Query["send"].ToString())
        {
            case "json": await Results.Json(new { marker }).ExecuteAsync(context); break;
            case "json-charset": response.ContentType = "application/json; charset=utf-8"; await response.Body.WriteAsync(Encoding.UTF8.GetBytes("\"" + marker + "\"")); break;
            case "html": await Results.Content("<p>" + marker + "</p>", ApiMediaTypes.Html).ExecuteAsync(context); break;
            case "html-upper-charset": response.ContentType = "TEXT/HTML; charset=utf-8"; await response.WriteAsync("<p>" + marker + "</p>"); break;
            case "png": response.ContentType = "image/png"; await response.Body.WriteAsync(bytes); break;
            case "text": await Results.Text(marker, ApiMediaTypes.PlainText).ExecuteAsync(context); break;
            case "problem": response.ContentType = Problem; await response.WriteAsync("{\"code\":\"" + marker + "\"}"); break;
            case "redirect": await Results.Redirect("/elsewhere").ExecuteAsync(context); break;
            case "untyped": await response.Body.WriteAsync(bytes); break;
            case "untyped-text": await response.WriteAsync(marker); break;
            case "javascript": response.ContentType = "text/javascript"; await response.WriteAsync(marker); break;
            // Through the body's pipe writer rather than its stream.
            case "pipe-other": await response.WriteAsJsonAsync(marker, (JsonSerializerOptions?)null, contentType: other); break;
            case "file-other": response.ContentType = other; await response.SendFileAsync(file); break;
            // A handler that catches the refusal and returns normally is refused all the same.
            case "swallow-other":
                response.ContentType = other;
                try { await response.WriteAsync(marker); }
                catch (InvalidOperationException) { }
                break;
            // Starting an empty response with an undeclared type, or flushing one, is refused too.
            case "empty-other": response.ContentType = other; await response.StartAsync(); break;
            case "flush-other": response.ContentType = other; await response.Body.FlushAsync(); break;
            case "started-untyped":
                await response.StartAsync();
                await response.Body.WriteAsync(bytes);
                break;
            case "flushed-untyped":
                await response.Body.FlushAsync();
                await response.Body.WriteAsync(bytes);
                break;
            default: throw new InvalidOperationException("Unknown fixture send.");
        }
    }

    private static WebApplication Build(LifecycleFixture fixture, EventCodes events)
    {
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { EnvironmentName = "Production" });
        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(events);
        builder.WebHost.ConfigureKestrel(server => server.Listen(IPAddress.Loopback, 0));
        builder.Services.AddModularynModules(LifecycleFixture.Environment, fixture.Registrations(LifecycleFixture.Graph()), LifecycleFixture.Unverified);
        return builder.Build();
    }

    private sealed class EventCodes : ILoggerProvider
    {
        private readonly ConcurrentQueue<int> codes = new();

        internal int Count(string code) => codes.Count(entry => entry.ToString(System.Globalization.CultureInfo.InvariantCulture) == code);

        public ILogger CreateLogger(string categoryName) => new Recorder(codes);

        public void Dispose() { }

        private sealed class Recorder(ConcurrentQueue<int> codes) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                codes.Enqueue(eventId.Id);
        }
    }
}
