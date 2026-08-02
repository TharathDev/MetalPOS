using System;
using Avalonia.Controls;
using PosApp.ViewModels;

namespace PosApp.Views;

public partial class LoginWindow : Window
{
    private LoginViewModel? _viewModel;

    public LoginWindow()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => AttachViewModel();
    }

    public event EventHandler? LoginSucceeded;

    private void AttachViewModel()
    {
        if (_viewModel is not null)
            _viewModel.Authenticated -= OnAuthenticated;

        _viewModel = DataContext as LoginViewModel;
        if (_viewModel is not null)
            _viewModel.Authenticated += OnAuthenticated;
    }

    private void OnAuthenticated() => LoginSucceeded?.Invoke(this, EventArgs.Empty);
}
