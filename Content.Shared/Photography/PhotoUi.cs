using Robust.Shared.Network;
using Robust.Shared.Serialization;

namespace Content.Shared.Photography;

public static class PhotographyConstants
{
    public const int CaptureSize = 512;
    public const int MaxEncodedBytes = 512 * 1024;
}

[Serializable, NetSerializable]
public enum PhotoOrigin : byte
{
    Camera,
    Uploaded,
}

/// <summary>
/// Immutable metadata assigned by the server when an image is accepted.
/// </summary>
public sealed class PhotoImageMetadata
{
    public readonly Vector2i Size;
    public readonly string Sha256;
    public readonly PhotoOrigin Origin;
    public readonly TimeSpan CreatedAt;
    public readonly string? CameraSerial;
    public readonly NetUserId? UploadedBy;

    public PhotoImageMetadata(
        Vector2i size,
        string sha256,
        PhotoOrigin origin,
        TimeSpan createdAt,
        string? cameraSerial,
        NetUserId? uploadedBy)
    {
        Size = size;
        Sha256 = sha256;
        Origin = origin;
        CreatedAt = createdAt;
        CameraSerial = cameraSerial;
        UploadedBy = uploadedBy;
    }

    public PhotoDisplayMetadata ToDisplayMetadata()
        => new(Size, Sha256);
}

/// <summary>
/// Viewer-safe metadata. Server-only provenance such as uploader identity is intentionally excluded.
/// </summary>
[Serializable, NetSerializable]
public sealed class PhotoDisplayMetadata
{
    public readonly Vector2i Size;
    public readonly string Sha256;

    public PhotoDisplayMetadata(Vector2i size, string sha256)
    {
        Size = size;
        Sha256 = sha256;
    }
}

[Serializable, NetSerializable]
public enum PhotoUiKey : byte
{
    Key,
}

/// <summary>
/// Small BUI state. Encoded image data is sent only in a targeted response.
/// </summary>
[Serializable, NetSerializable]
public sealed class PhotoBoundUserInterfaceState : BoundUserInterfaceState
{
    public readonly PhotoImageId? ImageId;
    public readonly PhotoDisplayMetadata? Metadata;

    public PhotoBoundUserInterfaceState(
        PhotoImageId? imageId,
        PhotoDisplayMetadata? metadata)
    {
        ImageId = imageId;
        Metadata = metadata;
    }
}

/// <summary>
/// Requests the image belonging to the BUI owner. The client never supplies an image ID.
/// </summary>
[Serializable, NetSerializable]
public sealed class RequestPhotoImageMessage : BoundUserInterfaceMessage
{
}

/// <summary>
/// A targeted, on-demand image response. This is never stored in component or BUI state.
/// </summary>
[Serializable, NetSerializable]
public sealed class PhotoImageDataMessage : BoundUserInterfaceMessage
{
    public readonly PhotoImageId ImageId;
    public readonly byte[] EncodedPng;

    public PhotoImageDataMessage(PhotoImageId imageId, byte[] encodedPng)
    {
        ImageId = imageId;
        EncodedPng = encodedPng;
    }
}
