using Avalonia.Controls;
using Avalonia.Input;
using PosApp.ViewModels;

namespace PosApp.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        SizeChanged += (_, args) => ApplyResponsiveLayout(args.NewSize.Width);
        DataContextChanged += (_, _) => ApplyResponsiveLayout(Bounds.Width);
    }

    private void ApplyResponsiveLayout(double width)
    {
        if (DataContext is MaterialSelectionViewModel viewModel)
            viewModel.UpdateResponsiveLayout(width);
    }

    /// <summary>Clicking the content area while the cart is expanded collapses it back to the rail.</summary>
    private void CartScrim_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is MaterialSelectionViewModel viewModel)
            viewModel.IsCartExpanded = false;
    }
}