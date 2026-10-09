using System.IO;
using System.Windows;
using PhoneCam.Desktop.Hosting;

namespace PhoneCam.Desktop;

public partial class App : Application
{
    // One instance only: a second copy would fight over the LAN ports.
    private Mutex? _singleInstance;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _singleInstance = new Mutex(initiallyOwned: true, @"Local\PhoneCam.Desktop", out bool isFirst);
        if (!isFirst)
        {
            MessageBox.Show("PhoneCam już działa.", "PhoneCam", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        DispatcherUnhandledException += (_, args) =>
        {
            string log = Path.Combine(AppPaths.Logs, "crash.log");
            File.AppendAllText(log, $"{DateTime.Now:O}\n{args.Exception}\n\n");
            MessageBox.Show($"Nieoczekiwany błąd: {args.Exception.Message}\n\nSzczegóły: {log}", "PhoneCam", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };

        MainWindow = new MainWindow();
        MainWindow.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}
