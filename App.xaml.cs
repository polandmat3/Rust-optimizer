using System.Windows;

namespace ProjectOpti;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        Theme.ApplySaved();

        // Keep the app alive while the login window closes.
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        var login = new LoginWindow();
        if (login.ShowDialog() == true)
        {
            var main = new MainWindow();
            MainWindow = main;
            ShutdownMode = ShutdownMode.OnMainWindowClose;
            main.Show();
        }
        else
        {
            Shutdown();
        }
    }
}
