using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform;
using Avalonia.Threading;

namespace PsExporter.Views;

public partial class ExportNoticeWindow : Window
{
    private readonly DispatcherTimer _dismissTimer = new();
    private Func<bool>? _openDirectory;

    public ExportNoticeWindow()
    {
        InitializeComponent();
        _dismissTimer.Tick += (_, _) => Close();
        PointerEntered += (_, _) => _dismissTimer.Stop();
        PointerExited += (_, _) => { if (IsVisible) _dismissTimer.Start(); };
    }

    internal void ShowOn(Screen screen, TimeSpan duration, int count, string outputDirectory, bool opensParent, Func<bool> openDirectory)
    {
        _openDirectory = openDirectory;
        _dismissTimer.Interval = duration;
        NoticeTitle.Text = $"已导出 {count} 个文件";
        NoticeAction.Text = opensParent ? "打开上级" : "打开目录";
        NoticePath.Text = outputDirectory;
        ToolTip.SetTip(OpenNoticeDirectory, outputDirectory);
        AutomationProperties.SetHelpText(OpenNoticeDirectory, outputDirectory);
        AutomationProperties.SetName(OpenNoticeDirectory, $"{NoticeTitle.Text}，{NoticeAction.Text}");

        var area = screen.WorkingArea;
        var width = (int)Math.Ceiling(Width * screen.Scaling);
        var height = (int)Math.Ceiling(Height * screen.Scaling);
        var inset = (int)Math.Round(8 * screen.Scaling);
        WindowStartupLocation = WindowStartupLocation.Manual;
        Position = new PixelPoint(
            area.X + Math.Max(0, area.Width - width) / 2,
            Math.Max(area.Y, area.Bottom - height - inset));
        Show();
        if (!IsPointerOver) _dismissTimer.Start();
    }

    private void OnOpenClick(object? sender, RoutedEventArgs e)
    {
        if (_openDirectory?.Invoke() == true)
        {
            Close();
            return;
        }
        NoticeAction.Text = "打开失败";
        AutomationProperties.SetName(OpenNoticeDirectory, $"{NoticeTitle.Text}，打开目录失败");
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key != Key.Escape || e.Handled) return;
        e.Handled = true;
        Close();
    }

    protected override void OnClosed(EventArgs e)
    {
        _dismissTimer.Stop();
        base.OnClosed(e);
    }
}
