using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using PsExporter.Models;
using PsExporter.Services;

namespace PsExporter.ViewModels;

public partial class MainViewModel
{
    [ObservableProperty] private string _sliceFolderName = string.Empty;
    private string _quickImageName = string.Empty;
    private string _layerImagePrefix = string.Empty;
    private DispatcherTimer? _parameterMemoryTimer;
    private bool _windowAnimationActive;
    private System.Threading.Tasks.TaskCompletionSource? _windowAnimationIdle;
    private bool _parameterMemoryDeferred;

    public void SetWindowAnimationActive(bool active)
    {
        if (active && !_windowAnimationActive)
            _windowAnimationIdle = new System.Threading.Tasks.TaskCompletionSource(System.Threading.Tasks.TaskCreationOptions.RunContinuationsAsynchronously);
        _windowAnimationActive = active;
        if (!active)
        {
            _windowAnimationIdle?.TrySetResult();
            _windowAnimationIdle = null;
        }
        if (active && _parameterMemoryTimer?.IsEnabled == true)
        {
            _parameterMemoryTimer.Stop();
            _parameterMemoryDeferred = true;
        }
        else if (!active && _parameterMemoryDeferred)
        {
            QueueParameterMemory();
        }
    }

    private async System.Threading.Tasks.Task WaitForWindowAnimationIdleAsync()
    {
        // 事件驱动等待；连续反向/下一段动画也必须完成后才应用识别结果，不轮询。
        while (_windowAnimationActive && _windowAnimationIdle is { } idle)
            await idle.Task;
    }

    private void QueueParameterMemory()
    {
        if (!_settingsInitialized || _applyingPreset) return;
        if (_windowAnimationActive)
        {
            _parameterMemoryDeferred = true;
            return;
        }
        if (_parameterMemoryTimer is null)
        {
            _parameterMemoryTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            _parameterMemoryTimer.Tick += (_, _) =>
            {
                // 即使停止计时器前已有 Tick 排队，也不在动画里落盘。
                if (_windowAnimationActive)
                {
                    _parameterMemoryTimer.Stop();
                    _parameterMemoryDeferred = true;
                    return;
                }
                FlushParameterMemory();
            };
        }
        _parameterMemoryTimer.Stop();
        _parameterMemoryTimer.Start();
    }

    public void FlushParameterMemory()
    {
        // 显式关闭/退出时仍可强制补存；动效期间的普通失焦由窗口延后调用。
        if (_parameterMemoryTimer?.IsEnabled != true && !_parameterMemoryDeferred) return;
        _parameterMemoryTimer?.Stop();
        _parameterMemoryDeferred = false;
        SaveBehaviorSettings();
    }

    public string SliceFolderNameError
    {
        get
        {
            ExportSettingsValidator.TryValidateSliceFolder((SliceFolderName ?? string.Empty).Trim(), out var error);
            return error;
        }
    }
    public bool HasSliceFolderNameError => !string.IsNullOrEmpty(SliceFolderNameError);
    partial void OnSliceFolderNameChanged(string value)
    {
        OnPropertyChanged(nameof(SliceFolderNameError));
        OnPropertyChanged(nameof(HasSliceFolderNameError));
        QueueParameterMemory();
    }
    partial void OnExportPathOptionChanged(ExportPathOption value) => QueueParameterMemory();
    partial void OnQualityChanged(int value) => QueueParameterMemory();
    partial void OnIsViewSettingChanged(bool value) => QueueParameterMemory();
    partial void OnImageNameChanged(string value)
    {
        if (SelectedMode == ExportMode.QuickExport) _quickImageName = value;
        else if (SelectedMode == ExportMode.Layers) _layerImagePrefix = value;
        QueueParameterMemory();
    }
}
