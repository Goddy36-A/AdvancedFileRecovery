using System.Security.Principal;
using System.Windows;

namespace FileRecovery.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Belt-and-suspenders check: app.manifest's requireAdministrator should mean
        // Windows never lets us reach this line unelevated (the user either accepts
        // the UAC prompt or the process never starts). We still verify explicitly so
        // that if UAC was somehow bypassed or the manifest is stripped by a
        // repackaging tool, we fail with a clear message instead of confusing
        // "Access Denied" errors deep inside disk I/O.
        if (!IsRunningElevated())
        {
            MessageBox.Show(
                "Advanced File Recovery needs Administrator privileges to read raw disks and volumes.\n\n" +
                "Please right-click the application and choose \"Run as administrator\", or accept the " +
                "UAC prompt when launching normally.",
                "Administrator privileges required",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            Shutdown(1);
            return;
        }
    }

    private static bool IsRunningElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }
}
