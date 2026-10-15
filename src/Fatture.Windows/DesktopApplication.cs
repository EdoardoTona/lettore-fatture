using Microsoft.VisualBasic.ApplicationServices;

namespace Fatture.Windows;

// Reuse the desktop framework's single-instance handling. Explorer opens files
// in the existing document list, even when it launches one command per file.
internal sealed class DesktopApplication : WindowsFormsApplicationBase
{
    internal MainWindow? Window { get; private set; }
    public DesktopApplication()
    {
        IsSingleInstance = true;
        EnableVisualStyles = true;
        HighDpiMode = HighDpiMode.PerMonitorV2;
        ShutdownStyle = ShutdownMode.AfterMainFormCloses;
    }
    protected override void OnCreateMainForm()
    {
        Window = new MainWindow(CommandLineArgs.ToArray());
        MainForm = Window;
    }
    protected override void OnStartupNextInstance(StartupNextInstanceEventArgs e)
    {
        Window?.OpenFromShell(e.CommandLine);
        e.BringToForeground = true;
        base.OnStartupNextInstance(e);
    }
}
