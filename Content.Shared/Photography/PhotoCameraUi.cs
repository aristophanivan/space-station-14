using Robust.Shared.Serialization;

namespace Content.Shared.Photography;

[Serializable, NetSerializable]
public readonly record struct PhotoCaptureToken(Guid Value)
{
    public static PhotoCaptureToken New() => new(Guid.NewGuid());
}

[Serializable, NetSerializable]
public enum PhotoCameraUiKey : byte
{
    Key,
}

[Serializable, NetSerializable]
public enum PhotoCameraStatus : byte
{
    Ready,
    NoFilm,
    FilmEmpty,
    Processing,
}

[Serializable, NetSerializable]
public enum PhotoCaptureResult : byte
{
    Success,
    InvalidSession,
    CameraNotHeld,
    NoFilm,
    FilmEmpty,
    Cooldown,
    Busy,
    QueueFull,
    InvalidImage,
    StorageFull,
}

/// <summary>
/// Small persistent camera state. Capture authority is sent separately and only to the BUI actor.
/// </summary>
[Serializable, NetSerializable]
public sealed class PhotoCameraBoundUserInterfaceState : BoundUserInterfaceState
{
    public readonly int ShotsRemaining;
    public readonly int FilmCapacity;
    public readonly PhotoCameraStatus Status;
    public readonly TimeSpan CooldownEnds;

    public PhotoCameraBoundUserInterfaceState(
        int shotsRemaining,
        int filmCapacity,
        PhotoCameraStatus status,
        TimeSpan cooldownEnds)
    {
        ShotsRemaining = shotsRemaining;
        FilmCapacity = filmCapacity;
        Status = status;
        CooldownEnds = cooldownEnds;
    }
}

/// <summary>
/// Targeted authority for one camera BUI session. Never stored in component or persistent BUI state.
/// </summary>
[Serializable, NetSerializable]
public sealed class PhotoCameraSessionMessage : BoundUserInterfaceMessage
{
    public readonly PhotoCaptureToken Token;

    public PhotoCameraSessionMessage(PhotoCaptureToken token)
    {
        Token = token;
    }
}

/// <summary>
/// One capture attempt. The BUI owner identifies the camera; the token binds actor, camera and session.
/// </summary>
[Serializable, NetSerializable]
public sealed class TakePhotoMessage : BoundUserInterfaceMessage
{
    public readonly PhotoCaptureToken Token;
    public readonly byte[] EncodedPng;

    public TakePhotoMessage(PhotoCaptureToken token, byte[] encodedPng)
    {
        Token = token;
        EncodedPng = encodedPng;
    }
}

[Serializable, NetSerializable]
public sealed class PhotoCaptureResultMessage : BoundUserInterfaceMessage
{
    public readonly PhotoCaptureResult Result;

    public PhotoCaptureResultMessage(PhotoCaptureResult result)
    {
        Result = result;
    }
}
