using System.Net;
using System.Text;
using Xunit;
using static Modularyn.Framework.Ingress.Tests.IngressServer;

namespace Modularyn.Framework.Ingress.Tests;

public sealed class RequestLimitTests
{
    // Oversized requests use RawHttpsAsync: the server refuses and closes while the client could still be sending, so a
    // client that keeps writing can be reset before it reads the refusal. Each raw request is sent whole, then read.
    [Fact(DisplayName = "TM-G02-009 bodies beyond the declared limit are refused before the handler, or at the limit while it reads")]
    public async Task TM_G02_009_Bodies_beyond_the_declared_limit_are_refused_before_the_handler_or_at_the_limit_while_it_reads()
    {
        await using var server = await StartAsync(Settings(("Limits:MaxRequestBodyBytes", "1024"), ("Limits:MaxFormValueBytes", "1024")));
        var declared = await server.RawHttpsAsync(Head("POST /upload", "Content-Length: 2048"), new byte[2048]);
        Assert.Equal(413, declared.Status);
        Assert.Equal(0, server.Handled);
        Assert.Empty(server.UploadRefusals);

        // A chunked body has no declared length, so the handler starts; its read is refused once the body passes the limit.
        var chunk = Encoding.ASCII.GetBytes("800\r\n").Concat(new byte[2048]).Concat("\r\n0\r\n\r\n"u8.ToArray()).ToArray();
        var chunked = await server.RawHttpsAsync(Head("POST /upload", "Transfer-Encoding: chunked"), chunk);
        Assert.Equal(413, chunked.Status);
        Assert.Equal(1, server.Handled);
        Assert.Equal(0, server.UploadsCompleted);
        var refusal = Assert.Single(server.UploadRefusals);
        Assert.Equal(413, refusal.Status);
        Assert.InRange(refusal.Received, 0, 1024);

        var within = await server.SendAsync(true, HttpMethod.Post, "/upload", request => request.Content = new ByteArrayContent(new byte[1024]));
        Assert.Equal(HttpStatusCode.OK, within.StatusCode);
        Assert.Equal("1024", await within.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal(1, server.UploadsCompleted);
    }

    [Fact(DisplayName = "TM-G02-009 oversized request lines, headers and header counts are refused by the server")]
    public async Task TM_G02_009_Oversized_request_lines_headers_and_header_counts_are_refused_by_the_server()
    {
        await using var server = await StartAsync(Settings(("Limits:MaxRequestHeadersTotalBytes", "4096"), ("Limits:MaxRequestHeaderCount", "20"),
            ("Limits:MaxRequestLineBytes", "1024")));
        Assert.Equal(431, (await server.RawHttpsAsync(Head("GET /", "X-Large: " + new string('a', 8192)))).Status);
        Assert.Equal(431, (await server.RawHttpsAsync(Head("GET /", Enumerable.Range(0, 30).Select(index => $"X-Header-{index}: v").ToArray()))).Status);
        Assert.Equal(414, (await server.RawHttpsAsync(Head("GET /" + new string('a', 2048)))).Status);
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

    internal static string Head(string requestLine, params string[] headers) =>
        requestLine + " HTTP/1.1\r\nHost: " + PublicHost + "\r\n" + string.Concat(headers.Select(header => header + "\r\n")) + "Connection: close\r\n\r\n";

    private static StringContent Json(int depth) =>
        new(new string('[', depth) + new string(']', depth), Encoding.UTF8, "application/json");
}
