using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using ClaudeNotch.Core;

namespace ClaudeNotch.UI;

/// <summary>数据统计窗(占位,P3 完整实现:KPI/热力图/趋势/打卡/模型/项目/缓存/导出)。</summary>
public sealed class AnalyticsWindow : Window
{
    public AnalyticsWindow(HistoryStore store)
    {
        Title = L.Tr("数据统计", "Analytics");
        Width = 960; Height = 780;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Content = new TextBlock
        {
            Text = L.Tr("数据统计(开发中)", "Analytics (WIP)"),
            Margin = new Thickness(24),
            FontFamily = new FontFamily(Palette.FontFamily),
            Foreground = Palette.Brush(Palette.Text),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
    }
}
