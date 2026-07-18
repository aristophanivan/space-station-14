using System.Collections.Generic;
using System.Reflection;
using System.Security.Cryptography;
using System.Linq;
using Content.Client.Photography;
using Content.IntegrationTests.Fixtures;
using Content.IntegrationTests.Fixtures.Attributes;
using Content.Server.Photography;
using Content.Shared.CCVar;
using Content.Shared.Paper;
using Content.Shared.Photography;
using Content.Shared.UserInterface;
using Robust.Shared.GameObjects;
using Robust.Shared.Maths;
using Robust.Shared.Prototypes;

namespace Content.IntegrationTests.Tests.Photography;

[TestOf(typeof(PhotoImageStorageSystem))]
[TestOf(typeof(PhotoImageCacheSystem))]
public sealed class PhotographyTests : GameTest
{
    private static readonly EntProtoId PhotoPrototype = "Photograph";

    private static readonly byte[] ValidPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");

    private static readonly string[] RemovedPrototypeIds =
    [
        "TravelCamera",
        "PhotographBlack",
        "PhotographRed",
        "PhotographBlue",
        "PhotographGreen",
        "PhotographYellow",
        "PhotographPurple",
        "PhotographRainbow",
    ];

    [SidedDependency(Side.Server)] private readonly PhotoImageStorageSystem _storage = null!;
    [SidedDependency(Side.Client)] private readonly PhotoImageCacheSystem _cache = null!;

    [Test]
    public async Task StorageIsImmutableDeduplicatedAndRoundScoped()
    {
        await Server.WaitAssertion(() =>
        {
            _storage.ClearRoundCache();
            var source = (byte[]) ValidPng.Clone();
            var expected = (byte[]) source.Clone();

            Assert.That(_storage.TryAddCanonicalImage(
                source,
                (1, 1),
                PhotoOrigin.Camera,
                TimeSpan.FromMinutes(1),
                "CAM-0001",
                null,
                out var firstId,
                out var firstError), Is.True, firstError.ToString());

            source[0] = 0;
            Assert.That(_storage.TryGetImage(firstId, out var first), Is.True);
            Assert.That(first.EncodedPng.Span.SequenceEqual(expected), Is.True);

            var returnedCopy = first.EncodedPng.ToArray();
            returnedCopy[0] = 0;
            Assert.That(_storage.TryGetImage(firstId, out var secondRead), Is.True);
            Assert.That(secondRead.EncodedPng.Span.SequenceEqual(expected), Is.True);

            Assert.That(_storage.TryAddCanonicalImage(
                expected,
                (1, 1),
                PhotoOrigin.Uploaded,
                TimeSpan.FromMinutes(2),
                null,
                null,
                out var secondId,
                out var secondError), Is.True, secondError.ToString());

            using (Assert.EnterMultipleScope())
            {
                Assert.That(secondId, Is.Not.EqualTo(firstId));
                Assert.That(_storage.ImageCount, Is.EqualTo(2));
                Assert.That(_storage.BlobCount, Is.EqualTo(1));
                Assert.That(_storage.StoredBlobBytes, Is.EqualTo(expected.Length));
            }

            Assert.That(_storage.TryGetImage(secondId, out var second), Is.True);
            Assert.That(second.Metadata.Origin, Is.EqualTo(PhotoOrigin.Uploaded));
            Assert.That(first.Metadata.Origin, Is.EqualTo(PhotoOrigin.Camera));

            _storage.ClearRoundCache();
            using (Assert.EnterMultipleScope())
            {
                Assert.That(_storage.TryGetImage(firstId, out _), Is.False);
                Assert.That(_storage.ImageCount, Is.Zero);
                Assert.That(_storage.BlobCount, Is.Zero);
                Assert.That(_storage.StoredBlobBytes, Is.Zero);
            }
        });
    }

    [Test]
    public async Task StorageEnforcesEnvelopeAndRoundBudget()
    {
        await Server.WaitAssertion(() =>
        {
            _storage.ClearRoundCache();

            Assert.That(_storage.TryAddCanonicalImage(
                new byte[] { 1, 2, 3 },
                (1, 1),
                PhotoOrigin.Camera,
                TimeSpan.Zero,
                null,
                null,
                out _,
                out var invalidPng), Is.False);
            Assert.That(invalidPng, Is.EqualTo(PhotoImageStorageError.InvalidPng));

            Assert.That(_storage.TryAddCanonicalImage(
                ValidPng,
                (0, 1),
                PhotoOrigin.Camera,
                TimeSpan.Zero,
                null,
                null,
                out _,
                out var invalidSize), Is.False);
            Assert.That(invalidSize, Is.EqualTo(PhotoImageStorageError.InvalidSize));

            var originalBudget = Server.CfgMan.GetCVar(CCVars.PhotographyRoundStorageBytes);
            try
            {
                Server.CfgMan.SetCVar(CCVars.PhotographyRoundStorageBytes, ValidPng.Length - 1);
                Assert.That(_storage.TryAddCanonicalImage(
                    ValidPng,
                    (1, 1),
                    PhotoOrigin.Camera,
                    TimeSpan.Zero,
                    null,
                    null,
                    out _,
                    out var budgetError), Is.False);
                Assert.That(budgetError, Is.EqualTo(PhotoImageStorageError.RoundStorageFull));
            }
            finally
            {
                Server.CfgMan.SetCVar(CCVars.PhotographyRoundStorageBytes, originalBudget);
                _storage.ClearRoundCache();
            }

            var originalRecordLimit = Server.CfgMan.GetCVar(CCVars.PhotographyMaxImageRecords);
            try
            {
                Server.CfgMan.SetCVar(CCVars.PhotographyMaxImageRecords, 1);
                Assert.That(_storage.TryAddCanonicalImage(
                    ValidPng,
                    (1, 1),
                    PhotoOrigin.Camera,
                    TimeSpan.Zero,
                    null,
                    null,
                    out _,
                    out _), Is.True);
                Assert.That(_storage.TryAddCanonicalImage(
                    ValidPng,
                    (1, 1),
                    PhotoOrigin.Camera,
                    TimeSpan.Zero,
                    null,
                    null,
                    out _,
                    out var recordError), Is.False);
                Assert.That(recordError, Is.EqualTo(PhotoImageStorageError.RoundRecordLimit));
            }
            finally
            {
                Server.CfgMan.SetCVar(CCVars.PhotographyMaxImageRecords, originalRecordLimit);
                _storage.ClearRoundCache();
            }
        });
    }

    [Test]
    public async Task ClientCacheCoalescesAndValidatesRequests()
    {
        await Client.WaitAssertion(() =>
        {
            _cache.Clear();
            var id = PhotoImageId.New();
            var metadata = new PhotoDisplayMetadata(
                (1, 1),
                Convert.ToHexString(SHA256.HashData(ValidPng)));

            var loaded = 0;
            var failures = new Dictionary<PhotoImageId, PhotoImageCacheFailure>();
            _cache.ImageLoaded += OnLoaded;
            _cache.ImageFailed += OnFailed;
            try
            {
                Assert.That(_cache.BeginRequest(id, metadata), Is.True);
                Assert.That(_cache.BeginRequest(id, metadata), Is.False);

                _cache.Accept(new PhotoImageDataMessage(id, (byte[]) ValidPng.Clone()));
                using (Assert.EnterMultipleScope())
                {
                    Assert.That(loaded, Is.EqualTo(1));
                    Assert.That(_cache.TryGet(id, out var texture), Is.True);
                    Assert.That(texture.Size, Is.EqualTo(new Vector2i(1, 1)));
                    Assert.That(_cache.BeginRequest(id, metadata), Is.False);
                }

                var invalidHashId = PhotoImageId.New();
                var invalidHashMetadata = new PhotoDisplayMetadata(
                    (1, 1),
                    new string('0', 64));
                Assert.That(_cache.BeginRequest(invalidHashId, invalidHashMetadata), Is.True);
                _cache.Accept(new PhotoImageDataMessage(invalidHashId, (byte[]) ValidPng.Clone()));
                Assert.That(failures[invalidHashId], Is.EqualTo(PhotoImageCacheFailure.InvalidHash));
                Assert.That(_cache.TryGet(invalidHashId, out _), Is.False);

                var invalidDimensionsId = PhotoImageId.New();
                var invalidDimensionsMetadata = new PhotoDisplayMetadata(
                    (2, 2),
                    Convert.ToHexString(SHA256.HashData(ValidPng)));
                Assert.That(_cache.BeginRequest(invalidDimensionsId, invalidDimensionsMetadata), Is.True);
                _cache.Accept(new PhotoImageDataMessage(invalidDimensionsId, (byte[]) ValidPng.Clone()));
                Assert.That(failures[invalidDimensionsId], Is.EqualTo(PhotoImageCacheFailure.InvalidDimensions));
                Assert.That(_cache.TryGet(invalidDimensionsId, out _), Is.False);

                _cache.Clear();
                Assert.That(_cache.TryGet(id, out _), Is.False);
            }
            finally
            {
                _cache.ImageLoaded -= OnLoaded;
                _cache.ImageFailed -= OnFailed;
                _cache.Clear();
            }

            return;

            void OnLoaded(PhotoImageId loadedId, Robust.Client.Graphics.OwnedTexture texture)
            {
                Assert.That(loadedId, Is.EqualTo(id));
                loaded++;
            }

            void OnFailed(PhotoImageId failedId, PhotoImageCacheFailure failure)
            {
                failures[failedId] = failure;
            }
        });
    }

    [Test]
    public void LegacyPrototypesAreGoneAndPhotographIsIndependentFromPaper()
    {
        foreach (var id in RemovedPrototypeIds)
        {
            Assert.That(SProtoMan.TryIndex<EntityPrototype>(id, out _), Is.False, id);
            Assert.That(CProtoMan.TryIndex<EntityPrototype>(id, out _), Is.False, id);
        }

        var serverFactory = Server.ResolveDependency<IComponentFactory>();
        var clientFactory = Client.ResolveDependency<IComponentFactory>();
        Assert.That(SProtoMan.TryIndex<EntityPrototype>(PhotoPrototype, out var serverPhoto), Is.True);
        Assert.That(CProtoMan.TryIndex<EntityPrototype>(PhotoPrototype, out var clientPhoto), Is.True);

        Assert.That(serverPhoto!.TryComp<PhotoComponent>(out _, serverFactory), Is.True);
        Assert.That(serverPhoto.TryComp<PaperComponent>(out _, serverFactory), Is.False);
        Assert.That(serverPhoto.TryComp<ActivatableUIComponent>(out _, serverFactory), Is.True);
        Assert.That(serverPhoto.TryComp<UserInterfaceComponent>(out _, serverFactory), Is.True);
        Assert.That(clientPhoto!.TryComp<PhotoComponent>(out _, clientFactory), Is.True);

        Assert.That(ContainsByteArray(typeof(PhotoComponent)), Is.False);
        Assert.That(ContainsByteArray(typeof(PhotoBoundUserInterfaceState)), Is.False);
        Assert.That(typeof(PhotoDisplayMetadata).GetFields()
            .Any(field => field.FieldType == typeof(Robust.Shared.Network.NetUserId)), Is.False);
        Assert.That(typeof(PhotoDisplayMetadata).GetField(nameof(PhotoImageMetadata.Origin)), Is.Null,
            "The ordinary photograph viewer must not receive provenance.");
        Assert.That(typeof(PhotoBoundUserInterfaceState).GetField(nameof(PhotoComponent.IsCopy)), Is.Null,
            "The ordinary photograph viewer must not receive original/copy status.");
    }

    private static bool ContainsByteArray(Type type)
        => type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Any(field => field.FieldType == typeof(byte[]));
}
