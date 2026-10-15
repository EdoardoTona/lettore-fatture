using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Fatture.Windows;

internal static class FileAssociations
{
    internal const string ApplicationId = "LettoreFatture";
    private const string Capabilities = @"Software\LettoreFatture\Capabilities";
    private static readonly string[] Extensions = [".xml", ".p7m", ".eml", ".pdf"];

    public static void Register()
    {
        var executable = Environment.ProcessPath ?? throw new IOException("Percorso dell’app non disponibile.");
        var command = $"\"{executable}\" \"%1\"";
        using var application = Registry.CurrentUser.CreateSubKey(@"Software\Classes\Applications\LettoreFatture.exe");
        application.SetValue("FriendlyAppName", "Lettore Fatture");
        using (var open = application.CreateSubKey(@"shell\open\command")) open.SetValue("", command);
        using (var types = application.CreateSubKey("SupportedTypes"))
            foreach (var extension in Extensions) types.SetValue(extension, "");
        using var capabilities = Registry.CurrentUser.CreateSubKey(Capabilities);
        capabilities.SetValue("ApplicationName", "Lettore Fatture");
        capabilities.SetValue("ApplicationDescription", "Visualizzatore locale di fatture XML, documenti P7M ed email PEC.");
        using var associations = capabilities.CreateSubKey("FileAssociations");
        foreach (var extension in Extensions)
        {
            var progId = ApplicationId + extension;
            using var type = Registry.CurrentUser.CreateSubKey(@"Software\Classes\" + progId);
            type.SetValue("", "Lettore Fatture — " + extension[1..].ToUpperInvariant());
            using (var icon = type.CreateSubKey("DefaultIcon")) icon.SetValue("", $"\"{executable}\",0");
            using (var open = type.CreateSubKey(@"shell\open\command")) open.SetValue("", command);
            using var candidates = Registry.CurrentUser.CreateSubKey(@"Software\Classes\" + extension + @"\OpenWithProgids");
            candidates.SetValue(progId, "");
            associations.SetValue(extension, progId);
        }
        using var registered = Registry.CurrentUser.CreateSubKey(@"Software\RegisteredApplications");
        registered.SetValue(ApplicationId, Capabilities);
        SHChangeNotify(0x08000000, 0, IntPtr.Zero, IntPtr.Zero);
    }

    public static void Unregister()
    {
        foreach (var extension in Extensions)
        {
            using var candidates = Registry.CurrentUser.OpenSubKey(@"Software\Classes\" + extension + @"\OpenWithProgids", writable: true);
            candidates?.DeleteValue(ApplicationId + extension, throwOnMissingValue: false);
            Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\" + ApplicationId + extension, throwOnMissingSubKey: false);
        }
        Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\Applications\LettoreFatture.exe", throwOnMissingSubKey: false);
        Registry.CurrentUser.DeleteSubKeyTree(@"Software\LettoreFatture", throwOnMissingSubKey: false);
        using var registered = Registry.CurrentUser.OpenSubKey(@"Software\RegisteredApplications", writable: true);
        registered?.DeleteValue(ApplicationId, throwOnMissingValue: false);
        SHChangeNotify(0x08000000, 0, IntPtr.Zero, IntPtr.Zero);
    }

    public static void ShowSettings() => Process.Start(new ProcessStartInfo("ms-settings:defaultapps") { UseShellExecute = true });

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(uint eventId, uint flags, IntPtr item1, IntPtr item2);
}
