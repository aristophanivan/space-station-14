using System.IO;
using System.Security.Cryptography;
using Content.Shared.GameTicking;
using Content.Shared.Photography;
using Robust.Client.Graphics;

namespace Content.Client.Photography;

public enum PhotoImageCacheFailure : byte
{
    InvalidResponse,
    InvalidHash,
    InvalidDimensions,
    DecodeFailed,
}

/// <summary>
/// Owns decoded photograph textures and coalesces concurrent requests for the same image.
/// </summary>
public sealed partial class PhotoImageCacheSystem : EntitySystem
{
    [Dependency] private IClyde _clyde = default!;

    private readonly Dictionary<PhotoImageId, OwnedTexture> _textures = new();
    private readonly Dictionary<PhotoImageId, PendingRequest> _pending = new();

    public event Action<PhotoImageId, OwnedTexture>? ImageLoaded;
    public event Action<PhotoImageId, PhotoImageCacheFailure>? ImageFailed;
    public event Action? CacheClearing;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeNetworkEvent<RoundRestartCleanupEvent>(_ => Clear());
    }

    public override void Shutdown()
    {
        Clear();
        base.Shutdown();
    }

    public bool TryGet(PhotoImageId imageId, out OwnedTexture texture)
        => _textures.TryGetValue(imageId, out texture!);

    /// <summary>
    /// Returns true only for the first caller that should send the BUI request.
    /// </summary>
    public bool BeginRequest(PhotoImageId imageId, PhotoDisplayMetadata metadata)
    {
        if (_textures.ContainsKey(imageId))
            return false;

        if (_pending.TryGetValue(imageId, out var pending))
        {
            pending.Waiters++;
            return false;
        }

        _pending.Add(imageId, new PendingRequest(metadata));
        return true;
    }

    public void CancelRequest(PhotoImageId imageId)
    {
        if (!_pending.TryGetValue(imageId, out var pending))
            return;

        if (--pending.Waiters <= 0)
            _pending.Remove(imageId);
    }

    public void Accept(PhotoImageDataMessage message)
    {
        if (!_pending.Remove(message.ImageId, out var pending) ||
            message.EncodedPng.Length == 0 ||
            message.EncodedPng.Length > PhotographyConstants.MaxEncodedBytes)
        {
            ImageFailed?.Invoke(message.ImageId, PhotoImageCacheFailure.InvalidResponse);
            return;
        }

        var hash = Convert.ToHexString(SHA256.HashData(message.EncodedPng));
        if (!hash.Equals(pending.Metadata.Sha256, StringComparison.Ordinal))
        {
            ImageFailed?.Invoke(message.ImageId, PhotoImageCacheFailure.InvalidHash);
            return;
        }

        OwnedTexture texture;
        try
        {
            using var stream = new MemoryStream(message.EncodedPng, writable: false);
            texture = _clyde.LoadTextureFromPNGStream(stream, $"photograph-{message.ImageId}");
        }
        catch (Exception e)
        {
            Log.Warning("Failed to decode photograph {ImageId}: {Exception}", message.ImageId, e);
            ImageFailed?.Invoke(message.ImageId, PhotoImageCacheFailure.DecodeFailed);
            return;
        }

        if (texture.Size != pending.Metadata.Size)
        {
            texture.Dispose();
            ImageFailed?.Invoke(message.ImageId, PhotoImageCacheFailure.InvalidDimensions);
            return;
        }

        _textures.Add(message.ImageId, texture);
        ImageLoaded?.Invoke(message.ImageId, texture);
    }

    public void Clear()
    {
        CacheClearing?.Invoke();

        foreach (var texture in _textures.Values)
        {
            texture.Dispose();
        }

        _textures.Clear();
        _pending.Clear();
    }

    private sealed class PendingRequest(PhotoDisplayMetadata metadata)
    {
        public readonly PhotoDisplayMetadata Metadata = metadata;
        public int Waiters = 1;
    }
}
