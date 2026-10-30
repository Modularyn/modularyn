using System.Globalization;
using System.Text;

namespace Modularyn.Modules.Files.Domain;

/// <summary>
/// Bounds and canonical forms for the client-supplied metadata of a file. A display name is normalized to Unicode NFC and
/// then checked; containers and owner references have one canonical ASCII form. Anything else is refused with
/// <see cref="FileStorageError.InvalidName"/>, never repaired, and none of these values ever reaches a storage path.
/// </summary>
public static class FileMetadataRules
{
    public const int MaxNameLength = 255;
    public const int MaxContainerLength = 200;
    public const int MaxContainerSegmentLength = 64;
    public const int MaxOwnerLength = 200;
    public const int MaxContentTypeLength = 100;

    // Reserved on Windows and refused in names everywhere, since a name becomes a download's file name.
    private const string ReservedCharacters = "/\\<>:\"|?*";
    private static readonly string[] DeviceNames =
    [
        "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$",
        "COM0", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "COM¹", "COM²", "COM³",
        "LPT0", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9", "LPT¹", "LPT²", "LPT³"
    ];

    /// <summary>
    /// The NFC form of a display name of 1 to 255 UTF-16 code units, with no control, format, separator, unassigned or
    /// unpaired surrogate character, none of <c>/ \ &lt; &gt; : " | ? *</c>, no surrounding whitespace, no trailing dot and
    /// no device name such as CON. The runtime normalizes with its Unicode data; under invariant globalization it has
    /// none, and the name is checked as given.
    /// </summary>
    public static string Name(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > MaxNameLength) throw Invalid();
        string normalized;
        try { normalized = value.Normalize(NormalizationForm.FormC); }
        catch (ArgumentException) { throw Invalid(); }
        if (normalized.Length is 0 or > MaxNameLength) throw Invalid();
        var index = 0;
        Rune first = default, last = default;
        while (index < normalized.Length)
        {
            if (Rune.DecodeFromUtf16(normalized.AsSpan(index), out var rune, out var consumed) != System.Buffers.OperationStatus.Done) throw Invalid();
            if (index == 0) first = rune;
            last = rune;
            if (Unsafe(rune)) throw Invalid();
            index += consumed;
        }
        if (Rune.IsWhiteSpace(first) || Rune.IsWhiteSpace(last) || last.Value == '.') throw Invalid();
        var stem = normalized.Split('.')[0].TrimEnd();
        if (DeviceNames.Any(device => string.Equals(device, stem, StringComparison.OrdinalIgnoreCase))) throw Invalid();
        return normalized;
    }

    /// <summary>
    /// A logical container: up to 200 characters in <c>/</c>-separated segments of at most 64 lowercase ASCII letters,
    /// digits, <c>_</c> and <c>-</c>, with inner single dots, such as <c>projects/p-42/attachments</c>.
    /// </summary>
    public static string Container(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > MaxContainerLength) throw Invalid();
        foreach (var segment in value.Split('/'))
        {
            if (segment.Length is 0 or > MaxContainerSegmentLength || segment[0] == '.' || segment[^1] == '.'
                || segment.Contains("..", StringComparison.Ordinal)
                || !segment.All(character => character is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '_' or '-' or '.'))
                throw Invalid();
        }
        return value;
    }

    /// <summary>An owner reference: 1 to 200 printable ASCII characters without spaces. It names a record; it carries no authority.</summary>
    public static string Owner(string? value) => value is { Length: > 0 and <= MaxOwnerLength } && value.All(character => character is >= '!' and <= '~')
        ? value : throw Invalid();

    private static bool Unsafe(Rune rune)
    {
        if (rune.IsAscii && ReservedCharacters.Contains((char)rune.Value, StringComparison.Ordinal)) return true;
        return Rune.GetUnicodeCategory(rune) is UnicodeCategory.Control or UnicodeCategory.Format or UnicodeCategory.LineSeparator
            or UnicodeCategory.ParagraphSeparator or UnicodeCategory.Surrogate or UnicodeCategory.OtherNotAssigned;
    }

    private static FileStorageException Invalid() => new(FileStorageError.InvalidName);
}
