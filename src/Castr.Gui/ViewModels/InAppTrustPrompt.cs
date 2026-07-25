using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Castr.Core.Trust;

namespace Castr.Gui.ViewModels;

/// <summary>
/// A single-view (mobile) <see cref="ITrustPrompt"/>. Instead of popping a modal <see cref="System.Windows"/>-style
/// dialog window (which Avalonia's mobile heads do not support), it surfaces the pending Trust-On-First-Use
/// decision as a bindable <see cref="Pending"/> view-model that the hosting view renders as an in-page overlay.
/// The user's Accept/Reject choice completes the awaiting session.
/// <para>
/// Mirrors <see cref="Castr.Gui.Trust.DialogTrustPrompt"/>'s hard contract: it can only ever return
/// <c>true</c> or <c>false</c> and must never throw into the session loop — every internal failure (dispatcher
/// teardown, cancellation) collapses to a graceful denial. Platform-neutral, so the future iOS head reuses it.
/// </para>
/// </summary>
public sealed partial class InAppTrustPrompt : ObservableObject, ITrustPrompt
{
    /// <summary>The decision currently awaiting the user, or <c>null</c> when none is pending. Bind overlay visibility to this.</summary>
    [ObservableProperty]
    private TrustPromptViewModel? _pending;

    public async Task<bool> RequestTrustAsync(TrustPromptContext context, CancellationToken cancellationToken)
    {
        try
        {
            if (cancellationToken.IsCancellationRequested)
                return false;

            var vm = new TrustPromptViewModel(context);
            await Dispatcher.UIThread.InvokeAsync(() => Pending = vm);

            await using var registration = cancellationToken.Register(vm.Cancel);
            bool accepted = await vm.Result.ConfigureAwait(true);

            await Dispatcher.UIThread.InvokeAsync(() => Pending = null);
            return accepted;
        }
        catch
        {
            // Any failure is a graceful denial — never let it escape into the pull session.
            try { await Dispatcher.UIThread.InvokeAsync(() => Pending = null); } catch { /* ignore */ }
            return false;
        }
    }
}
