using System.Numerics;
using Content.Shared.Containers.ItemSlots;
using Content.Shared.GameTicking;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Photography;
using Content.Shared.UserInterface;
using Robust.Server.GameObjects;
using Robust.Shared.Audio.Systems;
using Robust.Shared.Containers;
using Robust.Shared.Physics.Systems;
using Robust.Shared.Timing;

namespace Content.Server.Photography;

/// <summary>
/// Owns camera sessions and commits processed captures as film-backed physical photographs.
/// </summary>
public sealed partial class PhotoCameraSystem : EntitySystem
{
    private static readonly TimeSpan AttemptCooldown = TimeSpan.FromMilliseconds(500);

    [Dependency] private SharedHandsSystem _hands = default!;
    [Dependency] private SharedAudioSystem _audio = default!;
    [Dependency] private ItemSlotsSystem _itemSlots = default!;
    [Dependency] private PhotoImageProcessorSystem _processor = default!;
    [Dependency] private PhotoImageStorageSystem _storage = default!;
    [Dependency] private SharedPhysicsSystem _physics = default!;
    [Dependency] private IGameTiming _timing = default!;
    [Dependency] private UserInterfaceSystem _ui = default!;

    private readonly Dictionary<EntityUid, CameraSession> _sessions = new();
    private readonly Dictionary<Guid, PendingCapture> _pending = new();
    private readonly HashSet<EntityUid> _processing = new();
    private readonly Dictionary<EntityUid, TimeSpan> _cooldowns = new();
    private readonly Dictionary<EntityUid, TimeSpan> _attemptCooldowns = new();
    private readonly Dictionary<EntityUid, TimeSpan> _actorAttemptCooldowns = new();
    private readonly Dictionary<EntityUid, EntityUid> _waitingForToken = new();
    private readonly List<KeyValuePair<EntityUid, EntityUid>> _waitingSnapshot = new();
    private TimeSpan _nextTokenCheck = TimeSpan.MaxValue;

    internal int PendingCaptureCount => _pending.Count;

    internal bool TryGetSessionToken(EntityUid camera, EntityUid actor, out PhotoCaptureToken token)
    {
        if (_sessions.TryGetValue(camera, out var session) && session.Actor == actor)
        {
            token = session.Token;
            return true;
        }

        token = default;
        return false;
    }

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<PhotoCameraComponent, MapInitEvent>(OnMapInit);
        SubscribeLocalEvent<PhotoCameraComponent, BeforeActivatableUIOpenEvent>(OnBeforeUiOpen);
        SubscribeLocalEvent<PhotoCameraComponent, ComponentShutdown>(OnShutdown);
        SubscribeLocalEvent<PhotoCameraComponent, EntInsertedIntoContainerMessage>(OnFilmInserted);
        SubscribeLocalEvent<PhotoCameraComponent, EntRemovedFromContainerMessage>(OnFilmRemoved);

        Subs.BuiEvents<PhotoCameraComponent>(PhotoCameraUiKey.Key, subs =>
        {
            subs.Event<BoundUIOpenedEvent>(OnUiOpened);
            subs.Event<BoundUIClosedEvent>(OnUiClosed);
            subs.Event<TakePhotoMessage>(OnTakePhoto);
        });

        SubscribeLocalEvent<RoundRestartCleanupEvent>(_ => ClearRoundState());
        _processor.ImageProcessed += OnImageProcessed;
    }

    public override void Shutdown()
    {
        _processor.ImageProcessed -= OnImageProcessed;
        ClearRoundState();
        base.Shutdown();
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        if (_waitingForToken.Count == 0 || _nextTokenCheck > _timing.CurTime)
            return;

        _nextTokenCheck = TimeSpan.MaxValue;
        _waitingSnapshot.Clear();
        _waitingSnapshot.AddRange(_waitingForToken);
        foreach (var (camera, actor) in _waitingSnapshot)
            IssueToken(camera, actor);
    }

    private void OnMapInit(Entity<PhotoCameraComponent> ent, ref MapInitEvent args)
    {
        if (string.IsNullOrWhiteSpace(ent.Comp.CameraSerial))
            ent.Comp.CameraSerial = $"CAM-{Guid.NewGuid():N}"[..12].ToUpperInvariant();
    }

    private void OnBeforeUiOpen(Entity<PhotoCameraComponent> ent, ref BeforeActivatableUIOpenEvent args)
    {
        UpdateUi(ent);
    }

    private void OnUiOpened(EntityUid uid, PhotoCameraComponent component, BoundUIOpenedEvent args)
    {
        if (!PhotoCameraUiKey.Key.Equals(args.UiKey))
            return;

        _physics.SetLinearVelocity(args.Actor, Vector2.Zero);
        IssueToken(uid, args.Actor);
    }

    private void OnUiClosed(EntityUid uid, PhotoCameraComponent component, BoundUIClosedEvent args)
    {
        if (_sessions.TryGetValue(uid, out var session) && session.Actor == args.Actor)
            _sessions.Remove(uid);

        if (_waitingForToken.GetValueOrDefault(uid) == args.Actor)
            _waitingForToken.Remove(uid);
    }

    private void OnShutdown(EntityUid uid, PhotoCameraComponent component, ComponentShutdown args)
    {
        _sessions.Remove(uid);
        _processing.Remove(uid);
        _cooldowns.Remove(uid);
        _attemptCooldowns.Remove(uid);
        _waitingForToken.Remove(uid);

        var pendingToRemove = new List<Guid>();
        foreach (var (requestId, capture) in _pending)
        {
            if (capture.Camera == uid)
                pendingToRemove.Add(requestId);
        }

        foreach (var requestId in pendingToRemove)
            _pending.Remove(requestId);
    }

    private void OnFilmInserted(
        Entity<PhotoCameraComponent> camera,
        ref EntInsertedIntoContainerMessage args)
    {
        if (args.Container.ID == PhotoCameraComponent.FilmSlotId)
        {
            UpdateUi(camera);
            if (_waitingForToken.TryGetValue(camera, out var actor))
                IssueToken(camera, actor);
        }
    }

    private void OnFilmRemoved(
        Entity<PhotoCameraComponent> camera,
        ref EntRemovedFromContainerMessage args)
    {
        if (args.Container.ID == PhotoCameraComponent.FilmSlotId)
        {
            if (_sessions.Remove(camera, out var session))
                _waitingForToken[camera] = session.Actor;
            UpdateUi(camera);
        }
    }

    private void OnTakePhoto(EntityUid uid, PhotoCameraComponent component, TakePhotoMessage message)
    {
        if (!PhotoCameraUiKey.Key.Equals(message.UiKey) ||
            !_ui.IsUiOpen(uid, PhotoCameraUiKey.Key, message.Actor) ||
            !_sessions.TryGetValue(uid, out var session) ||
            session.Actor != message.Actor ||
            session.Token != message.Token)
        {
            SendResult(uid, message.Actor, PhotoCaptureResult.InvalidSession, issueToken: false);
            return;
        }

        // Consume authority before any expensive or stateful work.
        _sessions.Remove(uid);

        if (!_hands.IsHolding(message.Actor, uid))
        {
            SendResult(uid, message.Actor, PhotoCaptureResult.CameraNotHeld);
            return;
        }

        if (_processing.Contains(uid))
        {
            SendResult(uid, message.Actor, PhotoCaptureResult.Busy);
            return;
        }

        if (_cooldowns.GetValueOrDefault(uid) > _timing.CurTime)
        {
            SendResult(uid, message.Actor, PhotoCaptureResult.Cooldown);
            return;
        }

        if (_attemptCooldowns.GetValueOrDefault(uid) > _timing.CurTime)
        {
            SendResult(uid, message.Actor, PhotoCaptureResult.Cooldown);
            return;
        }

        if (_actorAttemptCooldowns.GetValueOrDefault(message.Actor) > _timing.CurTime)
        {
            SendResult(uid, message.Actor, PhotoCaptureResult.Cooldown);
            return;
        }

        // Authorized attempts are rate-limited before film checks or decode enqueue.
        _attemptCooldowns[uid] = _timing.CurTime + AttemptCooldown;
        _actorAttemptCooldowns[message.Actor] = _timing.CurTime + AttemptCooldown;

        if (!TryGetFilm(uid, out _, out var film))
        {
            SendResult(uid, message.Actor, PhotoCaptureResult.NoFilm);
            return;
        }

        if (film.ShotsRemaining <= 0)
        {
            SendResult(uid, message.Actor, PhotoCaptureResult.FilmEmpty);
            return;
        }

        if (message.EncodedPng.Length == 0 ||
            message.EncodedPng.Length > PhotographyConstants.MaxEncodedBytes)
        {
            SendResult(uid, message.Actor, PhotoCaptureResult.InvalidImage);
            return;
        }

        if (!_processor.TryQueue(message.EncodedPng, out var requestId, out var queueError))
        {
            SendResult(uid, message.Actor, queueError == PhotoImageQueueError.QueueFull
                ? PhotoCaptureResult.QueueFull
                : PhotoCaptureResult.InvalidImage);
            return;
        }

        _processing.Add(uid);
        _pending.Add(requestId, new PendingCapture(uid, message.Actor));
        UpdateUi((uid, component));
    }

    private void OnImageProcessed(PhotoImageProcessingResult result)
    {
        if (!_pending.Remove(result.RequestId, out var capture))
            return;

        _processing.Remove(capture.Camera);

        if (!TryComp(capture.Camera, out PhotoCameraComponent? camera) ||
            !_ui.IsUiOpen(capture.Camera, PhotoCameraUiKey.Key, capture.Actor) ||
            !_hands.IsHolding(capture.Actor, capture.Camera))
        {
            return;
        }

        if (result.Error != PhotoImageProcessingError.None || result.EncodedPng == null)
        {
            SendResult(capture.Camera, capture.Actor, PhotoCaptureResult.InvalidImage);
            UpdateUi((capture.Camera, camera));
            return;
        }

        if (!TryGetFilm(capture.Camera, out var filmUid, out var film))
        {
            SendResult(capture.Camera, capture.Actor, PhotoCaptureResult.NoFilm);
            UpdateUi((capture.Camera, camera));
            return;
        }

        if (film.ShotsRemaining <= 0)
        {
            SendResult(capture.Camera, capture.Actor, PhotoCaptureResult.FilmEmpty);
            UpdateUi((capture.Camera, camera));
            return;
        }

        // Validate the physical output boundary before committing immutable storage.
        var photo = Spawn("Photograph", Transform(capture.Actor).Coordinates);
        var photoComponent = Comp<PhotoComponent>(photo);

        if (!_storage.TryAddCanonicalImage(
                result.EncodedPng,
                result.Size,
                PhotoOrigin.Camera,
                _timing.CurTime,
                camera.CameraSerial,
                null,
                out var imageId,
                out _))
        {
            QueueDel(photo);
            SendResult(capture.Camera, capture.Actor, PhotoCaptureResult.StorageFull);
            return;
        }

        photoComponent.ImageId = imageId;
        photoComponent.IsCopy = false;
        Dirty(photo, photoComponent);

        film.ShotsRemaining--;
        Dirty(filmUid, film);

        _hands.TryPickupAnyHand(capture.Actor, photo);

        _cooldowns[capture.Camera] = _timing.CurTime + camera.CaptureCooldown;
        _audio.PlayPvs(camera.ShutterSound, capture.Camera);
        SendResult(capture.Camera, capture.Actor, PhotoCaptureResult.Success);
        UpdateUi((capture.Camera, camera));
    }

    private bool TryGetFilm(EntityUid camera, out EntityUid filmUid, out PhotoFilmComponent film)
    {
        filmUid = default;
        film = default!;
        if (!_itemSlots.TryGetSlot(camera, PhotoCameraComponent.FilmSlotId, out var slot) ||
            slot.Item is not { } item ||
            !TryComp(item, out PhotoFilmComponent? filmComponent))
        {
            return false;
        }

        filmUid = item;
        film = filmComponent;
        return true;
    }

    private void IssueToken(EntityUid camera, EntityUid actor)
    {
        if (!_ui.IsUiOpen(camera, PhotoCameraUiKey.Key, actor) || !_hands.IsHolding(actor, camera))
        {
            _waitingForToken.Remove(camera);
            return;
        }

        var wakeAt = _cooldowns.GetValueOrDefault(camera);
        var attemptWakeAt = _attemptCooldowns.GetValueOrDefault(camera);
        var actorWakeAt = _actorAttemptCooldowns.GetValueOrDefault(actor);
        if (attemptWakeAt > wakeAt)
            wakeAt = attemptWakeAt;
        if (actorWakeAt > wakeAt)
            wakeAt = actorWakeAt;

        if (_processing.Contains(camera) ||
            wakeAt > _timing.CurTime ||
            !TryGetFilm(camera, out _, out var film) || film.ShotsRemaining <= 0)
        {
            _waitingForToken[camera] = actor;
            if (wakeAt > _timing.CurTime && wakeAt < _nextTokenCheck)
                _nextTokenCheck = wakeAt;

            if (TryComp(camera, out PhotoCameraComponent? waitingComponent))
                UpdateUi((camera, waitingComponent));
            return;
        }

        if (actorWakeAt <= _timing.CurTime)
            _actorAttemptCooldowns.Remove(actor);

        var token = PhotoCaptureToken.New();
        _sessions[camera] = new CameraSession(actor, token);
        _waitingForToken.Remove(camera);
        _ui.ServerSendUiMessage(
            camera,
            PhotoCameraUiKey.Key,
            new PhotoCameraSessionMessage(token),
            actor);
    }

    private void SendResult(
        EntityUid camera,
        EntityUid actor,
        PhotoCaptureResult result,
        bool issueToken = true)
    {
        _ui.ServerSendUiMessage(
            camera,
            PhotoCameraUiKey.Key,
            new PhotoCaptureResultMessage(result),
            actor);

        if (issueToken)
            IssueToken(camera, actor);

        if (TryComp(camera, out PhotoCameraComponent? component))
            UpdateUi((camera, component));
    }

    private void UpdateUi(Entity<PhotoCameraComponent> camera)
    {
        var shots = 0;
        var capacity = 0;
        PhotoCameraStatus status;

        if (_processing.Contains(camera))
        {
            status = PhotoCameraStatus.Processing;
        }
        else if (!TryGetFilm(camera, out _, out var film))
        {
            status = PhotoCameraStatus.NoFilm;
        }
        else
        {
            shots = film.ShotsRemaining;
            capacity = film.Capacity;
            status = shots > 0 ? PhotoCameraStatus.Ready : PhotoCameraStatus.FilmEmpty;
        }

        var cooldownEnds = _cooldowns.GetValueOrDefault(camera);
        var attemptEnds = _attemptCooldowns.GetValueOrDefault(camera);
        if (attemptEnds > cooldownEnds)
            cooldownEnds = attemptEnds;
        if (_waitingForToken.TryGetValue(camera, out var actor))
        {
            var actorAttemptEnds = _actorAttemptCooldowns.GetValueOrDefault(actor);
            if (actorAttemptEnds > cooldownEnds)
                cooldownEnds = actorAttemptEnds;
        }

        _ui.SetUiState(camera.Owner, PhotoCameraUiKey.Key, new PhotoCameraBoundUserInterfaceState(
            shots,
            capacity,
            status,
            cooldownEnds));
    }

    private void ClearRoundState()
    {
        _sessions.Clear();
        _pending.Clear();
        _processing.Clear();
        _cooldowns.Clear();
        _attemptCooldowns.Clear();
        _actorAttemptCooldowns.Clear();
        _waitingForToken.Clear();
        _waitingSnapshot.Clear();
        _nextTokenCheck = TimeSpan.MaxValue;
    }

    private readonly record struct CameraSession(EntityUid Actor, PhotoCaptureToken Token);
    private readonly record struct PendingCapture(EntityUid Camera, EntityUid Actor);
}
