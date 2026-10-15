using Fatture.Cli;
using Velopack;

namespace Fatture.Windows;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        VelopackApp.Build()
            .SetAutoApplyOnStartup(false)
            .OnAfterInstallFastCallback(_ => FileAssociations.Register())
            .OnAfterUpdateFastCallback(_ => FileAssociations.Register())
            .OnBeforeUninstallFastCallback(_ => FileAssociations.Unregister())
            .Run();
        // The same executable hosts the worker. No second runtime is distributed.
        if (args is ["--core-bridge"])
        {
            Console.InputEncoding = System.Text.Encoding.UTF8;
            Console.OutputEncoding = new System.Text.UTF8Encoding(false);
            return BridgeServer.Run();
        }
        if (args is ["--register"]) { FileAssociations.Register(); return 0; }
        if (args is ["--unregister"]) { FileAssociations.Unregister(); return 0; }
        ApplicationConfiguration.Initialize();
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => MessageBox.Show(e.Exception.Message,
            "Lettore Fatture", MessageBoxButtons.OK, MessageBoxIcon.Error);
        if (args.Contains("--smoke-test"))
        {
            using var window = new MainWindow(args);
            Application.Run(window);
            return window.ExitCode;
        }
        var application = new DesktopApplication();
        application.Run(args);
        return application.Window?.ExitCode ?? 0;
    }
}
