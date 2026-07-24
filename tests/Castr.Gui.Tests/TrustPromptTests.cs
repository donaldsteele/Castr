using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Castr.Core.Security;
using Castr.Core.Trust;
using Castr.Gui.Trust;
using Castr.Gui.ViewModels;
using Castr.Gui.Views;

namespace Castr.Gui.Tests;

public class TrustPromptTests
{
    private static TrustPromptContext SampleContext() =>
        new(PublicKeyId.FromRawEd25519(new byte[32]), "incoming-transfer", FileCount: 3, TotalBytes: 4096);

    [AvaloniaFact]
    public async Task TrustPromptViewModel_Accept_ResolvesTrue()
    {
        var vm = new TrustPromptViewModel(SampleContext());
        bool? decided = null;
        vm.Decided += a => decided = a;

        vm.AcceptCommand.Execute(null);

        Assert.True(await vm.Result);
        Assert.Equal(true, decided);
    }

    [AvaloniaFact]
    public async Task TrustPromptViewModel_Reject_ResolvesFalse()
    {
        var vm = new TrustPromptViewModel(SampleContext());

        vm.RejectCommand.Execute(null);

        Assert.False(await vm.Result);
    }

    [AvaloniaFact]
    public async Task TrustPromptDialog_Renders_And_AcceptButtonResolvesDecision()
    {
        var vm = new TrustPromptViewModel(SampleContext());
        var dialog = new TrustPromptDialog { DataContext = vm };
        dialog.Show();

        // The dialog rendered the accept/reject buttons from the bound view-model.
        var buttons = dialog.GetVisualDescendants().OfType<Button>().ToList();
        Assert.Equal(2, buttons.Count);

        // Driving the accept command (as the button would) resolves the decision to true.
        vm.AcceptCommand.Execute(null);
        Assert.True(await vm.Result);
    }

    [AvaloniaFact]
    public async Task DialogTrustPrompt_PreCancelledToken_ReturnsFalse_WithoutThrowing()
    {
        // Owner provider returns null (no window); the critical contract is: never throw, resolve to deny.
        var prompt = new DialogTrustPrompt(() => null);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        bool result = await prompt.RequestTrustAsync(SampleContext(), cts.Token);

        Assert.False(result);
    }
}
