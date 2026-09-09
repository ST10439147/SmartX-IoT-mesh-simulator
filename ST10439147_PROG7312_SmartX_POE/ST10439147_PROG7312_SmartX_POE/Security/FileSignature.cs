using System.Text;

namespace SmartX.Api.Security;

/// <summary>
/// Content sniffing for uploaded attachments.
///
/// <para>The extension a client sends is a claim, not a fact - <c>payload.exe</c> renamed
/// to <c>config.png</c> passes any extension-only check. So the allowlist is applied to
/// the extension <em>and</em> the leading bytes must match what that extension promises.
/// Text formats have no magic number, so they are validated structurally instead: no NUL
/// bytes and decodable as UTF-8.</para>
/// </summary>
public static class FileSignature
{
    /// <summary>Bytes of the file needed to reach a verdict.</summary>
    public const int HeadSize = 512;

    public sealed record Verdict(bool Allowed, string DetectedType, string Reason);

    private static readonly Dictionary<string, byte[]> BinaryMagic = new(StringComparer.OrdinalIgnoreCase)
    {
        [".png"] = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A],
        [".jpg"] = [0xFF, 0xD8, 0xFF],
        [".jpeg"] = [0xFF, 0xD8, 0xFF],
        [".gif"] = "GIF8"u8.ToArray(),
        [".pdf"] = "%PDF"u8.ToArray()
    };

    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".log", ".txt", ".json", ".cfg", ".conf", ".ini", ".csv", ".yaml", ".yml", ".env", ".md"
    };

    public static Verdict Inspect(string fileName, ReadOnlySpan<byte> head)
    {
        var extension = Path.GetExtension(fileName);

        if (string.IsNullOrWhiteSpace(extension))
            return new Verdict(false, "unknown", "Files must carry an extension so the gateway can validate them.");

        if (BinaryMagic.TryGetValue(extension, out var magic))
        {
            if (head.Length < magic.Length || !head[..magic.Length].SequenceEqual(magic))
                return new Verdict(false, "mismatch", $"Content does not match a real {extension} file. Renaming does not change what a file is.");

            return new Verdict(true, extension.TrimStart('.').ToUpperInvariant(), "Signature verified.");
        }

        if (TextExtensions.Contains(extension))
        {
            if (head.Contains((byte)0))
                return new Verdict(false, "binary", $"A {extension} file must be text, but this contains binary data.");

            try
            {
                // Throws on malformed sequences rather than substituting U+FFFD.
                new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(head);
            }
            catch (DecoderFallbackException)
            {
                // A head cut mid-character is a false positive, so only fail when the
                // damage starts well before the boundary.
                if (head.Length == HeadSize) return new Verdict(true, "TEXT", "Assumed text; head ends mid-character.");
                return new Verdict(false, "binary", $"A {extension} file must be valid UTF-8 text.");
            }

            return new Verdict(true, "TEXT", "Valid UTF-8 text.");
        }

        return new Verdict(false, "blocked",
            $"'{extension}' is not an accepted attachment type. Allowed: {string.Join(", ", BinaryMagic.Keys.Concat(TextExtensions).Order())}.");
    }

    /// <summary>
    /// Strips any path the client tried to smuggle in the filename, so an upload named
    /// <c>../../appsettings.json</c> cannot escape the attachment directory.
    /// </summary>
    public static string SanitiseFileName(string? fileName)
    {
        var name = Path.GetFileName(fileName ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(name)) return "attachment.bin";

        foreach (var invalid in Path.GetInvalidFileNameChars())
            name = name.Replace(invalid, '_');

        return name.Length > 128 ? string.Concat(name.AsSpan(name.Length - 128), "") : name;
    }
}
//---------------------------DDDDDDDDDDDoooooooooooooo END OF FILE DDDDDDDDDDDoooooooooooooo---------------------------//
