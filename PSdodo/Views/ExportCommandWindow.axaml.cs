using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using System.ComponentModel;
using PsExporter.Controls;
using PsExporter.Core.Automation;
using PsExporter.Models;
using PsExporter.Services;
using PsExporter.ViewModels;

namespace PsExporter.Views;

/// <summary>生成并复制一行外部导出指令；「试运行」把这条指令交回主界面执行。</summary>
public partial class ExportCommandWindow : Window
{
    private static readonly TimeSpan FeedbackDuration = TimeSpan.FromSeconds(1.5);

    private MainViewModel? _model;
    private readonly Dictionary<ExportPresetCategory, string?> _selection = [];
    private readonly DispatcherTimer _feedbackTimer = new() { Interval = FeedbackDuration };
    private bool _memoryApplied;
    private bool _followMainInterface = true;
    private bool _busy;
    private bool _usePreset = true;
    private ExportPresetCategory _category;
    private ExportInvocation? _invocation;
    private string? _command;
    private string _availability = "";

    public ExportCommandWindow()
    {
        InitializeComponent();
        CommandForms.ItemsSource = ExportInvocation.Forms;
        _feedbackTimer.Tick += (_, _) => ClearStatus();
        // 冒泡会让命令文本框先吃掉 Ctrl+C，这里用按键到达窗口时的第一站，但只在没有选区时接管。
        AddHandler(KeyDownEvent, OnWindowKeyDown, RoutingStrategies.Tunnel);
        PresetChoices.AddHandler(KeyDownEvent, OnPresetListKeyDown, RoutingStrategies.Tunnel);
        HelpContent.AddHandler(KeyDownEvent, OnWindowKeyDown, RoutingStrategies.Tunnel);
        var presetFlyout = PresetPicker.Flyout!;
        PresetPicker.SizeChanged += (_, e) => PresetChoices.Width = Math.Clamp(e.NewSize.Width - 10, 200, 300);
        presetFlyout.Opened += (_, _) => Dispatcher.UIThread.Post(FocusPresetChoice, DispatcherPriority.Loaded);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_memoryApplied || DataContext is not MainViewModel model) return;
        _memoryApplied = true;
        _model = model;
        var (width, height) = model.GetExportCommandWindowSize();
        Width = width;
        Height = height;
        _usePreset = model.ExportCommandUsePreset;
        foreach (var pair in model.ExportCommandPresets)
            if (Enum.TryParse<ExportPresetCategory>(pair.Key, out var category)) _selection[category] = pair.Value;
        _category = ExportPresetCatalog.CategoryFor(model.SelectedMode);
        if (string.IsNullOrEmpty(_selection.TryGetValue(_category, out var remembered) ? remembered : null))
            _selection[_category] = model.CurrentPresetCommandId;
        model.AutomationPresetsChanged += OnPresetsChanged;
        model.PropertyChanged += OnModelPropertyChanged;
        RefreshPresets();
    }

    // Photoshop 连接状态决定「试运行」能不能按，不跟着刷新就会出现「PS 已连上、钮还是灰的」。
    private void OnModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.IsPsRunning) or nameof(MainViewModel.IsBridgeAvailable)
            or nameof(MainViewModel.IsBusy) or nameof(MainViewModel.IsPreparingExport))
            Dispatcher.UIThread.Post(RefreshActions);
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        Focus();
    }

    protected override void OnClosed(EventArgs e)
    {
        _feedbackTimer.Stop();
        if (_model is { } model)
        {
            model.AutomationPresetsChanged -= OnPresetsChanged;
            model.PropertyChanged -= OnModelPropertyChanged;
            model.RememberExportCommandWindowSize(Width, Height);
        }
        base.OnClosed(e);
    }

    /// <summary>主界面又换了预设、而这个窗口还开着时被调用。窗口里手动选过就不再覆盖。</summary>
    public void SyncFromMainInterface()
    {
        if (_model is not { } model || !_followMainInterface) return;
        _category = ExportPresetCatalog.CategoryFor(model.SelectedMode);
        _selection[_category] = model.CurrentPresetCommandId;
        RefreshPresets();
    }

    private void OnPresetsChanged()
    {
        if (_followMainInterface) SyncFromMainInterface();
        else RefreshPresets();
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();

    private string? SelectedId => _selection.TryGetValue(_category, out var id) ? id : null;

    private void OnSourceClick(object? sender, RoutedEventArgs e)
    {
        _usePreset = (sender as Button)?.CommandParameter is "preset";
        _followMainInterface = false;
        RememberChoice();
        RefreshPreview();
    }

    private void OnCategoryClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { CommandParameter: string category } || !Enum.TryParse<ExportPresetCategory>(category, out var parsed)) return;
        _category = parsed;
        _followMainInterface = false;
        RefreshPresets();
        RememberChoice();
    }

    private void OnPresetClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: ExportPresetListItem item }) return;
        _selection[_category] = item.Id;
        _followMainInterface = false;
        DismissFlyout(PresetPicker);
        RefreshPresets();
        RememberChoice();
    }

    private void RememberChoice() => _model?.RememberExportCommandChoice(_usePreset, _category, SelectedId);

    private void RefreshPresets()
    {
        if (_model is not { } model)
        {
            PresetChoices.ItemsSource = Array.Empty<ExportPresetListItem>();
            RefreshPreview();
            return;
        }
        if (SelectedId is null)
            _selection[_category] = model.AutomationPresets.Where(item => item.Category == _category)
                .Select(item => item.Id).FirstOrDefault();
        var choices = model.BuildPresetChoices(_category, SelectedId);
        PresetChoices.ItemsSource = choices;
        PresetPicker.IsEnabled = choices.Count > 0;
        RefreshPreview();
    }

    private void RefreshPreview()
    {
        SourceTrack.SelectionValue = _usePreset ? "preset" : "current";
        CategoryTrack.SelectionValue = _category.ToString();
        PresetFields.IsVisible = _usePreset;
        var preset = _usePreset ? FindPreset(SelectedId) : null;
        PresetName.Text = preset?.Name ?? (PresetPicker.IsEnabled ? "请选择预设" : "此板块暂无预设");
        PresetPicker.SetValue(ToolTip.TipProperty, PresetName.Text);

        var note = "";
        if (!_usePreset)
        {
            SummaryStrip.IsVisible = false;
            note = "运行时读取主界面的导出板块和参数。";
        }
        else if (preset is null)
        {
            SummaryStrip.IsVisible = false;
            note = PresetPicker.IsEnabled ? "所选预设已删除，请重新选择。" : "这个板块还没有预设，先在主界面保存一份。";
        }
        else
        {
            var p = ExportPresetCatalog.ParametersForPreset(preset);
            var format = ExportPresetCatalog.DescribeFormat(p);
            var scope = ExportPresetCatalog.DescribeScope(_category, p);
            var destination = ExportPresetCatalog.DescribeDestination(p);
            var naming = ExportPresetCatalog.DescribeNaming(_category, p);
            SetBadge(FormatBadge, format);
            SetBadge(ScopeBadge, scope);
            SetBadge(DestinationBadge, destination);
            SummaryStrip.IsVisible = true;
            ToolTip.SetTip(SummaryStrip, string.Join(" · ", new[] { format, scope, destination, naming }.Where(value => value.Length > 0)));
        }
        SourceNote.Text = note;
        SourceNote.IsVisible = note.Length > 0;

        _invocation = !_usePreset
            ? MainViewModel.InvocationForCurrentInterface()
            : preset is null ? null : MainViewModel.InvocationForPreset(preset);
        _command = _invocation is null ? null : MainViewModel.BuildExportCommand(_invocation);
        var path = MainViewModel.ExecutablePath;
        ExePath.Text = path is { Length: > 0 } ? $"程序 · {path}" : "无法确定程序路径";
        ToolTip.SetTip(ExePath, path);
        var preview = _command ?? "";
        if (CommandPreview.Text != preview)
        {
            CommandPreview.Text = preview;
            CommandViewport.Offset = default;
            ClearStatus();
        }
        CommandViewport.IsVisible = preview.Length > 0;
        CommandPlaceholder.IsVisible = !CommandViewport.IsVisible;
        RefreshActions();
    }

    private static bool IsExecutableAvailable()
        => MainViewModel.ExecutablePath is { Length: > 0 } path && File.Exists(path);

    private void RefreshActions()
    {
        var pathUsable = IsExecutableAvailable();
        ExePath.Classes.Set("command-error", !pathUsable);
        var usable = pathUsable && _command is { Length: > 0 };
        var connected = _model is { IsPsRunning: true, IsBridgeAvailable: true };
        var exporting = _model is { IsBusy: true } or { IsPreparingExport: true };
        var status = !pathUsable ? "程序路径不可用"
            : !usable ? "等待有效预设"
            : !connected ? "PS 未连接"
            : exporting ? "已有导出任务"
            : "";
        if (CopyStatus.Text == _availability) CopyStatus.Text = status;
        _availability = status;
        CopyCommand.IsEnabled = !_busy && usable;
        RunOnce.IsEnabled = !_busy && usable && connected && !exporting;
    }

    private ExportPreset? FindPreset(string? id)
        => id is null || _model is null
            ? null
            : _model.AutomationPresets.FirstOrDefault(item => item.Category == _category && item.Id == id);

    private static void SetBadge(ContinuousBorder badge, string value)
    {
        badge.IsVisible = value.Length > 0;
        if (badge.Child is TextBlock text) text.Text = value;
    }

    private void ShowStatus(string message, bool isError)
    {
        CopyStatus.Text = message;
        CopyStatus.Classes.Set("command-error", isError);
        _feedbackTimer.Stop();
        if (!isError) _feedbackTimer.Start();
    }

    private void ClearStatus()
    {
        _feedbackTimer.Stop();
        CopyStatus.Text = _availability;
        CopyStatus.Classes.Set("command-error", false);
    }

    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (PresetPicker.Flyout?.IsOpen == true)
        {
            OnPresetListKeyDown(sender, e);
            return;
        }
        if (HelpButton.Flyout?.IsOpen == true)
        {
            if (e.Key == Key.Escape)
            {
                DismissFlyout(HelpButton);
                e.Handled = true;
            }
            return;
        }
        switch (e.Key)
        {
            case Key.Escape:
                Close();
                e.Handled = true;
                return;
            case Key.Enter when e.Source is not Button:
                if (e.Source is SelectableTextBlock boxWithSelection && !string.IsNullOrEmpty(boxWithSelection.SelectedText))
                    boxWithSelection.Copy();
                else if (CopyCommand.IsEnabled)
                    _ = CopyAsync();
                e.Handled = true;
                return;
            case Key.C when e.KeyModifiers == KeyModifiers.Control:
                if (e.Source is SelectableTextBlock box && !string.IsNullOrEmpty(box.SelectedText))
                {
                    box.Copy(); // 只复制选中的那一段
                    e.Handled = true;
                    return;
                }
                if (CopyCommand.IsEnabled)
                {
                    _ = CopyAsync();
                    e.Handled = true;
                }
                return;
        }
    }

    private void OnPresetListKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            DismissFlyout(PresetPicker);
            e.Handled = true;
            return;
        }
        var options = GetPresetOptionButtons();
        if (options.Length == 0) return;
        var index = Array.FindIndex(options, option => option.IsFocused);
        var next = e.Key switch
        {
            Key.Down => (index + 1) % options.Length,
            Key.Up => index <= 0 ? options.Length - 1 : index - 1,
            Key.Home => 0,
            Key.End => options.Length - 1,
            Key.Tab or Key.Enter when index >= 0 => index,
            _ => -1
        };
        if (next < 0) return;
        var target = options[next];
        if (e.Key is Key.Tab or Key.Enter) target.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        else
        {
            target.Focus(NavigationMethod.Directional);
            target.BringIntoView();
        }
        e.Handled = true;
    }

    private Button[] GetPresetOptionButtons() => PresetChoices.GetVisualDescendants().OfType<Button>().ToArray();

    private void FocusPresetChoice()
    {
        if (PresetPicker.Flyout?.IsOpen != true) return;
        var options = GetPresetOptionButtons();
        var target = options.FirstOrDefault(option => option.Classes.Contains("selected")) ?? options.FirstOrDefault();
        target?.Focus(NavigationMethod.Tab);
        target?.BringIntoView();
    }

    private static void DismissFlyout(Button anchor)
    {
        anchor.Flyout?.Hide();
        anchor.Focus(NavigationMethod.Tab);
    }

    private async Task CopyAsync()
    {
        RefreshActions();
        if (!CopyCommand.IsEnabled || _command is not { Length: > 0 } command) return;
        _busy = true;
        RefreshActions();
        try
        {
            if (Clipboard is { } clipboard)
            {
                await clipboard.SetTextAsync(command);
                if (IsVisible && _command == command) ShowStatus("已复制", false);
            }
            else ShowStatus("剪贴板不可用，请手动选中命令文本。", true);
        }
        catch
        {
            if (IsVisible && _command == command) ShowStatus("复制失败，请重试。", true);
        }
        finally
        {
            _busy = false;
            RefreshActions();
        }
    }

    private async void OnCopyCommandClick(object? sender, RoutedEventArgs e) => await CopyAsync();

    private void OnRunOnceClick(object? sender, RoutedEventArgs e)
    {
        if (!RunOnce.IsEnabled || _model is not { } model || _invocation is not { } invocation) return;
        if (model.TryStartExternalExport(invocation))
        {
            ShowStatus("已提交，进度在主界面。", false);
            if (Owner is MainWindow mainWindow)
            {
                // 子窗居中时会遮住主窗口的进度区，试运行后先让出视野。
                WindowState = WindowState.Minimized;
                if (model.IsViewSetting) model.ToggleSettingsViewCommand.Execute(null);
                // 这不是"把窗口叫过来"，只是让出视野：主窗口原位不动，不能被搬去鼠标旁。
                mainWindow.ActivateFromExternalRequest(keepWindowPosition: true);
            }
        }
        else ShowStatus("未开始：主界面正忙，或这条指令的预设已不存在。", true);
    }
}
