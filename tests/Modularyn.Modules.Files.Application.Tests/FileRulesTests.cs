using System.Text;
using System.Text.Json;
using Modularyn.Modules.Files.Application.Contracts;
using Modularyn.Modules.Files.Domain;
using Xunit;

namespace Modularyn.Modules.Files.Application.Tests;

/// <summary>The rules for client-supplied metadata, content detection, grant tokens, delivery and policy, without providers.</summary>
public sealed class FileRulesTests
{
    [Theory]
    [InlineData("notes.txt")]
    [InlineData("..hidden")]
    [InlineData("report.tar.gz")]
    [InlineData("#1 notes (draft) [v2].txt")]
    [InlineData("r\u00e9sum\u00e9.pdf")]
    [InlineData("\u65e5\u672c\u8a9e.txt")]
    [InlineData("emoji \ud83d\ude00.png")]
    public void TM_G02_043_Safe_names_are_accepted_in_their_normalized_form(string name) =>
        Assert.Equal(name.Normalize(NormalizationForm.FormC), FileMetadataRules.Name(name));

    [Fact]
    public void TM_G02_043_A_name_is_normalized_where_the_runtime_has_Unicode_data()
    {
        // Under invariant globalization .NET has no normalization data and keeps the name as given; either way the stored
        // form is the runtime's NFC of the input, and its checks apply to that form.
        var decomposed = "re\u0301sume\u0301.pdf";
        Assert.Equal(decomposed.Normalize(NormalizationForm.FormC), FileMetadataRules.Name(decomposed));
        Assert.Equal(new string('a', FileMetadataRules.MaxNameLength), FileMetadataRules.Name(new string('a', FileMetadataRules.MaxNameLength)));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" notes.txt")]
    [InlineData("notes.txt ")]
    [InlineData("notes.")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("../notes.txt")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("name:stream")]
    [InlineData("a<b")]
    [InlineData("a>b")]
    [InlineData("a\"b")]
    [InlineData("a|b")]
    [InlineData("a?b")]
    [InlineData("a*b")]
    [InlineData("CON")]
    [InlineData("con.txt")]
    [InlineData("Lpt1.log")]
    [InlineData("COM\u00b9")]
    [InlineData("NUL .txt")]
    [InlineData("photo\u202egpj.exe")]
    [InlineData("zero\u200bwidth")]
    [InlineData("bom\ufeffname")]
    [InlineData("line\u2028separator")]
    [InlineData("unassigned\u0378")]
    public void TM_G02_043_Unsafe_names_are_refused(string name) =>
        Assert.Equal(FileStorageError.InvalidName, Assert.Throws<FileStorageException>(() => FileMetadataRules.Name(name)).Code);

    [Fact]
    public void TM_G02_043_Names_with_control_characters_or_a_lone_surrogate_are_refused()
    {
        foreach (var name in new[] { "tab\tname", "line\nname", "nul\u0000name", "esc\u001bname", "del\u007fname", "c1\u0085name", "lone\ud800surrogate",
                     "trail\udc00", "\ud83dpair" })
            Assert.Equal(FileStorageError.InvalidName, Assert.Throws<FileStorageException>(() => FileMetadataRules.Name(name)).Code);
    }

    [Fact]
    public void TM_G02_043_Names_containers_and_owner_references_are_length_bounded()
    {
        foreach (var refuse in new Action[]
        {
            () => FileMetadataRules.Name(null),
            () => FileMetadataRules.Name(new string('a', FileMetadataRules.MaxNameLength + 1)),
            () => FileMetadataRules.Container(new string('a', 64) + "/" + new string('b', 64) + "/" + new string('c', 64) + "/" + new string('d', 7)),
            () => FileMetadataRules.Container(new string('a', FileMetadataRules.MaxContainerSegmentLength + 1)),
            () => FileMetadataRules.Owner(new string('a', FileMetadataRules.MaxOwnerLength + 1))
        })
            Assert.Equal(FileStorageError.InvalidName, Assert.Throws<FileStorageException>(refuse).Code);
        Assert.Equal(200, FileMetadataRules.Container(new string('a', 64) + "/" + new string('b', 64) + "/" + new string('c', 64) + "/" + new string('d', 5)).Length);
        Assert.Equal(200, FileMetadataRules.Owner(new string('o', 200)).Length);
    }

    [Theory]
    [InlineData("documents", true)]
    [InlineData("projects/p-42/attachments", true)]
    [InlineData("a.b_c-d", true)]
    [InlineData("", false)]
    [InlineData("Documents", false)]
    [InlineData("/documents", false)]
    [InlineData("documents/", false)]
    [InlineData("a//b", false)]
    [InlineData("../documents", false)]
    [InlineData("a/../b", false)]
    [InlineData("a/./b", false)]
    [InlineData(".hidden", false)]
    [InlineData("trailing.", false)]
    [InlineData("a..b", false)]
    [InlineData("a b", false)]
    [InlineData("a\\b", false)]
    [InlineData("caf\u00e9", false)]
    public void TM_G02_043_A_container_has_one_canonical_form(string container, bool accepted)
    {
        if (accepted) Assert.Equal(container, FileMetadataRules.Container(container));
        else Assert.Equal(FileStorageError.InvalidName, Assert.Throws<FileStorageException>(() => FileMetadataRules.Container(container)).Code);
    }

    [Theory]
    [InlineData("project-1", true)]
    [InlineData("../../untouched", true)]
    [InlineData("urn:record:42", true)]
    [InlineData("", false)]
    [InlineData("two words", false)]
    [InlineData(" leading", false)]
    [InlineData("caf\u00e9", false)]
    [InlineData("tab\tbed", false)]
    public void TM_G02_043_An_owner_reference_is_printable_ASCII_and_carries_no_authority(string owner, bool accepted)
    {
        if (accepted) Assert.Equal(owner, FileMetadataRules.Owner(owner));
        else Assert.Equal(FileStorageError.InvalidName, Assert.Throws<FileStorageException>(() => FileMetadataRules.Owner(owner)).Code);
    }

    public static TheoryData<string, byte[]> Detected => new()
    {
        { "image/png", [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x01] },
        { "image/jpeg", [0xFF, 0xD8, 0xFF, 0xE0, 0x00] },
        { "image/gif", "GIF89a\u0001\u0000"u8.ToArray() },
        { "image/gif", "GIF87a"u8.ToArray() },
        { "image/webp", "RIFF\u0000\u0000\u0000\u0000WEBPVP8 "u8.ToArray() },
        { "image/bmp", "BM\u0000\u0001"u8.ToArray() },
        { "image/x-icon", [0x00, 0x00, 0x01, 0x00, 0x01] },
        { "application/pdf", "%PDF-1.7\n"u8.ToArray() },
        { "application/postscript", "%!PS-Adobe-3.0"u8.ToArray() },
        { "text/html", "<!DOCTYPE html><p>"u8.ToArray() },
        { "text/html", " \r\n\t<HtMl>"u8.ToArray() },
        { "text/html", "<script src=x>"u8.ToArray() },
        { "text/html", "<a href=x>"u8.ToArray() },
        { "text/html", "<!-- comment"u8.ToArray() },
        { "image/svg+xml", "<svg xmlns=\"http://www.w3.org/2000/svg\">"u8.ToArray() },
        { "text/xml", "<?xml version=\"1.0\"?>"u8.ToArray() },
        { "application/zip", [0x50, 0x4B, 0x03, 0x04, 0x14] },
        { "application/gzip", [0x1F, 0x8B, 0x08, 0x00] },
        { "application/vnd.rar", "Rar!\u001a\u0007\u0000"u8.ToArray() },
        { "application/vnd.microsoft.portable-executable", "MZ\u0090\u0000"u8.ToArray() },
        { "application/x-executable", [0x7F, 0x45, 0x4C, 0x46, 0x02] },
        { "application/x-mach-binary", [0xCF, 0xFA, 0xED, 0xFE, 0x07] },
        { "text/plain", "plain words\nand lines\r\n"u8.ToArray() },
        { "text/plain", "<abbr> is not a tag that sniffs as HTML"u8.ToArray() },
        { "text/plain", [0xEF, 0xBB, 0xBF, (byte)'<', (byte)'h', (byte)'t', (byte)'m', (byte)'l', (byte)'>'] },
        { "text/plain", [0xFF, 0xFE, (byte)'a', 0x00] },
        { "text/plain", [] },
        { "application/octet-stream", [0x00, 0x01, 0x02, 0x03] },
        { "application/octet-stream", "text then a \u0001 control"u8.ToArray() }
    };

    [Theory]
    [MemberData(nameof(Detected))]
    public void TM_G02_042_The_type_follows_the_bytes_alone(string expected, byte[] head)
    {
        Assert.Equal(expected, FileContentTypes.Detect(head));
        Assert.Contains(expected, FileContentTypes.Detectable);
    }

    [Fact]
    public void TM_G02_042_Policies_allow_only_detectable_types_and_approve_only_raster_images_inline()
    {
        var policy = new FilesPolicy();
        Assert.Equal(FilesPolicy.DefaultAllowedContentTypes, policy.AllowedContentTypes);
        Assert.DoesNotContain(FileContentTypes.Html, policy.AllowedContentTypes);
        Assert.DoesNotContain(FileContentTypes.Svg, policy.AllowedContentTypes);
        Assert.DoesNotContain(FileContentTypes.Binary, policy.AllowedContentTypes);
        Assert.Equal(new[] { "image/gif", "image/jpeg", "image/png", "image/webp" }, policy.InlineContentTypes.Order());
        Assert.Empty(new FilesPolicy(allowedContentTypes: [FileContentTypes.PlainText]).InlineContentTypes);
        foreach (var refuse in new Action[]
        {
            () => _ = new FilesPolicy(allowedContentTypes: []),
            () => _ = new FilesPolicy(allowedContentTypes: ["image/png", "text/html; charset=utf-8"]),
            () => _ = new FilesPolicy(allowedContentTypes: [FileContentTypes.PlainText], inlineContentTypes: [FileContentTypes.Png]),
            () => _ = new FilesPolicy(allowedContentTypes: [FileContentTypes.Svg], inlineContentTypes: [FileContentTypes.Svg]),
            () => _ = new FilesPolicy(inlineContentTypes: [FileContentTypes.PlainText])
        })
            Assert.Throws<ArgumentException>(refuse);
        foreach (var refuse in new Action[]
        {
            () => _ = new FilesPolicy(maximumPendingUploads: 0),
            () => _ = new FilesPolicy(maximumPendingBytes: -1),
            () => _ = new FilesPolicy(uploadTimeout: TimeSpan.Zero),
            () => _ = new FilesPolicy(uploadTimeout: TimeSpan.FromDays(2)),
            () => _ = new FilesPolicy(maximumGrantLifetime: TimeSpan.FromDays(8)),
            () => _ = new FilesPolicy(leaseWait: TimeSpan.FromMinutes(2))
        })
            Assert.Throws<ArgumentOutOfRangeException>(refuse);
    }

    [Fact]
    public void TM_G02_044_A_grant_token_has_one_canonical_form_and_never_shows_its_secret()
    {
        var grantId = Guid.NewGuid();
        var token = FileGrantToken.Create(grantId);
        var text = token.Reveal();
        Assert.Equal(FileGrantToken.EncodedLength, text.Length);
        Assert.StartsWith(FileGrantToken.Prefix + grantId.ToString("N"), text, StringComparison.Ordinal);
        Assert.True(FileGrantToken.TryParse(text, out var parsed));
        Assert.Equal(grantId, parsed.GrantId);
        Assert.Equal(token.ComputeVerifier(grantId), parsed.ComputeVerifier(grantId));
        Assert.NotEqual(token.ComputeVerifier(Guid.NewGuid()), token.ComputeVerifier(Guid.NewGuid()));
        Assert.NotEqual(token.ComputeVerifier(grantId), FileGrantToken.Create(grantId).ComputeVerifier(grantId));
        Assert.Equal(32, token.ComputeVerifier(grantId).Length);
        Assert.NotEqual(text, FileGrantToken.Create(grantId).Reveal());
        // Formatting, debugging and JSON see only the redacted form, and a token is never deserialized.
        Assert.Equal(FileGrantToken.Redacted, token.ToString());
        Assert.Equal(FileGrantToken.Redacted, $"{token}");
        Assert.Equal("{\"Token\":\"[file-grant]\"}", JsonSerializer.Serialize(new { Token = token }));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<FileGrantToken>("\"" + text + "\""));

        var secret = text[(FileGrantToken.Prefix.Length + 32)..];
        var last = secret[^1];
        foreach (var malformed in new[]
        {
            null, "", text + "A", text[..^1], "mfg2_" + text[5..], text.ToUpperInvariant(),
            FileGrantToken.Prefix + grantId.ToString("N").ToUpperInvariant() + secret,
            FileGrantToken.Prefix + Guid.Empty.ToString("N") + secret,
            text[..^1] + "=", text[..^1] + "+", text[..^1] + "/",
            // The last character of 32 bytes in base64url carries 2 spare bits; a variant in them is not the canonical text.
            text[..^1] + (char)(last ^ 1)
        })
            Assert.False(FileGrantToken.TryParse(malformed, out _), malformed);
        Assert.Throws<ArgumentException>(() => FileGrantToken.Create(Guid.Empty));
    }

    [Fact]
    public void TM_G02_044_A_delivery_disables_sniffing_and_names_its_file_safely()
    {
        var delivery = new FileDelivery("text/plain", FileDisposition.Attachment, "report 100% \u00e9t\u00e9 #1.txt");
        var headers = delivery.Headers().ToDictionary(header => header.Key, header => header.Value);
        Assert.Equal("text/plain", headers["Content-Type"]);
        Assert.Equal("nosniff", headers["X-Content-Type-Options"]);
        Assert.Equal("attachment; filename=\"report 100_ _t_ #1.txt\"; filename*=UTF-8''report%20100%25%20%C3%A9t%C3%A9%20#1.txt",
            headers["Content-Disposition"]);
        Assert.Equal("default-src 'none'; sandbox", headers["Content-Security-Policy"]);
        Assert.Equal("same-origin", headers["Cross-Origin-Resource-Policy"]);
        Assert.Equal("private, no-store", headers["Cache-Control"]);
        Assert.StartsWith("inline;", (delivery with { Disposition = FileDisposition.Inline }).Headers().Single(header =>
            header.Key == "Content-Disposition").Value, StringComparison.Ordinal);
    }
}
