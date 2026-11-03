using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using PsExporter.ViewModels;

namespace PsExporter.Views;

public partial class MainWindow
{
    private Window? _updatePrompt;
    private string? _promptedUpdateVersion;

    internal void AttachUpdatePrompt(MainViewModel model)
    {
        void QueuePrompt() => Dispatcher.UIThread.Post(() => ShowUpdatePrompt(model), DispatcherPriority.Background);
        model.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.CanApplyUpdate)) QueuePrompt();
        };
        Activated += (_, _) => QueuePrompt();
        Closed += (_, _) => _updatePrompt?.Close();
    }

    private void ShowUpdatePrompt(MainViewModel model)
    {
        if (_isClosing || _isHiddenToTray || _updatePrompt is not null
            || model.AutomaticUpdateVersion is not { } version || _promptedUpdateVersion == version
            || Application.Current is App { IsExitRequested: true }) return;

        var owned = IsVisible && IsActive && !_isSnapAnimating && (!_isDocked || _isDockRevealed);
        var later = new Button { Content = "稍后", IsCancel = true, MinHeight = 36 };
        var install = new Button
        {
            Content = model.IsUpdateDownloaded ? "重启并安装" : "下载并安装",
            MinHeight = 36
        };
        var prompt = new Window
        {
            Title = "PSexp 软件更新",
            Icon = Icon,
            Width = 420,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            Topmost = owned && Topmost,
            ShowInTaskbar = !owned,
            WindowStartupLocation = owned ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen,
            Content = new StackPanel
            {
                Margin = new Thickness(24),
                Spacing = 14,
                Children =
                {
                    new TextBlock { Text = $"发现新版本 v{version}", FontSize = 20, FontWeight = FontWeight.SemiBold },
                    new TextBlock
                    {
                        Text = model.IsUpdateDownloaded ? "更新包已下载并校验。现在重启并安装吗？"
                            : "现在下载更新包并重启安装吗？",
                        TextWrapping = TextWrapping.Wrap
                    },
                    new ScrollViewer
                    {
                        IsVisible = model.HasUpdateChangelog,
                        MaxHeight = 160,
                        Content = new TextBlock { Text = model.UpdateChangelog, TextWrapping = TextWrapping.Wrap }
                    },
                    new StackPanel
                    {
                        Spacing = 8, Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
                        Children = { later, install }
                    }
                }
            }
        };
        // Keep the consent tied to the version shown, even if a manual check runs behind the prompt.
        void RefreshInstallButton(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(MainViewModel.CanApplyUpdate))
                install.IsEnabled = model.AutomaticUpdateVersion == version;
        }
        model.PropertyChanged += RefreshInstallButton;
        install.Click += async (_, _) =>
        {
            prompt.Close();
            if (model.AutomaticUpdateVersion == version) await model.ApplyUpdateCommand.ExecuteAsync(null);
        };
        later.Click += (_, _) => prompt.Close();
        prompt.Opened += (_, _) => later.Focus();
        prompt.Closed += (_, _) =>
        {
            model.PropertyChanged -= RefreshInstallButton;
            _updatePrompt = null;
        };
        _promptedUpdateVersion = version;
        _updatePrompt = prompt;
        if (owned) _ = prompt.ShowDialog(this);
        else prompt.Show();
    }
}
