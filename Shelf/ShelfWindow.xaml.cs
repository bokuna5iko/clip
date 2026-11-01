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
using VerticalAlignment = System.Windows.VerticalAlignment;
using Panel = System.Windows.Controls.Panel;
using Brush = System.Windows.Media.Brush;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
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

    private const string GroupTagPrefix = "group:";
    private const string NameTag = "name";
    private const string TileTag = "tile";
    private static readonly int[] ZoomSteps = [24, 32, 40, 48, 64, 80, 96];
    private static readonly SolidColorBrush GroupFaceBrush = Freeze("#2A2A2A");
    private static readonly SolidColorBrush GroupFaceHoverBrush = Freeze("#353535");
    private static readonly SolidColorBrush GroupBorderBrush = Freeze("#33FFFFFF");
    private static readonly SolidColorBrush TileHoverBrush = Freeze("#2E2E2E");

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
    private string? _renameGroupId;
    private string _query = "";
    private bool _pinsExpanded;
    private int _pinColumns = -1;

    private bool _dragBar;
    private double _dragBarStartX;
    private double _dragBarOriginLeft;
    private bool _gripDrag;
    private double _gripStartX;
    private double _gripOriginWidth;
    private bool _canvasLayout;
    private bool _groupMove;
    private bool _groupMoved;
    private string? _groupMoveId;
    private Point _groupMoveStart;
    private double _groupOriginX;
    private double _groupOriginY;

    public ShelfWindow(ShelfStore store)
    {
        _store = store;
        InitializeComponent();
        LoadZooms();
        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(8) };
        _refreshTimer.Tick += (_, _) =>
        {
            if (_groupMove)
                return;
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
        RoundCorners();
        DesktopIconsService.Hide();
        EnsureCanvasLayout();
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
        FullWidthButton.Content = _fullWidth ? "Обычная ширина" : "Во весь экран";
    }

    private void RoundCorners()
    {
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero)
                return;
            var preference = NativeMethods.DwmwcpRound;
            NativeMethods.DwmSetWindowAttribute(hwnd, NativeMethods.DwmwaWindowCornerPreference, ref preference, sizeof(int));
        }
        catch
        {
            // Older systems without rounded-corner policy.
        }
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
        _pinColumns = -1;
        RefreshPins();
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

    private void PinsMore_Click(object sender, RoutedEventArgs e)
    {
        _pinsExpanded = !_pinsExpanded;
        RefreshPins();
    }

    private void PinnedBand_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!e.WidthChanged)
            return;
        var columns = PinColumnCount();
        if (columns == _pinColumns)
            return;
        RefreshPins();
    }

    private void GroupsScroll_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!_canvasLayout)
        {
            EnsureCanvasLayout();
            if (_canvasLayout)
            {
                Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(RefreshGroups));
                return;
            }
        }

        if ((e.WidthChanged || e.HeightChanged) && !_groupMove)
            SizeGroupsCanvas();
    }

    private void RefreshPins()
    {
        var pins = _store.GetPins().Where(pin => Matches(pin.Path)).ToList();
        var columns = PinColumnCount();
        _pinColumns = columns;
        var capacity = Math.Max(1, columns) * 2;
        var showToggle = pins.Count > capacity;
        PinsMoreButton.Visibility = showToggle ? Visibility.Visible : Visibility.Collapsed;
        PinsMoreButton.Content = _pinsExpanded ? "Свернуть  ▴" : "Показать все  ▾";
        var visible = _pinsExpanded || !showToggle ? pins : pins.Take(capacity).ToList();

        PinsEmpty.Visibility = pins.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        PinnedScroller.Visibility = pins.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        PinnedHost.Columns = columns;
        PinnedHost.Rows = _pinsExpanded && pins.Count > capacity ? 0 : visible.Count > columns ? 2 : 1;
        PinnedHost.Children.Clear();
        foreach (var pin in visible)
        {
            PinnedHost.Children.Add(CreateTile(
                pin.Path,
                _pinZoom,
                () => OpenPath(pin.Path),
                () => StartPinDrag(pin),
                PinMenu(pin),
                stretch: true));
        }
    }

    private int PinColumnCount()
    {
        var tile = Math.Max(84, _pinZoom + 56);
        var width = PinnedBand.ActualWidth;
        if (width < 1)
            width = Math.Max(tile, ActualWidth - 64);
        return Math.Max(1, (int)Math.Floor(width / tile));
    }

    private void RefreshRecent()
    {
        var paths = RecentFilesService.GetTargets();
        RecentSection.Visibility = paths.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        RecentScroller.MaxHeight = 3 * (_recentZoom + 36);
        RecentHost.Children.Clear();
        foreach (var path in paths)
            RecentHost.Children.Add(CreateRecentRow(path));
    }

    private void RefreshGroups()
    {
        if (_groupMove)
            return;
        _books.Clear();
        GroupsCanvas.Children.Clear();
        var groups = _store.GetGroups()
            .OrderBy(group => group.Y)
            .ThenBy(group => group.X)
            .ThenBy(group => group.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        var shown = 0;
        foreach (var group in groups)
        {
            var all = _store.GetBooks(group.Id).ToList();
            var nameHit = _query.Length > 0 && group.Name.Contains(_query, StringComparison.OrdinalIgnoreCase);
            var books = nameHit ? all : all.Where(book => Matches(book.Path)).ToList();
            if (_query.Length > 0 && !nameHit && books.Count == 0)
                continue;
            _books[group.Id] = all;
            var card = CreateGroupCard(group, books);
            Canvas.SetLeft(card, group.X);
            Canvas.SetTop(card, group.Y);
            GroupsCanvas.Children.Add(card);
            shown++;
        }

        GroupsEmpty.Visibility = shown == 0 && _query.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        GroupsCanvas.UpdateLayout();
        SizeGroupsCanvas();
    }

    private bool Matches(string path)
    {
        if (_query.Length == 0)
            return true;
        return Path.GetFileName(path).Contains(_query, StringComparison.OrdinalIgnoreCase)
               || path.Contains(_query, StringComparison.OrdinalIgnoreCase);
    }

    private void EnsureCanvasLayout()
    {
        if (_canvasLayout)
            return;
        if (_store.GetState("group_layout") == "canvas")
        {
            _canvasLayout = true;
            return;
        }

        if (GroupsScroll.ActualWidth < 32)
            return;

        var groups = _store.GetGroups();
        if (groups.Count > 0 && groups.All(IsFlowIndex))
        {
            var face = GroupFaceWidth(false);
            var pitchX = face + 18;
            var pitchY = face + 52;
            var columns = Math.Max(1, (int)Math.Floor(GroupsScroll.ActualWidth / pitchX));
            var ordered = groups
                .OrderBy(group => group.Y)
                .ThenBy(group => group.X)
                .ThenBy(group => group.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
            for (var i = 0; i < ordered.Count; i++)
            {
                var x = 8 + (i % columns) * pitchX;
                var y = 8 + (i / columns) * pitchY;
                _store.MoveGroup(ordered[i].Id, x, y);
            }
        }

        _store.SetState("group_layout", "canvas");
        _canvasLayout = true;
    }

    private static bool IsFlowIndex(ShelfGroup group)
    {
        return Math.Abs(group.X) < 0.001
            && group.Y >= 0
            && group.Y < 500
            && Math.Abs(group.Y - Math.Round(group.Y)) < 0.001;
    }

    private double GroupFaceWidth(bool expanded)
    {
        return expanded
            ? 4 * (_groupZoom + 48) + 20
            : 4 * (_groupZoom + 12) + 16;
    }

    private void SizeGroupsCanvas()
    {
        var viewW = GroupsScroll.ViewportWidth > 1 ? GroupsScroll.ViewportWidth : Math.Max(0, GroupsScroll.ActualWidth - 1);
        var viewH = GroupsScroll.ViewportHeight > 1 ? GroupsScroll.ViewportHeight : Math.Max(0, GroupsScroll.ActualHeight - 1);
        if (viewW < 1)
            viewW = Math.Max(320, ActualWidth - 48);
        if (viewH < 1)
            viewH = 280;
        var width = 0d;
        var height = 0d;
        foreach (var child in GroupsCanvas.Children.OfType<FrameworkElement>())
        {
            var left = Canvas.GetLeft(child);
            var top = Canvas.GetTop(child);
            if (double.IsNaN(left))
                left = 0;
            if (double.IsNaN(top))
                top = 0;
            var childWidth = child.ActualWidth > 1 ? child.ActualWidth : child.Width;
            var childHeight = child.ActualHeight > 1 ? child.ActualHeight : child.Height;
            if (double.IsNaN(childWidth) || childWidth < 1)
                childWidth = GroupFaceWidth(false);
            if (double.IsNaN(childHeight) || childHeight < 1)
                childHeight = GroupFaceWidth(false) + 40;
            width = Math.Max(width, left + childWidth + 28);
            height = Math.Max(height, top + childHeight + 28);
        }

        width = Math.Max(width, Math.Max(0, viewW - 1));
        height = Math.Max(height, Math.Max(0, viewH - 1));
        if (_groupMove)
            return;
        if (double.IsNaN(GroupsCanvas.Width) || Math.Abs(GroupsCanvas.Width - width) > 0.5)
            GroupsCanvas.Width = width;
        if (double.IsNaN(GroupsCanvas.Height) || Math.Abs(GroupsCanvas.Height - height) > 0.5)
            GroupsCanvas.Height = height;
    }

    private void AutoScrollGroups()
    {
        var point = Mouse.GetPosition(GroupsScroll);
        const double edge = 36;
        const double step = 24;
        if (point.X < edge)
            GroupsScroll.ScrollToHorizontalOffset(GroupsScroll.HorizontalOffset - step);
        else if (point.X > GroupsScroll.ActualWidth - edge)
            GroupsScroll.ScrollToHorizontalOffset(GroupsScroll.HorizontalOffset + step);
        if (point.Y < edge)
            GroupsScroll.ScrollToVerticalOffset(GroupsScroll.VerticalOffset - step);
        else if (point.Y > GroupsScroll.ActualHeight - edge)
            GroupsScroll.ScrollToVerticalOffset(GroupsScroll.VerticalOffset + step);
    }

    private Border CreateGroupCard(ShelfGroup group, IReadOnlyList<ShelfBook> visibleBooks)
    {
        var expanded = group.Id == _expandedGroupId || _query.Length > 0;
        var faceWidth = GroupFaceWidth(expanded);
        var card = new Border
        {
            Tag = GroupTagPrefix + group.Id,
            AllowDrop = true,
            Background = Brushes.Transparent,
            Width = faceWidth,
            VerticalAlignment = VerticalAlignment.Top,
            HorizontalAlignment = HorizontalAlignment.Left,
            Cursor = Cursors.Hand,
            ContextMenu = GroupMenu(group)
        };
        card.DragOver += Group_DragOver;
        card.Drop += Group_Drop;
        Panel.SetZIndex(card, expanded ? 1 : 0);

        var face = new Border
        {
            Width = faceWidth,
            Background = GroupFaceBrush,
            BorderBrush = GroupBorderBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(8),
            Effect = CreateLift(),
            HorizontalAlignment = HorizontalAlignment.Left
        };
        if (visibleBooks.Count == 0)
            face.Child = CreateEmptyFace();
        else if (expanded)
            face.Child = CreateBookWrap(visibleBooks);
        else
            face.Child = CreateMosaic(visibleBooks);

        var caption = (Brush)FindResource("Brush.Muted");
        var captionHover = (Brush)FindResource("Brush.Foreground");
        var nameHost = new Grid
        {
            Width = faceWidth,
            Margin = new Thickness(0, 8, 0, 0),
            Tag = NameTag,
            Cursor = Cursors.SizeAll
        };
        var name = new TextBlock
        {
            Text = group.Name,
            FontSize = 12,
            Foreground = caption,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxHeight = 32,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Cursor = Cursors.SizeAll,
            ToolTip = group.Name
        };
        name.MouseDown += (_, e) =>
        {
            if (e.ClickCount != 2 || e.ChangedButton != MouseButton.Left)
                return;
            BeginRename(group, name);
            e.Handled = true;
        };
        nameHost.Children.Add(name);

        var stack = new StackPanel();
        stack.Children.Add(face);
        stack.Children.Add(nameHost);
        card.Child = stack;
        card.MouseEnter += (_, _) =>
        {
            face.Background = GroupFaceHoverBrush;
            name.Foreground = captionHover;
        };
        card.MouseLeave += (_, _) =>
        {
            if (_groupMoveId == group.Id)
                return;
            face.Background = GroupFaceBrush;
            name.Foreground = caption;
        };
        card.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount > 1)
                return;
            if (e.OriginalSource is not DependencyObject source)
                return;
            if (FindAncestor<TextBox>(source) is not null || IsTagged(source, TileTag))
                return;
            _groupMove = true;
            _groupMoved = false;
            _groupMoveId = group.Id;
            _groupMoveStart = e.GetPosition(GroupsCanvas);
            _groupOriginX = group.X;
            _groupOriginY = group.Y;
            Panel.SetZIndex(card, 8);
            if (!card.CaptureMouse())
            {
                _groupMove = false;
                _groupMoveId = null;
                Panel.SetZIndex(card, expanded ? 1 : 0);
                return;
            }

            e.Handled = true;
        };
        card.MouseMove += (_, e) =>
        {
            if (!_groupMove || _groupMoveId != group.Id || e.LeftButton != MouseButtonState.Pressed)
                return;
            var point = e.GetPosition(GroupsCanvas);
            if (!_groupMoved && Math.Abs(point.X - _groupMoveStart.X) + Math.Abs(point.Y - _groupMoveStart.Y) < 8)
                return;
            _groupMoved = true;
            card.Cursor = Cursors.SizeAll;
            card.Opacity = 0.94;
            var x = Math.Clamp(_groupOriginX + point.X - _groupMoveStart.X, 0, 4000);
            var y = Math.Clamp(_groupOriginY + point.Y - _groupMoveStart.Y, 0, 4000);
            Canvas.SetLeft(card, x);
            Canvas.SetTop(card, y);
            group.X = x;
            group.Y = y;
            var cardWidth = card.ActualWidth > 1 ? card.ActualWidth : faceWidth;
            var cardHeight = card.ActualHeight > 1 ? card.ActualHeight : faceWidth;
            if (x + cardWidth + 48 > GroupsCanvas.Width)
                GroupsCanvas.Width = x + cardWidth + 160;
            if (y + cardHeight + 48 > GroupsCanvas.Height)
                GroupsCanvas.Height = y + cardHeight + 160;
            AutoScrollGroups();
        };
        card.LostMouseCapture += (_, _) => FinishGroupMove(group, card, face, caption, captionHover, name);
        card.MouseLeftButtonUp += (_, e) =>
        {
            var moved = _groupMoved;
            var onName = e.OriginalSource is DependencyObject source && IsTagged(source, NameTag);
            if (card.IsMouseCaptured)
                card.ReleaseMouseCapture();
            else
                FinishGroupMove(group, card, face, caption, captionHover, name);
            if (moved || onName || expanded || visibleBooks.Count == 0)
            {
                e.Handled = true;
                return;
            }

            if (e.OriginalSource is DependencyObject origin && FindAncestor<TextBox>(origin) is not null)
                return;
            _expandedGroupId = group.Id;
            RefreshGroups();
            e.Handled = true;
        };

        if (_renameGroupId == group.Id)
        {
            _renameGroupId = null;
            var target = name;
            Dispatcher.BeginInvoke(() =>
            {
                if (target.Parent is not null)
                    BeginRename(group, target);
            });
        }

        return card;
    }

    private void FinishGroupMove(ShelfGroup group, Border card, Border face, Brush caption, Brush captionHover, TextBlock name)
    {
        if (_groupMoveId != group.Id)
            return;
        var moved = _groupMoved;
        _groupMove = false;
        _groupMoved = false;
        _groupMoveId = null;
        card.Opacity = 1;
        card.Cursor = Cursors.Hand;
        var over = card.IsMouseOver;
        face.Background = over ? GroupFaceHoverBrush : GroupFaceBrush;
        name.Foreground = over ? captionHover : caption;
        Panel.SetZIndex(card, group.Id == _expandedGroupId || _query.Length > 0 ? 1 : 0);
        if (!moved)
            return;
        _store.MoveGroup(group.Id, group.X, group.Y);
        SizeGroupsCanvas();
    }

    private FrameworkElement CreateEmptyFace()
    {
        var cell = _groupZoom + 12;
        var grid = new Grid
        {
            MinWidth = 4 * cell,
            MinHeight = 4 * cell
        };
        grid.Children.Add(new TextBlock
        {
            Text = "Пусто",
            FontSize = 13,
            Foreground = (Brush)FindResource("Brush.Muted"),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false
        });
        return grid;
    }

    private UniformGrid CreateMosaic(IReadOnlyList<ShelfBook> books)
    {
        var cell = _groupZoom + 12;
        var grid = new UniformGrid
        {
            Columns = 4,
            Rows = 4,
            MinWidth = 4 * cell,
            MinHeight = 4 * cell
        };
        var overflow = books.Count > 16;
        var shown = overflow ? books.Take(15) : books;
        foreach (var book in shown)
            grid.Children.Add(CreateGlyph(book));
        if (overflow)
            grid.Children.Add(CreateMoreGlyph(books.Count - 15));
        return grid;
    }

    private FrameworkElement CreateGlyph(ShelfBook book)
    {
        var cell = new Grid
        {
            Width = _groupZoom,
            Height = _groupZoom,
            Margin = new Thickness(6),
            ToolTip = Path.GetFileName(book.Path),
            ContextMenu = BookMenu(book)
        };
        cell.Children.Add(new Image
        {
            Width = _groupZoom,
            Height = _groupZoom,
            Source = FileIconService.Get(book.Path),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false
        });
        return cell;
    }

    private FrameworkElement CreateMoreGlyph(int extra)
    {
        var cell = new Grid
        {
            Width = _groupZoom,
            Height = _groupZoom,
            Margin = new Thickness(6)
        };
        cell.Children.Add(new TextBlock
        {
            Text = $"+{extra}",
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = 13,
            Foreground = (Brush)FindResource("Brush.Muted"),
            IsHitTestVisible = false
        });
        return cell;
    }

    private void BeginRename(ShelfGroup group, TextBlock title)
    {
        if (title.Parent is not Panel host)
            return;
        var box = new TextBox
        {
            Text = group.Name,
            Margin = title.Margin,
            Background = Freeze("#2A2A2A"),
            Foreground = Brushes.White,
            BorderBrush = Freeze("#5A5A5A"),
            TextAlignment = TextAlignment.Center,
            FontSize = 12
        };
        var index = host.Children.IndexOf(title);
        host.Children.RemoveAt(index);
        host.Children.Insert(index, box);
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

    private WrapPanel CreateBookWrap(IReadOnlyList<ShelfBook> books)
    {
        var pitch = _groupZoom + 48;
        var wrap = new WrapPanel { Width = 4 * pitch + 4 };
        for (var i = 0; i < books.Count; i++)
        {
            var book = books[i];
            var index = _books.TryGetValue(book.GroupId, out var all)
                ? all.FindIndex(item => item.Id == book.Id)
                : i;
            if (index < 0)
                index = i;
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

    private FrameworkElement CreateRecentRow(string path)
    {
        var row = new Border
        {
            CornerRadius = new CornerRadius(6),
            Background = Brushes.Transparent,
            Padding = new Thickness(8, 6, 8, 6),
            Margin = new Thickness(2),
            Cursor = Cursors.Hand,
            ToolTip = path
        };
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(_recentZoom + 12) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var image = new Image
        {
            Width = _recentZoom,
            Height = _recentZoom,
            Source = FileIconService.Get(path),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false
        };
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
        text.Children.Add(new TextBlock
        {
            Text = Path.GetFileName(path),
            FontSize = 13,
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        var folder = Path.GetFileName(Path.GetDirectoryName(path) ?? "");
        text.Children.Add(new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(folder) ? "Недавно" : folder,
            FontSize = 11,
            Foreground = (Brush)FindResource("Brush.Muted"),
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 1, 0, 0)
        });
        Grid.SetColumn(text, 1);
        grid.Children.Add(image);
        grid.Children.Add(text);
        row.Child = grid;
        row.MouseEnter += (_, _) => row.Background = TileHoverBrush;
        row.MouseLeave += (_, _) => row.Background = Brushes.Transparent;

        var dragged = false;
        Point? start = null;
        row.MouseLeftButtonDown += (_, e) =>
        {
            start = e.GetPosition(row);
            dragged = false;
            row.CaptureMouse();
            e.Handled = true;
        };
        row.MouseMove += (_, e) =>
        {
            if (start is null || e.LeftButton != MouseButtonState.Pressed)
                return;
            var point = e.GetPosition(row);
            if (Math.Abs(point.X - start.Value.X) + Math.Abs(point.Y - start.Value.Y) < 6)
                return;
            dragged = true;
            start = null;
            row.ReleaseMouseCapture();
            StartPathDrag(path);
        };
        row.MouseLeftButtonUp += (_, e) =>
        {
            row.ReleaseMouseCapture();
            if (!dragged && start is not null)
                OpenPath(path);
            start = null;
            e.Handled = true;
        };
        return row;
    }

    private FrameworkElement CreateTile(
        string path,
        int iconSize,
        Action? open,
        Action? drag,
        ContextMenu? menu,
        Action<DragEventArgs>? drop = null,
        bool stretch = false)
    {
        var tile = new Border
        {
            Width = stretch ? double.NaN : iconSize + 40,
            Margin = new Thickness(4),
            Padding = new Thickness(4, 8, 4, 6),
            CornerRadius = new CornerRadius(6),
            Cursor = open is null ? Cursors.Arrow : Cursors.Hand,
            AllowDrop = drop is not null,
            Background = Brushes.Transparent,
            HorizontalAlignment = stretch ? HorizontalAlignment.Stretch : HorizontalAlignment.Left,
            Tag = TileTag
        };
        var stack = new StackPanel { IsHitTestVisible = false };
        stack.Children.Add(new Image
        {
            Width = iconSize,
            Height = iconSize,
            Source = FileIconService.Get(path),
            HorizontalAlignment = HorizontalAlignment.Center
        });
        stack.Children.Add(new TextBlock
        {
            Text = Path.GetFileName(path),
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextAlignment = TextAlignment.Center,
            MaxHeight = 32,
            Margin = new Thickness(0, 6, 0, 0)
        });
        tile.Child = stack;
        tile.ToolTip = path;
        if (menu is not null)
            tile.ContextMenu = menu;
        tile.MouseEnter += (_, _) => tile.Background = TileHoverBrush;
        tile.MouseLeave += (_, _) => tile.Background = Brushes.Transparent;

        var dragged = false;
        Point? start = null;
        tile.MouseLeftButtonDown += (_, e) =>
        {
            if (open is null && drag is null)
                return;
            start = e.GetPosition(tile);
            dragged = false;
            tile.CaptureMouse();
            e.Handled = true;
        };
        tile.MouseMove += (_, e) =>
        {
            if (start is null || e.LeftButton != MouseButtonState.Pressed || drag is null)
                return;
            var point = e.GetPosition(tile);
            if (Math.Abs(point.X - start.Value.X) + Math.Abs(point.Y - start.Value.Y) < 6)
                return;
            dragged = true;
            start = null;
            tile.ReleaseMouseCapture();
            drag();
        };
        tile.MouseLeftButtonUp += (_, e) =>
        {
            tile.ReleaseMouseCapture();
            if (!dragged && start is not null)
                open?.Invoke();
            start = null;
            e.Handled = open is not null;
        };
        if (drop is not null)
        {
            tile.DragOver += (_, e) => AcceptDrop(e);
            tile.Drop += (_, e) =>
            {
                drop(e);
                e.Handled = true;
            };
        }

        return tile;
    }

    private static SolidColorBrush Freeze(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)!);
        brush.Freeze();
        return brush;
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
        if (sender is not Border { Tag: string tag } || !TryGroupId(tag, out var groupId))
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
        var menu = CreateMenu();
        menu.Items.Add(CreateItem("Новая группа", () =>
        {
            _store.AddGroup("Группа", Math.Max(0, position.X), Math.Max(0, position.Y));
            RefreshGroups();
        }));
        menu.PlacementTarget = GroupsCanvas;
        menu.IsOpen = true;
        e.Handled = true;
    }

    private ContextMenu GroupMenu(ShelfGroup group)
    {
        var menu = CreateMenu();
        menu.Items.Add(CreateItem("Переименовать", () =>
        {
            _renameGroupId = group.Id;
            RefreshGroups();
        }));
        menu.Items.Add(CreateItem("Удалить группу", () =>
        {
            if (MessageBox.Show("Удалить группу? Файлы на диске останутся.", "Шкаф", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
                return;
            if (_expandedGroupId == group.Id)
                _expandedGroupId = null;
            _store.DeleteGroup(group.Id);
            RefreshGroups();
        }));
        return menu;
    }

    private ContextMenu BookMenu(ShelfBook book)
    {
        var menu = CreateMenu();
        menu.Items.Add(CreateItem("Убрать из группы", () =>
        {
            _store.RemoveBook(book.Id);
            RefreshGroups();
        }));
        menu.Items.Add(CreateItem("Удалить файл", () => DeletePath(book.Path)));
        return menu;
    }

    private ContextMenu PinMenu(ShelfPin pin)
    {
        var menu = CreateMenu();
        menu.Items.Add(CreateItem("Убрать из закреплённых", () =>
        {
            _store.RemovePin(pin.Id);
            RefreshPins();
        }));
        menu.Items.Add(CreateItem("Удалить файл", () => DeletePath(pin.Path)));
        return menu;
    }

    private ContextMenu CreateMenu()
    {
        return new ContextMenu
        {
            Style = (Style)FindResource("ShelfContextMenu")
        };
    }

    private MenuItem CreateItem(string header, Action action)
    {
        var item = new MenuItem
        {
            Header = header,
            Style = (Style)FindResource("ShelfMenuItem")
        };
        item.Click += (_, _) => action();
        return item;
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

    private static bool TryGroupId(object? tag, out string groupId)
    {
        if (tag is string value && value.StartsWith(GroupTagPrefix, StringComparison.Ordinal))
        {
            groupId = value[GroupTagPrefix.Length..];
            return groupId.Length > 0;
        }

        groupId = "";
        return false;
    }

    private static bool IsTagged(DependencyObject? source, string tag)
    {
        while (source is not null)
        {
            if (source is FrameworkElement element && Equals(element.Tag, tag))
                return true;
            if (source is Border border && TryGroupId(border.Tag, out _))
                return false;
            source = VisualTreeHelper.GetParent(source);
        }

        return false;
    }

    private static DropShadowEffect CreateLift()
    {
        return new DropShadowEffect
        {
            Color = Colors.Black,
            BlurRadius = 16,
            ShadowDepth = 1,
            Opacity = 0.35,
            RenderingBias = RenderingBias.Performance
        };
    }

    private static Border? FindCard(DependencyObject? source)
    {
        while (source is not null)
        {
            if (source is Border border && TryGroupId(border.Tag, out _))
                return border;
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
