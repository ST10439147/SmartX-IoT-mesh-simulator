using System.Security.Cryptography;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Net.Http.Headers;
using SmartX.Api.Security;
using SmartX.Shared.Telemetry;

namespace SmartX.Api.Services;

/// <summary>
/// Handles device config files, hardware logs and deployment photos attached to a sensor
/// profile.
///
/// <para>The body is read with <see cref="MultipartReader"/> rather than bound to
/// <c>IFormFile</c>. Model binding buffers the whole upload into memory or a temp file
/// before the handler ever runs, so a handful of concurrent 20 MB uploads becomes a
/// gateway-wide memory spike. Streaming keeps peak memory at one 64 KB chunk per
/// request, whatever the file size.</para>
///
/// <para>Bytes land on disk encrypted (AES-256-GCM, see <see cref="ChunkedAesGcm"/>),
/// under a generated name, outside the web root. The client's filename is metadata only
/// - it never touches the filesystem path.</para>
/// </summary>
public sealed class AttachmentService
{
    /// <summary>25 MB. A device config or a deployment photo; not a firmware dump.</summary>
    public const long MaxBytes = 25 * 1024 * 1024;

    private readonly TelemetryStore _store;
    private readonly ILogger<AttachmentService> _logger;
    private readonly string _root;
    private readonly byte[] _key;

    public AttachmentService(TelemetryStore store, IConfiguration config, ILogger<AttachmentService> logger)
    {
        _store = store;
        _logger = logger;

        _root = Path.GetFullPath(config["SmartX:AttachmentRoot"] ?? "storage/attachments");
        Directory.CreateDirectory(_root);

        _key = LoadOrCreateKey(config, logger);
    }

    public sealed record UploadResult(bool Ok, AttachmentInfo? Attachment, string? Error);

    public async Task<UploadResult> StoreAsync(HttpRequest request, string sensorId, CancellationToken ct)
    {
        if (!_store.Exists(sensorId))
            return new UploadResult(false, null, $"No sensor is registered with id '{sensorId}'.");

        if (!MediaTypeHeaderValue.TryParse(request.ContentType, out var mediaType)
            || !mediaType.MediaType.HasValue
            || !mediaType.MediaType.Value!.StartsWith("multipart/", StringComparison.OrdinalIgnoreCase))
        {
            return new UploadResult(false, null, "Expected a multipart/form-data upload.");
        }

        var boundary = HeaderUtilities.RemoveQuotes(mediaType.Boundary).Value;
        if (string.IsNullOrWhiteSpace(boundary))
            return new UploadResult(false, null, "The multipart boundary is missing.");

        var reader = new MultipartReader(boundary, request.Body);
        var section = await reader.ReadNextSectionAsync(ct);

        while (section is not null)
        {
            if (ContentDispositionHeaderValue.TryParse(section.ContentDisposition, out var disposition)
                && disposition.FileName.HasValue)
            {
                return await StoreSectionAsync(section, disposition, sensorId, ct);
            }

            section = await reader.ReadNextSectionAsync(ct);
        }

        return new UploadResult(false, null, "The request contained no file part.");
    }

    private async Task<UploadResult> StoreSectionAsync(
        MultipartSection section,
        ContentDispositionHeaderValue disposition,
        string sensorId,
        CancellationToken ct)
    {
        var fileName = FileSignature.SanitiseFileName(HeaderUtilities.RemoveQuotes(disposition.FileName).Value);

        // Peel off just enough to identify the content before a single byte is stored.
        var head = new byte[FileSignature.HeadSize];
        var headLength = await section.Body.ReadAtLeastAsync(head, head.Length, throwOnEndOfStream: false, ct);

        var verdict = FileSignature.Inspect(fileName, head.AsSpan(0, headLength));
        if (!verdict.Allowed)
        {
            _logger.LogWarning("Rejected upload {File} for {Sensor}: {Reason}", fileName, sensorId, verdict.Reason);
            return new UploadResult(false, null, verdict.Reason);
        }

        var id = Guid.NewGuid().ToString("N");
        var path = Path.Combine(_root, $"{id}.sxa");

        try
        {
            await using var source = new InspectedUploadStream(head.AsMemory(0, headLength), section.Body, MaxBytes);
            await using var destination = new FileStream(
                path, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                bufferSize: 64 * 1024, useAsync: true);

            var plaintextBytes = await ChunkedAesGcm.EncryptAsync(source, destination, _key, ct);
            await destination.FlushAsync(ct);

            var info = new AttachmentInfo(
                Id: id,
                SensorId: sensorId,
                FileName: fileName,
                DetectedType: verdict.DetectedType,
                PlaintextBytes: plaintextBytes,
                StoredBytes: destination.Length,
                UploadedAt: DateTimeOffset.UtcNow,
                Sha256: source.Sha256);

            _store.AddAttachment(info);
            _logger.LogInformation("Stored {File} ({Bytes} bytes) for {Sensor} as {Id}", fileName, plaintextBytes, sensorId, id);

            return new UploadResult(true, info, null);
        }
        catch (UploadTooLargeException ex)
        {
            TryDelete(path);
            return new UploadResult(false, null, ex.Message);
        }
        catch (Exception ex)
        {
            TryDelete(path);
            _logger.LogError(ex, "Failed to store {File} for {Sensor}", fileName, sensorId);
            return new UploadResult(false, null, "The gateway could not store the attachment.");
        }
    }

    /// <summary>Decrypts an attachment straight into the response body - never onto disk in the clear.</summary>
    public async Task<bool> WriteDecryptedAsync(AttachmentInfo info, Stream destination, CancellationToken ct)
    {
        var path = Path.Combine(_root, $"{info.Id}.sxa");
        if (!File.Exists(path)) return false;

        await using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, useAsync: true);
        await ChunkedAesGcm.DecryptAsync(source, destination, _key, ct);
        return true;
    }

    public bool Delete(AttachmentInfo info) => TryDelete(Path.Combine(_root, $"{info.Id}.sxa"));

    private static bool TryDelete(string path)
    {
        try
        {
            if (!File.Exists(path)) return false;
            File.Delete(path);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
    }

    /// <summary>
    /// Key comes from configuration, which in production means an environment variable or
    /// a secret store - never appsettings.json in the repo. For a local run the gateway
    /// generates one and keeps it beside the attachments so restarts can still read them.
    /// </summary>
    private static byte[] LoadOrCreateKey(IConfiguration config, ILogger logger)
    {
        var configured = config["SmartX:AttachmentKey"];
        if (!string.IsNullOrWhiteSpace(configured))
        {
            var bytes = Convert.FromBase64String(configured);
            if (bytes.Length != ChunkedAesGcm.KeySize)
                throw new InvalidOperationException($"SmartX:AttachmentKey must decode to {ChunkedAesGcm.KeySize} bytes.");
            return bytes;
        }

        var keyPath = Path.GetFullPath(config["SmartX:AttachmentKeyPath"] ?? "storage/attachment.key");
        Directory.CreateDirectory(Path.GetDirectoryName(keyPath)!);

        if (File.Exists(keyPath)) return File.ReadAllBytes(keyPath);

        var generated = RandomNumberGenerator.GetBytes(ChunkedAesGcm.KeySize);
        File.WriteAllBytes(keyPath, generated);
        logger.LogWarning("No SmartX:AttachmentKey configured. Generated a development key at {Path} - set the config value in production.", keyPath);

        return generated;
    }
}
