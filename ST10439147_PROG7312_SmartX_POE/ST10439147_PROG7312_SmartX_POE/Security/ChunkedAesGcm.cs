using System.Buffers.Binary;
using System.Security.Cryptography;

namespace SmartX.Api.Security;

/// <summary>
/// Streaming authenticated encryption for uploaded device configs, logs and photos.
///
/// <para>AES-GCM is a one-shot primitive: the built-in API wants the whole plaintext in
/// memory, which would defeat the point of streaming the multipart body in the first
/// place. So the payload is framed into 64 KB chunks and each chunk is sealed
/// independently. Peak memory is one chunk regardless of file size.</para>
///
/// <para>Framing each chunk separately opens two attacks that a naive implementation
/// misses, both closed here through the associated data:</para>
/// <list type="bullet">
///   <item><b>Reordering</b> - the chunk index is authenticated, so swapping two frames
///   fails the tag check.</item>
///   <item><b>Truncation</b> - the last frame is flagged, and that flag is authenticated,
///   so lopping frames off the end is detected rather than silently accepted.</item>
/// </list>
///
/// <para>Nonces are never reused: a random 8-byte per-file prefix is concatenated with a
/// 4-byte big-endian chunk counter.</para>
///
/// <para>Layout: <c>"SXA1" | prefix[8] | frame*</c> where a frame is
/// <c>isFinal[1] | plaintextLength[4] | tag[16] | ciphertext[n]</c>.</para>
/// </summary>
public static class ChunkedAesGcm
{
    public const int ChunkSize = 64 * 1024;
    public const int KeySize = 32;             // AES-256

    private const int NonceSize = 12;          // AesGcm.NonceByteSizes.MaxSize
    private const int TagSize = 16;            // AesGcm.TagByteSizes.MaxSize
    private const int PrefixSize = 8;
    private const int FrameHeaderSize = 1 + 4 + TagSize;

    private static ReadOnlySpan<byte> Magic => "SXA1"u8;

    /// <summary>Encrypts <paramref name="source"/> into <paramref name="destination"/>. Returns plaintext bytes consumed.</summary>
    // Reference [28]: Microsoft. AesGcm Class. Microsoft Learn.
    //   https://learn.microsoft.com/dotnet/api/system.security.cryptography.aesgcm
    // Reference [29]: Dworkin, M. (2007). NIST Special Publication 800-38D: Recommendation for Block Cipher Modes of Operation: Galois/Counter Mode (GCM) and GMAC. NIST.
    //   https://doi.org/10.6028/NIST.SP.800-38D
    // Reference [30]: McGrew, D. (2008). RFC 5116: An Interface and Algorithms for Authenticated Encryption. IETF.
    //   https://www.rfc-editor.org/rfc/rfc5116
    public static async Task<long> EncryptAsync(Stream source, Stream destination, byte[] key, CancellationToken ct = default)
    {
        ValidateKey(key);

        var prefix = RandomNumberGenerator.GetBytes(PrefixSize);
        await destination.WriteAsync(Magic.ToArray(), ct);
        await destination.WriteAsync(prefix, ct);

        using var aes = new AesGcm(key, TagSize);

        var plaintext = new byte[ChunkSize];
        var ciphertext = new byte[ChunkSize];
        var frameHeader = new byte[FrameHeaderSize];
        var nonce = new byte[NonceSize];
        var aad = new byte[5];

        prefix.CopyTo(nonce, 0);

        uint counter = 0;
        long total = 0;

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            // ReadAtLeastAsync loops internally; a short read only means end of stream.
            var read = await source.ReadAtLeastAsync(plaintext, ChunkSize, throwOnEndOfStream: false, ct);
            var isFinal = read < ChunkSize;
            total += read;

            BinaryPrimitives.WriteUInt32BigEndian(nonce.AsSpan(PrefixSize), counter);
            BinaryPrimitives.WriteUInt32BigEndian(aad.AsSpan(0), counter);
            aad[4] = isFinal ? (byte)1 : (byte)0;

            frameHeader[0] = aad[4];
            BinaryPrimitives.WriteInt32BigEndian(frameHeader.AsSpan(1), read);

            aes.Encrypt(
                nonce,
                plaintext.AsSpan(0, read),
                ciphertext.AsSpan(0, read),
                frameHeader.AsSpan(5, TagSize),
                aad);

            await destination.WriteAsync(frameHeader, ct);
            await destination.WriteAsync(ciphertext.AsMemory(0, read), ct);

            if (isFinal) break;
            counter++;
        }

        await destination.FlushAsync(ct);
        return total;
    }

    /// <summary>
    /// Decrypts and verifies. Throws <see cref="CryptographicException"/> if any frame
    /// has been altered, reordered, or if the stream was truncated before its final frame.
    /// </summary>
    // Reference [28]: Microsoft. AesGcm Class. Microsoft Learn.
    //   https://learn.microsoft.com/dotnet/api/system.security.cryptography.aesgcm
    // Reference [30]: McGrew, D. (2008). RFC 5116: An Interface and Algorithms for Authenticated Encryption. IETF.
    //   https://www.rfc-editor.org/rfc/rfc5116
    public static async Task<long> DecryptAsync(Stream source, Stream destination, byte[] key, CancellationToken ct = default)
    {
        ValidateKey(key);

        var header = new byte[Magic.Length + PrefixSize];
        if (await source.ReadAtLeastAsync(header, header.Length, throwOnEndOfStream: false, ct) != header.Length)
            throw new CryptographicException("Attachment is shorter than its own header.");

        if (!header.AsSpan(0, Magic.Length).SequenceEqual(Magic))
            throw new CryptographicException("Attachment is not in the SmartX encrypted format.");

        using var aes = new AesGcm(key, TagSize);

        var plaintext = new byte[ChunkSize];
        var ciphertext = new byte[ChunkSize];
        var frameHeader = new byte[FrameHeaderSize];
        var nonce = new byte[NonceSize];
        var aad = new byte[5];

        header.AsSpan(Magic.Length, PrefixSize).CopyTo(nonce);

        uint counter = 0;
        long total = 0;

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            if (await source.ReadAtLeastAsync(frameHeader, FrameHeaderSize, throwOnEndOfStream: false, ct) != FrameHeaderSize)
                throw new CryptographicException("Attachment was truncated: no final frame was found.");

            var isFinal = frameHeader[0] == 1;
            var length = BinaryPrimitives.ReadInt32BigEndian(frameHeader.AsSpan(1));

            if ((uint)length > ChunkSize)
                throw new CryptographicException("Attachment declares an impossible frame length.");

            if (await source.ReadAtLeastAsync(ciphertext.AsMemory(0, length), length, throwOnEndOfStream: false, ct) != length)
                throw new CryptographicException("Attachment was truncated mid-frame.");

            BinaryPrimitives.WriteUInt32BigEndian(nonce.AsSpan(PrefixSize), counter);
            BinaryPrimitives.WriteUInt32BigEndian(aad.AsSpan(0), counter);
            aad[4] = frameHeader[0];

            // Throws if the ciphertext, the chunk index, or the final-frame flag was touched.
            aes.Decrypt(
                nonce,
                ciphertext.AsSpan(0, length),
                frameHeader.AsSpan(5, TagSize),
                plaintext.AsSpan(0, length),
                aad);

            await destination.WriteAsync(plaintext.AsMemory(0, length), ct);
            total += length;

            if (isFinal) break;
            counter++;
        }

        await destination.FlushAsync(ct);
        return total;
    }

    private static void ValidateKey(byte[] key)
    {
        if (key is not { Length: KeySize })
            throw new ArgumentException($"Attachment key must be exactly {KeySize} bytes (AES-256).", nameof(key));
    }
}
//---------------------------DDDDDDDDDDDoooooooooooooo END OF FILE DDDDDDDDDDDoooooooooooooo---------------------------//
