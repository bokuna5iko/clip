using System.Diagnostics;
using System.Windows;
using DragEventArgs = System.Windows.DragEventArgs;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using Point = System.Windows.Point;
using Button = System.Windows.Controls.Button;
using TextBox = System.Windows.Controls.TextBox;
using Image = System.Windows.Controls.Image;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;
using Brushes = System.Windows.Media.Brushes;
using Cursors = System.Windows.Input.Cursors;
using DataObject = System.Windows.DataObject;
using DataFormats = System.Windows.DataFormats;
using DragDropEffects = System.Windows.DragDropEffects;
using MessageBox = System.Windows.MessageBox;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Clip.Data;
using Clip.Models;
using Clip.Native;
using Clip.Services;

namespace Clip.Shelf;

public partial class ShelfWindow : Window
{
    public const string BookDragFormat = "ClipShelfBook";
    public const string PinDragFormat = "ClipShelfPin";

    private static readonly int[] ZoomSteps = [24, 32, 40, 48, 64, 80, 96];

    private readonly ShelfStore _store;
    private readonly DispatcherTimer _refreshTimer;
    private readonly Dictionary<string, List<ShelfBook>> _books = new();

    private int _pinZoom = 40;
    private int _recentZoom = 32;
    private int _groupZoom = 32;
    private bool _fullWidth;
    private double _normalLeft;
    private double _normalWidth;
    private bool _raised;
    private bool _allowClose;
    private string? _expandedGroupId;
    private string _query = "";

    private bool _dragBar;
    private double _dragBarStartX;
    private double _dragBarOriginLeft;
    private bool _gripDrag;
    private double _gripStartX;
    private double _gripOriginWidth;

    private bool _groupMove;
    private string? _groupMoveId;
    private Point _groupMoveStart;
    private double _groupOriginX;
    private double _groupOriginY;
    private bool _groupMoved;

    public ShelfWindow(ShelfStore store)
    {
        _store = store;
        InitializeComponent();
        LoadZooms();
        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(8) };
        _refreshTimer.Tick += (_, _) =>
        {
            _store.RemoveMissing();
            RefreshPins();
            RefreshRecent();
            RefreshGroups();
        };
    }

    public void ToggleRaise()
    {
        if (_raised)
            Lower();
        else
            Raise();
    }

    public void PrepareShutdown()
    {
        _allowClose = true;
        SaveGeometry();
        DesktopIconsService.Restore();
        _refreshTimer.Stop();
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        FitHeight();
        RestoreGeometry();
        DesktopIconsService.Hide();
        RefreshPins();
        RefreshRecent();
        RefreshGroups();
        Lower();
        _refreshTimer.Start();
    }

    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_allowClose)
            return;
        e.Cancel = true;
        Lower();
    }

    private void Window_StateChanged(object? sender, EventArgs e)
    {
        if (WindowState != WindowState.Minimized)
            return;
        WindowState = WindowState.Normal;
        if (_raised)
            Raise();
        else
            Lower();
    }

    private void Window_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_expandedGroupId is null)
            return;
        if (FindCard(e.OriginalSource as DependencyObject) is not null)
            return;
        _expandedGroupId = null;
        RefreshGroups();
    }

    private void Raise()
    {
        _raised = true;
        FitHeight();
        Topmost = true;
        Activate();
        _store.RemoveMissing();
        RefreshRecent();
        RefreshGroups();
    }

    private void Lower()
    {
        _raised = false;
        Topmost = false;
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero)
        {
            NativeMethods.SetWindowPos(
                hwnd,
                NativeMethods.HwndBottom,
                0,
                0,
                0,
                0,
                NativeMethods.SwpNoMove | NativeMethods.SwpNoSize | NativeMethods.SwpNoActivate);
        }
    }

    private void FitHeight()
    {
        var work = SystemParameters.WorkArea;
        Top = work.Top;
        Height = work.Height;
    }

    private void LoadZooms()
    {
        _pinZoom = ReadZoom("zoom_pin", 40);
        _recentZoom = ReadZoom("zoom_recent", 32);
        _groupZoom = ReadZoom("zoom_group", 32);
    }

    private int ReadZoom(string key, int fallback)
    {
        return int.TryParse(_store.GetState(key), out var value) && ZoomSteps.Contains(value)
            ? value
            : fallback;
    }

    private void RestoreGeometry()
    {
        var work = SystemParameters.WorkArea;
        _normalWidth = double.TryParse(_store.GetState("win_width"), out var width) ? width : 920;
        _normalLeft = double.TryParse(_store.GetState("win_left"), out var left) ? left : work.Left + 48;
        _fullWidth = _store.GetState("win_full") == "1";
        ApplyWidth();
    }

    private void ApplyWidth()
    {
        var work = SystemParameters.WorkArea;
        if (_fullWidth)
        {
            Left = work.Left;
            Width = work.Width;
            return;
        }

        Width = Math.Clamp(_normalWidth, MinWidth, work.Width);
        var maxLeft = Math.Max(work.Left, work.Right - Width);
        Left = Math.Clamp(_normalLeft, work.Left, maxLeft);
    }

    private void SaveGeometry()
    {
        if (!_fullWidth)
        {
            _normalLeft = Left;
            _normalWidth = Width;
        }

        _store.SetState("win_left", _normalLeft.ToString(System.Globalization.CultureInfo.InvariantCulture));
        _store.SetState("win_width", _normalWidth.ToString(System.Globalization.CultureInfo.InvariantCulture));
        _store.SetState("win_full", _fullWidth ? "1" : "0");
    }

    private void FullWidth_Click(object sender, RoutedEventArgs e)
    {
        if (!_fullWidth)
        {
            _normalLeft = Left;
            _normalWidth = Width;
            _fullWidth = true;
        }
        else
        {
            _fullWidth = false;
        }

        ApplyWidth();
        SaveGeometry();
        SizeGroupsCanvas();
    }

    private void DragBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source || FindAncestor<Button>(source) is not null)
            return;
        _dragBar = true;
        _dragBarStartX = System.Windows.Forms.Cursor.Position.X;
        _dragBarOriginLeft = Left;
        DragBar.CaptureMouse();
        e.Handled = true;
    }

    private void DragBar_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragBar || e.LeftButton != MouseButtonState.Pressed)
            return;
        var work = SystemParameters.WorkArea;
        var dx = System.Windows.Forms.Cursor.Position.X - _dragBarStartX;
        var maxLeft = Math.Max(work.Left, work.Right - Width);
        Left = Math.Clamp(_dragBarOriginLeft + dx, work.Left, maxLeft);
        _fullWidth = false;
    }

    private void DragBar_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragBar)
            return;
        _dragBar = false;
        DragBar.ReleaseMouseCapture();
        SaveGeometry();
    }

    private void RightGrip_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _gripDrag = true;
        _gripStartX = System.Windows.Forms.Cursor.Position.X;
        _gripOriginWidth = Width;
        RightGrip.CaptureMouse();
        e.Handled = true;
    }

    private void RightGrip_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_gripDrag || e.LeftButton != MouseButtonState.Pressed)
            return;
        var work = SystemParameters.WorkArea;
        var dx = System.Windows.Forms.Cursor.Position.X - _gripStartX;
        Width = Math.Clamp(_gripOriginWidth + dx, MinWidth, work.Right - Left);
        _fullWidth = false;
        SizeGroupsCanvas();
    }

    private void RightGrip_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_gripDrag)
            return;
        _gripDrag = false;
        RightGrip.ReleaseMouseCapture();
        _normalWidth = Width;
        _normalLeft = Left;
        SaveGeometry();
    }

    private void Band_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) == 0)
            return;
        if (sender is not FrameworkElement element || element.Tag is not string band)
            return;
        e.Handled = true;
        StepZoom(band, e.Delta > 0 ? 1 : -1);
    }

    private void StepZoom(string band, int direction)
    {
        var current = band switch
        {
            "pin" => _pinZoom,
            "recent" => _recentZoom,
            _ => _groupZoom
        };
        var index = Array.IndexOf(ZoomSteps, current);
        if (index < 0)
            index = 2;
        var next = Math.Clamp(index + direction, 0, ZoomSteps.Length - 1);
        if (next == index)
            return;
        var value = ZoomSteps[next];
        if (band == "pin")
        {
            _pinZoom = value;
            _store.SetState("zoom_pin", value.ToString());
            RefreshPins();
        }
        else if (band == "recent")
        {
            _recentZoom = value;
            _store.SetState("zoom_recent", value.ToString());
            RefreshRecent();
        }
        else
        {
            _groupZoom = value;
            _store.SetState("zoom_group", value.ToString());
            RefreshGroups();
        }
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _query = SearchBox.Text.Trim();
        RefreshPins();
        RefreshGroups();
    }

    private void RefreshPins()
    {
        PinnedHost.Children.Clear();
        foreach (var pin in _store.GetPins().Where(pin => Matches(pin.Path)))
            PinnedHost.Children.Add(CreateTile(pin.Path, _pinZoom, () => OpenPath(pin.Path), () => StartPinDrag(pin), PinMenu(pin)));
    }

    private void RefreshRecent()
    {
        RecentHost.Children.Clear();
        foreach (var path in RecentFilesService.GetTargets())
            RecentHost.Children.Add(CreateTile(path, _recentZoom, () => OpenPath(path), () => StartPathDrag(path), null));
    }

    private void RefreshGroups()
    {
        _books.Clear();
        GroupsCanvas.Children.Clear();
        foreach (var group in _store.GetGroups())
        {
            var books = _store.GetBooks(group.Id)
                .Where(book => Matches(book.Path))
                .ToList();
            if (_query.Length > 0 && books.Count == 0)
                continue;
            _books[group.Id] = _store.GetBooks(group.Id).ToList();
            var card = CreateGroupCard(group, books);
            Canvas.SetLeft(card, group.X);
            Canvas.SetTop(card, group.Y);
            GroupsCanvas.Children.Add(card);
        }

        SizeGroupsCanvas();
    }

    private bool Matches(string path)
    {
        if (_query.Length == 0)
            return true;
        return Path.GetFileName(path).Contains(_query, StringComparison.OrdinalIgnoreCase)
               || path.Contains(_query, StringComparison.OrdinalIgnoreCase);
    }

    private void SizeGroupsCanvas()
    {
        var width = Math.Max(GroupsScroll.ViewportWidth, Width - 40);
        var height = Math.Max(GroupsScroll.ViewportHeight, 240);
        foreach (var child in GroupsCanvas.Children.OfType<FrameworkElement>())
        {
            var right = Canvas.GetLeft(child) + child.Width;
            var bottom = Canvas.GetTop(child) + child.ActualHeight;
            if (right > width)
                width = right + 24;
            if (bottom > height)
                height = bottom + 24;
        }

        GroupsCanvas.Width = width;
        GroupsCanvas.Height = height;
    }

    private Border CreateGroupCard(ShelfGroup group, IReadOnlyList<ShelfBook> visibleBooks)
    {
        var expanded = group.Id == _expandedGroupId;
        var card = new Border
        {
            Width = Math.Max(240, 4 * (_groupZoom + 36) + 20),
            Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(expanded ? "#243044" : "#1A2230")!),
            BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#3B82F6")),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(8),
            Tag = group.Id,
            AllowDrop = true,
            Cursor = Cursors.Hand
        };
        card.DragOver += Group_DragOver;
        card.Drop += Group_Drop;

        var panel = new DockPanel();
        panel.Children.Add(CreateTitle(group));
        var body = expanded
            ? CreateBookWrap(_books[group.Id], openBooks: true)
            : CreateBookWrap(visibleBooks.Take(4).ToList(), openBooks: false);
        DockPanel.SetDock(panel.Children[0], Dock.Top);
        panel.Children.Add(body);
        card.Child = panel;
        card.MouseLeftButtonUp += (_, e) =>
        {
            if (expanded || _groupMoved)
                return;
            if (e.OriginalSource is DependencyObject source && FindAncestor<TextBox>(source) is not null)
                return;
            _expandedGroupId = group.Id;
            RefreshGroups();
            e.Handled = true;
        };
        return card;
    }

    private UIElement CreateTitle(ShelfGroup group)
    {
        var title = new TextBlock
        {
            Text = group.Name,
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(4, 2, 4, 8)
        };
        title.MouseLeftButtonDown += (_, e) =>
        {
            _groupMove = true;
            _groupMoved = false;
            _groupMoveId = group.Id;
            _groupMoveStart = e.GetPosition(GroupsCanvas);
            _groupOriginX = group.X;
            _groupOriginY = group.Y;
            title.CaptureMouse();
            e.Handled = true;
        };
        title.MouseMove += (_, e) =>
        {
            if (!_groupMove || _groupMoveId != group.Id || e.LeftButton != MouseButtonState.Pressed)
                return;
            var point = e.GetPosition(GroupsCanvas);
            if (Math.Abs(point.X - _groupMoveStart.X) + Math.Abs(point.Y - _groupMoveStart.Y) < 4)
                return;
            _groupMoved = true;
            var x = Math.Max(0, _groupOriginX + point.X - _groupMoveStart.X);
            var y = Math.Max(0, _groupOriginY + point.Y - _groupMoveStart.Y);
            if (title.Parent is DockPanel dock && dock.Parent is Border card)
            {
                Canvas.SetLeft(card, x);
                Canvas.SetTop(card, y);
            }

            group.X = x;
            group.Y = y;
        };
        title.MouseLeftButtonUp += (_, e) =>
        {
            if (_groupMoveId != group.Id)
                return;
            _groupMove = false;
            title.ReleaseMouseCapture();
            if (_groupMoved)
            {
                _store.MoveGroup(group.Id, group.X, group.Y);
                SizeGroupsCanvas();
                e.Handled = true;
            }
        };
        title.MouseRightButtonUp += (_, e) =>
        {
            GroupMenu(group).IsOpen = true;
            e.Handled = true;
        };
        title.MouseDown += (_, e) =>
        {
            if (e.ClickCount == 2 && e.ChangedButton == MouseButton.Left)
            {
                BeginRename(group, title);
                e.Handled = true;
            }
        };
        return title;
    }

    private void BeginRename(ShelfGroup group, TextBlock title)
    {
        if (title.Parent is not DockPanel dock)
            return;
        var box = new TextBox
        {
            Text = group.Name,
            Margin = title.Margin,
            Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#1E2128")),
            Foreground = Brushes.White,
            BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#3B82F6"))
        };
        var index = dock.Children.IndexOf(title);
        dock.Children.RemoveAt(index);
        dock.Children.Insert(index, box);
        box.Focus();
        box.SelectAll();
        void Commit()
        {
            _store.RenameGroup(group.Id, box.Text);
            RefreshGroups();
        }

        box.LostFocus += (_, _) => Commit();
        box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                Commit();
            }
        };
    }

    private WrapPanel CreateBookWrap(IReadOnlyList<ShelfBook> books, bool openBooks)
    {
        var wrap = new WrapPanel { Width = Math.Max(220, 4 * (_groupZoom + 36)) };
        if (!openBooks)
        {
            foreach (var book in books)
                wrap.Children.Add(CreateTile(book.Path, _groupZoom, null, null, null));
            return wrap;
        }

        for (var i = 0; i < books.Count; i++)
        {
            var book = books[i];
            var index = i;
            wrap.Children.Add(CreateTile(
                book.Path,
                _groupZoom,
                () => OpenPath(book.Path),
                () => StartBookDrag(book),
                BookMenu(book),
                e => DropOnBook(book, index, e)));
        }

        return wrap;
    }

    private FrameworkElement CreateTile(
        string path,
        int iconSize,
        Action? open,
        Action? drag,
        ContextMenu? menu,
        Action<DragEventArgs>? drop = null)
    {
        var panel = new StackPanel
        {
            Width = iconSize + 36,
            Margin = new Thickness(4),
            Cursor = open is null ? Cursors.Arrow : Cursors.Hand,
            AllowDrop = drop is not null,
            Tag = path
        };
        var image = new Image
        {
            Width = iconSize,
            Height = iconSize,
            Source = FileIconService.Get(path),
            HorizontalAlignment = HorizontalAlignment.Center,
            IsHitTestVisible = false
        };
        var label = new TextBlock
        {
            Text = Path.GetFileName(path),
            FontSize = 11,
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextAlignment = TextAlignment.Center,
            Margin = new Thickness(0, 4, 0, 0),
            IsHitTestVisible = false
        };
        panel.Children.Add(image);
        panel.Children.Add(label);
        panel.ToolTip = path;
        if (menu is not null)
            panel.ContextMenu = menu;

        var dragged = false;
        Point? start = null;
        panel.MouseLeftButtonDown += (_, e) =>
        {
            if (open is null && drag is null)
                return;
            start = e.GetPosition(panel);
            dragged = false;
            panel.CaptureMouse();
            e.Handled = true;
        };
        panel.MouseMove += (_, e) =>
        {
            if (start is null || e.LeftButton != MouseButtonState.Pressed || drag is null)
                return;
            var point = e.GetPosition(panel);
            if (Math.Abs(point.X - start.Value.X) + Math.Abs(point.Y - start.Value.Y) < 6)
                return;
            dragged = true;
            start = null;
            panel.ReleaseMouseCapture();
            drag();
        };
        panel.MouseLeftButtonUp += (_, e) =>
        {
            panel.ReleaseMouseCapture();
            if (!dragged && start is not null)
                open?.Invoke();
            start = null;
            e.Handled = open is not null;
        };
        if (drop is not null)
        {
            panel.DragOver += (_, e) =>
            {
                AcceptDrop(e);
            };
            panel.Drop += (_, e) =>
            {
                drop(e);
                e.Handled = true;
            };
        }

        return panel;
    }

    private void StartBookDrag(ShelfBook book)
    {
        var data = new DataObject();
        data.SetData(BookDragFormat, book.Id);
        DragDrop.DoDragDrop(this, data, DragDropEffects.Move);
    }

    private void StartPinDrag(ShelfPin pin)
    {
        var data = new DataObject();
        data.SetData(PinDragFormat, pin.Id);
        data.SetData(DataFormats.FileDrop, new[] { pin.Path });
        DragDrop.DoDragDrop(this, data, DragDropEffects.Copy | DragDropEffects.Move);
    }

    private void StartPathDrag(string path)
    {
        DragDrop.DoDragDrop(this, new DataObject(DataFormats.FileDrop, new[] { path }), DragDropEffects.Copy);
    }

    private void Pinned_DragOver(object sender, DragEventArgs e) => AcceptDrop(e);

    private void Pinned_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(BookDragFormat) && e.Data.GetData(BookDragFormat) is string bookId)
        {
            var book = _store.GetAllBooks().FirstOrDefault(item => item.Id == bookId);
            if (book is not null)
                _store.AddPin(book.Path);
        }

        foreach (var path in ReadDroppedPaths(e))
            _store.AddPin(path);
        RefreshPins();
        e.Handled = true;
    }

    private void Group_DragOver(object sender, DragEventArgs e) => AcceptDrop(e);

    private void Group_Drop(object sender, DragEventArgs e)
    {
        if (sender is not Border { Tag: string groupId })
            return;
        if (e.Data.GetDataPresent(BookDragFormat) && e.Data.GetData(BookDragFormat) is string bookId)
        {
            var count = _store.GetBooks(groupId).Count;
            _store.MoveBook(bookId, groupId, count);
        }
        else
        {
            foreach (var path in ReadDroppedPaths(e))
                _store.AddBook(groupId, path);
        }

        RefreshGroups();
        e.Handled = true;
    }

    private void DropOnBook(ShelfBook target, int index, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(BookDragFormat) && e.Data.GetData(BookDragFormat) is string bookId)
            _store.MoveBook(bookId, target.GroupId, index);
        else
        {
            foreach (var path in ReadDroppedPaths(e))
                _store.AddBook(target.GroupId, path);
        }

        RefreshGroups();
    }

    private static void AcceptDrop(DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop) || e.Data.GetDataPresent(BookDragFormat) || e.Data.GetDataPresent(PinDragFormat))
        {
            e.Effects = e.Data.GetDataPresent(BookDragFormat) ? DragDropEffects.Move : DragDropEffects.Copy;
            e.Handled = true;
        }
        else
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
        }
    }

    private IEnumerable<string> ReadDroppedPaths(DragEventArgs e)
    {
        if (e.Data.GetDataPresent(PinDragFormat) && e.Data.GetData(PinDragFormat) is string pinId)
        {
            var pin = _store.GetPins().FirstOrDefault(item => item.Id == pinId);
            if (pin is not null)
                yield return pin.Path;
            yield break;
        }

        if (e.Data.GetDataPresent(DataFormats.FileDrop) && e.Data.GetData(DataFormats.FileDrop) is string[] files)
        {
            foreach (var file in files)
                yield return file;
        }
    }

    private void Groups_PreviewMouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (FindCard(e.OriginalSource as DependencyObject) is not null)
            return;
        var position = e.GetPosition(GroupsCanvas);
        var menu = new ContextMenu();
        var create = new MenuItem { Header = "Новая группа" };
        create.Click += (_, _) =>
        {
            _store.AddGroup("Группа", Math.Max(0, position.X), Math.Max(0, position.Y));
            RefreshGroups();
        };
        menu.Items.Add(create);
        menu.IsOpen = true;
        e.Handled = true;
    }

    private void Groups_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _groupMoved = false;
    }

    private ContextMenu GroupMenu(ShelfGroup group)
    {
        var menu = new ContextMenu();
        var rename = new MenuItem { Header = "Переименовать" };
        rename.Click += (_, _) =>
        {
            _expandedGroupId = group.Id;
            RefreshGroups();
        };
        var delete = new MenuItem { Header = "Удалить группу" };
        delete.Click += (_, _) =>
        {
            if (MessageBox.Show("Удалить группу? Файлы на диске останутся.", "Шкаф", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
                return;
            if (_expandedGroupId == group.Id)
                _expandedGroupId = null;
            _store.DeleteGroup(group.Id);
            RefreshGroups();
        };
        menu.Items.Add(rename);
        menu.Items.Add(delete);
        return menu;
    }

    private ContextMenu BookMenu(ShelfBook book)
    {
        var menu = new ContextMenu();
        var remove = new MenuItem { Header = "Убрать из группы" };
        remove.Click += (_, _) =>
        {
            _store.RemoveBook(book.Id);
            RefreshGroups();
        };
        var delete = new MenuItem { Header = "Удалить файл" };
        delete.Click += (_, _) => DeletePath(book.Path);
        menu.Items.Add(remove);
        menu.Items.Add(delete);
        return menu;
    }

    private ContextMenu PinMenu(ShelfPin pin)
    {
        var menu = new ContextMenu();
        var remove = new MenuItem { Header = "Убрать из закреплённых" };
        remove.Click += (_, _) =>
        {
            _store.RemovePin(pin.Id);
            RefreshPins();
        };
        var delete = new MenuItem { Header = "Удалить файл" };
        delete.Click += (_, _) => DeletePath(pin.Path);
        menu.Items.Add(remove);
        menu.Items.Add(delete);
        return menu;
    }

    private void DeletePath(string path)
    {
        var name = Path.GetFileName(path);
        if (MessageBox.Show($"Удалить «{name}» в корзину?", "Шкаф", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK)
            return;
        if (!RecycleService.Send(path))
            return;
        _store.RemovePath(path);
        RefreshPins();
        RefreshGroups();
        RefreshRecent();
    }

    private static void OpenPath(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch
        {
            // ignore
        }
    }

    private Border? FindCard(DependencyObject? source)
    {
        while (source is not null)
        {
            if (source is Border { Tag: string })
                return (Border)source;
            source = VisualTreeHelper.GetParent(source);
        }

        return null;
    }

    private static T? FindAncestor<T>(DependencyObject? source) where T : DependencyObject
    {
        while (source is not null)
        {
            if (source is T match)
                return match;
            source = VisualTreeHelper.GetParent(source);
        }

        return null;
    }
}
