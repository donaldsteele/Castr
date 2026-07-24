using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Castr.Core.Trust;

namespace Castr.Gui.ViewModels;

/// <summary>
/// Backs the Trust-On-First-Use dialog. Presents an unknown sender's identity and offer, and exposes the
/// human's decision as a single completing <see cref="Result"/> task. Fully testable without a window: a test
/// constructs it from a <see cref="TrustPromptContext"/>, invokes <see cref="AcceptCommand"/> /
/// <see cref="RejectCommand"/>, and awaits <see cref="Result"/>. The dialog window listens for
/// <see cref="Decided"/> to close itself.
/// </summary>
public sealed partial class TrustPromptViewModel : ObservableObject
{
    private readonly TaskCompletionSource<bool> _decision =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public TrustPromptViewModel(TrustPromptContext context)
    {
        SenderId = context.SenderId.Value;
        TransferName = string.IsNullOrEmpty(context.TransferName) ? "(unnamed transfer)" : context.TransferName;
        FileCount = context.FileCount;
        TotalBytes = context.TotalBytes;
        TotalBytesText = TransferProgressViewModel.FormatBytes(context.TotalBytes);
        Prompt = $"Accept transfer \"{TransferName}\" ({FileCount} file(s), {TotalBytesText}) " +
                 "from a sender you have not trusted before?";
    }

    /// <summary>Resolves true (accept) or false (reject / cancel). Never faults.</summary>
    public Task<bool> Result => _decision.Task;

    /// <summary>Raised once with the human's decision so the hosting window can close.</summary>
    public event Action<bool>? Decided;

    public string SenderId { get; }

    public string TransferName { get; }

    public int FileCount { get; }

    public long TotalBytes { get; }

    public string TotalBytesText { get; }

    public string Prompt { get; }

    [RelayCommand]
    private void Accept() => Resolve(true);

    [RelayCommand]
    private void Reject() => Resolve(false);

    /// <summary>Fail-safe used when the prompt is cancelled or its window is closed without a choice.</summary>
    public void Cancel() => Resolve(false);

    private void Resolve(bool accepted)
    {
        if (_decision.TrySetResult(accepted))
            Decided?.Invoke(accepted);
    }
}
