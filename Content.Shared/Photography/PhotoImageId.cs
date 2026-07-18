using Robust.Shared.Serialization;

namespace Content.Shared.Photography;

/// <summary>
/// Identifies one immutable photograph record for the duration of a round.
/// </summary>
[Serializable, NetSerializable]
public readonly record struct PhotoImageId(Guid Value)
{
    public static PhotoImageId New() => new(Guid.NewGuid());

    public override string ToString() => Value.ToString();
}
