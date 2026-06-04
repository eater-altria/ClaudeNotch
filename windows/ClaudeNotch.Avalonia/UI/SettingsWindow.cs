using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using ClaudeNotch.Core;

namespace ClaudeNotch.UI;

/// <summary>设置窗(占位,P2 完整实现:语言/通用/通知/价格/汇率/集成状态)。</summary>
public sealed class SettingsWindow : Window
{
    public Action? OnSettingsApplied;

    public SettingsWindow(AppSettings settings, ModelPriceStore prices, ExchangeRateStore rates)
    {
        Title = L.Tr("设置", "Settings");
        Width = 520; Height = 700;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Content = new TextBlock
        {
            Text = L.Tr("设置(开发中)", "Settings (WIP)"),
            Margin = new Thickness(24),
            FontFamily = new FontFamily(Palette.FontFamily),
            Foreground = Palette.Brush(Palette.Text),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
    }
}
