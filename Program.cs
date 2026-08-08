using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Fonts;
using PosApp.Services;
using System;
using System.Linq;
using System.Threading.Tasks;

namespace PosApp;

sealed class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static int Main(string[] args)
    {
        // Headless one-shot backup (for cron/CI or diagnostics): initialise the
        // local database and push a single snapshot to the server, then exit.
        if (args.Contains("--backup-now"))
            return RunBackupOnce().GetAwaiter().GetResult();

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        return 0;
    }

    private static async Task<int> RunBackupOnce()
    {
        var db = new DatabaseService();
        db.Initialize();
        var sync = new TursoSyncService(db);
        sync.StatusChanged += s => Console.WriteLine($"[{(s.Success ? "OK" : "FAIL")}] {s.Message}");
        Console.WriteLine($"Enabled={sync.Enabled}  Endpoint={sync.Endpoint}");
        var ok = await sync.SyncNowAsync().ConfigureAwait(false);
        return ok ? 0 : 1;
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
#if DEBUG
            .WithDeveloperTools()
#endif
            .WithInterFont()
            // Bundle a Khmer typeface (Danh Hong's "Khmer", OFL) and use it as a
            // glyph fallback for the Khmer Unicode block only. The Latin UI fonts
            // (Lora / Cormorant Garamond) have no Khmer glyphs, so without this the
            // localized Khmer strings would render in whatever the OS substitutes,
            // which differs across platforms. Scoping to U+1780–U+17FF keeps every
            // Latin glyph on the primary fonts.
            .With(new FontManagerOptions
            {
                FontFallbacks = new[]
                {
                    new FontFallback
                    {
                        FontFamily = new FontFamily("avares://PosApp/Assets/Fonts#Khmer"),
                        UnicodeRange = new UnicodeRange(new UnicodeRangeSegment(0x1780, 0x17FF)),
                    },
                },
            })
            .LogToTrace();
}
