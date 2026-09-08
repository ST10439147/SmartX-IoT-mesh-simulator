using System.Security.Cryptography;
using System.Text;
using SmartX.Api.Security;
using SmartX.Shared.Diagnostics;

namespace SmartX.Api;

/// <summary>
/// The one runnable check. Runs the shared data-structure suite plus the crypto
/// round-trip that only exists on the server side.
///
/// <code>dotnet run --project src/SmartX.Api -- --selftest</code>
/// </summary>
internal static class SelfCheckRunner
{
    public static int Run()
    {
        Console.WriteLine("SmartX self-check");
        Console.WriteLine(new string('-', 64));

        try
        {
            foreach (var line in SelfCheck.Run())
                Console.WriteLine($"  ok  {line}");

            foreach (var line in RunCryptoChecks())
                Console.WriteLine($"  ok  {line}");
        }
        catch (Exception ex)
        {
            Console.WriteLine();
            Console.Error.WriteLine($"  FAILED  {ex.Message}");
            return 1;
        }

        Console.WriteLine(new string('-', 64));
        Console.WriteLine("all checks passed");
        return 0;
    }

    private static IEnumerable<string> RunCryptoChecks()
    {
        var log = new List<string>();
        var key = RandomNumberGenerator.GetBytes(ChunkedAesGcm.KeySize);

        // Sizes chosen to straddle the chunk boundary: empty, tiny, exactly one chunk,
        // and one chunk plus a byte. Off-by-one framing bugs live at exactly these sizes.
        foreach (var size in new[] { 0, 1, 1024, ChunkedAesGcm.ChunkSize, ChunkedAesGcm.ChunkSize + 1, ChunkedAesGcm.ChunkSize * 2 })
        {
            var plaintext = RandomNumberGenerator.GetBytes(size);

            using var encrypted = new MemoryStream();
            var written = ChunkedAesGcm.EncryptAsync(new MemoryStream(plaintext), encrypted, key).GetAwaiter().GetResult();
            Assert(written == size, $"encrypt should consume all {size} plaintext bytes, reported {written}");

            encrypted.Position = 0;
            using var decrypted = new MemoryStream();
            ChunkedAesGcm.DecryptAsync(encrypted, decrypted, key).GetAwaiter().GetResult();

            Assert(decrypted.ToArray().SequenceEqual(plaintext), $"round trip failed at {size} bytes");
            Assert(encrypted.Length > size, "ciphertext should carry its header and tags");
        }

        log.Add($"crypto: AES-256-GCM round-trips at 0, 1, 1 KB, {ChunkedAesGcm.ChunkSize / 1024} KB and multi-chunk sizes");

        // Tampering must be caught, not silently decrypted into garbage.
        var payload = Encoding.UTF8.GetBytes("sensor=A4:CF:12:9B:04:7E; calibration=1.042");
        using (var sealed1 = new MemoryStream())
        {
            ChunkedAesGcm.EncryptAsync(new MemoryStream(payload), sealed1, key).GetAwaiter().GetResult();

            var bytes = sealed1.ToArray();
            bytes[^1] ^= 0xFF;   // flip a bit in the ciphertext

            Assert(Throws(() => ChunkedAesGcm.DecryptAsync(new MemoryStream(bytes), Stream.Null, key).GetAwaiter().GetResult()),
                "a flipped ciphertext bit must fail authentication");
        }

        using (var sealed2 = new MemoryStream())
        {
            ChunkedAesGcm.EncryptAsync(new MemoryStream(payload), sealed2, key).GetAwaiter().GetResult();

            var truncated = sealed2.ToArray()[..^8];
            Assert(Throws(() => ChunkedAesGcm.DecryptAsync(new MemoryStream(truncated), Stream.Null, key).GetAwaiter().GetResult()),
                "a truncated attachment must be rejected");
        }

        using (var sealed3 = new MemoryStream())
        {
            ChunkedAesGcm.EncryptAsync(new MemoryStream(payload), sealed3, key).GetAwaiter().GetResult();
            sealed3.Position = 0;

            var wrongKey = RandomNumberGenerator.GetBytes(ChunkedAesGcm.KeySize);
            Assert(Throws(() => ChunkedAesGcm.DecryptAsync(sealed3, Stream.Null, wrongKey).GetAwaiter().GetResult()),
                "the wrong key must not decrypt an attachment");
        }

        log.Add("crypto: bit-flips, truncation and wrong keys are all rejected");

        // Content sniffing: the extension is a claim, the bytes are the evidence.
        var png = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 1, 2 };
        var mzExe = "MZ\0"u8.ToArray();

        Assert(FileSignature.Inspect("deployment.png", png).Allowed, "a real PNG should be accepted");
        Assert(!FileSignature.Inspect("payload.png", mzExe).Allowed, "an executable renamed to .png must be rejected");
        Assert(!FileSignature.Inspect("payload.exe", mzExe).Allowed, "executables are not an accepted type at all");
        Assert(FileSignature.Inspect("gateway.log", "boot ok\nmesh joined\n"u8).Allowed, "a plain text log should be accepted");
        Assert(!FileSignature.Inspect("gateway.log", png).Allowed, "binary content in a .log must be rejected");
        Assert(FileSignature.SanitiseFileName("../../appsettings.json") == "appsettings.json",
            "path traversal must be stripped from the stored filename");

        log.Add("uploads: magic-byte sniffing, text validation and path-traversal stripping");

        return log;
    }

    private static bool Throws(Action action)
    {
        try
        {
            action();
            return false;
        }
        catch (CryptographicException)
        {
            return true;
        }
    }

    private static void Assert(bool condition, string because)
    {
        if (!condition) throw new SelfCheckFailure(because);
    }
}
//---------------------------DDDDDDDDDDDoooooooooooooo END OF FILE DDDDDDDDDDDoooooooooooooo---------------------------//
