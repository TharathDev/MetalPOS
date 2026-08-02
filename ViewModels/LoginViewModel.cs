using System;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PosApp.Services;

namespace PosApp.ViewModels;

public partial class LoginViewModel : ViewModelBase
{
    private readonly AuthService _auth;

    public LoginViewModel(AuthService auth)
    {
        _auth = auth;
    }

    public event Action? Authenticated;
    public string? AuthenticatedPhone { get; private set; }

    [ObservableProperty]
    public partial string PhoneNumber { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Password { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string ErrorMessage { get; set; } = string.Empty;

    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);

    [RelayCommand]
    private void Login()
    {
        var result = _auth.Authenticate(PhoneNumber, Password);
        if (!result.Succeeded)
        {
            Password = string.Empty;
            ErrorMessage = result.Message;
            return;
        }

        ErrorMessage = string.Empty;
        AuthenticatedPhone = result.Phone;
        Password = string.Empty;
        Authenticated?.Invoke();
    }
}
