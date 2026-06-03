using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace ClaudeNotch.UI;

/// <summary>
/// 把 WinForms 的 ContextMenuStrip（托盘右键菜单）从经典/XP 灰渲染成 Win11 深色扁平观感。
/// WPF 主题库（iNKORE.UI.WPF.Modern）只能套到 WPF 控件，托盘菜单是 WinForms，故单独处理。
/// </summary>
static class DarkMenu
{
    static readonly Color Bg = Color.FromArgb(0x2C, 0x2C, 0x2E);
    static readonly Color Hover = Color.FromArgb(0x3A, 0x3A, 0x3C);
    static readonly Color Fg = Color.FromArgb(0xEC, 0xEC, 0xEC);
    static readonly Color Line = Color.FromArgb(0x3D, 0x3D, 0x40);

    /// <summary>对一个 ContextMenuStrip 应用深色扁平样式（含圆角、去图标边距、深色项）。</summary>
    public static void Apply(ContextMenuStrip menu)
    {
        menu.RenderMode = ToolStripRenderMode.Professional;
        menu.Renderer = new DarkRenderer();
        menu.BackColor = Bg;
        menu.ForeColor = Fg;
        menu.ShowImageMargin = false;          // 去掉左侧宽图标槽——经典菜单最明显的“老”特征
        menu.DropShadowEnabled = true;
        try { menu.Font = new Font("Segoe UI Variable Text", 9f); } catch { /* 缺字体自动回退 */ }

        foreach (ToolStripItem it in menu.Items)
        {
            it.ForeColor = Fg;
            if (it is ToolStripMenuItem mi) mi.Padding = new Padding(2, 3, 2, 3);
        }

        // Win11 圆角：弹出窗句柄就绪后用 DWM 设置（旧系统忽略，无害）
        menu.HandleCreated += (_, _) => TryRoundCorners(menu.Handle);
        if (menu.IsHandleCreated) TryRoundCorners(menu.Handle);
    }

    sealed class DarkRenderer : ToolStripProfessionalRenderer
    {
        public DarkRenderer() : base(new DarkColors()) { }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = Fg;
            base.OnRenderItemText(e);
        }

        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
        {
            // 极淡描边，避免经典菜单的双线浮雕边框
            using var pen = new Pen(Line);
            var r = e.AffectedBounds;
            e.Graphics.DrawRectangle(pen, 0, 0, r.Width - 1, r.Height - 1);
        }
    }

    sealed class DarkColors : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground => Bg;
        public override Color ImageMarginGradientBegin => Bg;
        public override Color ImageMarginGradientMiddle => Bg;
        public override Color ImageMarginGradientEnd => Bg;
        public override Color MenuItemSelected => Hover;
        public override Color MenuItemSelectedGradientBegin => Hover;
        public override Color MenuItemSelectedGradientEnd => Hover;
        public override Color MenuItemBorder => Hover;
        public override Color MenuBorder => Line;
        public override Color SeparatorDark => Line;
        public override Color SeparatorLight => Line;
    }

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    static void TryRoundCorners(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;
        try
        {
            int round = 2;   // DWMWCP_ROUND
            DwmSetWindowAttribute(hwnd, 33 /* WindowCornerPreference */, ref round, sizeof(int));
        }
        catch { }
    }
}
