using System.Text;
using Microsoft.UI.Xaml;

namespace DwgFlaechen.App;

public partial class App : Application
{
    /// <summary>Protokolldatei für Startprobleme (wie in der WPF-Version): %TEMP%\DwgFlaechen.log</summary>
    public static readonly string LogFile = Path.Combine(Path.GetTempPath(), "DwgFlaechen.log");

    private Window? _window;

    public App()
    {
        InitializeComponent();

        // Fehler protokollieren; Fehler im UI-Thread nicht zum Absturz führen lassen
        UnhandledException += (_, e) =>
        {
            Log("Fehler", e.Exception);
            e.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log("Unbehandelter Fehler", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log("Task-Fehler", e.Exception);
            e.SetObserved();
        };
        Log("Start", null);
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            // Optional: Datei als Kommandozeilenargument ("Öffnen mit …" / Drag auf die .exe)
            string? file = Environment.GetCommandLineArgs().Skip(1)
                .FirstOrDefault(a => a.EndsWith(".dwg", StringComparison.OrdinalIgnoreCase) ||
                                     a.EndsWith(".dxf", StringComparison.OrdinalIgnoreCase));
            _window = new MainWindow(file);
            _window.Activate();
            Log("Hauptfenster angezeigt", null);
        }
        catch (Exception ex)
        {
            Log("Das Hauptfenster konnte nicht erstellt werden", ex);
            throw;
        }
    }

    public static void Log(string title, Exception? ex)
    {
        try
        {
            var sb = new StringBuilder();
            sb.Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")).Append("  [WinUI]  ").Append(title);
            if (ex == null)
                sb.Append("  (OS ").Append(Environment.OSVersion.VersionString)
                  .Append(", .NET ").Append(Environment.Version)
                  .Append(", 64 Bit: ").Append(Environment.Is64BitProcess).Append(')');
            else
                sb.AppendLine().Append(ex);
            File.AppendAllText(LogFile, sb.AppendLine().ToString());
        }
        catch
        {
            // Protokollieren darf den Start nie verhindern
        }
    }
}
