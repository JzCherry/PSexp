using Avalonia.Controls;
using Avalonia.Interactivity;

namespace PsExporter.Views;

public partial class UpdatePromptWindow : Window
{
    public UpdatePromptWindow()
    {
        InitializeComponent();
        Opened += (_, _) => LaterButton.Focus();
    }

    internal UpdatePromptWindow(string version, bool downloaded) : this()
    {
        VersionLabel.Text = $"v{version}";
        ShowUpdateAction(downloaded);
    }

    internal Button InstallAction => InstallButton;

    internal void ShowUpdateAction(bool downloaded, string? status = null)
    {
        DownloadProgressRow.IsVisible = false;
        LaterButton.Content = "稍后";
        UpdateDescription.Text = status ?? (downloaded
            ? "更新已准备就绪，重启 PSexp 即可安装。"
            : "下载完成后，PSexp 将重启并安装更新。");
        InstallButton.Content = downloaded ? "重启并安装" : "下载并安装";
    }

    internal void ShowDownloadProgress(double percent)
    {
        DownloadProgress.Value = percent;
        DownloadPercent.Text = $"{Math.Floor(percent)}%";
        DownloadProgressRow.IsVisible = true;
        UpdateDescription.Text = percent >= 100 ? "正在校验更新包…" : "正在下载更新…";
    }

    internal void ShowInstalling()
    {
        DownloadProgressRow.IsVisible = false;
        UpdateDescription.Text = "正在重启并安装更新…";
    }

    internal void BeginUpdate()
    {
        InstallButton.IsEnabled = false;
        LaterButton.Content = "隐藏";
    }

    private void OnDismissClick(object? sender, RoutedEventArgs e) => Close();
}
