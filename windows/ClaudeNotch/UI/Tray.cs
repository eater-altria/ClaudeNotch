using System.Drawing;
using System.Windows.Forms;
using ClaudeNotch.Core;

namespace ClaudeNotch.UI;

/// <summary>系统托盘图标 + 菜单（功能与 macOS 状态栏一致：设置/数据统计/刷新/退出）+ 气泡通知。</summary>
public sealed class Tray : IDisposable
{
    readonly NotifyIcon _icon;

    public Action? OpenSettings, OpenAnalytics, RefreshAll, ToggleWidget, Quit;

    public Tray()
    {
        _icon = new NotifyIcon
        {
            Icon = MakeIcon(),
            Visible = true,
            Text = "ClaudeNotch",
        };
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left) ToggleWidget?.Invoke();
        };
        RebuildMenu();
        L.Changed += RebuildMenu;
    }

    public void RebuildMenu()
    {
        var menu = new ContextMenuStrip();
        ToolStripMenuItem Item(string text, Action? act)
        {
            var mi = new ToolStripMenuItem(text);
            mi.Click += (_, _) => act?.Invoke();
            return mi;
        }
        menu.Items.Add(Item(L.Tr("设置…", "Settings…"), () => OpenSettings?.Invoke()));
        menu.Items.Add(Item(L.Tr("数据统计…", "Analytics…"), () => OpenAnalytics?.Invoke()));
        menu.Items.Add(Item(L.Tr("显示/隐藏挂件", "Show/Hide widget"), () => ToggleWidget?.Invoke()));
        menu.Items.Add(Item(L.Tr("立即刷新", "Refresh Now"), () => RefreshAll?.Invoke()));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(Item(L.Tr("退出", "Quit"), () => Quit?.Invoke()));
        _icon.ContextMenuStrip = menu;
    }

    public void SetTooltip(string text)
    {
        // NotifyIcon.Text 上限 63 字符
        _icon.Text = text.Length > 63 ? text[..63] : text;
    }

    public void ShowBalloon(string title, string body)
    {
        _icon.BalloonTipTitle = title;
        _icon.BalloonTipText = body;
        _icon.ShowBalloonTip(5000);
    }

    static Icon MakeIcon()
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            using var track = new Pen(Color.FromArgb(90, 255, 255, 255), 4f);
            g.DrawEllipse(track, 5, 5, 22, 22);
            using var arc = new Pen(Color.FromArgb(46, 199, 113), 4f) { StartCap = System.Drawing.Drawing2D.LineCap.Round, EndCap = System.Drawing.Drawing2D.LineCap.Round };
            g.DrawArc(arc, 5, 5, 22, 22, -90, 250);
        }
        return Icon.FromHandle(bmp.GetHicon());
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}
