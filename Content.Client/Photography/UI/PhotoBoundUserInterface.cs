using Content.Shared.Photography;
using JetBrains.Annotations;
using Robust.Client.Graphics;
using Robust.Client.UserInterface;

namespace Content.Client.Photography.UI;

[UsedImplicitly]
public sealed class PhotoBoundUserInterface : BoundUserInterface
{
    private readonly PhotoImageCacheSystem _cache;
    private PhotoWindow? _window;
    private PhotoImageId? _imageId;
    private PhotoImageId? _pendingImageId;

    public PhotoBoundUserInterface(EntityUid owner, Enum uiKey) : base(owner, uiKey)
    {
        _cache = EntMan.System<PhotoImageCacheSystem>();
    }

    protected override void Open()
    {
        base.Open();

        _window = this.CreateWindow<PhotoWindow>();
        _window.Title = EntMan.GetComponent<MetaDataComponent>(Owner).EntityName;
        _cache.ImageLoaded += OnImageLoaded;
        _cache.ImageFailed += OnImageFailed;
        _cache.CacheClearing += OnCacheClearing;
    }

    protected override void UpdateState(BoundUserInterfaceState state)
    {
        base.UpdateState(state);

        if (_window == null || state is not PhotoBoundUserInterfaceState photoState)
            return;

        if (photoState.ImageId is not { } imageId || photoState.Metadata == null)
        {
            CancelPendingRequest();
            _imageId = null;
            _window.ShowError(Loc.GetString("photograph-ui-image-unavailable"));
            return;
        }

        if (_imageId != imageId)
            CancelPendingRequest();

        _imageId = imageId;
        _window.ShowLoading();

        if (_cache.TryGet(imageId, out var texture))
        {
            _window.ShowImage(texture);
            return;
        }

        if (_pendingImageId == imageId)
            return;

        _pendingImageId = imageId;
        if (_cache.BeginRequest(imageId, photoState.Metadata))
            SendMessage(new RequestPhotoImageMessage());
    }

    protected override void ReceiveMessage(BoundUserInterfaceMessage message)
    {
        if (message is PhotoImageDataMessage image)
            _cache.Accept(image);
    }

    private void OnImageLoaded(PhotoImageId imageId, OwnedTexture texture)
    {
        if (_imageId == imageId)
        {
            _pendingImageId = null;
            _window?.ShowImage(texture);
        }
    }

    private void OnImageFailed(PhotoImageId imageId, PhotoImageCacheFailure failure)
    {
        if (_imageId != imageId)
            return;

        _pendingImageId = null;

        var message = failure switch
        {
            PhotoImageCacheFailure.InvalidHash => "photograph-ui-error-integrity",
            PhotoImageCacheFailure.InvalidDimensions => "photograph-ui-error-integrity",
            PhotoImageCacheFailure.DecodeFailed => "photograph-ui-error-decode",
            _ => "photograph-ui-error-response",
        };

        _window?.ShowError(Loc.GetString(message));
    }

    private void OnCacheClearing()
    {
        _pendingImageId = null;
        _window?.ShowError(Loc.GetString("photograph-ui-image-unavailable"));
    }

    private void CancelPendingRequest()
    {
        if (_pendingImageId is not { } imageId)
            return;

        _cache.CancelRequest(imageId);
        _pendingImageId = null;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            CancelPendingRequest();

            _cache.ImageLoaded -= OnImageLoaded;
            _cache.ImageFailed -= OnImageFailed;
            _cache.CacheClearing -= OnCacheClearing;
        }

        base.Dispose(disposing);
    }
}
