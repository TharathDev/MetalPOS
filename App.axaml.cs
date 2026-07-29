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
            // Local SQLite is the source of truth (creates tables + seeds on first run).
            var database = new DatabaseService();
            database.Initialize();

            // Hourly backup of the local database to the remote Turso (libSQL)
            // server. No-ops safely when no auth token is configured.
            var sync = new TursoSyncService(database);
            sync.StartBackgroundSync(System.TimeSpan.FromHours(1));
            desktop.ShutdownRequested += (_, _) => sync.Stop();

            desktop.MainWindow = new MainWindow
            {
                DataContext = new MaterialSelectionViewModel(database, sync),
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}
