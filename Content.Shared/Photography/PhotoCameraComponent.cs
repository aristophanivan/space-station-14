using Robust.Shared.Audio;

namespace Content.Shared.Photography;

/// <summary>
/// A physical camera that creates server-owned photographs through an active BUI session.
/// Runtime capture authority is kept by the server system, not this component state.
/// </summary>
[RegisterComponent]
public sealed partial class PhotoCameraComponent : Component
{
    public const string FilmSlotId = "photo-film";

    [DataField]
    public TimeSpan CaptureCooldown = TimeSpan.FromSeconds(2);

    [DataField]
    public string CameraSerial = string.Empty;

    [DataField]
    public SoundSpecifier ShutterSound = new SoundPathSpecifier("/Audio/Items/photo_shutter.ogg");
}
