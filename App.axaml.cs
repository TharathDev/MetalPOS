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
            // Create and initialize the local SQLite database (creates tables + seeds on first run).
            // Kept ready for the backend phase; the current screen is UI-only.
            var database = new DatabaseService();
            database.Initialize();

            desktop.MainWindow = new MainWindow
            {
                DataContext = new MaterialSelectionViewModel(),
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}
