using Robust.Shared.GameStates;

namespace Content.Shared.Photography;

/// <summary>
/// A physical film cassette. A frame is committed only after image processing and storage succeed.
/// </summary>
[RegisterComponent, NetworkedComponent, AutoGenerateComponentState]
public sealed partial class PhotoFilmComponent : Component
{
    [DataField, AutoNetworkedField]
    public int ShotsRemaining = 10;

    [DataField, AutoNetworkedField]
    public int Capacity = 10;
}
