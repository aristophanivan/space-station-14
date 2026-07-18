using System.IO;
using System.Threading.Tasks;
using Content.Shared.Fax;
using JetBrains.Annotations;
using Robust.Client.GameObjects;
using Robust.Client.UserInterface;

namespace Content.Client.Fax.UI;

[UsedImplicitly]
public sealed partial class FaxBoundUi : BoundUserInterface
{
    [Dependency] private IFileDialogManager _fileDialogManager = default!;

    [ViewVariables]
    private FaxWindow? _window;

    private bool _dialogIsOpen = false;

    public FaxBoundUi(EntityUid owner, Enum uiKey) : base(owner, uiKey)
    {
    }

    protected override void Open()
    {
        base.Open();

        _window = this.CreateWindow<FaxWindow>();
        _window.FileButtonPressed += OnFileButtonPressed;
        _window.ImageButtonPressed += OnImageButtonPressed;
        _window.CopyButtonPressed += OnCopyButtonPressed;
        _window.SendButtonPressed += OnSendButtonPressed;
        _window.RefreshButtonPressed += OnRefreshButtonPressed;
        _window.PeerSelected += OnPeerSelected;
    }

    private async void OnImageButtonPressed()
    {
        if (_dialogIsOpen)
            return;

        _dialogIsOpen = true;
        var filters = new FileDialogFilters(new FileDialogFilters.Group("png", "jpg", "jpeg"));
        await using var file = await _fileDialogManager.OpenFile(filters, FileAccess.Read);
        _dialogIsOpen = false;

        if (_window == null || _window.Disposed || file == null)
            return;

        var bytes = await ReadLimited(file, FaxImageFileMessageValidation.MaxEncodedBytes);
        if (bytes == null)
        {
            _window.ShowImageStatus(Loc.GetString("fax-machine-ui-image-result-too-large"), Color.Red);
            return;
        }

        _window.ShowImageStatus(Loc.GetString("fax-machine-ui-image-processing"), Color.LightGray);
        SendMessage(new FaxImageFileMessage("photograph", bytes));
    }

    private static async Task<byte[]?> ReadLimited(Stream input, int maxBytes)
    {
        using var output = new MemoryStream();
        var buffer = new byte[81920];
        while (true)
        {
            var read = await input.ReadAsync(buffer);
            if (read == 0)
                return output.ToArray();

            if (output.Length + read > maxBytes)
                return null;

            await output.WriteAsync(buffer.AsMemory(0, read));
        }
    }

    private async void OnFileButtonPressed()
    {
        if (_dialogIsOpen)
            return;

        _dialogIsOpen = true;
        var filters = new FileDialogFilters(new FileDialogFilters.Group("txt"));
        await using var file = await _fileDialogManager.OpenFile(filters, FileAccess.Read);
        _dialogIsOpen = false;

        if (_window == null || _window.Disposed || file == null)
        {
            return;
        }

        using var reader = new StreamReader(file);

        var firstLine = await reader.ReadLineAsync();
        string? label = null;
        var content = await reader.ReadToEndAsync();

        if (firstLine is { })
        {
            if (firstLine.StartsWith('#'))
            {
                label = firstLine[1..].Trim();
            }
            else
            {
                content = firstLine + "\n" + content;
            }
        }

        SendMessage(new FaxFileMessage(
            label?[..Math.Min(label.Length, FaxFileMessageValidation.MaxLabelSize)],
            content[..Math.Min(content.Length, FaxFileMessageValidation.MaxContentSize)],
            _window.OfficePaper));
    }

    private void OnSendButtonPressed()
    {
        SendMessage(new FaxSendMessage());
    }

    private void OnCopyButtonPressed()
    {
        SendMessage(new FaxCopyMessage());
    }

    private void OnRefreshButtonPressed()
    {
        SendMessage(new FaxRefreshMessage());
    }

    private void OnPeerSelected(string address)
    {
        SendMessage(new FaxDestinationMessage(address));
    }

    protected override void UpdateState(BoundUserInterfaceState state)
    {
        base.UpdateState(state);

        if (_window == null || state is not FaxUiState cast)
            return;

        _window.UpdateState(cast);
    }

    protected override void ReceiveMessage(BoundUserInterfaceMessage message)
    {
        base.ReceiveMessage(message);

        if (_window == null || message is not FaxImagePrintResultMessage result)
            return;

        var loc = result.Result switch
        {
            FaxImagePrintResult.Queued => "fax-machine-ui-image-result-queued",
            FaxImagePrintResult.TooLarge => "fax-machine-ui-image-result-too-large",
            FaxImagePrintResult.Busy => "fax-machine-ui-image-result-busy",
            FaxImagePrintResult.StorageFull => "fax-machine-ui-image-result-storage-full",
            FaxImagePrintResult.UploadsDisabled => "fax-machine-ui-image-result-disabled",
            FaxImagePrintResult.UploadLimit => "fax-machine-ui-image-result-limit",
            _ => "fax-machine-ui-image-result-invalid",
        };
        _window.ShowImageStatus(Loc.GetString(loc),
            result.Result == FaxImagePrintResult.Queued ? Color.Green : Color.Red);
    }
}
