using Avalonia;
using Avalonia.Controls;
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
        if (_isClosing || _updatePrompt is not null
            || model.AutomaticUpdateVersion is not { } version || _promptedUpdateVersion == version
            || Application.Current is App { IsExitRequested: true }) return;

        var owned = !_isHiddenToTray && IsVisible && IsActive && WindowState != WindowState.Minimized
            && !_isSnapAnimating && (!_isDocked || _isDockRevealed);
        var prompt = new UpdatePromptWindow(version, model.IsUpdateDownloaded)
        {
            Topmost = !owned || Topmost,
            ShowInTaskbar = !owned,
            WindowStartupLocation = owned ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen
        };
        var install = prompt.InstallAction;
        var applying = false;
        // Keep the consent tied to the version shown, even if a manual check runs behind the prompt.
        void RefreshPrompt(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(MainViewModel.CanApplyUpdate))
                install.IsEnabled = !applying && model.AutomaticUpdateVersion == version;
            if (!applying) return;
            if (e.PropertyName == nameof(MainViewModel.UpdateProgress) && !model.IsInstallingUpdate)
                prompt.ShowDownloadProgress(model.UpdateProgress);
            if (e.PropertyName == nameof(MainViewModel.IsInstallingUpdate) && model.IsInstallingUpdate)
                prompt.ShowInstalling();
        }
        model.PropertyChanged += RefreshPrompt;
        install.Click += async (_, _) =>
        {
            if (applying || model.AutomaticUpdateVersion != version) return;
            applying = true;
            prompt.BeginUpdate();
            if (model.IsUpdateDownloaded) prompt.ShowInstalling();
            else prompt.ShowDownloadProgress(0);
            try
            {
                await model.ApplyUpdateCommand.ExecuteAsync(null);
            }
            finally
            {
                applying = false;
                if (_updatePrompt == prompt)
                {
                    prompt.ShowUpdateAction(model.IsUpdateDownloaded, model.UpdateStatus);
                    install.IsEnabled = model.AutomaticUpdateVersion == version;
                }
            }
        };
        prompt.Closed += (_, _) =>
        {
            model.PropertyChanged -= RefreshPrompt;
            _updatePrompt = null;
        };
        _promptedUpdateVersion = version;
        _updatePrompt = prompt;
        if (owned) _ = prompt.ShowDialog(this);
        else prompt.Show();
    }
}
