using System.Windows;

namespace TiffLightViewer;

public partial class App : Application
{
    private DataClasysFileService? _fileService;

    private void OnStartup(object sender, StartupEventArgs e)
    {
        _fileService = new DataClasysFileService();
        var initialPath = e.Args.Length > 0 ? e.Args[0] : null;
        var window = new MainWindow(_fileService, initialPath);
        MainWindow = window;
        window.Show();
    }

    private void OnExit(object sender, ExitEventArgs e)
    {
        _fileService?.Dispose();
    }
}
