using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Xunit;
using static Modularyn.Framework.Ingress.Tests.IngressServer;

namespace Modularyn.Framework.Ingress.Tests;

// Response media types are declared in the API inventory but not yet checked against what a handler sends, so the ingress
// disables content sniffing on every response: a type a handler gets wrong is never sniffed into HTML.
public sealed class ContentTypeOptionsTests
{
    [Fact(DisplayName = "TM-G02-011 every response the pipeline produces disables content sniffing, whatever a handler sets")]
    public async Task TM_G02_011_Every_response_the_pipeline_produces_disables_content_sniffing_whatever_a_handler_sets()
    {
        await using var server = await StartAsync(IngressServer.Settings(("Limits:MaxRequestBodyBytes", "16"), ("Limits:MaxFormValueBytes", "16")),
            endpoints: application =>
            {
                // A handler that labels markup as plain text, and one that tries to allow sniffing or to remove the header.
                application.MapGet("/mislabelled", () => Results.Text("<html><body><script>alert(1)</script></body></html>", "text/plain"));
                application.MapGet("/sniffable", (HttpContext context) =>
                {
                    context.Response.Headers.XContentTypeOptions = "allow";
                    context.Response.OnStarting(() =>
                    {
                        context.Response.Headers.Remove("X-Content-Type-Options");
                        return Task.CompletedTask;
                    });
                    return "content";
                });
            });
        var responses = new[]
        {
            await server.SendAsync(true, HttpMethod.Get, "/mislabelled"),
            await server.SendAsync(true, HttpMethod.Get, "/sniffable"),
            await server.SendAsync(true, HttpMethod.Get, "/missing"),
            await server.SendAsync(true, HttpMethod.Post, "/change"),
            await server.SendAsync(false, HttpMethod.Get, "/"),
            await server.SendAsync(true, HttpMethod.Options, "/", request =>
            {
                request.Headers.Add("Origin", "https://attacker.example.net");
                request.Headers.Add("Access-Control-Request-Method", "POST");
            })
        };
        Assert.Equal(new[]
        {
            HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.NotFound, HttpStatusCode.BadRequest, HttpStatusCode.PermanentRedirect,
            HttpStatusCode.Forbidden
        }, responses.Select(response => response.StatusCode));
        Assert.All(responses, response => Assert.Equal("nosniff", Assert.Single(response.Headers.GetValues("X-Content-Type-Options"))));
        // The ingress's own refusal of an oversized body carries it too; see RequestLimitTests for why the body is sent whole.
        var oversized = await server.RawHttpsAsync(RequestLimitTests.Head("POST /upload", "Content-Length: 64"), new byte[64]);
        Assert.Equal(413, oversized.Status);
        Assert.Equal("nosniff", oversized.Headers["X-Content-Type-Options"]);
        // The host's own host filtering answers an unconfigured host before the pipeline runs, as it does without HSTS; its
        // refusal has no body to sniff.
        var unconfigured = await server.SendAsync(true, HttpMethod.Get, "/", host: "unlisted.example.test");
        Assert.Equal(HttpStatusCode.BadRequest, unconfigured.StatusCode);
        Assert.Empty(await unconfigured.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken));
    }
}
