using Avalonia.Controls;
using Castr.Gui.ViewModels;

namespace Castr.Gui.Views;

/// <summary>
/// Modal Trust-On-First-Use dialog. Purely a view over <see cref="TrustPromptViewModel"/>: it wires the
/// view-model's one-shot <see cref="TrustPromptViewModel.Decided"/> signal to closing the window, and treats
/// a manual close (title-bar X) as a rejection via <see cref="TrustPromptViewModel.Cancel"/>.
/// </summary>
public partial class TrustPromptDialog : Window
{
    private TrustPromptViewModel? _viewModel;

    public TrustPromptDialog()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Closing += OnClosing;
    }

    private void OnDataContextChanged(object? sender, System.EventArgs e)
    {
        if (_viewModel is not null)
            _viewModel.Decided -= OnDecided;

        _viewModel = DataContext as TrustPromptViewModel;

        if (_viewModel is not null)
            _viewModel.Decided += OnDecided;
    }

    private void OnDecided(bool accepted)
    {
        if (IsVisible)
            Close(accepted);
    }

    private void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        // If the user dismissed the window without choosing, that is a graceful rejection.
        _viewModel?.Cancel();
    }
}
