using Robust.Shared.GameStates;

namespace Content.Shared.Photography;

/// <summary>
/// A physical photograph that refers to immutable round-scoped image data.
/// </summary>
[RegisterComponent, NetworkedComponent, AutoGenerateComponentState]
public sealed partial class PhotoComponent : Component
{
    /// <summary>
    /// The server-owned image record displayed by this photograph.
    /// </summary>
    [DataField, AutoNetworkedField]
    public PhotoImageId? ImageId;

    /// <summary>
    /// Whether this entity is a physical copy of another photograph.
    /// </summary>
    [DataField, AutoNetworkedField]
    public bool IsCopy;
}
