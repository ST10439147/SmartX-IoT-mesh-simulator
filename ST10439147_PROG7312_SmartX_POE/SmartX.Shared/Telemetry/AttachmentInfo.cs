namespace SmartX.Shared.Telemetry;

/// <summary>
/// Metadata for a device config, hardware log or deployment photo attached to a sensor
/// profile. The bytes themselves live encrypted on disk; only this record is public.
/// </summary>
public sealed record AttachmentInfo(
    string Id,
    string SensorId,
    string FileName,
    string DetectedType,
    long PlaintextBytes,
    long StoredBytes,
    DateTimeOffset UploadedAt,
    string Sha256);
