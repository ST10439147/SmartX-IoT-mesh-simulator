using System.Security.Cryptography;
using System.Text;
using SmartX.Api.Security;
using SmartX.Api.Services;
using SmartX.Shared.Diagnostics;
using SmartX.Shared.Telemetry;

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

            foreach (var line in RunQueueChecks())
                Console.WriteLine($"  ok  {line}");

            foreach (var line in RunCommandChecks())
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
    //----------------------------------------------------------------------------------------------------------------------//
    // Reference [26]: OWASP. File Upload Cheat Sheet. OWASP Cheat Sheet Series.
    //   https://cheatsheetseries.owasp.org/cheatsheets/File_Upload_Cheat_Sheet.html
    // Reference [27]: OWASP. Path Traversal.
    //   https://owasp.org/www-community/attacks/Path_Traversal
    // Reference [28]: Microsoft. AesGcm Class. Microsoft Learn.
    //   https://learn.microsoft.com/dotnet/api/system.security.cryptography.aesgcm
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
    //----------------------------------------------------------------------------------------------------------------------//
    private static IEnumerable<string> RunQueueChecks()
    {
        var log = new List<string>();
        var queue = new IngestQueue();

        for (var i = 1; i <= 5; i++) queue.TryEnqueue("meter-1", i);
        Assert(queue.Depth == 5, "five packets should be waiting");

        var order = new List<double>();
        while (queue.Depth > 0) order.Add(queue.DequeueAsync().AsTask().GetAwaiter().GetResult().Value);
        Assert(order.SequenceEqual([1, 2, 3, 4, 5]), $"expected FIFO 1..5 but got {string.Join(",", order)}");

        // Backed-up normal lane, then criticals arriving late: they must leave first,
        // spikes ahead of threshold breaches, and equal ranks in arrival order.
        for (var i = 1; i <= 3; i++) queue.TryEnqueue("meter-1", i);
        queue.TryEnqueue("meter-1", 4, AlertKind.ThresholdBreach);
        queue.TryEnqueue("meter-1", 5, AlertKind.Spike);
        queue.TryEnqueue("meter-1", 6, AlertKind.ThresholdBreach);
        queue.TryEnqueue("meter-1", 7);
        Assert(queue.Depth == 4 && queue.CriticalDepth == 3, "criticals wait in their own lane");

        order.Clear();
        while (queue.Depth + queue.CriticalDepth > 0) order.Add(queue.DequeueAsync().AsTask().GetAwaiter().GetResult().Value);
        Assert(order.SequenceEqual([5, 4, 6, 1, 2, 3, 7]), $"expected 5,4,6,1,2,3,7 but got {string.Join(",", order)}");

        // The processing trail keeps the order the worker reported, and stays bounded.
        queue.Throttle = TimeSpan.FromMilliseconds(5);
        for (var i = 1; i <= 260; i++) queue.MarkProcessed(new QueuedPacket(i, "meter-1", i, DateTimeOffset.UtcNow));
        var recent = queue.Recent;
        Assert(recent.Count == 200 && recent[0].Position == 61 && recent[^1].Position == 260, "trail should hold the newest 200, oldest first");
        Assert(queue.Throttle == TimeSpan.Zero, "the flood throttle must clear itself once the queue is empty");

        for (var i = 0; i < IngestQueue.MaxDepth; i++) queue.TryEnqueue("meter-1", i);
        Assert(!queue.TryEnqueue("meter-1", 0), "a full queue must refuse the packet, not grow without bound");
        Assert(queue.TryEnqueue("meter-1", 0, AlertKind.Spike), "a full normal lane must not turn a critical packet away");

        log.Add("queue: FIFO for normal packets, criticals jump the line, a full lane sheds load");
        return log;
    }
    //----------------------------------------------------------------------------------------------------------------------//
    private static IEnumerable<string> RunCommandChecks()
    {
        var log = new List<string>();
        var commands = new CommandService();

        Assert(!commands.TryUndo(out _), "undo on an empty stack must report nothing to undo, not throw");

        commands.Issue("open valve A", ["valve-a"], true);
        commands.Issue("shut all valves", ["valve-a", "valve-b"], false);

        Assert(commands.History.Select(c => c.Label).SequenceEqual(["shut all valves", "open valve A"]),
            "history must list the most recent command first");
        Assert(commands.TryGetOverride("valve-a", out var a) && !a && commands.TryGetOverride("valve-b", out var b) && !b,
            "the batch command should have shut both valves");

        // Undo the batch: A goes back to the earlier override, B back to auto.
        Assert(commands.TryUndo(out var undone) && undone!.Label == "shut all valves", "undo must pop the last command issued");
        Assert(commands.TryGetOverride("valve-a", out a) && a, "valve A should be back to the override it had before");
        Assert(!commands.TryGetOverride("valve-b", out _), "valve B had no override before, so it returns to auto");

        Assert(commands.TryUndo(out _) && !commands.TryGetOverride("valve-a", out _), "second undo releases valve A");
        Assert(!commands.TryUndo(out _) && commands.History.Count == 0, "the stack is empty again");

        log.Add("stack: commands undo last-in-first-out, a batch reverts as one, empty undo is safe");
        return log;
    }
    //----------------------------------------------------------------------------------------------------------------------//
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
    //----------------------------------------------------------------------------------------------------------------------//
    private static void Assert(bool condition, string because)
    {
        if (!condition) throw new SelfCheckFailure(because);
    }
}
//---------------------------DDDDDDDDDDDoooooooooooooo END OF FILE DDDDDDDDDDDoooooooooooooo---------------------------//
