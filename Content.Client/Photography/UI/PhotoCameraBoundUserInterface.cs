using System.IO;
using System.Threading.Tasks;
using Content.Shared.Photography;
using JetBrains.Annotations;
using Robust.Client.Input;
using Robust.Client.UserInterface;
using Robust.Shared.Asynchronous;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace Content.Client.Photography.UI;

[UsedImplicitly]
public sealed class PhotoCameraBoundUserInterface : BoundUserInterface
{
    private readonly ITaskManager _tasks;
    private readonly IInputManager _inputManager;
    private PhotoCameraWindow? _window;
    private PhotoCaptureToken? _token;
    private bool _disposed;

    public PhotoCameraBoundUserInterface(EntityUid owner, Enum uiKey) : base(owner, uiKey)
    {
        _tasks = IoCManager.Resolve<ITaskManager>();
        _inputManager = IoCManager.Resolve<IInputManager>();
    }

    protected override void Open()
    {
        base.Open();
        ReleasePressedInputs();
        _window = this.CreateWindow<PhotoCameraWindow>();
        _window.Title = EntMan.GetComponent<MetaDataComponent>(Owner).EntityName;
        _window.ShutterPressed += OnShutterPressed;
    }

    private void ReleasePressedInputs()
    {
        // Opening the viewfinder while a movement key is held otherwise leaves the
        // corresponding simulation command pressed. Release every physical key so
        // movement and other held actions cannot leak into the camera UI.
        for (var value = 1; value <= byte.MaxValue; value++)
        {
            var key = (Keyboard.Key) value;
            if (!_inputManager.IsKeyDown(key))
                continue;

            _inputManager.KeyUp(new KeyEventArgs(
                key,
                false,
                false,
                false,
                false,
                false,
                0));
        }
    }

    protected override void UpdateState(BoundUserInterfaceState state)
    {
        base.UpdateState(state);
        if (state is PhotoCameraBoundUserInterfaceState cameraState)
            _window?.SetState(cameraState);
    }

    protected override void ReceiveMessage(BoundUserInterfaceMessage message)
    {
        switch (message)
        {
            case PhotoCameraSessionMessage session:
                _token = session.Token;
                _window?.SetTokenAvailable(true);
                break;
            case PhotoCaptureResultMessage result:
                _window?.ShowResult(result.Result);
                break;
        }
    }

    private void OnShutterPressed()
    {
        if (_window == null || _token is not { } token)
            return;

        _token = null;
        _window.SetTokenAvailable(false);
        _window.SetCapturing();
        _window.Capture(image => BeginEncoding(image, token));
    }

    private void BeginEncoding(Image<Rgba32> image, PhotoCaptureToken token)
    {
        var copy = image.Clone();
        _ = EncodeAndSend(copy, token);
    }

    private async Task EncodeAndSend(Image<Rgba32> image, PhotoCaptureToken token)
    {
        byte[]? png;
        try
        {
            png = await Task.Run(() => Encode(image));
        }
        catch
        {
            png = null;
        }

        _tasks.RunOnMainThread(() =>
        {
            if (_disposed || !IsOpened)
                return;

            if (png != null)
                SendMessage(new TakePhotoMessage(token, png));
            else
                _window?.ShowResult(PhotoCaptureResult.InvalidImage);
        });
    }

    private static byte[]? Encode(Image<Rgba32> image)
    {
        using (image)
        {
            if (image.Width != PhotographyConstants.CaptureSize ||
                image.Height != PhotographyConstants.CaptureSize)
                return null;

            using var output = new MemoryStream();
            image.SaveAsPng(output);

            return output.Length <= PhotographyConstants.MaxEncodedBytes
                ? output.ToArray()
                : null;
        }
    }

    protected override void Dispose(bool disposing)
    {
        _disposed = true;
        if (disposing && _window != null)
            _window.ShutterPressed -= OnShutterPressed;

        base.Dispose(disposing);
    }
}
