using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Threading;

namespace DwgFlaechen.Wpf;

public partial class App : Application
{
    /// <summary>Protokolldatei für Startprobleme: %TEMP%\DwgFlaechen.log</summary>
    public static readonly string LogFile = Path.Combine(Path.GetTempPath(), "DwgFlaechen.log");

    public App()
    {
        // Fehler außerhalb des UI-Threads bzw. vor dem ersten Fenster abfangen und protokollieren
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Report("Unbehandelter Fehler", e.ExceptionObject as Exception, fatal: true);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log("Task-Fehler", e.Exception);
            e.SetObserved();
        };
        Log("Start", null);
    }

    private void App_Startup(object sender, StartupEventArgs e)
    {
        try
        {
            // Optional: Datei als Argument ("Öffnen mit …" oder DWG auf die .exe ziehen)
            string? file = e.Args.FirstOrDefault(a =>
                a.EndsWith(".dwg", StringComparison.OrdinalIgnoreCase) ||
                a.EndsWith(".dxf", StringComparison.OrdinalIgnoreCase));
            var w = new MainWindow(file);
            MainWindow = w;
            w.Show();
            Log("Hauptfenster angezeigt", null);
        }
        catch (Exception ex)
        {
            Report("Das Hauptfenster konnte nicht erstellt werden", ex, fatal: true);
            Shutdown(1);
        }
    }

    private void App_DispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Report("Fehler", e.Exception, fatal: false);
        e.Handled = true;
    }

    private static void Report(string title, Exception? ex, bool fatal)
    {
        Log(title, ex);
        try
        {
            MessageBox.Show(
                $"{title}:\n\n{ex?.GetType().Name}: {ex?.Message}\n\nDetails stehen in:\n{LogFile}",
                "DWG Flächen", MessageBoxButton.OK, fatal ? MessageBoxImage.Error : MessageBoxImage.Warning);
        }
        catch
        {
            // MessageBox ggf. nicht möglich – Protokoll reicht
        }
    }

    public static void Log(string title, Exception? ex)
    {
        try
        {
            var sb = new StringBuilder();
            sb.Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")).Append("  [WPF]  ").Append(title);
            if (ex == null)
            {
                sb.Append("  (OS ").Append(Environment.OSVersion.VersionString)
                  .Append(", .NET ").Append(Environment.Version)
                  .Append(", 64 Bit: ").Append(Environment.Is64BitProcess)
                  .Append(", Rendering-Tier: ").Append(System.Windows.Media.RenderCapability.Tier >> 16).Append(')');
            }
            else
            {
                sb.AppendLine().Append(ex);
            }
            File.AppendAllText(LogFile, sb.AppendLine().ToString());
        }
        catch
        {
            // Protokollieren darf den Start nie verhindern
        }
    }
}
