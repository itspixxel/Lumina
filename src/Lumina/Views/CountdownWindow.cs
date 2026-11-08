using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Lumina.Views;

/// <summary>Small click-through countdown shown before a delayed capture.</summary>
internal sealed class CountdownWindow : Window
{
    private readonly TextBlock _text;

    private CountdownWindow()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        IsHitTestVisible = false;
        SizeToContent = SizeToContent.WidthAndHeight;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        _text = new TextBlock
        {
            FontSize = 72,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brushes.White,
            MinWidth = 90,
            TextAlignment = TextAlignment.Center,
        };
        Content = new Border
        {
            CornerRadius = new CornerRadius(24),
            Background = new SolidColorBrush(Color.FromArgb(0xCC, 0x1C, 0x1C, 0x1E)),
            Padding = new Thickness(36, 8, 36, 16),
            Child = _text,
        };
    }

    public static async Task RunAsync(int seconds)
    {
        var window = new CountdownWindow();
        window.Show();
        for (int i = seconds; i > 0; i--)
        {
            window._text.Text = i.ToString();
            await Task.Delay(1000);
        }
        window.Close();
        await Task.Delay(150); // let it fade before the screen is grabbed
    }
}
