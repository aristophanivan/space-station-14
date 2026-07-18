using Content.Client.Photography;
using Content.IntegrationTests.Fixtures.Attributes;
using Content.IntegrationTests.Tests.Interaction;
using Content.Server.Photography;
using Content.Shared.Photography;

namespace Content.IntegrationTests.Tests.Photography;

[TestOf(typeof(PhotoSystem))]
public sealed class PhotoInteractionTests : InteractionTest
{
    private static readonly byte[] ValidPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");

    [SidedDependency(Side.Server)] private readonly PhotoImageStorageSystem _storage = null!;
    [SidedDependency(Side.Server)] private readonly PhotoSystem _photoSystem = null!;
    [SidedDependency(Side.Client)] private readonly PhotoImageCacheSystem _cache = null!;

    [Test]
    public async Task OpenViewerRequestsAndCachesImageForItsActor()
    {
        await SpawnTarget("Photograph");

        PhotoImageId imageId = default;
        await Server.WaitAssertion(() =>
        {
            Assert.That(_storage.TryAddCanonicalImage(
                ValidPng,
                (1, 1),
                PhotoOrigin.Camera,
                TimeSpan.Zero,
                "TEST-CAMERA",
                null,
                out imageId,
                out var error), Is.True, error.ToString());

            var photo = SEntMan.GetEntity(Target!.Value);
            var component = SEntMan.GetComponent<PhotoComponent>(photo);
            component.ImageId = imageId;
            SEntMan.Dirty(photo, component);
        });

        await Activate();
        await RunTicks(15);

        Assert.That(IsUiOpen(PhotoUiKey.Key), Is.True);
        await Client.WaitAssertion(() =>
        {
            Assert.That(_cache.TryGet(imageId, out var texture), Is.True);
            Assert.That(texture.Size.X, Is.EqualTo(1));
            Assert.That(texture.Size.Y, Is.EqualTo(1));
        });

        Assert.That(_photoSystem.ServedSessionCount, Is.EqualTo(1));
        await SendBui(PhotoUiKey.Key, new RequestPhotoImageMessage());
        await SendBui(PhotoUiKey.Key, new RequestPhotoImageMessage());
        Assert.That(_photoSystem.ServedSessionCount, Is.EqualTo(1),
            "Only one image response may be served per open viewer session.");

        await Client.WaitAssertion(() =>
        {
            _cache.Clear();
            Assert.That(_cache.TryGet(imageId, out _), Is.False);
        });
        await CloseBui(PhotoUiKey.Key);
        Assert.That(_photoSystem.ServedSessionCount, Is.Zero);
        await Server.WaitAssertion(() => _storage.ClearRoundCache());
    }
}
