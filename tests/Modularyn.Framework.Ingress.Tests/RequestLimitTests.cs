using System.Net;
using System.Text;
using Xunit;
using static Modularyn.Framework.Ingress.Tests.IngressServer;

namespace Modularyn.Framework.Ingress.Tests;

public sealed class RequestLimitTests
{
    [Fact(DisplayName = "TM-G02-009 bodies beyond the declared limit are refused before or during the first read")]
    public async Task TM_G02_009_Bodies_beyond_the_declared_limit_are_refused_before_or_during_the_first_read()
    {
        await using var server = await StartAsync(Settings(("Limits:MaxRequestBodyBytes", "1024"), ("Limits:MaxFormValueBytes", "1024")));
        var declared = await server.SendAsync(true, HttpMethod.Post, "/upload", request => request.Content = new ByteArrayContent(new byte[2048]));
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, declared.StatusCode);
        Assert.Equal(0, server.Handled);

        var chunked = await server.SendAsync(true, HttpMethod.Post, "/upload", request =>
        {
            request.Content = new StreamContent(new UnseekableStream(new byte[2048]));
            request.Headers.TransferEncodingChunked = true;
        });
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, chunked.StatusCode);

        var within = await server.SendAsync(true, HttpMethod.Post, "/upload", request => request.Content = new ByteArrayContent(new byte[1024]));
        Assert.Equal(HttpStatusCode.OK, within.StatusCode);
        Assert.Equal("1024", await within.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Fact(DisplayName = "TM-G02-009 oversized request lines, headers and header counts are refused by the server")]
    public async Task TM_G02_009_Oversized_request_lines_headers_and_header_counts_are_refused_by_the_server()
    {
        await using var server = await StartAsync(Settings(("Limits:MaxRequestHeadersTotalBytes", "4096"), ("Limits:MaxRequestHeaderCount", "20"),
            ("Limits:MaxRequestLineBytes", "1024")));
        var large = await server.SendAsync(true, HttpMethod.Get, "/", request => request.Headers.Add("X-Large", new string('a', 8192)));
        Assert.Equal(HttpStatusCode.RequestHeaderFieldsTooLarge, large.StatusCode);
        var many = await server.SendAsync(true, HttpMethod.Get, "/", request =>
        {
            for (var index = 0; index < 30; index++) request.Headers.Add($"X-Header-{index}", "v");
        });
        Assert.Equal(HttpStatusCode.RequestHeaderFieldsTooLarge, many.StatusCode);
        var line = await server.SendAsync(true, HttpMethod.Get, "/" + new string('a', 2048));
        Assert.Equal(HttpStatusCode.RequestUriTooLong, line.StatusCode);
        Assert.Equal(0, server.Handled);
        var ordinary = await server.SendAsync(true, HttpMethod.Get, "/");
        Assert.Equal(HttpStatusCode.OK, ordinary.StatusCode);
        Assert.False(ordinary.Headers.Contains("Server"));
    }

    [Fact(DisplayName = "TM-G02-009 JSON and form bodies beyond their declared structure are refused")]
    public async Task TM_G02_009_Json_and_form_bodies_beyond_their_declared_structure_are_refused()
    {
        await using var server = await StartAsync(Settings(("Limits:MaxJsonDepth", "8"), ("Limits:MaxFormValueCount", "4")));
        var shallow = await server.SendAsync(true, HttpMethod.Post, "/json", request => request.Content = Json(4));
        Assert.Equal(HttpStatusCode.OK, shallow.StatusCode);
        var deep = await server.SendAsync(true, HttpMethod.Post, "/json", request => request.Content = Json(20));
        Assert.Equal(HttpStatusCode.BadRequest, deep.StatusCode);

        var form = await server.SendAsync(true, HttpMethod.Post, "/change", request =>
        {
            request.Headers.Add("Cookie", "session=present");
            request.Content = new FormUrlEncodedContent(Enumerable.Range(0, 10).Select(index => KeyValuePair.Create($"field{index}", "v")));
        });
        Assert.Equal(HttpStatusCode.BadRequest, form.StatusCode);
        Assert.Equal(0, server.Changes);
    }

    private static StringContent Json(int depth) =>
        new(new string('[', depth) + new string(']', depth), Encoding.UTF8, "application/json");

    private sealed class UnseekableStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
    }
}
