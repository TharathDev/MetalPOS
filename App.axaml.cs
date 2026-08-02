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
                    sync.StartBackgroundSync(System.TimeSpan.FromHours(1));
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
