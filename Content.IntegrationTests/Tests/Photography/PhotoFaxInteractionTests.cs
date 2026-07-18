using System.IO;
using System.Linq;
using Content.IntegrationTests.Fixtures.Attributes;
using Content.IntegrationTests.Tests.Interaction;
using Content.Server.Fax;
using Content.Server.Forensics;
using Content.Server.Photography;
using Content.Shared.Containers.ItemSlots;
using Content.Shared.Fax;
using Content.Shared.Fax.Components;
using Content.Shared.Photography;
using Content.Shared.Power.Components;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.PixelFormats;
using Robust.Shared.Maths;

namespace Content.IntegrationTests.Tests.Photography;

[TestOf(typeof(FaxSystem))]
[TestOf(typeof(ForensicScannerSystem))]
public sealed class PhotoFaxInteractionTests : InteractionTest
{
    private static readonly byte[] ValidPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");

    [SidedDependency(Side.Server)] private readonly FaxSystem _fax = null!;
    [SidedDependency(Side.Server)] private readonly ForensicScannerSystem _forensicScanner = null!;
    [SidedDependency(Side.Server)] private readonly ItemSlotsSystem _itemSlots = null!;
    [SidedDependency(Side.Server)] private readonly PhotoImageStorageSystem _storage = null!;

    [Test]
    public async Task FaxCopiesPhysicalPhotographWithoutDuplicatingImageBlob()
    {
        var faxNet = await Spawn("FaxMachineBase");
        var photoNet = await Spawn("Photograph");
        var fax = SEntMan.GetEntity(faxNet);
        var sourcePhoto = SEntMan.GetEntity(photoNet);
        PhotoImageId imageId = default;

        await Server.WaitAssertion(() =>
        {
            _storage.ClearRoundCache();
            Assert.That(_storage.TryAddCanonicalImage(
                ValidPng,
                (1, 1),
                PhotoOrigin.Camera,
                TimeSpan.Zero,
                "FAX-TEST",
                null,
                out imageId,
                out var error), Is.True, error.ToString());

            var photo = SEntMan.GetComponent<PhotoComponent>(sourcePhoto);
            photo.ImageId = imageId;
            photo.IsCopy = false;
            SEntMan.Dirty(sourcePhoto, photo);

            Assert.That(_itemSlots.TryInsert(fax, "Paper", sourcePhoto, SPlayer), Is.True);

            var faxComponent = SEntMan.GetComponent<FaxMachineComponent>(fax);
            _fax.Copy(fax, faxComponent, new FaxCopyMessage { Actor = SPlayer });
            Assert.That(faxComponent.PrintingQueue, Has.Count.EqualTo(1));
            var queued = faxComponent.PrintingQueue.Peek();
            Assert.That(queued.Kind, Is.EqualTo(FaxPrintoutKind.Photograph));
            Assert.That(queued.PhotoImageId, Is.EqualTo(imageId));
            Assert.That(queued.PhotoIsCopy, Is.True);

            _fax.SpawnPrintoutFromQueue(fax, faxComponent);
            Assert.That(_storage.ImageCount, Is.EqualTo(1),
                "Fax copies must reuse the immutable image record.");

            var copies = 0;
            var query = SEntMan.EntityQueryEnumerator<PhotoComponent>();
            while (query.MoveNext(out var uid, out var candidate))
            {
                if (uid == sourcePhoto)
                    continue;

                Assert.That(candidate.ImageId, Is.EqualTo(imageId));
                Assert.That(candidate.IsCopy, Is.True);
                copies++;
            }

            Assert.That(copies, Is.EqualTo(1));
            _storage.ClearRoundCache();
        });
    }

    [Test]
    public async Task FaxUploadsJpegAndPrintsAnOriginalPhotograph()
    {
        await SpawnTarget("FaxMachineBase");
        var fax = STarget!.Value;
        await Server.WaitPost(() => SEntMan.RemoveComponent<ActivatableUIRequiresPowerComponent>(fax));
        await RunTicks(5);

        await Activate();
        Assert.That(IsUiOpen(FaxUiKey.Key), Is.True);

        await SendBui(FaxUiKey.Key, new FaxImageFileMessage("uploaded test", CreateJpeg()));
        await RunTicks(40);

        await Server.WaitAssertion(() =>
        {
            var faxComponent = SEntMan.GetComponent<FaxMachineComponent>(fax);
            Assert.That(faxComponent.PrintingQueue, Has.Count.EqualTo(1));
            var queued = faxComponent.PrintingQueue.Peek();
            Assert.That(queued.Kind, Is.EqualTo(FaxPrintoutKind.Photograph));
            Assert.That(queued.PhotoIsCopy, Is.False);
            Assert.That(queued.PhotoImageId, Is.Not.Null);

            var imageId = queued.PhotoImageId!.Value;
            Assert.That(_storage.TryGetMetadata(imageId, out var metadata), Is.True);
            Assert.That(metadata.Origin, Is.EqualTo(PhotoOrigin.Uploaded));

            _fax.SpawnPrintoutFromQueue(fax, faxComponent);
            var printed = SEntMan.EntityQuery<PhotoComponent>()
                .Single(photo => photo.ImageId == imageId);
            Assert.That(printed.IsCopy, Is.False);
            _storage.ClearRoundCache();
        });

        await CloseBui(FaxUiKey.Key);
    }

    [Test]
    public async Task ForensicScannerGetsHiddenPhotographMetadata()
    {
        var photoNet = await Spawn("Photograph");
        var photoUid = SEntMan.GetEntity(photoNet);

        await Server.WaitAssertion(() =>
        {
            _storage.ClearRoundCache();
            Assert.That(_storage.TryAddCanonicalImage(
                ValidPng,
                (1, 1),
                PhotoOrigin.Uploaded,
                TimeSpan.Zero,
                null,
                null,
                out var imageId,
                out var error), Is.True, error.ToString());

            var photo = SEntMan.GetComponent<PhotoComponent>(photoUid);
            photo.ImageId = imageId;
            photo.IsCopy = true;

            var data = _forensicScanner.GetPhotoData(photoUid);
            Assert.That(data, Is.Not.Null);
            Assert.That(data!.Size, Is.EqualTo(new Vector2i(1, 1)));
            Assert.That(data.Origin, Is.EqualTo(PhotoOrigin.Uploaded));
            Assert.That(data.IsCopy, Is.True);
            _storage.ClearRoundCache();
        });
    }

    private static byte[] CreateJpeg()
    {
        using var image = new Image<Rgba32>(1, 1, SixLabors.ImageSharp.Color.Black);
        using var output = new MemoryStream();
        image.Save(output, new JpegEncoder());
        return output.ToArray();
    }
}
