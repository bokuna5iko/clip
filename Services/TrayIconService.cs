using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Clip.Services;

public sealed class TrayIconService : IDisposable
{
    private readonly NotifyIcon _notifyIcon;
    private readonly Icon _icon;

    public event EventHandler? TogglePanelRequested;
    public event EventHandler? OpenRequested;
    public event EventHandler? ExitRequested;

    public TrayIconService()
    {
        _icon = CreateTrayIcon();
        _notifyIcon = new NotifyIcon
        {
            Icon = _icon,
            Text = "Clip",
            Visible = true
        };
        _notifyIcon.MouseClick += OnMouseClick;

        var menu = new ContextMenuStrip();
        var openItem = new ToolStripMenuItem("Открыть");
        openItem.Click += (_, _) => OpenRequested?.Invoke(this, EventArgs.Empty);
        var exitItem = new ToolStripMenuItem("Выход");
        exitItem.Click += (_, _) => ExitRequested?.Invoke(this, EventArgs.Empty);
        menu.Items.Add(openItem);
        menu.Items.Add(exitItem);
        _notifyIcon.ContextMenuStrip = menu;
    }

    private void OnMouseClick(object? sender, MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
            TogglePanelRequested?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _icon.Dispose();
    }

    private static Icon CreateTrayIcon()
    {
        const int size = 32;
        using var bmp = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            using var brush = new SolidBrush(Color.FromArgb(59, 130, 246));
            var rect = new RectangleF(2, 2, size - 4, size - 4);
            using var path = RoundedRect(rect, 6);
            g.FillPath(brush, path);

            using var pen = new Pen(Color.White, 2f);
            g.DrawLine(pen, 10, 9, 22, 9);
            g.DrawRectangle(pen, 10, 12, 12, 14);
            g.DrawLine(pen, 13, 16, 19, 16);
            g.DrawLine(pen, 13, 20, 19, 20);
        }

        var handle = bmp.GetHicon();
        var icon = Icon.FromHandle(handle);
        var clone = (Icon)icon.Clone();
        DestroyIcon(handle);
        return clone;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr handle);

    private static GraphicsPath RoundedRect(RectangleF bounds, float radius)
    {
        var path = new GraphicsPath();
        var d = radius * 2;
        path.AddArc(bounds.X, bounds.Y, d, d, 180, 90);
        path.AddArc(bounds.Right - d, bounds.Y, d, d, 270, 90);
        path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90);
        path.AddArc(bounds.X, bounds.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}
