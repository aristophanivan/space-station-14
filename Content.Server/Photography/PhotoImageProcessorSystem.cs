using System.Collections.Concurrent;
using System.Buffers.Binary;
using System.IO;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Content.Shared.CCVar;
using Content.Shared.Photography;
using Robust.Shared.Configuration;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;

namespace Content.Server.Photography;

public enum PhotoImageProcessingError : byte
{
    None,
    InvalidImage,
    InvalidDimensions,
    EncodedImageTooLarge,
    Cancelled,
}

public enum PhotoImageQueueError : byte
{
    None,
    InvalidImage,
    EncodedImageTooLarge,
    QueueFull,
}

public readonly record struct PhotoImageProcessingResult(
    Guid RequestId,
    byte[]? EncodedPng,
    Vector2i Size,
    PhotoImageProcessingError Error);

/// <summary>
/// Runs untrusted image decoding and canonical PNG encoding on a bounded background queue.
/// Results are delivered from <see cref="Update"/> on the ECS thread.
/// </summary>
public sealed partial class PhotoImageProcessorSystem : EntitySystem
{
    [Dependency] private IConfigurationManager _cfg = default!;

    private readonly ConcurrentQueue<PhotoImageProcessingResult> _completed = new();
    private CancellationTokenSource? _cancellation;
    private Channel<ProcessingJob>? _queue;
    private Task[] _workers = [];
    private int _maxDimension;
    private int _maxDecodedBytes;
    private int _maxEncodedBytes;
    private int _queueCapacity;
    private int _reservedQueueSlots;

    public event Action<PhotoImageProcessingResult>? ImageProcessed;

    public override void Initialize()
    {
        base.Initialize();

        _cancellation = new CancellationTokenSource();
        _maxDimension = _cfg.GetCVar(CCVars.PhotographyMaxDimension);
        _maxDecodedBytes = _cfg.GetCVar(CCVars.PhotographyMaxDecodedBytes);
        _maxEncodedBytes = Math.Min(
            _cfg.GetCVar(CCVars.PhotographyMaxEncodedBytes),
            PhotographyConstants.MaxEncodedBytes);
        _queueCapacity = Math.Max(1, _cfg.GetCVar(CCVars.PhotographyProcessingQueueCapacity));
        _queue = Channel.CreateBounded<ProcessingJob>(new BoundedChannelOptions(_queueCapacity)
        {
            // TryWrite must fail when the queue is full. DropWrite can report success
            // even though the item was discarded, leaving a camera permanently busy.
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = false,
            SingleWriter = true,
        });

        var workerCount = Math.Clamp(_cfg.GetCVar(CCVars.PhotographyProcessingWorkers), 1, 8);
        _workers = new Task[workerCount];
        for (var i = 0; i < workerCount; i++)
            _workers[i] = Task.Run(() => ProcessQueue(_cancellation.Token));
    }

    public bool TryQueue(
        ReadOnlyMemory<byte> encodedImage,
        out Guid requestId,
        out PhotoImageQueueError error)
    {
        requestId = Guid.NewGuid();
        error = PhotoImageQueueError.None;
        if (_queue == null || encodedImage.IsEmpty)
        {
            error = PhotoImageQueueError.InvalidImage;
            return false;
        }

        if (encodedImage.Length > _maxEncodedBytes)
        {
            error = PhotoImageQueueError.EncodedImageTooLarge;
            return false;
        }

        if (!IsSupportedImageEnvelope(encodedImage.Span))
        {
            error = PhotoImageQueueError.InvalidImage;
            return false;
        }

        // Reserve bounded capacity before cloning the untrusted network buffer.
        // Rejected writes therefore do not allocate another max-sized byte array.
        if (Interlocked.Increment(ref _reservedQueueSlots) > _queueCapacity)
        {
            Interlocked.Decrement(ref _reservedQueueSlots);
            error = PhotoImageQueueError.QueueFull;
            return false;
        }

        // Network message ownership does not cross into the worker queue.
        if (_queue.Writer.TryWrite(new ProcessingJob(requestId, encodedImage.ToArray())))
            return true;

        Interlocked.Decrement(ref _reservedQueueSlots);
        error = PhotoImageQueueError.QueueFull;
        return false;
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        while (_completed.TryDequeue(out var result))
            ImageProcessed?.Invoke(result);
    }

    public override void Shutdown()
    {
        _queue?.Writer.TryComplete();
        _cancellation?.Cancel();
        Task.WaitAll(_workers, TimeSpan.FromSeconds(2));
        _cancellation?.Dispose();
        _workers = [];
        _queue = null;

        while (_completed.TryDequeue(out _))
        {
        }

        base.Shutdown();
    }

    private async Task ProcessQueue(CancellationToken cancellation)
    {
        if (_queue == null)
            return;

        try
        {
            await foreach (var job in _queue.Reader.ReadAllAsync(cancellation))
            {
                Interlocked.Decrement(ref _reservedQueueSlots);
                _completed.Enqueue(Process(job, cancellation));
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private PhotoImageProcessingResult Process(ProcessingJob job, CancellationToken cancellation)
    {
        try
        {
            cancellation.ThrowIfCancellationRequested();
            using var input = new MemoryStream(job.Bytes, writable: false);
            var decoderOptions = new DecoderOptions
            {
                SkipMetadata = true,
                MaxFrames = 1,
            };
            var identified = Image.Identify(decoderOptions, input);

            if (!DimensionsAreAllowed(identified.Width, identified.Height))
            {
                return new PhotoImageProcessingResult(
                    job.RequestId,
                    null,
                    default,
                    PhotoImageProcessingError.InvalidDimensions);
            }

            // Identify reads image headers without allocating the decoded pixel buffer.
            // Rewind only after the declared dimensions have passed the hard limits.
            input.Position = 0;
            using var decoded = Image.Load<Rgba32>(decoderOptions, input);
            if (!DimensionsAreAllowed(decoded.Width, decoded.Height) ||
                decoded.Width != identified.Width || decoded.Height != identified.Height)
            {
                return new PhotoImageProcessingResult(
                    job.RequestId,
                    null,
                    default,
                    PhotoImageProcessingError.InvalidDimensions);
            }

            var pixelBytes = checked(decoded.Width * decoded.Height * 4);
            var rgba = new byte[pixelBytes];
            decoded.CopyPixelDataTo(rgba);
            using var canonical = Image.LoadPixelData<Rgba32>(rgba, decoded.Width, decoded.Height);
            using var output = new MemoryStream();
            canonical.SaveAsPng(output, new PngEncoder
            {
                CompressionLevel = PngCompressionLevel.BestSpeed,
                ColorType = PngColorType.RgbWithAlpha,
            });

            if (output.Length > _maxEncodedBytes)
            {
                return new PhotoImageProcessingResult(
                    job.RequestId,
                    null,
                    default,
                    PhotoImageProcessingError.EncodedImageTooLarge);
            }

            return new PhotoImageProcessingResult(
                job.RequestId,
                output.ToArray(),
                (decoded.Width, decoded.Height),
                PhotoImageProcessingError.None);
        }
        catch (OperationCanceledException)
        {
            return new PhotoImageProcessingResult(
                job.RequestId,
                null,
                default,
                PhotoImageProcessingError.Cancelled);
        }
        catch (Exception)
        {
            return new PhotoImageProcessingResult(
                job.RequestId,
                null,
                default,
                PhotoImageProcessingError.InvalidImage);
        }
    }

    private sealed record ProcessingJob(Guid RequestId, byte[] Bytes);

    private bool DimensionsAreAllowed(int width, int height)
    {
        if (width <= 0 || height <= 0 || width > _maxDimension || height > _maxDimension)
            return false;

        try
        {
            return checked((long) width * height * 4) <= _maxDecodedBytes;
        }
        catch (OverflowException)
        {
            return false;
        }
    }

    private static bool IsSupportedImageEnvelope(ReadOnlySpan<byte> bytes)
    {
        ReadOnlySpan<byte> signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        if (!bytes.StartsWith(signature))
            return bytes.Length >= 3 &&
                   bytes[0] == 0xFF &&
                   bytes[1] == 0xD8 &&
                   bytes[2] == 0xFF;

        // APNG is deliberately unsupported. Walk complete chunks only; malformed
        // envelopes are left for ImageSharp to reject in the bounded worker.
        var offset = signature.Length;
        while (offset <= bytes.Length - 12)
        {
            var dataLength = BinaryPrimitives.ReadUInt32BigEndian(bytes[offset..]);
            if (dataLength > int.MaxValue || dataLength > bytes.Length - offset - 12)
                break;

            var type = bytes.Slice(offset + 4, 4);
            if (type.SequenceEqual("acTL"u8))
                return false;

            offset += checked((int) dataLength + 12);
        }

        return true;
    }
}
