using System.Security.Cryptography;

namespace SmartX.Api.Security;

/// <summary>
/// Read-only pass-through over an upload, doing three jobs in the single pass the bytes
/// are already making:
///
/// <list type="number">
///   <item>replays the head bytes that were peeled off for signature sniffing, so the
///   encryptor still sees the whole file;</item>
///   <item>hashes the plaintext with SHA-256 for the integrity value shown in the UI;</item>
///   <item>enforces the size ceiling as the bytes go past, so an oversized upload is cut
///   off mid-flight rather than after it has already been written to disk.</item>
/// </list>
///
/// <para>Doing all three here is what keeps memory flat: nothing ever holds the whole
/// file, and the file is never buffered to disk just to be inspected.</para>
/// </summary>
public sealed class InspectedUploadStream(ReadOnlyMemory<byte> head, Stream inner, long maxBytes) : Stream
{
    private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    private int _headOffset;
    private byte[]? _digest;

    public long BytesRead { get; private set; }

    /// <summary>Lowercase hex SHA-256 of the plaintext. Valid once the stream is fully read.</summary>
    public string Sha256 => Convert.ToHexString(_digest ??= _hash.GetHashAndReset()).ToLowerInvariant();

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        int read;

        if (_headOffset < head.Length)
        {
            // Serve the sniffed head first, then fall through to the socket on later calls.
            read = Math.Min(buffer.Length, head.Length - _headOffset);
            head.Slice(_headOffset, read).CopyTo(buffer);
            _headOffset += read;
        }
        else
        {
            read = await inner.ReadAsync(buffer, cancellationToken);
        }

        if (read <= 0) return read;

        BytesRead += read;
        if (BytesRead > maxBytes)
            throw new UploadTooLargeException($"Attachment exceeds the {maxBytes / (1024 * 1024)} MB limit.");

        _hash.AppendData(buffer.Span[..read]);
        return read;
    }

    public override int Read(byte[] buffer, int offset, int count)
        => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => BytesRead; set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing) _hash.Dispose();
        base.Dispose(disposing);
    }
}

public sealed class UploadTooLargeException(string message) : Exception(message);
