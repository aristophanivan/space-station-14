using System.Numerics;
using Content.IntegrationTests.Fixtures.Attributes;
using Content.IntegrationTests.Tests.Interaction;
using Content.Server.Photography;
using Content.Shared.Containers.ItemSlots;
using Content.Shared.Photography;
using Robust.Client.Input;
using Robust.Shared.GameObjects;
using Robust.Shared.Input;
using Robust.Shared.Physics.Components;
using Robust.Shared.Physics.Systems;

namespace Content.IntegrationTests.Tests.Photography;

[TestOf(typeof(PhotoCameraSystem))]
[TestOf(typeof(PhotoImageProcessorSystem))]
public sealed class PhotoCameraInteractionTests : InteractionTest
{
    [TestPrototypes]
    private const string TestPrototypes = """
- type: entity
  id: PhotoCameraInteractionTestMob
  parent: InteractionTestMob
  components:
  - type: InputMover
  - type: Physics
    bodyType: KinematicController
""";

    protected override string PlayerPrototype => "PhotoCameraInteractionTestMob";

    private static readonly byte[] ValidPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");

    [SidedDependency(Side.Server)] private readonly PhotoCameraSystem _cameraSystem = null!;
    [SidedDependency(Side.Server)] private readonly ItemSlotsSystem _itemSlots = null!;
    [SidedDependency(Side.Server)] private readonly SharedPhysicsSystem _physics = null!;
    [SidedDependency(Side.Client)] private readonly IInputManager _inputManager = null!;

    [Test]
    public async Task OpeningViewfinderReleasesHeldMovementInput()
    {
        var cameraNet = await PlaceInHands("PhotoCamera");

        await Client.WaitPost(() =>
        {
            _inputManager.KeyDown(new KeyEventArgs(
                Keyboard.Key.W,
                false,
                false,
                false,
                false,
                false,
                0));
            Assert.That(_inputManager.IsKeyDown(Keyboard.Key.W), Is.True);
            Assert.That(_inputManager.DownKeyFunctions, Does.Contain(EngineKeyFunctions.MoveUp));
        });
        await Server.WaitAssertion(() =>
            Assert.That(_physics.SetLinearVelocity(SPlayer, new Vector2(2f, 1f), wakeBody: false), Is.True));

        await Activate(cameraNet);
        Assert.That(IsUiOpen(PhotoCameraUiKey.Key), Is.True);
        await RunTicks(5);

        await Server.WaitAssertion(() =>
            Assert.That(SEntMan.GetComponent<PhysicsComponent>(SPlayer).LinearVelocity, Is.EqualTo(Vector2.Zero)));

        await Client.WaitAssertion(() =>
        {
            Assert.That(_inputManager.IsKeyDown(Keyboard.Key.W), Is.False);
            Assert.That(_inputManager.DownKeyFunctions, Does.Not.Contain(EngineKeyFunctions.MoveUp));
        });

        await CloseBui(PhotoCameraUiKey.Key, cameraNet);
    }

    [Test]
    public async Task CaptureRequiresFilmAndConsumesNoFrameOnInvalidImage()
    {
        var cameraNet = await PlaceInHands("PhotoCamera");
        var camera = SEntMan.GetEntity(cameraNet);

        await Activate(cameraNet);
        await RunTicks(15);
        Assert.That(IsUiOpen(PhotoCameraUiKey.Key), Is.True);
        Assert.That(_cameraSystem.TryGetSessionToken(camera, SPlayer, out _), Is.False);
        Assert.That(CountPhotographs(), Is.Zero);

        var filmNet = await Spawn("PhotoFilm");
        var filmUid = SEntMan.GetEntity(filmNet);
        await Server.WaitAssertion(() =>
        {
            Assert.That(_itemSlots.TryInsert(
                camera,
                PhotoCameraComponent.FilmSlotId,
                filmUid,
                SPlayer), Is.True);
        });
        await RunTicks(15);

        var invalidToken = GetToken(camera);
        await SendBui(
            PhotoCameraUiKey.Key,
            new TakePhotoMessage(invalidToken,
                new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3 }),
            cameraNet);
        await RunTicks(20);

        var film = SEntMan.GetComponent<PhotoFilmComponent>(filmUid);
        Assert.That(film.ShotsRemaining, Is.EqualTo(10));
        Assert.That(CountPhotographs(), Is.Zero);
        Assert.That(_cameraSystem.PendingCaptureCount, Is.Zero);

        await CloseBui(PhotoCameraUiKey.Key, cameraNet);
    }

    [Test]
    public async Task SuccessfulCaptureConsumesOneFrameAndTokenCannotReplay()
    {
        var cameraNet = await PlaceInHands("PhotoCamera");
        var camera = SEntMan.GetEntity(cameraNet);
        var filmNet = await Spawn("PhotoFilm");
        var filmUid = SEntMan.GetEntity(filmNet);
        await Server.WaitAssertion(() =>
        {
            Assert.That(_itemSlots.TryInsert(
                camera,
                PhotoCameraComponent.FilmSlotId,
                filmUid,
                SPlayer), Is.True);
        });

        await Activate(cameraNet);
        await RunTicks(15);
        var token = GetToken(camera);
        await SendBui(PhotoCameraUiKey.Key, new TakePhotoMessage(token, ValidPng), cameraNet);
        await RunTicks(30);

        Assert.That(SEntMan.GetComponent<PhotoFilmComponent>(filmUid).ShotsRemaining, Is.EqualTo(9));
        Assert.That(CountPhotographs(), Is.EqualTo(1));

        await SendBui(PhotoCameraUiKey.Key, new TakePhotoMessage(token, ValidPng), cameraNet);
        await RunTicks(20);

        Assert.That(SEntMan.GetComponent<PhotoFilmComponent>(filmUid).ShotsRemaining, Is.EqualTo(9));
        Assert.That(CountPhotographs(), Is.EqualTo(1));
        Assert.That(_cameraSystem.PendingCaptureCount, Is.Zero);

        await CloseBui(PhotoCameraUiKey.Key, cameraNet);
    }

    private PhotoCaptureToken GetToken(EntityUid camera)
    {
        Assert.That(_cameraSystem.TryGetSessionToken(camera, SPlayer, out var token), Is.True);
        return token;
    }

    private int CountPhotographs()
    {
        var count = 0;
        var query = SEntMan.EntityQueryEnumerator<PhotoComponent>();
        while (query.MoveNext(out _, out _))
            count++;

        return count;
    }
}
