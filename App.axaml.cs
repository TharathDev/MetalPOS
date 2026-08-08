using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using PosApp.Services;
using PosApp.ViewModels;
using PosApp.Views;

namespace PosApp;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Bootstrap the local database so the seeded administrator can sign in.
            // The main application window and background sync remain gated by login.
            var database = new DatabaseService();
            database.Initialize();

            void ShowLoginWindow()
            {
                var loginWindow = new LoginWindow
                {
                    DataContext = new LoginViewModel(new AuthService(database)),
                };

                var hasAuthenticated = false;
                loginWindow.LoginSucceeded += (_, _) =>
                {
                    if (hasAuthenticated)
                        return;
                    hasAuthenticated = true;

                    var sync = new TursoSyncService(database);
                    // Honour the interval the user last chose in Settings → Backup
                    // (stored in AppState), defaulting to hourly.
                    var savedMinutes = int.TryParse(database.GetAppState("BackupIntervalMinutes"), out var m) && m > 0 ? m : 60;
                    sync.StartBackgroundSync(System.TimeSpan.FromMinutes(savedMinutes));
                    desktop.ShutdownRequested += (_, _) => sync.Stop();

                    var signedInPhone = (loginWindow.DataContext as LoginViewModel)?.AuthenticatedPhone;
                    var viewModel = new MaterialSelectionViewModel(database, sync, signedInPhone);
                    var mainWindow = new MainWindow
                    {
                        DataContext = viewModel,
                    };
                    viewModel.SignOutRequested += () =>
                    {
                        sync.Stop();
                        ShowLoginWindow();
                        mainWindow.Close();
                    };

                    desktop.MainWindow = mainWindow;
                    mainWindow.Show();
                    loginWindow.Close();
                };
                loginWindow.Closed += (_, _) =>
                {
                    if (!hasAuthenticated && desktop.MainWindow == loginWindow)
                        desktop.Shutdown();
                };

                desktop.MainWindow = loginWindow;
                loginWindow.Show();
            }

            ShowLoginWindow();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
