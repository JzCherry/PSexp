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
    private int? _expectedFileCount;
    private bool _placed;

    public ExportNoticeWindow()
    {
        InitializeComponent();
        _dismissTimer.Tick += (_, _) => Close();
        PointerEntered += (_, _) => _dismissTimer.Stop();
        // 只有终态才有倒计时可续：导出中卡片没有计时器，鼠标扫过屏幕底沿把默认 1 秒的间隔启起来，
        // 进度卡就会自己消失。
        PointerExited += (_, _) => { if (IsVisible && !ShowsProgress) _dismissTimer.Start(); };
    }

    /// <summary>卡片正挂在屏幕上当进度条用，还没到可以点击的程度。</summary>
    internal bool ShowsProgress { get; private set; }

    /// <summary>有总数就报“已出/总数”，Photoshop 没给总数时只能报已出多少个。</summary>
    public static string ProgressReadout(int exportedCount, int? expectedFileCount)
        => expectedFileCount is > 0
            ? $"{exportedCount} / {expectedFileCount.Value}"
            : $"{exportedCount} 个";

    public static string EndedTitle(bool cancelled) => cancelled ? "已停止" : "导出未完成";

    internal void ShowProgress(Screen screen, string outputDirectory, int? expectedFileCount, int exportedCount, bool opensParent)
    {
        ShowsProgress = true;
        _openDirectory = null;
        _dismissTimer.Stop();
        _expectedFileCount = expectedFileCount;
        Choose(Mark.Dots, Right.Count, bar: true, clickable: false);
        NoticeTitle.Text = "正在导出";
        // 结束才露面的"打开目录"提前把文字摆好（只靠透明度藏着）：右侧那一栏的宽度从第一帧就按最宽内容算定，
        // 换成结果态时列宽不会重排。
        SetOpenAffordance(opensParent);
        NoticePath.Text = outputDirectory;
        ToolTip.SetTip(OpenNoticeDirectory, outputDirectory);
        AutomationProperties.SetHelpText(OpenNoticeDirectory, outputDirectory);
        ApplyCount(exportedCount);
        PlaceAndShow(screen);
    }

    internal void UpdateProgress(int exportedCount)
    {
        if (ShowsProgress) ApplyCount(exportedCount);
    }

    internal void ShowSuccess(Screen screen, TimeSpan duration, int count, string outputDirectory, bool opensParent,
        Func<bool> openDirectory)
    {
        ShowsProgress = false;
        _openDirectory = openDirectory;
        Choose(Mark.Check, Right.Open, bar: false, clickable: true);
        NoticeTitle.Text = $"已导出 {count} 个文件";
        SetOpenAffordance(opensParent);
        NoticePath.Text = outputDirectory;
        ToolTip.SetTip(OpenNoticeDirectory, outputDirectory);
        AutomationProperties.SetHelpText(OpenNoticeDirectory, outputDirectory);
        AutomationProperties.SetName(OpenNoticeDirectory, $"{NoticeTitle.Text}，{NoticeAction.Text}");
        PlaceAndShow(screen);
        StartDismissTimer(duration);
    }

    /// <summary>没导出出结果：卡片留在原地换成不可点的终态，倒计时后自己关。</summary>
    internal void ShowEnded(TimeSpan duration, bool cancelled)
    {
        if (!ShowsProgress) return;
        ShowsProgress = false;
        _openDirectory = null;
        Choose(Mark.Cross, Right.None, bar: false, clickable: false);
        NoticeTitle.Text = EndedTitle(cancelled);
        AutomationProperties.SetName(OpenNoticeDirectory, NoticeTitle.Text);
        StartDismissTimer(duration);
    }

    private void SetOpenAffordance(bool opensParent) => NoticeAction.Text = opensParent ? "打开上级" : "打开目录";

    /// <summary>状态切换只动透明度和命中：藏起来的元件仍参与测量，卡片几何才不会二次变化。</summary>
    private void Choose(Mark mark, Right right, bool bar, bool clickable)
    {
        NoticeIconDots.Opacity = mark == Mark.Dots ? 1 : 0;
        NoticeIconCheck.Opacity = mark == Mark.Check ? 1 : 0;
        NoticeIconCross.Opacity = mark == Mark.Cross ? 1 : 0;
        NoticeCount.Opacity = right == Right.Count ? 1 : 0;
        NoticeOpenGroup.Opacity = right == Right.Open ? 1 : 0;
        NoticeProgress.Opacity = bar ? 1 : 0;
        OpenNoticeDirectory.IsHitTestVisible = clickable;
    }

    private enum Mark { Dots, Check, Cross }

    private enum Right { None, Count, Open }

    private void ApplyCount(int exportedCount)
    {
        var maximum = _expectedFileCount is > 0 ? _expectedFileCount.Value : 1;
        NoticeProgress.IsIndeterminate = _expectedFileCount is not > 0;
        NoticeProgress.Maximum = maximum;
        NoticeProgress.Value = Math.Min(exportedCount, maximum);
        NoticeCount.Text = ProgressReadout(exportedCount, _expectedFileCount);
        AutomationProperties.SetName(OpenNoticeDirectory, $"正在导出，{NoticeCount.Text}");
    }

    private void StartDismissTimer(TimeSpan duration)
    {
        _dismissTimer.Stop();
        _dismissTimer.Interval = duration;
        if (!IsPointerOver) _dismissTimer.Start();
    }

    /// <summary>位置只在第一次出现时算定，之后状态切换不再改它，卡片不会二次摆位。</summary>
    private void PlaceAndShow(Screen screen)
    {
        if (!_placed)
        {
            var area = screen.WorkingArea;
            var width = (int)Math.Ceiling(Width * screen.Scaling);
            var height = (int)Math.Ceiling(Height * screen.Scaling);
            var inset = (int)Math.Round(8 * screen.Scaling);
            WindowStartupLocation = WindowStartupLocation.Manual;
            Position = new PixelPoint(
                area.X + Math.Max(0, area.Width - width) / 2,
                Math.Max(area.Y, area.Bottom - height - inset));
            _placed = true;
        }

        if (!IsVisible) Show();
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
