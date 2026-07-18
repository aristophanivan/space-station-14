using System.Security.Cryptography;
using Content.Shared.CCVar;
using Content.Shared.GameTicking;
using Content.Shared.Photography;
using Robust.Shared.Configuration;
using Robust.Shared.Network;

namespace Content.Server.Photography;

public enum PhotoImageStorageError : byte
{
    None,
    EmptyImage,
    InvalidPng,
    InvalidSize,
    EncodedImageTooLarge,
    DecodedImageTooLarge,
    RoundStorageFull,
    RoundRecordLimit,
    HashCollision,
}

public sealed class PhotoStoredImage
{
    public readonly ReadOnlyMemory<byte> EncodedPng;
    public readonly PhotoImageMetadata Metadata;

    internal PhotoStoredImage(byte[] encodedPng, PhotoImageMetadata metadata)
    {
        EncodedPng = encodedPng;
        Metadata = metadata;
    }
}

/// <summary>
/// Owns immutable, deduplicated photograph blobs for the duration of a round.
/// </summary>
public sealed partial class PhotoImageStorageSystem : EntitySystem
{
    private static readonly byte[] PngSignature = [137, 80, 78, 71, 13, 10, 26, 10];

    [Dependency] private IConfigurationManager _cfg = default!;

    private readonly Dictionary<PhotoImageId, PhotoImageEntry> _images = new();
    private readonly Dictionary<string, PhotoImageBlob> _blobs = new(StringComparer.Ordinal);
    private long _storedBlobBytes;

    internal int ImageCount => _images.Count;
    internal int BlobCount => _blobs.Count;
    internal long StoredBlobBytes => _storedBlobBytes;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<RoundRestartCleanupEvent>(_ => ClearRoundCache());
    }

    /// <summary>
    /// Accepts PNG data that has already passed the canonicalization pipeline.
    /// This method still enforces cheap envelope and round-budget checks.
    /// </summary>
    public bool TryAddCanonicalImage(
        ReadOnlyMemory<byte> encodedPng,
        Vector2i size,
        PhotoOrigin origin,
        TimeSpan createdAt,
        string? cameraSerial,
        NetUserId? uploadedBy,
        out PhotoImageId imageId,
        out PhotoImageStorageError error)
    {
        imageId = default;
        error = PhotoImageStorageError.None;

        if (encodedPng.IsEmpty)
        {
            error = PhotoImageStorageError.EmptyImage;
            return false;
        }

        var maxEncodedBytes = Math.Min(
            _cfg.GetCVar(CCVars.PhotographyMaxEncodedBytes),
            PhotographyConstants.MaxEncodedBytes);
        if (encodedPng.Length > maxEncodedBytes)
        {
            error = PhotoImageStorageError.EncodedImageTooLarge;
            return false;
        }

        if (encodedPng.Length < PngSignature.Length ||
            !encodedPng.Span[..PngSignature.Length].SequenceEqual(PngSignature))
        {
            error = PhotoImageStorageError.InvalidPng;
            return false;
        }

        var maxDimension = _cfg.GetCVar(CCVars.PhotographyMaxDimension);
        if (size.X <= 0 || size.Y <= 0 || size.X > maxDimension || size.Y > maxDimension)
        {
            error = PhotoImageStorageError.InvalidSize;
            return false;
        }

        long decodedBytes;
        try
        {
            decodedBytes = checked((long) size.X * size.Y * 4);
        }
        catch (OverflowException)
        {
            error = PhotoImageStorageError.InvalidSize;
            return false;
        }

        if (decodedBytes > _cfg.GetCVar(CCVars.PhotographyMaxDecodedBytes))
        {
            error = PhotoImageStorageError.DecodedImageTooLarge;
            return false;
        }

        if (_images.Count >= _cfg.GetCVar(CCVars.PhotographyMaxImageRecords))
        {
            error = PhotoImageStorageError.RoundRecordLimit;
            return false;
        }

        var hash = Convert.ToHexString(SHA256.HashData(encodedPng.Span));
        if (!_blobs.TryGetValue(hash, out var blob))
        {
            if (_storedBlobBytes + encodedPng.Length > _cfg.GetCVar(CCVars.PhotographyRoundStorageBytes))
            {
                error = PhotoImageStorageError.RoundStorageFull;
                return false;
            }

            blob = new PhotoImageBlob(encodedPng.ToArray());
            _blobs.Add(hash, blob);
            _storedBlobBytes += blob.Bytes.Length;
        }
        else if (!blob.Bytes.AsSpan().SequenceEqual(encodedPng.Span))
        {
            error = PhotoImageStorageError.HashCollision;
            return false;
        }

        imageId = PhotoImageId.New();
        var metadata = new PhotoImageMetadata(size, hash, origin, createdAt, cameraSerial, uploadedBy);
        _images.Add(imageId, new PhotoImageEntry(blob, metadata));
        return true;
    }

    public bool TryGetImage(PhotoImageId imageId, out PhotoStoredImage image)
    {
        if (!_images.TryGetValue(imageId, out var entry))
        {
            image = default!;
            return false;
        }

        // The storage-owned array never leaves this system.
        image = new PhotoStoredImage(entry.Blob.Bytes.AsSpan().ToArray(), entry.Metadata);
        return true;
    }

    internal bool TryGetMetadata(PhotoImageId imageId, out PhotoImageMetadata metadata)
    {
        if (_images.TryGetValue(imageId, out var entry))
        {
            metadata = entry.Metadata;
            return true;
        }

        metadata = default!;
        return false;
    }

    public void ClearRoundCache()
    {
        _images.Clear();
        _blobs.Clear();
        _storedBlobBytes = 0;
    }

    private sealed record PhotoImageEntry(PhotoImageBlob Blob, PhotoImageMetadata Metadata);

    private sealed class PhotoImageBlob(byte[] bytes)
    {
        public readonly byte[] Bytes = bytes;
    }
}
