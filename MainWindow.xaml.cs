using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Clip.Clipboard;
using Clip.Data;
using Clip.Models;
using Clip.Native;
using Clip.ViewModels;

namespace Clip;

public partial class MainWindow : Window
{
    private readonly ClipStore _store;
    private readonly MainViewModel _viewModel;
    private HwndSource? _hwndSource;
    private bool _hotkeyRegistered;
    private bool _allowClose;
    private DateTime _ignoreClipboardUntil;
    private DateTime _suppressDeactivateUntil;
    private DateTime _closedByDeactivateAt;

    public MainWindow(ClipStore store)
    {
        _store = store;
        _viewModel = new MainViewModel(store);
        DataContext = _viewModel;
        InitializeComponent();
        StartupCheckBox.IsChecked = Services.StartupService.IsEnabled();
        LoadRetentionBox();
    }

    public void TogglePanel()
    {
        if (IsVisible)
            HidePanel();
        else if ((DateTime.UtcNow - _closedByDeactivateAt).TotalMilliseconds < 400)
            return;
        else
            ShowPanel();
    }

    public void ShowPanel()
    {
        _suppressDeactivateUntil = DateTime.UtcNow.AddMilliseconds(300);
        PositionBottomRight();
        Show();
        Activate();
        SearchBox.Focus();
        Keyboard.Focus(SearchBox);
        _viewModel.RefreshItems();
    }

    public void HidePanel() => Hide();

    public void PrepareShutdown()
    {
        _allowClose = true;
        if (_hwndSource is not null)
        {
            if (_hotkeyRegistered)
            {
                NativeMethods.UnregisterHotKey(_hwndSource.Handle, NativeMethods.HotkeyId);
                _hotkeyRegistered = false;
            }

            NativeMethods.RemoveClipboardFormatListener(_hwndSource.Handle);
        }
    }

    private void PositionBottomRight()
    {
        var work = SystemParameters.WorkArea;
        Left = work.Right - Width - 12;
        Top = work.Bottom - Height - 12;
    }

    private void Window_SourceInitialized(object? sender, EventArgs e)
    {
        _hwndSource = (HwndSource)PresentationSource.FromVisual(this)!;
        _hwndSource.AddHook(WndProc);

        try
        {
            _hotkeyRegistered = NativeMethods.RegisterHotKey(
                _hwndSource.Handle,
                NativeMethods.HotkeyId,
                NativeMethods.MOD_CONTROL | NativeMethods.MOD_SHIFT,
                NativeMethods.VkV);
        }
        catch
        {
            _hotkeyRegistered = false;
        }

        try
        {
            NativeMethods.AddClipboardFormatListener(_hwndSource.Handle);
        }
        catch
        {
            // ignore
        }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == NativeMethods.WM_HOTKEY && wParam.ToInt32() == NativeMethods.HotkeyId)
        {
            TogglePanel();
            handled = true;
        }
        else if (msg == NativeMethods.WM_CLIPBOARDUPDATE)
        {
            Dispatcher.BeginInvoke(ProcessClipboardUpdate);
            handled = true;
        }

        return IntPtr.Zero;
    }

    private void ProcessClipboardUpdate()
    {
        if (DateTime.UtcNow < _ignoreClipboardUntil)
            return;

        var capture = ClipboardReader.TryCapture(_store);
        if (capture is null)
            return;

        var inserted = _store.InsertOrBump(
            capture.Kind,
            capture.TextContent,
            capture.ImagePath,
            capture.FilesJson,
            capture.Preview);

        if (!inserted && capture.Kind == "image" && !string.IsNullOrEmpty(capture.ImagePath))
        {
            try
            {
                File.Delete(capture.ImagePath);
            }
            catch
            {
                // ignore
            }
        }

        if (IsVisible)
            _viewModel.RefreshItems();
    }

    public void RestoreItem(ClipItem item)
    {
        _ignoreClipboardUntil = DateTime.UtcNow.AddMilliseconds(500);
        if (!ClipboardWriter.TryRestore(item))
            _ignoreClipboardUntil = DateTime.MinValue;
        else
            HidePanel();
    }

    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_allowClose)
            return;
        e.Cancel = true;
        HidePanel();
    }

    private void Window_Deactivated(object? sender, EventArgs e)
    {
        if (DateTime.UtcNow < _suppressDeactivateUntil)
            return;
        if (!IsVisible)
            return;
        HidePanel();
        if (Left > -1000)
            _closedByDeactivateAt = DateTime.UtcNow;
    }

    private void Window_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            HidePanel();
            e.Handled = true;
        }
    }

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 1)
            DragMove();
    }

    private void RecentRail_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.IsSettings = false;
        RecentPanel.Visibility = Visibility.Visible;
        SettingsPanel.Visibility = Visibility.Collapsed;
        RecentAccent.Visibility = Visibility.Visible;
        SettingsAccent.Visibility = Visibility.Collapsed;
    }

    private void SettingsRail_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.IsSettings = true;
        RecentPanel.Visibility = Visibility.Collapsed;
        SettingsPanel.Visibility = Visibility.Visible;
        RecentAccent.Visibility = Visibility.Collapsed;
        SettingsAccent.Visibility = Visibility.Visible;
        StartupCheckBox.IsChecked = Services.StartupService.IsEnabled();
        LoadRetentionBox();
    }

    private void LoadRetentionBox() =>
        RetentionDaysBox.Text = _store.GetRetentionDays().ToString();

    private void RetentionDaysBox_LostFocus(object sender, RoutedEventArgs e) =>
        ApplyRetentionDays();

    private void RetentionDaysBox_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
            return;
        ApplyRetentionDays();
        e.Handled = true;
    }

    private void ApplyRetentionDays()
    {
        var text = RetentionDaysBox.Text.Trim();
        if (text.Length == 0)
        {
            _store.SetRetentionDays(0);
            RetentionDaysBox.Text = "0";
            return;
        }

        if (!int.TryParse(text, out var days) || days < 0)
        {
            LoadRetentionBox();
            return;
        }

        if (days > 3650)
            days = 3650;

        RetentionDaysBox.Text = days.ToString();
        _store.SetRetentionDays(days);
        _store.PurgeExpired();
        _viewModel.RefreshItems();
    }

    private void ItemsList_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject d &&
            FindAncestor<System.Windows.Controls.Button>(d) is not null)
            return;

        if (e.OriginalSource is not DependencyObject source)
            return;

        var container = ItemsList.ContainerFromElement(source) as System.Windows.Controls.ListBoxItem;
        if (container?.DataContext is ClipItem item)
            RestoreItem(item);
    }

    private static T? FindAncestor<T>(DependencyObject current) where T : DependencyObject
    {
        while (current is not null)
        {
            if (current is T match)
                return match;
            current = System.Windows.Media.VisualTreeHelper.GetParent(current);
        }
        return null;
    }

    private void ItemsList_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Enter && _viewModel.SelectedItem is ClipItem item)
        {
            RestoreItem(item);
            e.Handled = true;
        }
    }

    private void DeleteItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: ClipItem item })
            _viewModel.DeleteItem(item);
    }

    private void ClearHistory_Click(object sender, RoutedEventArgs e) =>
        _viewModel.ClearHistory();

    private void StartupCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (StartupCheckBox.IsChecked is bool enabled)
            Services.StartupService.SetEnabled(enabled);
    }
}
