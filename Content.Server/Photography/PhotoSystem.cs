using Content.Shared.Photography;
using Content.Shared.UserInterface;
using Robust.Server.GameObjects;

namespace Content.Server.Photography;

/// <summary>
/// Authorizes photograph viewing and serves image data only to the requesting BUI actor.
/// </summary>
public sealed partial class PhotoSystem : EntitySystem
{
    [Dependency] private PhotoImageStorageSystem _storage = default!;
    [Dependency] private UserInterfaceSystem _ui = default!;

    private readonly HashSet<(EntityUid Photo, EntityUid Actor)> _servedSessions = new();

    internal int ServedSessionCount => _servedSessions.Count;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<PhotoComponent, BeforeActivatableUIOpenEvent>(OnBeforeUiOpen);

        Subs.BuiEvents<PhotoComponent>(PhotoUiKey.Key, subs =>
        {
            subs.Event<RequestPhotoImageMessage>(OnImageRequested);
            subs.Event<BoundUIOpenedEvent>(OnUiOpened);
            subs.Event<BoundUIClosedEvent>(OnUiClosed);
        });
    }

    private void OnUiOpened(EntityUid uid, PhotoComponent component, BoundUIOpenedEvent args)
    {
        _servedSessions.Remove((uid, args.Actor));
    }

    private void OnUiClosed(EntityUid uid, PhotoComponent component, BoundUIClosedEvent args)
    {
        _servedSessions.Remove((uid, args.Actor));
    }

    private void OnBeforeUiOpen(Entity<PhotoComponent> ent, ref BeforeActivatableUIOpenEvent args)
    {
        _ui.SetUiState(ent.Owner, PhotoUiKey.Key, BuildUiState(ent));
    }

    private PhotoBoundUserInterfaceState BuildUiState(Entity<PhotoComponent> ent)
    {
        PhotoDisplayMetadata? metadata = null;
        if (ent.Comp.ImageId is { } imageId && _storage.TryGetMetadata(imageId, out var imageMetadata))
            metadata = imageMetadata.ToDisplayMetadata();

        return new PhotoBoundUserInterfaceState(ent.Comp.ImageId, metadata);
    }

    private void OnImageRequested(
        EntityUid uid,
        PhotoComponent component,
        RequestPhotoImageMessage message)
    {
        if (!PhotoUiKey.Key.Equals(message.UiKey) ||
            !_ui.IsUiOpen(uid, PhotoUiKey.Key, message.Actor) ||
            _servedSessions.Contains((uid, message.Actor)) ||
            component.ImageId is not { } imageId ||
            !_storage.TryGetImage(imageId, out var image))
        {
            return;
        }

        _servedSessions.Add((uid, message.Actor));

        _ui.ServerSendUiMessage(
            uid,
            PhotoUiKey.Key,
            new PhotoImageDataMessage(imageId, image.EncodedPng.ToArray()),
            message.Actor);
    }

}
