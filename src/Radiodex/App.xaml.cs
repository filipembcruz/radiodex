using System;
using System.Threading;
using System.Windows;
using System.Windows.Threading;

namespace Radiodex;

public partial class App : Application
{
    private static Mutex? _single;

    private void OnStartup(object sender, StartupEventArgs e)
    {
        // First run from Downloads (or a newer download): install to the user's Programs folder,
        // create Desktop and Start menu shortcuts, and continue from the installed copy.
        if (Services.Installer.InstallAndRelaunchIfNeeded())
        {
            Shutdown();
            return;
        }

        // One instance only: a second launch just exits.
        _single = new Mutex(true, "Radiodex.SingleInstance", out var first);
        if (!first)
        {
            Shutdown();
            return;
        }

        DispatcherUnhandledException += OnUnhandled;
        var window = new MainWindow();
        MainWindow = window;
        window.Show();
    }

    private static void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // Keep the radio playing; show what went wrong instead of closing.
        MessageBox.Show(e.Exception.Message, "Radiodex", MessageBoxButton.OK, MessageBoxImage.Warning);
        e.Handled = true;
    }
}
