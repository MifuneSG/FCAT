using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FCAT.Services;

namespace FCAT.ViewModels;

public partial class LoginViewModel : ObservableObject
{
    private readonly EsiAuthService _auth;
    private readonly ShellViewModel _shell;

    public LoginViewModel(EsiAuthService auth, ShellViewModel shell)
    {
        _auth = auth;
        _shell = shell;
    }

    [ObservableProperty] private bool _isLoggingIn;
    [ObservableProperty] private string _errorMessage = string.Empty;

    [RelayCommand]
    private async Task LoginAsync()
    {
        IsLoggingIn = true;
        ErrorMessage = string.Empty;

        var ok = await _auth.AuthenticateAsync();

        if (ok)
            _shell.ShowMenu();
        else
            ErrorMessage = "Authentication failed or timed out. Try again.";

        IsLoggingIn = false;
    }
}
