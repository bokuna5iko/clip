using System.Windows;
using Clip.Data;
using Clip.Services;
using Clip.Shelf;

namespace Clip;

public partial class App : System.Windows.Application
{
    private SingleInstanceService? _singleInstance;
    private TrayIconService? _tray;
    private MainWindow? _mainWindow;
    private ShelfWindow? _shelf;
    private ClipStore? _store;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _singleInstance = SingleInstanceService.TryAcquireFirstInstance(() =>
        {
            Dispatcher.Invoke(() => _mainWindow?.TogglePanel());
        });

        if (_singleInstance is null)
        {
            Shutdown();
            return;
        }

        _singleInstance.StartListening();
        _store = new ClipStore();
        _mainWindow = new MainWindow(_store);
        _shelf = new ShelfWindow(new ShelfStore());
        _mainWindow.ShelfHotkeyPressed += () => _shelf.ToggleRaise();

        _tray = new TrayIconService();
        _tray.TogglePanelRequested += (_, _) => Dispatcher.Invoke(() => _mainWindow.TogglePanel());
        _tray.OpenRequested += (_, _) => Dispatcher.Invoke(() => _mainWindow.ShowPanel());
        _tray.ShelfRequested += (_, _) => Dispatcher.Invoke(() => _shelf.ToggleRaise());
        _tray.ExitRequested += (_, _) => Dispatcher.Invoke(ShutdownFromTray);

        // HWND hooks require a handle; create off-screen without flashing the panel.
        _mainWindow.Left = -20000;
        _mainWindow.Top = -20000;
        _mainWindow.Show();
        _mainWindow.Hide();
        _shelf.Show();
    }

    private void ShutdownFromTray()
    {
        _shelf?.PrepareShutdown();
        _shelf?.Close();
        _mainWindow?.PrepareShutdown();
        _tray?.Dispose();
        _singleInstance?.Dispose();
        Shutdown();
    }
}
