using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Avalonia.Controls.Primitives;
using PsExporter.Controls;
using PsExporter.Models;
using PsExporter.Services;
using PsExporter.ViewModels;
using SkiaSharp;

namespace PsExporter.Views;

public partial class MainWindow : Window
{
    private bool _isDraggingQualityMeter;
    private readonly ScaleTransform _qualityFeedbackScale = new();
    private readonly Stopwatch _qualityFeedbackClock = new();
    private bool _qualityFeedbackFramePending;
    private double _qualityFeedbackStartScale = 1;
    private readonly DispatcherTimer _windowSizeSaveTimer;
    private readonly DispatcherTimer _statusAnimationTimer;
    private readonly DispatcherTimer _edgeHoverTimer;
    private readonly Stopwatch _snapClock = new();
    private PixelPoint _snapTargetPos;
    private PixelPoint _dockExpandedPos;
    private PixelPoint _dockHiddenPos;
    private DockEdge _dockedEdge = DockEdge.None;
    private bool _isDocked;
    private bool _isDockRevealed;
    private bool _isSnapAnimating;
    private bool _isEdgeFinishing;
    private bool _isClosing;
    private double _dockScaling = 1;
    private double _captureScale = 1;
    private int _frameCount;
    private double _lastFrameMs;
    private double _maxFrameGapMs;
    private int _edgeAnimationGeneration;
    private DateTime _pointerOutsideDockSince = DateTime.MinValue;
    private readonly Stopwatch _psPulseClock = new();
    private readonly Stopwatch _refreshRotationClock = new();
    private readonly Stopwatch _refreshSettleClock = new();
    private readonly ScaleTransform _psStatusScaleTransform = new();
    private readonly RotateTransform _refreshIconRotateTransform = new();
    private static readonly PixelPoint _offscreenParkPos = new(-32000, -32000);
    private SKBitmap? _genieCapturedBitmap;
    // 最近一次神灯快照与纹理保留到下次动作准备或退出收纳，避免展开末帧集中释放资源。
    private SKBitmap? _dockRevealBitmap;
    private DockIconWindow? _dockIconWindow;
    private DockIconPlacement _dockIconPlacement;
    private MainViewModel? _animationViewModel;
    private bool _wasRefreshingStatus;
    private bool _isRefreshSettling;
    private double _refreshSettleStartAngle;
    private bool _hasRestoredWindowSize;
    private bool _hasProbedCurrentActivation;
    private bool _hasConstraintScreenSignature;
    private PixelRect _constraintScreenBounds;
    private double _constraintScreenScaling;

    // 原生移动循环也可能由单击启动。窗口确实移动后才解除贴边。
    private const int WM_ENTERSIZEMOVE = 0x0231;
    private const int WM_EXITSIZEMOVE = 0x0232;
    private const int WM_MOVING = 0x0216;
    private const int GWL_WNDPROC = -4;
    private PixelPoint? _nativeMoveStartPosition;
    private bool _nativeMoveObserved;
    private IntPtr _originalWndProc;
    private WndProcDelegate? _subclassProc;   // 持有引用防止 GC 回收
    private bool _isSubclassed;

    private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr CallWindowProc(IntPtr lpPrevWndFunc, IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out NativePoint point);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    private IntPtr SubclassWndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        // 只约束本窗口动画期间的鼠标激活，不安装全局键鼠钩子。
        if (msg == 0x0021 && _isSnapAnimating) return new IntPtr(3); // WM_MOUSEACTIVATE / MA_NOACTIVATE
        if (msg == SummonHotkeyMessage && wParam.ToInt64() == SummonHotkeyId)
        {
            OnSummonHotkeyMessage();
            return IntPtr.Zero;
        }
        if (msg == RawInputMessage && _isPickingSummonProgram) ObserveSummonProgramPickInput(lParam);
        if (msg == 0x0112 && (wParam.ToInt64() & 0xFFF0) == 0xF020
            && TryQueueDockRetraction("native-system-minimize")) return IntPtr.Zero;
        if (msg == WM_ENTERSIZEMOVE)
        {
            _nativeMoveStartPosition = Position;
            _nativeMoveObserved = false;
        }
        else if (msg == WM_MOVING)
        {
            _nativeMoveObserved = true;
        }
        else if (msg == WM_EXITSIZEMOVE)
        {
            var startPosition = _nativeMoveObserved ? _nativeMoveStartPosition : null;
            var generation = _edgeAnimationGeneration;
            _nativeMoveStartPosition = null;
            _nativeMoveObserved = false;
            Dispatcher.UIThread.Post(() =>
            {
                if (generation != _edgeAnimationGeneration) return;
                if (_isDocked && startPosition is { } origin && Position != origin)
                    BeginUndockForDrag(restoreExpandedPosition: false);
                OnWindowMoveFinished();
            });
        }

        return CallWindowProc(_originalWndProc, hWnd, msg, wParam, lParam);
    }

    private void InstallWndProcSubclass()
    {
        if (_isSubclassed) return;
        var handle = TryGetPlatformHandle();
        if (handle is null) return;
        var hwnd = handle.Handle;
        if (hwnd == IntPtr.Zero) return;

        _subclassProc = SubclassWndProc;
        _originalWndProc = GetWindowLongPtr(hwnd, GWL_WNDPROC);
        SetWindowLongPtr(hwnd, GWL_WNDPROC, Marshal.GetFunctionPointerForDelegate(_subclassProc));
        _isSubclassed = true;
    }

    private void UninstallWndProcSubclass()
    {
        if (!_isSubclassed) return;
        var handle = TryGetPlatformHandle();
        if (handle is not null)
        {
            var hwnd = handle.Handle;
            if (hwnd != IntPtr.Zero)
                SetWindowLongPtr(hwnd, GWL_WNDPROC, _originalWndProc);
        }
        _isSubclassed = false;
        _subclassProc = null;
    }
    // ====== Win32 子类化结束 ======

    public MainWindow()
    {
        InitializeComponent();
        _settingsPageFadeTimer.Tick += OnSettingsPageFadeTick;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        PsStatusDot.RenderTransform = _psStatusScaleTransform;
        PsStatusDot.RenderTransformOrigin = new RelativePoint(0.5, 0.5, RelativeUnit.Relative);
        RefreshIcon.RenderTransform = _refreshIconRotateTransform;

        _windowSizeSaveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        _windowSizeSaveTimer.Tick += OnWindowSizeSaveTimerTick;
        _statusAnimationTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _statusAnimationTimer.Tick += OnStatusAnimationTimerTick;

        // 只在展开后补查系统焦点和指针；收纳静止时不轮询。
        _edgeHoverTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        _edgeHoverTimer.Tick += OnEdgeHoverTimerTick;

        Opened += OnWindowOpened;
        Activated += OnWindowActivated;
        Deactivated += OnWindowDeactivated;
        PropertyChanged += OnDockWindowPropertyChanged;
        PositionChanged += OnWindowPositionChanged;
        SizeChanged += OnWindowSizeChanged;
        Closing += OnWindowClosing;
        Closed += OnWindowClosed;
        Screens.Changed += OnScreensChanged;
        DataContextChanged += OnAnimationDataContextChanged;
        ActualThemeVariantChanged += (_, _) => ApplyExportHighlight();
        AttachAnimationViewModel(DataContext as MainViewModel);
        AttachQualityMeterPointerHandlers();
        QualityMeter.RenderTransform = _qualityFeedbackScale;
        QualityMeter.RenderTransformOrigin = new RelativePoint(0.5, 0.5, RelativeUnit.Relative);
        AddHandler(
            PointerPressedEvent,
            OnWindowPointerPressed,
            RoutingStrategies.Tunnel,
            handledEventsToo: true);
        // 抓取热键要先于下面的 Esc 阶梯与导出快捷键，否则组合键会被既有动作吃掉。
        AddHandler(KeyDownEvent, OnSummonHotkeyCaptureKeyDown, RoutingStrategies.Tunnel, handledEventsToo: true);
        HighlightPalette.AddHandler(KeyDownEvent, OnHighlightPaletteKeyDown, RoutingStrategies.Tunnel);
        AddHandler(KeyDownEvent, (_, args) =>
        {
            if (args.Handled || args.Key != Key.Escape) return;
            if (DismissLayerHelp()) { args.Handled = true; return; }
            // Close the innermost text menu first, keeping the preset name editor open.
            if (DismissTextInputMenu()) { args.Handled = true; return; }
            if (DataContext is MainViewModel { IsViewSetting: true } settingsModel)
            {
                if (IsPopupInputSource(args.Source)) return;
                settingsModel.ToggleSettingsViewCommand.Execute(null);
                SettingsToggleButton.Focus();
                args.Handled = true;
                return;
            }
            if (!IsPopupInputSource(args.Source) && ExportPresets.DismissOnEscape()) args.Handled = true;
        }, RoutingStrategies.Tunnel);
        AddHandler(KeyDownEvent, OnExportShortcutKeyDown, RoutingStrategies.Tunnel);
        AddHandler(KeyUpEvent, (_, args) =>
        {
            if (args.Key == Key.Enter) _exportEnterHeld = false;
        }, RoutingStrategies.Tunnel, handledEventsToo: true);
        SetWindowCornerRound();
    }

    public readonly record struct StatusPulseFrame(double Opacity, double Scale);

    public static StatusPulseFrame CalculatePsStatusPulse(bool isRunning, double elapsedMilliseconds)
    {
        if (!isRunning) return new StatusPulseFrame(1, 1);
        var phase = Math.Max(0, elapsedMilliseconds) % 1600 / 1600 * Math.PI * 2;
        var wave = (Math.Sin(phase - Math.PI / 2) + 1) / 2;
        return new StatusPulseFrame(0.58 + 0.42 * wave, 0.9 + 0.1 * wave);
    }

    public static double CalculateRefreshRotation(bool isRefreshing, double elapsedMilliseconds)
        => isRefreshing
            ? Math.Clamp(Math.Max(0, elapsedMilliseconds) / 700, 0, 1) * 360
            : 0;

    public static double CalculateRefreshSettleRotation(double startAngle, double elapsedMilliseconds)
    {
        var progress = Math.Clamp(Math.Max(0, elapsedMilliseconds) / 140, 0, 1);
        var eased = 1 - Math.Pow(1 - progress, 3);
        return Math.Clamp(startAngle, 0, 360) * (1 - eased);
    }

    public static bool IsRefreshSettleComplete(double elapsedMilliseconds)
        => elapsedMilliseconds >= 140;

    private void OnAnimationDataContextChanged(object? sender, EventArgs e)
        => AttachAnimationViewModel(DataContext as MainViewModel);

    private void AttachAnimationViewModel(MainViewModel? viewModel)
    {
        _animationViewModel?.SetWindowAnimationActive(false);
        InvalidateRetractSnapshot();
        if (ReferenceEquals(_animationViewModel, viewModel)) return;
        if (_animationViewModel is not null)
        {
            _animationViewModel.PropertyChanged -= OnAnimationViewModelPropertyChanged;
            _animationViewModel.DocumentChangeAvailable -= OnPhotoshopDocumentChanged;
            _animationViewModel.SettingsNavigationRequested -= OnSettingsNavigationRequested;
            _animationViewModel.ExportStarted -= OnExportStarted;
            _animationViewModel.ExportCompleted -= OnExportCompleted;
            _animationViewModel.ExportEndedWithoutResult -= OnExportEndedWithoutResult;
        }
        _exportNotice?.Close();
        _animationViewModel = viewModel;
        ApplyExportHighlight();
        if (_animationViewModel is not null)
        {
            _animationViewModel.PropertyChanged += OnAnimationViewModelPropertyChanged;
            _animationViewModel.DocumentChangeAvailable += OnPhotoshopDocumentChanged;
            _animationViewModel.SettingsNavigationRequested += OnSettingsNavigationRequested;
            _animationViewModel.ExportStarted += OnExportStarted;
            _animationViewModel.ExportCompleted += OnExportCompleted;
            _animationViewModel.ExportEndedWithoutResult += OnExportEndedWithoutResult;
        }
        UpdateStatusAnimationState(restartActiveClocks: true);
    }

    private void OnAnimationViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.CurrentExportHighlight))
            ApplyExportHighlight();
        if (e.PropertyName == nameof(MainViewModel.ShowExportNotice) && _animationViewModel?.ShowExportNotice == false)
            _exportNotice?.Close();
        if (e.PropertyName == nameof(MainViewModel.ExportedCount))
            OnExportProgressChanged();
        if (e.PropertyName is nameof(MainViewModel.IsBusy) or nameof(MainViewModel.IsRefreshingStatus)
            && _animationViewModel?.HasPendingDocumentChanges == true)
            QueueForegroundDocumentProbe();
        if ((e.PropertyName == nameof(MainViewModel.AutoRefreshOnActivated)
                && _animationViewModel?.AutoRefreshOnActivated == true)
            || (e.PropertyName == nameof(MainViewModel.IsAlwaysOnTop)
                && _animationViewModel?.IsAlwaysOnTop == true))
        {
            _hasProbedCurrentActivation = false;
            QueueForegroundDocumentProbe();
        }
        ++_edgeSceneRevision;
        _dockRevealBitmapStale = true;
        InvalidateRetractSnapshot();
        if (e.PropertyName == nameof(MainViewModel.Quality))
        {
            PulseQualityMeter();
        }
        if (e.PropertyName is nameof(MainViewModel.SelectedMode) or nameof(MainViewModel.IsViewSetting))
        {
            EndInputEditing();
            // 模式的字段数不同，进入新页面从顶部开始，避免沿用画板表单的旧滚动位置。
            ExportScrollViewer.Offset = default;
        }
        if (e.PropertyName == nameof(MainViewModel.SettingsSearchQuery))
            SettingsScrollViewer.Offset = default;
        if (e.PropertyName == nameof(MainViewModel.IsViewSetting) && _animationViewModel is { IsViewSetting: true })
        {
            SettingsScrollViewer.Offset = default;
            BeginSettingsPageFade();
        }
        if (e.PropertyName == nameof(MainViewModel.IsViewSetting) && _animationViewModel is { IsViewSetting: false })
            StopSettingsPageFade();
        if (string.IsNullOrEmpty(e.PropertyName)
            || e.PropertyName == nameof(MainViewModel.IsPsRunning)
            || e.PropertyName == nameof(MainViewModel.IsRefreshingStatus))
        {
            UpdateStatusAnimationState(restartActiveClocks: false);
            return;
        }

        if (e.PropertyName == nameof(MainViewModel.EnableEdgeSnap)
            && _animationViewModel is { EnableEdgeSnap: false }
            && _isDocked)
        {
            BeginUndockForDrag();
        }
    }

    private void UpdateStatusAnimationState(bool restartActiveClocks)
    {
        var isPsRunning = _animationViewModel?.IsPsRunning == true;
        var isRefreshing = _animationViewModel?.IsRefreshingStatus == true;

        if (isPsRunning)
        {
            if (restartActiveClocks || !_psPulseClock.IsRunning)
                _psPulseClock.Restart();
        }
        else
        {
            _psPulseClock.Reset();
            ApplyPsStatusPulse(CalculatePsStatusPulse(false, 0));
        }

        if (isRefreshing)
        {
            if (restartActiveClocks || !_refreshRotationClock.IsRunning)
            {
                _refreshRotationClock.Restart();
                _refreshSettleClock.Reset();
                _isRefreshSettling = false;
            }
        }
        else if (_wasRefreshingStatus)
        {
            _refreshRotationClock.Stop();
            _refreshSettleStartAngle = _refreshIconRotateTransform.Angle % 360;
            if (_refreshSettleStartAngle > 0.01)
            {
                _isRefreshSettling = true;
                _refreshSettleClock.Restart();
            }
            else
            {
                ResetRefreshAnimation();
            }
        }
        else if (!_isRefreshSettling)
        {
            ResetRefreshAnimation();
        }

        _wasRefreshingStatus = isRefreshing;
        if (IsVisible && WindowState != WindowState.Minimized
            && ShouldRunStatusAnimation(_isDocked, _isDockRevealed, _isSnapAnimating)
            && (isPsRunning || isRefreshing || _isRefreshSettling))
            _statusAnimationTimer.Start();
        else
            _statusAnimationTimer.Stop();
    }

    public static bool ShouldRunStatusAnimation(bool docked, bool revealed, bool animating)
        => !animating && (!docked || revealed);

    private void OnStatusAnimationTimerTick(object? sender, EventArgs e)
    {
        if (!IsVisible || WindowState == WindowState.Minimized || !ShouldRunStatusAnimation(_isDocked, _isDockRevealed, _isSnapAnimating))
        {
            _statusAnimationTimer.Stop();
            return;
        }
        var isPsRunning = _animationViewModel?.IsPsRunning == true;
        var isRefreshing = _animationViewModel?.IsRefreshingStatus == true;

        if (isPsRunning)
            ApplyPsStatusPulse(CalculatePsStatusPulse(true, _psPulseClock.Elapsed.TotalMilliseconds));
        if (isRefreshing)
        {
            _refreshIconRotateTransform.Angle = CalculateRefreshRotation(
                true,
                _refreshRotationClock.Elapsed.TotalMilliseconds);
        }
        else if (_isRefreshSettling)
        {
            _refreshIconRotateTransform.Angle = CalculateRefreshSettleRotation(
                _refreshSettleStartAngle,
                _refreshSettleClock.Elapsed.TotalMilliseconds);
            if (IsRefreshSettleComplete(_refreshSettleClock.Elapsed.TotalMilliseconds))
                ResetRefreshAnimation();
        }

        if (!isPsRunning && !isRefreshing && !_isRefreshSettling)
            _statusAnimationTimer.Stop();
    }

    private void ResetRefreshAnimation()
    {
        _refreshRotationClock.Reset();
        _refreshSettleClock.Reset();
        _isRefreshSettling = false;
        _refreshSettleStartAngle = 0;
        _refreshIconRotateTransform.Angle = 0;
    }

    private void ApplyPsStatusPulse(StatusPulseFrame frame)
    {
        PsStatusDot.Opacity = frame.Opacity;
        _psStatusScaleTransform.ScaleX = frame.Scale;
        _psStatusScaleTransform.ScaleY = frame.Scale;
    }

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        _isClosing = true;
        _exportNotice?.Close();
        _animationViewModel?.SetWindowAnimationActive(false);
        StopSettingsPageFade();
        _settingsPageFadeTimer.Tick -= OnSettingsPageFadeTick;
        Screens.Changed -= OnScreensChanged;
        _edgeAnimationGeneration++;
        _statusAnimationTimer.Stop();
        _windowSizeSaveTimer.Stop();
        _edgeHoverTimer.Stop();
        _snapClock.Stop();
        _psPulseClock.Stop();
        _refreshRotationClock.Stop();
        _refreshSettleClock.Stop();
        ClearDockAnimationSurface();
        CloseDockIcon();
        ClearDockRevealBitmap();
        _dockIconWindow?.Close();
        DetachSummonHotkey();
        UninstallWndProcSubclass();
        Deactivated -= OnWindowDeactivated;
        PropertyChanged -= OnDockWindowPropertyChanged;
        if (_animationViewModel is not null)
        {
            _animationViewModel.PropertyChanged -= OnAnimationViewModelPropertyChanged;
            _animationViewModel.DocumentChangeAvailable -= OnPhotoshopDocumentChanged;
            _animationViewModel.SettingsNavigationRequested -= OnSettingsNavigationRequested;
            _animationViewModel.ExportStarted -= OnExportStarted;
            _animationViewModel.ExportCompleted -= OnExportCompleted;
            _animationViewModel.ExportEndedWithoutResult -= OnExportEndedWithoutResult;
            _ = _animationViewModel.StopDocumentEventsAsync();
        }
        DataContextChanged -= OnAnimationDataContextChanged;
    }

    public readonly record struct WindowSizeConstraints(
        double Width,
        double Height,
        double MinWidth,
        double MinHeight,
        double MaxWidth,
        double MaxHeight);

    public static WindowSizeConstraints CalculateWindowConstraints(
        double requestedWidth,
        double requestedHeight,
        double workingAreaWidthDip,
        double workingAreaHeightDip,
        double safeMarginDip = 16)
    {
        var maxWidth = Math.Max(1, workingAreaWidthDip - safeMarginDip * 2);
        var maxHeight = Math.Max(1, workingAreaHeightDip - safeMarginDip * 2);
        var minWidth = Math.Min(AppSettings.MinimumWindowWidth, maxWidth);
        var minHeight = Math.Min(AppSettings.MinimumWindowHeight, maxHeight);
        var normalized = SettingsService.NormalizeWindowSize(requestedWidth, requestedHeight);
        var width = Math.Clamp(normalized.Width, minWidth, maxWidth);
        var height = Math.Clamp(normalized.Height, minHeight, maxHeight);
        return new WindowSizeConstraints(width, height, minWidth, minHeight, maxWidth, maxHeight);
    }

    public void RestoreSavedWindowSize()
    {
        if (_hasRestoredWindowSize || DataContext is not MainViewModel vm) return;
        var saved = vm.GetSavedWindowSize();
        ApplyScreenConstraints(saved.Width, saved.Height);
        _hasRestoredWindowSize = true;
    }

    private static bool NearlyEqual(double first, double second)
        => Math.Abs(first - second) < 0.1;

    private static bool HasSameScreenSignature(
        bool hasSignature,
        PixelRect currentBounds,
        double currentScaling,
        PixelRect nextBounds,
        double nextScaling)
        => hasSignature
           && currentBounds == nextBounds
           && NearlyEqual(currentScaling, nextScaling);

    private bool ApplyScreenConstraints(double requestedWidth, double requestedHeight, bool skipIfSameScreen = false, Screen? targetScreen = null)
    {
        var screen = targetScreen ?? Screens.ScreenFromWindow(this) ?? Screens.Primary;
        if (screen is null)
        {
            var normalized = SettingsService.NormalizeWindowSize(requestedWidth, requestedHeight);
            if (!NearlyEqual(MinWidth, AppSettings.MinimumWindowWidth))
                MinWidth = AppSettings.MinimumWindowWidth;
            if (!NearlyEqual(MinHeight, AppSettings.MinimumWindowHeight))
                MinHeight = AppSettings.MinimumWindowHeight;
            if (!NearlyEqual(Bounds.Width, normalized.Width))
                Width = normalized.Width;
            if (!NearlyEqual(Bounds.Height, normalized.Height))
                Height = normalized.Height;
            return true;
        }

        if (skipIfSameScreen && HasSameScreenSignature(
                _hasConstraintScreenSignature,
                _constraintScreenBounds,
                _constraintScreenScaling,
                screen.Bounds,
                screen.Scaling))
        {
            return false;
        }

        _constraintScreenBounds = screen.Bounds;
        _constraintScreenScaling = screen.Scaling;
        _hasConstraintScreenSignature = true;

        var scaling = Math.Max(screen.Scaling, 0.1);
        var constraints = CalculateWindowConstraints(
            requestedWidth,
            requestedHeight,
            screen.WorkingArea.Width / scaling,
            screen.WorkingArea.Height / scaling);
        if (!NearlyEqual(MinWidth, constraints.MinWidth))
            MinWidth = constraints.MinWidth;
        if (!NearlyEqual(MinHeight, constraints.MinHeight))
            MinHeight = constraints.MinHeight;
        if (!NearlyEqual(MaxWidth, constraints.MaxWidth))
            MaxWidth = constraints.MaxWidth;
        if (!NearlyEqual(MaxHeight, constraints.MaxHeight))
            MaxHeight = constraints.MaxHeight;
        if (!NearlyEqual(Bounds.Width, constraints.Width))
            Width = constraints.Width;
        if (!NearlyEqual(Bounds.Height, constraints.Height))
            Height = constraints.Height;
        return true;
    }

    private void OnWindowPositionChanged(object? sender, PixelPointEventArgs e)
    {
        InvalidateRetractSnapshot();
        if (_isRestoringPlacement || _isHiddenToTray || _isSnapAnimating || _isDocked) return;
        if (!_hasRestoredWindowSize || WindowState != WindowState.Normal) return;

        if (ApplyScreenConstraints(Bounds.Width, Bounds.Height, skipIfSameScreen: true))
            InvalidateVisual();

        _windowSizeSaveTimer.Stop();
        _windowSizeSaveTimer.Start();
        // 吸附评估由 WM_EXITSIZEMOVE 触发；此计时器仅保存位置。
    }

    /// <summary>
    /// 评估拖拽结束的位置。进入 48 DIP 的边缘热区即开始神灯收纳，最终变成贴边半露出图标。
    /// </summary>
    public bool EvaluateAndStartEdgeSnap(int thresholdDip = 48, int durationMs = DockAnimationMotion.HideDurationMs)
    {
        if (_isClosing || _isRestoringPlacement || !_placementRestorationCompleted || _isSnapAnimating || _isDocked || WindowState != WindowState.Normal) return false;
        if (DataContext is not MainViewModel vm || !vm.EnableEdgeSnap) return false;

        var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
        if (screen is null) return false;

        var scaling = Math.Max(screen.Scaling, 0.1);
        var widthPx = (int)Math.Round(Bounds.Width * scaling);
        var heightPx = (int)Math.Round(Bounds.Height * scaling);
        var windowRect = new PixelRect(Position.X, Position.Y, widthPx, heightPx);
        var dock = EdgeSnapHelper.CalculateDockTarget(
            windowRect,
            screen.WorkingArea,
            (int)Math.Round(thresholdDip * scaling),
            (int)Math.Round(12 * scaling));
        if (!dock.IsDockable) return false;

        var allScreenBounds = new System.Collections.Generic.List<PixelRect>();
        foreach (var availableScreen in Screens.All)
            allScreenBounds.Add(availableScreen.Bounds);
        if (!EdgeSnapHelper.IsOuterScreenEdge(screen.Bounds, allScreenBounds, dock.Edge))
            return false;

        _dockScaling = scaling;
        _dockedEdge = dock.Edge;
        _dockExpandedPos = dock.ExpandedPosition;
        _dockHiddenPos = dock.HiddenPosition;
        _dockIconPlacement = EdgeSnapHelper.CalculateDockIconPlacement(
            screen.WorkingArea,
            _dockExpandedPos,
            widthPx,
            heightPx,
            _dockedEdge,
            iconSizePx: (int)Math.Round(56 * scaling),
            // 仅露出一半图标；收纳态不保留完整窗口或额外窄条。
            edgeInsetPx: (int)Math.Round(28 * scaling));
        _isDocked = true;
        _isDockRevealed = false;
        vm.IsEdgeDocked = true;
        // 位置写盘移到动画交接后；关闭/托盘路径仍会独立补存。
        StartEdgeAnimation(_dockHiddenPos, durationMs);
        return true;
    }

    private SKBitmap? CaptureWindowBitmap()
    {
        SKBitmap? bitmap = null;
        try
        {
            var widthDip = Bounds.Width;
            var heightDip = Bounds.Height;
            if (widthDip <= 0 || heightDip <= 0) return null;

            var scaling = CalculateCaptureScale(widthDip, heightDip, _dockScaling);
            _captureScale = scaling;
            var pixelWidth = (int)Math.Max(1, Math.Round(widthDip * scaling));
            var pixelHeight = (int)Math.Max(1, Math.Round(heightDip * scaling));

            // Avalonia 12.1.1 的直接高 DPI Render 路径在阴影/裁剪恢复矩阵后，
            // 可能重复应用隐式 DPI，导致外框正常而内部文字/控件放大并被裁掉。
            // 用 96 DPI 目标与可缩放 VisualBrush 记录界面，再明确映射到物理像素。
            // 不能把已经错误放大的快照在播放末尾缩小来补偿。
            using var rtb = new RenderTargetBitmap(new PixelSize(pixelWidth, pixelHeight), new Vector(96, 96));
            using (var drawing = rtb.CreateDrawingContext())
            {
                var brush = new VisualBrush(RootGrid)
                {
                    Stretch = Stretch.Fill,
                    SourceRect = new RelativeRect(new Rect(0, 0, widthDip, heightDip), RelativeUnit.Absolute)
                };
                // 与真实窗口采用同一原点，包含 12 DIP 阴影边距。
                drawing.DrawRectangle(brush, null, new Rect(0, 0, pixelWidth, pixelHeight));
            }

            var info = new SKImageInfo(pixelWidth, pixelHeight, SKColorType.Bgra8888, SKAlphaType.Premul);
            bitmap = new SKBitmap(info);
            rtb.CopyPixels(
                new PixelRect(0, 0, pixelWidth, pixelHeight),
                bitmap.GetPixels(),
                bitmap.ByteCount,
                bitmap.RowBytes);
            bitmap.SetImmutable();
            return bitmap;
        }
        catch (Exception error)
        {
            bitmap?.Dispose();
            if (DataContext is MainViewModel diagnostics)
                diagnostics.LogDockAnimation("[dock] capture failed: " + error, failure: true);
            return null;
        }
    }

    private void ClearDockAnimationSurface(bool retainTexture = false, bool restoreInput = true)
    {
        if (restoreInput) ResetLiveWindowPreparation();
        InvalidateRetractSnapshot();
        ClearPointerAnimationSurface();
        if (!retainTexture) DisposePointerAnimationWindow();
        RestoreDockAnimationLayout();
        DockAnimationSurface.MeshData = null;
        if (!retainTexture) DockAnimationSurface.Bitmap = null;
        _genieCapturedBitmap?.Dispose();
        _genieCapturedBitmap = null;
    }

    private void ClearPointerAnimationSurface()
    {
        if (_pointerAnimationActive)
        {
            _pointerAnimationActive = false;
            _pointerAnimationWindow?.Clear();
        }
    }

    private void DisposePointerAnimationWindow()
    {
        ClearPointerAnimationSurface();
        var window = _pointerAnimationWindow;
        _pointerAnimationWindow = null;
        window?.Dispose();
    }

    private void ClearDockRevealBitmap()
    {
        _dockRevealBitmap?.Dispose();
        _dockRevealBitmap = null;
    }

    private void ShowDockIcon(double collapse = 1)
    {
        if (_dockIconWindow is null)
        {
            var sizeDip = _dockIconPlacement.SizePx / Math.Max(_dockScaling, 0.1);
            _dockIconWindow = new DockIconWindow(_dockIconPlacement.Position, sizeDip);
            _dockIconWindow.RevealRequested += OnDockIconRevealRequested;
            _dockIconWindow.SetTransition(collapse);
            _dockIconWindow.Show(); // 独立顶层工具窗，不随主窗口 Win+D 最小化。
        }
        _dockIconWindow.SetTransition(collapse);
        _dockIconWindow.ShowAt(_dockIconPlacement.Position, _dockIconPlacement.SizePx / _dockScaling, _dockScaling);
    }

    private void CloseDockIcon()
    {
        _dockIconWindow?.Park(_offscreenParkPos);
    }

    private void OnDockIconRevealRequested(object? sender, DockIconRevealEventArgs e)
    {
        if (!_isDocked || _isDockRevealed || _pointerDockPosition is not null || _dockMustFinishRetraction) return;
        // 首次拖到边缘时，不能因为刚出现的透明热区恰好落在鼠标下就立即弹回。
        if (_isSnapAnimating && !e.IsClick && !_edgeCanReverseOnPointerReturn) return;
        StartEdgeAnimation(_dockExpandedPos, DockAnimationMotion.RevealDurationMs);
    }

    private void BeginUndockForDrag(bool restoreExpandedPosition = true)
    {
        if (!_isDocked) return;
        _edgeHoverTimer.Stop();
        _edgeAnimationGeneration++;
        _isSnapAnimating = false;
        _isEdgeFinishing = false;
        _snapClock.Stop();
        ClearDockAnimationSurface();
        CloseDockIcon();
        ClearDockRevealBitmap();
        WindowTaskSwitcher.Configure(this, excluded: false);
        if (restoreExpandedPosition) Position = DockLivePosition;
        Opacity = 1.0;
        _isDocked = false;
        _isDockRevealed = false;
        _dockRevealedByHotkey = false;
        _dockHadForeground = false;
        _dockMustFinishRetraction = false;
        _pointerDockPosition = null;
        _resummonPointerAfterMotion = false;
        _dockedEdge = DockEdge.None;
        if (DataContext is MainViewModel vm)
        {
            vm.SetWindowAnimationActive(false);
            vm.IsEdgeDocked = false;
        }
        UpdateStatusAnimationState(restartActiveClocks: false);
        PersistWindowPlacement();
    }

    public static double CalculateCaptureScale(double width, double height, double screenScale)
    {
        if (width <= 0 || height <= 0 || !double.IsFinite(width * height)) return 1;
        if (!double.IsFinite(screenScale)) screenScale = 1;
        // 保留原屏幕像素密度，避免在展开末尾从低清纹理突然变成清晰文字。
        return Math.Min(Math.Max(screenScale, 0.1), Math.Sqrt(8_000_000 / (width * height)));
    }

    private static async System.Threading.Tasks.Task NextFrameAsync(Window window)
    {
        var completion = new System.Threading.Tasks.TaskCompletionSource();
        window.RequestAnimationFrame(_ => completion.TrySetResult());
        // 锁屏、最小化或系统暂停合成时仍要结束交接，不能让收纳状态永久锁住。
        await System.Threading.Tasks.Task.WhenAny(completion.Task, System.Threading.Tasks.Task.Delay(100));
    }

    private void OnScreensChanged(object? sender, EventArgs e)
    {
        if (_isClosing || _isRestoringPlacement || _isHiddenToTray || !_placementRestorationCompleted) return;
        if (_isDocked)
        {
            RestoreDockAfterScreenChange();
            return;
        }
        if (WindowState != WindowState.Normal) return;
        var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
        if (screen is null) return;
        ApplyScreenConstraints(Bounds.Width, Bounds.Height, targetScreen: screen);
        var width = (int)Math.Round(Width * screen.Scaling);
        var height = (int)Math.Round(Height * screen.Scaling);
        Position = new PixelPoint(
            Math.Clamp(Position.X, screen.WorkingArea.X, Math.Max(screen.WorkingArea.X, screen.WorkingArea.Right - width)),
            Math.Clamp(Position.Y, screen.WorkingArea.Y, Math.Max(screen.WorkingArea.Y, screen.WorkingArea.Bottom - height)));
        _windowSizeSaveTimer.Stop();
        _windowSizeSaveTimer.Start();
    }

    private void OnEdgeHoverTimerTick(object? sender, EventArgs e)
    {
        if (!CanRetractDock() || !_isDockRevealed || _isSnapAnimating)
        {
            _edgeHoverTimer.Stop();
            return;
        }

        if (IsDockWindowForeground()) _dockHadForeground = true;
        else if (_dockHadForeground || _dockRevealedByHotkey)
        {
            TryQueueDockRetraction("deactivated");
            return;
        }

        if (_dockRevealedByHotkey || _nativeMoveStartPosition is not null || !GetCursorPos(out var cursor)) return;

        var insideWindow = IsPointInsideWindow(cursor, _dockExpandedPos, _dockScaling) || IsPointInsideDockIcon(cursor);

        if (insideWindow)
        {
            InvalidateRetractSnapshot();
            _pointerOutsideDockSince = DateTime.MinValue;
            return;
        }

        if (_pointerOutsideDockSince == DateTime.MinValue)
        {
            _pointerOutsideDockSince = DateTime.UtcNow;
            QueueRetractSnapshot();
            return;
        }

        QueueRetractSnapshot();
        if ((DateTime.UtcNow - _pointerOutsideDockSince).TotalMilliseconds < (DataContext is MainViewModel timingModel ? timingModel.DockRetractDelayMs : 420)) return;

        _isDockRevealed = false;
        _pointerOutsideDockSince = DateTime.MinValue;
        StartEdgeAnimation(_dockHiddenPos, DockAnimationMotion.HideDurationMs);
    }

    private bool IsPointInsideWindow(NativePoint cursor, PixelPoint expandedPosition, double scaling)
    {
        var size = _edgeLayoutPrepared ? _edgeWindowSize : Bounds.Size;
        var widthPx = (int)Math.Round(size.Width * Math.Max(scaling, 0.1));
        var heightPx = (int)Math.Round(size.Height * Math.Max(scaling, 0.1));
        var slack = (int)Math.Ceiling(24 * Math.Max(scaling, 0.1));
        return cursor.X >= expandedPosition.X - slack
            && cursor.X < expandedPosition.X + widthPx + slack
            && cursor.Y >= expandedPosition.Y - slack
            && cursor.Y < expandedPosition.Y + heightPx + slack;
    }


    private void OnWindowOpened(object? sender, EventArgs e)
    {
        SetWindowCornerRound();
        // 此时窗口已关联实际显示器，避免初始化阶段按错误屏幕写入过小 MaxWidth。
        RestoreSavedWindowSize();
        UpdateStatusAnimationState(restartActiveClocks: true);
        InitializeSummonHotkey();
        Dispatcher.UIThread.Post(CompleteSavedPlacementRestore, DispatcherPriority.Loaded);
    }

    public static (bool FadeTop, bool FadeBottom) GetScrollFadeDirections(
        double offsetY,
        double viewportHeight,
        double extentHeight)
    {
        const double tolerance = 0.5;
        return (
            offsetY > tolerance,
            offsetY + viewportHeight < extentHeight - tolerance);
    }

    private void OnWindowActivated(object? sender, EventArgs e)
    {
        if (_isDocked && _isDockRevealed) _dockHadForeground = true;
        QueueForegroundDocumentProbe();
    }

    private void OnPhotoshopDocumentChanged(object? sender, EventArgs e)
    {
        // Visible pinned windows consume notifications without focus; other windows wait for foreground eligibility.
        Dispatcher.UIThread.Post(QueueForegroundDocumentProbe, DispatcherPriority.Loaded);
    }

    private bool CanReadForegroundDocument()
    {
        if (_isClosing || _isRestoringPlacement || !_placementRestorationCompleted
            || _isHiddenToTray || _isSnapAnimating || _edgeVisualSettling || (_isDocked && !_isDockRevealed)
            || !IsVisible || Opacity <= 0 || WindowState == WindowState.Minimized) return false;
        if (DataContext is not MainViewModel { AutoRefreshOnActivated: true } model) return false;
        // Use the user's pin preference, not temporary topmost used by export/docking.
        if (model.IsAlwaysOnTop) return true;
        if (!IsActive) return false;
        if (!OperatingSystem.IsWindows()) return true;
        var handle = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        return handle != IntPtr.Zero && GetForegroundWindow() == handle;
    }

    private void QueueForegroundDocumentProbe()
    {
        // Queue on activation, pin enable or a notifier event, after rendering settles. Never poll or activate here.
        Dispatcher.UIThread.Post(async () =>
        {
            if (!CanReadForegroundDocument() || DataContext is not MainViewModel current
                || (_hasProbedCurrentActivation && !current.HasPendingDocumentChanges)) return;
            var activationGeneration = _edgeAnimationGeneration;
            RootGrid.InvalidateVisual();
            var painted = await WaitForRenderedBatchAsync(this);
            if (!painted || activationGeneration != _edgeAnimationGeneration
                || !CanReadForegroundDocument() || DataContext is not MainViewModel vm
                || (_hasProbedCurrentActivation && !vm.HasPendingDocumentChanges)) return;
            var onlyIfChanged = _hasProbedCurrentActivation;
            _hasProbedCurrentActivation = true;
            // Also consumes activations during an existing probe/export, so completion cannot cause another probe.
            await vm.RefreshOnActivationAsync(CanReadForegroundDocument, onlyIfChanged);
        }, DispatcherPriority.Loaded);
    }

    private void OnWindowSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        InvalidateRetractSnapshot();
        if (_isRestoringPlacement || _isHiddenToTray || _isDocked || !_hasRestoredWindowSize || WindowState != WindowState.Normal) return;
        _windowSizeSaveTimer.Stop();
        _windowSizeSaveTimer.Start();
    }

    private void OnWindowSizeSaveTimerTick(object? sender, EventArgs e)
    {
        _windowSizeSaveTimer.Stop();
        if (_isSnapAnimating) return;
        SaveCurrentWindowSize();
    }

    private void OnWindowClosing(object? sender, WindowClosingEventArgs e)
    {
        EndInputEditing();
        if (DataContext is MainViewModel model) model.FlushParameterMemory();
        _windowSizeSaveTimer.Stop();
        SaveCurrentWindowSize();
        if (e.CloseReason is WindowCloseReason.ApplicationShutdown or WindowCloseReason.OSShutdown)
        {
            if (Application.Current is App application) application.MarkShutdownRequested();
            return;
        }
        if (Application.Current is App { HasTrayIcon: true, IsExitRequested: false } app
            && DataContext is MainViewModel { CloseToTray: true })
        {
            e.Cancel = true;
            HideToTray();
            app.NotifyHiddenToTray();
        }
    }

    public void HideToTray()
    {
        PersistWindowPlacement();
        _isHiddenToTray = true;
        FinishSummonProgramPick(restoreWindow: false);
        EndInputEditing();
        _exportCommandWindow?.Close();
        Hide();
        if (_isDocked) BeginUndockForDrag();
        CloseDockIcon();
        ClearDockAnimationSurface();
        StopSettingsPageFade();
        _statusAnimationTimer.Stop();
        _edgeHoverTimer.Stop();
    }

    private void SaveCurrentWindowSize()
    {
        if (!_hasRestoredWindowSize || WindowState != WindowState.Normal || DataContext is not MainViewModel vm)
            return;
        PersistWindowPlacement();
    }

    private void AttachQualityMeterPointerHandlers()
    {
        QualityMeter.AddHandler(
            PointerPressedEvent,
            OnQualityMeterPointerPressed,
            RoutingStrategies.Tunnel,
            handledEventsToo: true);
        QualityMeter.AddHandler(
            PointerMovedEvent,
            OnQualityMeterPointerMoved,
            RoutingStrategies.Tunnel,
            handledEventsToo: true);
        QualityMeter.AddHandler(
            PointerReleasedEvent,
            OnQualityMeterPointerReleased,
            RoutingStrategies.Tunnel,
            handledEventsToo: true);
        QualityMeter.PointerCaptureLost += OnQualityMeterPointerCaptureLost;
    }

    private void PulseQualityMeter()
    {
        if (_isClosing || !QualityMeter.IsEffectivelyVisible || _isSnapAnimating) return;
        _qualityFeedbackStartScale = _qualityFeedbackScale.ScaleY;
        _qualityFeedbackClock.Restart();
        RequestQualityFeedbackFrame();
    }

    private void RequestQualityFeedbackFrame()
    {
        if (_qualityFeedbackFramePending) return;
        _qualityFeedbackFramePending = true;
        RequestAnimationFrame(_ =>
        {
            _qualityFeedbackFramePending = false;
            if (_isClosing || !QualityMeter.IsEffectivelyVisible || _isSnapAnimating)
            {
                _qualityFeedbackScale.ScaleY = 1;
                _qualityFeedbackClock.Reset();
                return;
            }
            var t = Math.Clamp(_qualityFeedbackClock.Elapsed.TotalMilliseconds / 180, 0, 1);
            _qualityFeedbackScale.ScaleY = Math.Min(1.12,
                _qualityFeedbackStartScale + (1 - _qualityFeedbackStartScale) * t + 0.075 * Math.Sin(Math.PI * t));
            if (t < 1) RequestQualityFeedbackFrame();
            else
            {
                _qualityFeedbackScale.ScaleY = 1;
                _qualityFeedbackClock.Reset();
            }
        });
    }

    private Control? FindFocusedEditor()
    {
        Control? editor = null;
        for (var visual = FocusManager?.GetFocusedElement() as Visual; visual is not null; visual = visual.GetVisualParent())
            if (visual is TextBox or NumericUpDown) editor = (Control)visual;
        return editor;
    }

    private void EndInputEditing(KeyModifiers modifiers = KeyModifiers.None)
    {
        if (FindFocusedEditor() is null) return;
        if (!IsActive)
        {
            // Clear logical editing focus without asking an inactive native window to take keyboard focus.
            FocusManager?.Focus(null!, NavigationMethod.Pointer, modifiers);
            return;
        }
        // 将焦点交给窗口，避免空白/非焦点按钮把键盘焦点继续留在输入框。
        if (!Focus(NavigationMethod.Pointer, modifiers))
            FocusManager?.Focus(null!, NavigationMethod.Pointer, modifiers);
    }
    private void OnWindowPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        // Menu clicks must not clear the selected text, cancel the preset editor or drag the window.
        if (e.Handled || IsPopupInputSource(e.Source)) return;
        var pointer = e.GetCurrentPoint(this).Properties;
        if ((pointer.IsLeftButtonPressed || pointer.IsRightButtonPressed || pointer.IsMiddleButtonPressed)
            && (DismissLayerHelp() || DismissTextInputMenu()))
        {
            e.Handled = true; // Closing click must not activate a control under the menu.
            return;
        }
        var isExportClick = e.Source is Visual source
            && (ReferenceEquals(source, StartExportButton) || StartExportButton.IsVisualAncestorOf(source));
        if (ExportPresets.DismissOnOutsidePointer(e, allowEditorClickThrough: isExportClick)) return;
        // 先结束原输入框编辑，再让目标控件处理点击；不吞掉目标控件的事件。
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed && FindFocusedEditor() is { } editor)
        {
            var insideEditor = false;
            for (var visual = e.Source as Visual; visual is not null; visual = visual.GetVisualParent())
                if (ReferenceEquals(visual, editor)) { insideEditor = true; break; }
            if (!insideEditor) EndInputEditing(e.KeyModifiers);
        }
        if (e.Handled || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed
            || _isSnapAnimating || DataContext is not MainViewModel) return;
        var position = e.GetPosition(this);
        if (!MainContentBorder.Bounds.Deflate(new Thickness(8)).Contains(position)) return;
        for (var visual = e.Source as Visual; visual is not null; visual = visual.GetVisualParent())
        {
            if (visual is Button or TextBox or ScrollBar or Thumb or Slider or ComboBox or NumericUpDown
                or ToggleSwitch or CheckBox or ListBox or ComboBox or AnimatedSegmentTrack) return;
            if (visual is Control { Name: "QualityMeter" }) return;
        }
        // 空白处交给系统区分单击与移动，Windows 确认实际移动后才解除贴边。
        if (!_isSubclassed) BeginUndockForDrag();
        BeginMoveDrag(e);
        e.Handled = true;
    }

    private bool IsPopupInputSource(object? source)
    {
        if (source is not Visual visual) return false;
        // Native popups have a separate visual root; embedded popups expose a presenter ancestor.
        if (TopLevel.GetTopLevel(visual) is { } root && !ReferenceEquals(root, this)) return true;
        for (Visual? current = visual; current is not null; current = current.GetVisualParent())
            if (current is FlyoutPresenter or MenuFlyoutPresenter or Avalonia.Controls.ContextMenu) return true;
        return false;
    }

    private bool DismissTextInputMenu()
    {
        // Scan only on a pointer press/Esc: no polling, clipboard monitoring or global input hooks.
        foreach (var visual in RootGrid.GetVisualDescendants())
        {
            if (visual is not TextBox editor) continue;
            if (editor.ContextFlyout is { IsOpen: true } flyout) { flyout.Hide(); return true; }
            if (editor.ContextMenu is { IsOpen: true } menu) { menu.Close(); return true; }
        }
        return false;
    }

    public void ActivateFromExternalRequest(bool keepWindowPosition = false)
    {
        if (_isRestoringPlacement)
        {
            ActivateAfterPlacementRestore(() => ActivateFromExternalRequest(keepWindowPosition: true));
            return;
        }
        _isHiddenToTray = false;
        if (_isDocked) BeginUndockForDrag();
        var wasMinimized = WindowState == WindowState.Minimized;
        // 位置要在这次显示之前定好，窗口只能落位一次；启动恢复那条路径不移动窗口。
        if (wasMinimized) WindowState = WindowState.Normal;
        if (!keepWindowPosition) MoveToPointer();
        if (!IsVisible)
            Show();
        UpdateStatusAnimationState(restartActiveClocks: false);
        PersistWindowPlacement();
        BringToForeground();
    }

    private void BringToForeground()
    {
        Activate();
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;

        var handle = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (handle == IntPtr.Zero) return;
        _ = SetForegroundWindow(handle);
    }

    /// <summary>最小化按钮</summary>
    private void OnMinimizeClick(object? sender, RoutedEventArgs e)
    {
        if (!TryQueueDockRetraction("caption-minimize")) WindowState = WindowState.Minimized;
    }

    /// <summary>关闭按钮</summary>
    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();

    private void OnQualityMeterPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(QualityMeter).Properties.IsLeftButtonPressed)
        {
            _isDraggingQualityMeter = true;
            e.Pointer.Capture(QualityMeter);
            UpdateQualityFromPointer(QualityMeter, e);
            PulseQualityMeter();
            e.Handled = true;
        }
    }

    private void OnQualityMeterPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_isDraggingQualityMeter) return;

        if (!e.GetCurrentPoint(QualityMeter).Properties.IsLeftButtonPressed)
        {
            EndQualityMeterDrag(e.Pointer);
            return;
        }

        UpdateQualityFromPointer(QualityMeter, e);
        e.Handled = true;
    }

    private void OnQualityMeterPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_isDraggingQualityMeter) return;

        UpdateQualityFromPointer(QualityMeter, e);
        EndQualityMeterDrag(e.Pointer);
        e.Handled = true;
    }

    private void OnQualityMeterPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        _isDraggingQualityMeter = false;
    }

    private void EndQualityMeterDrag(IPointer pointer)
    {
        _isDraggingQualityMeter = false;
        pointer.Capture(null);
    }

    private void UpdateQualityFromPointer(Control control, PointerEventArgs e)
    {
        double x = e.GetPosition(control).X;
        double width = control.Bounds.Width;
        if (width > 0)
        {
            double ratio = Math.Clamp(x / width, 0.0, 1.0);
            int quality = Math.Clamp((int)Math.Round(ratio * 100), 1, 100);
            if (DataContext is MainViewModel vm)
            {
                vm.Quality = quality;
            }
        }
    }

    /// <summary>
    /// 在 Windows 11 上为无装饰窗口恢复圆角。
    /// </summary>
    private void SetWindowCornerRound()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;
        try
        {
            var handle = TryGetPlatformHandle();
            if (handle is null) return;
            var hwnd = handle.Handle;
            if (hwnd == IntPtr.Zero) return;

            var preference = 1; // 主窗由 ContinuousBorder 绘制外形，关闭 DWM 圆弧裁切。
            _ = DwmSetWindowAttribute(hwnd, 33, ref preference, sizeof(int));
            var ncaPolicy = 1; // 仅使用界面自身投影，临时动画画布不能出现原生矩形阴影。
            _ = DwmSetWindowAttribute(hwnd, 2, ref ncaPolicy, sizeof(int));
        }
        catch
        {
        }
    }

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr hwnd, int command);

    [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfo(uint action, uint parameter,
        [MarshalAs(UnmanagedType.Bool)] out bool value, uint flags);

    private Flyout? _openLayerHelp;

    private void OnLayerHelpOpened(object? sender, EventArgs e)
    {
        if (sender is not Flyout { Content: StackPanel content } flyout) return;
        _openLayerHelp = flyout;
        // SPI_GETCLIENTAREAANIMATION. Keep the final illustration readable when motion is off.
        const uint getClientAreaAnimation = 0x1042;
        if (!OperatingSystem.IsWindows()
            || !SystemParametersInfo(getClientAreaAnimation, 0, out var animationsEnabled, 0)
            || animationsEnabled)
        {
            content.Classes.Add("layer-demo-playing");
        }
    }

    private void OnLayerHelpClosed(object? sender, EventArgs e)
    {
        if (sender is Flyout { Content: StackPanel content })
            content.Classes.Remove("layer-demo-playing");
        if (ReferenceEquals(_openLayerHelp, sender)) _openLayerHelp = null;
    }

    private void OnLayerHelpKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && DismissLayerHelp()) e.Handled = true;
    }

    private void OnLayerHelpCloseClick(object? sender, RoutedEventArgs e)
    {
        if (DismissLayerHelp()) e.Handled = true;
    }

    private bool DismissLayerHelp()
    {
        if (_openLayerHelp is not { IsOpen: true } flyout) return false;
        flyout.Hide();
        return true;
    }
}
