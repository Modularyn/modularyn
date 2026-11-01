namespace Modularyn.Modules.Files.Application;

/// <summary>
/// Decides a version's type from its first bytes, never from its name or a client's claim. It follows the WHATWG MIME
/// Sniffing rules for a resource of unknown type with scriptable content recognized: HTML and XML after leading
/// whitespace, then PDF and PostScript, byte-order marks, images, archives and executables, and finally text or binary.
/// Every value it can return is in <see cref="Detectable"/>; a deployment allows a subset.
/// </summary>
public static class FileContentTypes
{
    /// <summary>The resource header length the WHATWG rules examine.</summary>
    public const int SniffLength = 1445;

    public const string Png = "image/png";
    public const string Jpeg = "image/jpeg";
    public const string Gif = "image/gif";
    public const string Webp = "image/webp";
    public const string Bmp = "image/bmp";
    public const string Icon = "image/x-icon";
    public const string Pdf = "application/pdf";
    public const string PlainText = "text/plain";
    public const string Html = "text/html";
    public const string Xml = "text/xml";
    public const string Svg = "image/svg+xml";
    public const string PostScript = "application/postscript";
    public const string Zip = "application/zip";
    public const string Gzip = "application/gzip";
    public const string Rar = "application/vnd.rar";
    public const string WindowsExecutable = "application/vnd.microsoft.portable-executable";
    public const string Executable = "application/x-executable";
    public const string MachExecutable = "application/x-mach-binary";
    public const string Binary = "application/octet-stream";

    public static IReadOnlySet<string> Detectable { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        Png, Jpeg, Gif, Webp, Bmp, Icon, Pdf, PlainText, Html, Xml, Svg, PostScript, Zip, Gzip, Rar, WindowsExecutable, Executable, MachExecutable, Binary
    };

    /// <summary>The raster image types a deployment may approve for inline display; every other type is delivered as an attachment.</summary>
    public static IReadOnlySet<string> InlineCapable { get; } = new HashSet<string>(StringComparer.Ordinal) { Png, Jpeg, Gif, Webp, Bmp, Icon };

    private static readonly string[] HtmlTags =
        ["<!DOCTYPE HTML", "<HTML", "<HEAD", "<SCRIPT", "<IFRAME", "<H1", "<DIV", "<FONT", "<TABLE", "<A", "<STYLE", "<TITLE", "<B", "<BODY", "<BR", "<P"];

    public static string Detect(ReadOnlySpan<byte> head)
    {
        var markup = head;
        while (markup.Length > 0 && markup[0] is 0x09 or 0x0A or 0x0C or 0x0D or 0x20) markup = markup[1..];
        foreach (var tag in HtmlTags)
            if (StartsWithIgnoringCase(markup, tag) && markup.Length > tag.Length && markup[tag.Length] is 0x20 or 0x3E) return Html;
        if (StartsWithIgnoringCase(markup, "<!--")) return Html;
        if (StartsWithIgnoringCase(markup, "<SVG") && (markup.Length == 4 || markup[4] is 0x20 or 0x3E or 0x09 or 0x0A or 0x0D or 0x2F)) return Svg;
        if (markup.StartsWith("<?xml"u8)) return Xml;
        if (head.StartsWith("%PDF-"u8)) return Pdf;
        if (head.StartsWith("%!PS-Adobe-"u8)) return PostScript;
        if (Starts(head, 0xFE, 0xFF) || Starts(head, 0xFF, 0xFE) || Starts(head, 0xEF, 0xBB, 0xBF)) return PlainText;
        if (head.StartsWith("GIF87a"u8) || head.StartsWith("GIF89a"u8)) return Gif;
        if (Starts(head, 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A)) return Png;
        if (Starts(head, 0xFF, 0xD8, 0xFF)) return Jpeg;
        if (head.Length >= 14 && head.StartsWith("RIFF"u8) && head[8..].StartsWith("WEBPVP"u8)) return Webp;
        if (head.StartsWith("BM"u8)) return Bmp;
        if (Starts(head, 0x00, 0x00, 0x01, 0x00) || Starts(head, 0x00, 0x00, 0x02, 0x00)) return Icon;
        if (Starts(head, 0x1F, 0x8B, 0x08)) return Gzip;
        if (Starts(head, 0x50, 0x4B, 0x03, 0x04)) return Zip;
        if (Starts(head, 0x52, 0x61, 0x72, 0x21, 0x1A, 0x07)) return Rar;
        if (head.StartsWith("MZ"u8)) return WindowsExecutable;
        if (Starts(head, 0x7F, 0x45, 0x4C, 0x46)) return Executable;
        if (Starts(head, 0xFE, 0xED, 0xFA, 0xCE) || Starts(head, 0xFE, 0xED, 0xFA, 0xCF)
            || Starts(head, 0xCE, 0xFA, 0xED, 0xFE) || Starts(head, 0xCF, 0xFA, 0xED, 0xFE)) return MachExecutable;
        foreach (var value in head)
            if (value is <= 0x08 or 0x0B or (>= 0x0E and <= 0x1A) or (>= 0x1C and <= 0x1F)) return Binary;
        return PlainText;
    }

    private static bool Starts(ReadOnlySpan<byte> head, params ReadOnlySpan<byte> prefix) => head.StartsWith(prefix);

    private static bool StartsWithIgnoringCase(ReadOnlySpan<byte> value, string pattern)
    {
        if (value.Length < pattern.Length) return false;
        for (var index = 0; index < pattern.Length; index++)
        {
            var actual = value[index];
            if (actual is >= (byte)'a' and <= (byte)'z') actual -= 0x20;
            if (actual != (byte)pattern[index]) return false;
        }
        return true;
    }
}
