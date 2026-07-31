using Avalonia.Controls;
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
}