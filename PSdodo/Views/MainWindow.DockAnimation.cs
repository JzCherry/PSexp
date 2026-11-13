using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Threading;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Rendering.Composition;
using PsExporter.Services;
using PsExporter.ViewModels;
using SkiaSharp;

namespace PsExporter.Views;

public partial class MainWindow
{
    private readonly DockAnimationMotion _edgeMotion = new();
    private EdgeSnapHelper.GenieMeshData? _animationTopology;
    private int _edgeMotionRevision;
    private bool _edgeCanReverseOnPointerReturn;
    private readonly Stopwatch _edgePlaybackClock = new();
    private double _edgeCaptureMs;
    private double _edgeReadyMs;
    private double _edgePrepareMs;
    private double _edgeMaxUpdateMs;
    private int _edgeLongIntervals;
    private bool _edgeFirstDrawConfirmed;
    private bool _edgeStartDesktopReady;
    private string _edgeCaptureSource = "fresh";
    private bool _edgeLayoutPrepared;
    private Size _edgeWindowSize;
    private Size _edgeSavedMaxSize;
    private bool? _edgeSavedHitTestVisible;
    private bool _dockRevealBitmapStale = true;
    private bool _edgeVisualSettling;
    private int _edgeSceneRevision;
    private int _edgeCapturedSceneRevision;
    private int _retractSnapshotSceneRevision;
    private readonly Stopwatch _edgeGeometryClock = new();
    private PixelPoint _edgeCanvasPosition;
    private Vector _edgeMeshOrigin;
    private Vector _edgeSourceOffset;
    private double _edgeCanvasWidth;
    private double _edgeCanvasHeight;
    private PointerDockOverlay? _pointerAnimationWindow;
    private bool _pointerAnimationActive;
    private SKBitmap? _retractSnapshot;
    private double _retractSnapshotScale;
    private double _retractSnapshotWidth;
    private double _retractSnapshotHeight;
    private double _retractSnapshotCaptureMs;
    private int _retractSnapshotRevision;
    private bool _retractSnapshotQueued;

    private void InvalidateRetractSnapshot()
    {
        ++_retractSnapshotRevision;
        _retractSnapshot?.Dispose();
        _retractSnapshot = null;
    }

    private void QueueRetractSnapshot()
    {
        if (_retractSnapshot is not null || _retractSnapshotQueued) return;
        var delay = DataContext is MainViewModel timingModel ? timingModel.DockRetractDelayMs : 420;
        if (_pointerOutsideDockSince == DateTime.MinValue || _qualityFeedbackClock.IsRunning
            || (DateTime.UtcNow - _pointerOutsideDockSince).TotalMilliseconds < Math.Max(0, delay - 100)) return;
        var revision = _retractSnapshotRevision;
        _retractSnapshotQueued = true;
        // 只在鼠标已经离开、还在等待收回时准备；不增加后台刷新计时器。
        Dispatcher.UIThread.Post(() =>
        {
            _retractSnapshotQueued = false;
            if (revision != _retractSnapshotRevision || _isClosing || _isHiddenToTray
                || !_isDocked || !_isDockRevealed || _isSnapAnimating
                || DataContext is MainViewModel { IsExporting: true } or MainViewModel { IsRefreshingStatus: true }
                || !GetCursorPos(out var pointer) || IsPointInsideWindow(pointer, _dockExpandedPos, _dockScaling)
                || IsPointInsideDockIcon(pointer)) return;
            var capture = Stopwatch.StartNew();
            var snapshot = CaptureWindowBitmap();
            if (snapshot is null) return;
            if (revision != _retractSnapshotRevision || _isClosing)
            {
                snapshot.Dispose();
                return;
            }
            _retractSnapshot = snapshot;
            _retractSnapshotSceneRevision = _edgeSceneRevision;
            _retractSnapshotScale = _captureScale;
            _retractSnapshotWidth = Bounds.Width;
            _retractSnapshotHeight = Bounds.Height;
            _retractSnapshotCaptureMs = capture.Elapsed.TotalMilliseconds;
        }, DispatcherPriority.Background);
    }

    private SKBitmap? TakeRetractSnapshot()
    {
        if (_retractSnapshot is null) return null;
        if (!NearlyEqual(_retractSnapshotWidth, Bounds.Width) || !NearlyEqual(_retractSnapshotHeight, Bounds.Height)
            || !NearlyEqual(_retractSnapshotScale, CalculateCaptureScale(Bounds.Width, Bounds.Height, _dockScaling)))
        {
            InvalidateRetractSnapshot();
            return null;
        }
        var snapshot = _retractSnapshot;
        _retractSnapshot = null;
        _captureScale = _retractSnapshotScale;
        _edgeCapturedSceneRevision = _retractSnapshotSceneRevision;
        _edgeCaptureMs = _retractSnapshotCaptureMs;
        _edgeCaptureSource = "retract-cache";
        return snapshot;
    }

    private async void StartEdgeAnimation(PixelPoint target, int durationMs)
    {
        if (_isClosing || !_isDocked) return;
        var hiding = target == _dockHiddenPos;
        _snapTargetPos = target;
        _isDockRevealed = !hiding;
        _pointerOutsideDockSince = DateTime.MinValue;
        if (_isSnapAnimating)
        {
            if (!_isEdgeFinishing && _snapClock.IsRunning && _edgeMotion.IsHiding != hiding)
                RetargetEdgeAnimation(hiding);
            return;
        }

        _isSnapAnimating = true;
        _edgeVisualSettling = true;
        _edgeSavedHitTestVisible ??= IsHitTestVisible;
        IsHitTestVisible = false;
        _qualityFeedbackScale.ScaleY = 1;
        _qualityFeedbackClock.Reset();
        StopSettingsPageFade();
        _edgeWindowSize = Bounds.Size;
        var sourcePosition = _pointerDockPosition ?? (hiding ? Position : _dockExpandedPos);
        _edgeCanvasPosition = new PixelPoint(Math.Min(sourcePosition.X, _dockExpandedPos.X), Math.Min(sourcePosition.Y, _dockExpandedPos.Y));
        var geometryScale = Math.Max(_dockScaling, 0.1);
        _edgeMeshOrigin = new Vector((_dockExpandedPos.X - _edgeCanvasPosition.X) / geometryScale, (_dockExpandedPos.Y - _edgeCanvasPosition.Y) / geometryScale);
        _edgeSourceOffset = new Vector((sourcePosition.X - _dockExpandedPos.X) / geometryScale, (sourcePosition.Y - _dockExpandedPos.Y) / geometryScale);
        _edgeCanvasWidth = _edgeWindowSize.Width + Math.Abs(_edgeSourceOffset.X);
        _edgeCanvasHeight = _edgeWindowSize.Height + Math.Abs(_edgeSourceOffset.Y);
        _edgeGeometryClock.Reset();
        _isEdgeFinishing = false;
        _snapClock.Reset();
        _edgePlaybackClock.Reset();
        _frameCount = 0;
        _maxFrameGapMs = 0;
        _windowSizeSaveTimer.Stop();
        _animationViewModel?.SetWindowAnimationActive(true);
        _edgeHoverTimer.Stop();
        UpdateStatusAnimationState(restartActiveClocks: false);
        var generation = ++_edgeAnimationGeneration;
        var preparing = Stopwatch.StartNew();
        _edgeCaptureMs = _edgeReadyMs = _edgePrepareMs = _edgeMaxUpdateMs = 0;
        _edgeLongIntervals = 0;
        _edgeFirstDrawConfirmed = false;
        _edgeStartDesktopReady = false;
        _edgeCaptureSource = "fresh";
        _edgeCanReverseOnPointerReturn = hiding && GetCursorPos(out var cursor)
            && !IsPointInsideWindow(cursor, _dockExpandedPos, _dockScaling);
        try
        {
            if (WindowState == global::Avalonia.Controls.WindowState.Minimized)
                WindowState = global::Avalonia.Controls.WindowState.Normal;
            if (hiding)
            {
                ClearDockRevealBitmap();
                _genieCapturedBitmap = TakeRetractSnapshot();
                if (_genieCapturedBitmap is null)
                {
                    var capturing = Stopwatch.StartNew();
                    _edgeCapturedSceneRevision = _edgeSceneRevision;
                    _genieCapturedBitmap = CaptureWindowBitmap();
                    _edgeCaptureMs = capturing.Elapsed.TotalMilliseconds;
                }
            }
            else
            {
                // 提前在目标位置布置主窗口，播放时保持透明。
                Opacity = 0;
                WindowTaskSwitcher.Configure(this, excluded: false);
                Position = DockLivePosition;
                RootGrid.UpdateLayout();
                if (_dockRevealBitmapStale) ClearDockRevealBitmap();
                _edgeCaptureSource = _dockRevealBitmap is null ? "fresh" : "reveal-cache";
                var capturing = Stopwatch.StartNew();
                if (_dockRevealBitmap is null) _edgeCapturedSceneRevision = _edgeSceneRevision;
                _genieCapturedBitmap = _dockRevealBitmap ?? CaptureWindowBitmap();
                _edgeCaptureMs = _dockRevealBitmap is null ? capturing.Elapsed.TotalMilliseconds : 0;
                _dockRevealBitmap = null;
            }
            if (_genieCapturedBitmap is null)
            {
                await FinishEdgeAnimationAsync(generation, hiding);
                return;
            }
            if (_pointerDockPosition is not null && !hiding && _dockIconWindow is { Opacity: > 0 } dockIcon)
                dockIcon.SetTransition(1);
            else
                ShowDockIcon(hiding ? 0 : 1);
            if (_pointerDockPosition is not null && OperatingSystem.IsWindows() && IsVisible)
            {
                try
                {
                    (_pointerAnimationWindow ??= new PointerDockOverlay()).Prepare(_genieCapturedBitmap);
                    _pointerAnimationActive = true;
                    SetAnimationMesh(hiding ? 0 : 1);
                }
                catch (Exception error)
                {
                    DisposePointerAnimationWindow();
                    _animationViewModel?.LogDockAnimation("[dock] 原生动画窗准备失败：" + error.Message, failure: true);
                }
            }
            if (_pointerDockPosition is not null)
                durationMs = DockAnimationMotion.PointerDuration(hiding, _edgeSourceOffset.Length);
            if (_pointerAnimationActive)
            {
                Opacity = 0;
                if (hiding)
                {
                    // 先让静止的快照接替主窗口，再开始移动，避免两张面板分开一帧。
                    var desktopReady = await WaitForRenderedBatchAsync(this) && await PointerDockOverlay.WaitForDesktopAsync();
                    if (generation != _edgeAnimationGeneration || _isClosing) return;
                    _edgeStartDesktopReady = desktopReady;
                    _pointerAnimationWindow!.BeginPlaybackTiming();
                }
            }
            else
            {
                PrepareDockAnimationSurface();
                SetAnimationMesh(hiding ? 0 : 1);
                DockAnimationSurface.BeginFrameTiming();
                Opacity = 1;
                if (!hiding) WindowTaskSwitcher.RaiseWithoutActivation(this);
                _ = ObserveEdgeFirstDrawAsync(generation, DockAnimationSurface.WaitForDrawAsync());
            }
            _edgeGeometryClock.Restart();
            _edgeMotion.Start(hiding, durationMs);
            _edgePrepareMs = preparing.Elapsed.TotalMilliseconds;
            _snapClock.Restart();
            _edgePlaybackClock.Restart();
            _frameCount = 0;
            _lastFrameMs = 0;
            _maxFrameGapMs = 0;
            ScheduleEdgeAnimationFallback(generation);
            QueueEdgeFrame(generation);
        }
        catch (Exception error)
        {
            Debug.WriteLine("收纳动画失败: " + error);
            if (DataContext is MainViewModel diagnostics) diagnostics.LogDockAnimation("[dock] prepare failed: " + error, failure: true);
            await FinishEdgeAnimationAsync(generation, hiding);
        }
    }

    private void PrepareDockAnimationSurface()
    {
        _edgeSavedMaxSize = new Size(MaxWidth, MaxHeight);
        _edgeLayoutPrepared = true;
        // 临时扩大画布时锁定控件的原尺寸，不能让表单随动画承载范围重排。
        RootGrid.Width = _edgeWindowSize.Width;
        RootGrid.Height = _edgeWindowSize.Height;
        RootGrid.HorizontalAlignment = HorizontalAlignment.Left;
        RootGrid.VerticalAlignment = VerticalAlignment.Top;
        RootGrid.RenderTransform = new TranslateTransform(_edgeMeshOrigin.X, _edgeMeshOrigin.Y);
        RootGrid.Opacity = 0;
        DockAnimationSurface.Bitmap = _genieCapturedBitmap;
        DockAnimationSurface.TextureScale = _captureScale;
        DockAnimationSurface.IsVisible = true;
        if (_edgeCanvasWidth > MaxWidth) MaxWidth = _edgeCanvasWidth;
        if (_edgeCanvasHeight > MaxHeight) MaxHeight = _edgeCanvasHeight;
        if (!NearlyEqual(Width, _edgeCanvasWidth)) Width = _edgeCanvasWidth;
        if (!NearlyEqual(Height, _edgeCanvasHeight)) Height = _edgeCanvasHeight;
        Position = _edgeCanvasPosition;
    }

    private void RestoreDockAnimationLayout()
    {
        DockAnimationSurface.IsVisible = false;
        DockAnimationSurface.CancelPendingDraw();
        if (_edgeLayoutPrepared)
        {
            _edgeLayoutPrepared = false;
            if (!NearlyEqual(Width, _edgeWindowSize.Width)) Width = _edgeWindowSize.Width;
            if (!NearlyEqual(Height, _edgeWindowSize.Height)) Height = _edgeWindowSize.Height;
            MaxWidth = _edgeSavedMaxSize.Width;
            MaxHeight = _edgeSavedMaxSize.Height;
            RootGrid.ClearValue(WidthProperty);
            RootGrid.ClearValue(HeightProperty);
            RootGrid.ClearValue(HorizontalAlignmentProperty);
            RootGrid.ClearValue(VerticalAlignmentProperty);
            RootGrid.RenderTransform = null;
        }
        RootGrid.Opacity = 1;
    }

    private async Task ObserveEdgeFirstDrawAsync(int generation, Task<bool> drawn, int timeoutMs = 100)
    {
        var waiting = Stopwatch.StartNew();
        var ready = await Task.WhenAny(drawn, Task.Delay(timeoutMs)) == drawn && await drawn;
        if (generation != _edgeAnimationGeneration || _isClosing) return;
        _edgeFirstDrawConfirmed = ready;
        _edgeReadyMs = waiting.Elapsed.TotalMilliseconds;
    }

    private static async Task<bool> WaitForRenderedBatchAsync(Visual visual)
    {
        var compositor = ElementComposition.GetElementVisual(visual)?.Compositor;
        if (compositor is null) return false;
        // Rendered 与 UI 帧计数不同：等待本批改动在渲染线程画出；不宣称 DWM 已呈现。
        var rendered = compositor.RequestCompositionBatchCommitAsync().Rendered;
        var ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = rendered.ContinueWith(t => ready.TrySetResult(t.IsCompletedSuccessfully), TaskScheduler.Default);
        return await Task.WhenAny(ready.Task, Task.Delay(200)) == ready.Task && await ready.Task;
    }

    private void ResetLiveWindowPreparation()
    {
        _edgeVisualSettling = false;
        if (_edgeSavedHitTestVisible is { } enabled)
        {
            IsHitTestVisible = enabled;
            _edgeSavedHitTestVisible = null;
        }
    }

    private async Task SavePlacementAfterDockMotionAsync(int generation)
    {
        // 图标还有 220ms 余韵，不在它的首帧插入同步写盘。
        await Task.Delay(250);
        if (!_isClosing && generation == _edgeAnimationGeneration && !_isSnapAnimating)
            PersistWindowPlacement();
    }

    private void RetargetEdgeAnimation(bool hiding)
    {
        int? duration = _pointerDockPosition is not null ? DockAnimationMotion.PointerDuration(hiding, _edgeSourceOffset.Length) : null;
        _edgeMotion.Retarget(hiding, _snapClock.Elapsed.TotalMilliseconds, duration);
        _snapClock.Restart();
        _pointerOutsideDockSince = DateTime.MinValue;
        ScheduleEdgeAnimationFallback(_edgeAnimationGeneration);
        // 保留同一张纹理、同一承载窗和已经排队的逐帧回调，不再截图或切换 HWND。
    }

    private void SetAnimationMesh(double collapse)
    {
        var scale = Math.Max(_dockScaling, 0.1);
        // Window.Position 是物理像素，网格是 DIP；使用实际图标中心，兼容四边与 DPI。
        var anchor = new SKPoint(
            (float)((_dockIconPlacement.Position.X + _dockIconPlacement.SizePx / 2d - _dockExpandedPos.X) / scale),
            (float)((_dockIconPlacement.Position.Y + _dockIconPlacement.SizePx / 2d - _dockExpandedPos.Y) / scale));
        _animationTopology = EdgeSnapHelper.CalculateGenieMeshData(_dockedEdge,
            collapse, isHiding: true, (float)_edgeWindowSize.Width, (float)_edgeWindowSize.Height,
            cols: 16, rows: 16, peekPx: 0, topology: _animationTopology,
            dockAnchor: anchor, dockSpan: (float)(_dockIconPlacement.SizePx / scale));
        // 首次收纳从真实拖动位置起步，平顺归位；反向仍延续同一条位置轨迹。
        // 鼠标唤起的位移随收纳程度往返；普通贴边仍使用原来的 100ms 归位。
        var settling = Math.Clamp(_edgeGeometryClock.Elapsed.TotalMilliseconds / 100, 0, 1);
        var alignment = _pointerDockPosition is not null ? collapse : settling * settling * (3 - 2 * settling);
        var offset = _edgeMeshOrigin + _edgeSourceOffset * (1 - alignment);
        if (offset.X != 0 || offset.Y != 0)
            for (var i = 0; i < _animationTopology.Vertices.Length; ++i)
            {
                var point = _animationTopology.Vertices[i];
                _animationTopology.Vertices[i] = new SKPoint(point.X + (float)offset.X, point.Y + (float)offset.Y);
            }
        if (_pointerAnimationActive)
        {
            var visible = _pointerAnimationWindow!.Draw(_animationTopology, _edgeCanvasPosition, scale,
                DockAnimationMotion.PointerMeshVisibility(collapse));
            if (visible && !_edgeFirstDrawConfirmed)
            {
                _edgeFirstDrawConfirmed = true;
                _edgeReadyMs = _edgePlaybackClock.Elapsed.TotalMilliseconds;
            }
        }
        else
        {
            DockAnimationSurface.MeshData = _animationTopology;
            DockAnimationSurface.MeshOpacity = DockAnimationMotion.MeshVisibility(collapse);
        }
        _dockIconWindow?.SetTransition(collapse);
    }

    private void QueueEdgeFrame(int generation)
    {
        if (_pointerAnimationActive)
            _ = QueuePointerFrameAsync(generation);
        else
            RequestAnimationFrame(_ => AdvanceEdgeFrame(generation));
    }

    private async Task QueuePointerFrameAsync(int generation)
    {
        await PointerDockOverlay.WaitForFrameAsync().ConfigureAwait(false);
        Dispatcher.UIThread.Post(() => AdvanceEdgeFrame(generation), DispatcherPriority.Render);
    }

    private void ScheduleEdgeAnimationFallback(int generation)
        => _ = EnsureEdgeAnimationCompletesAsync(generation, ++_edgeMotionRevision, _edgeMotion.DurationMs);

    private async Task EnsureEdgeAnimationCompletesAsync(int generation, int revision, double durationMs)
    {
        // 每个方向段仅一次超时兜底；旧方向的超时不能提前结束刚反向的动画。
        await Task.Delay((int)Math.Ceiling(durationMs) + 200);
        if (generation == _edgeAnimationGeneration && revision == _edgeMotionRevision
            && _isSnapAnimating && !_isEdgeFinishing && !_isClosing)
            await FinishEdgeAnimationAsync(generation, _edgeMotion.IsHiding);
    }

    private void UpdateEdgeAnimationIntentFromPointer()
    {
        if (_dockMustFinishRetraction || _pointerDockPosition is not null || !GetCursorPos(out var cursor)) return;
        var inside = IsPointInsideWindow(cursor, _dockExpandedPos, _dockScaling) || IsPointInsideDockIcon(cursor);
        if (_edgeMotion.IsHiding)
        {
            if (!inside) _edgeCanReverseOnPointerReturn = true;
            else if (_edgeCanReverseOnPointerReturn)
                StartEdgeAnimation(_dockExpandedPos, DockAnimationMotion.RevealDurationMs);
        }
        else if (_dockRevealedByHotkey) return;
        else if (inside)
        {
            _pointerOutsideDockSince = DateTime.MinValue;
        }
        else
        {
            if (_pointerOutsideDockSince == DateTime.MinValue) _pointerOutsideDockSince = DateTime.UtcNow;
            var delay = DataContext is MainViewModel model ? model.DockRetractDelayMs : 420;
            if ((DateTime.UtcNow - _pointerOutsideDockSince).TotalMilliseconds >= delay)
                StartEdgeAnimation(_dockHiddenPos, DockAnimationMotion.HideDurationMs);
        }
    }

    private bool IsPointInsideDockIcon(NativePoint cursor)
    {
        var padding = (int)Math.Ceiling(DockIconWindow.TriggerPaddingDip * _dockScaling);
        return new PixelRect(_dockIconPlacement.Position.X - padding, _dockIconPlacement.Position.Y - padding,
            _dockIconPlacement.SizePx + padding * 2, _dockIconPlacement.SizePx + padding * 2)
            .Contains(new PixelPoint(cursor.X, cursor.Y));
    }

    private async void AdvanceEdgeFrame(int generation)
    {
        if (generation != _edgeAnimationGeneration || !_isSnapAnimating || _isEdgeFinishing || _isClosing) return;
        try
        {
            UpdateEdgeAnimationIntentFromPointer();
            var updating = Stopwatch.StartNew();
            var elapsed = _snapClock.Elapsed.TotalMilliseconds;
            var playback = _edgePlaybackClock.Elapsed.TotalMilliseconds;
            var gap = playback - _lastFrameMs;
            _maxFrameGapMs = Math.Max(_maxFrameGapMs, gap);
            if (gap > 25) ++_edgeLongIntervals;
            _lastFrameMs = playback;
            _frameCount++;
            var frame = _edgeMotion.Sample(elapsed);
            if (frame.Complete && (_edgeSourceOffset == default || _edgeGeometryClock.ElapsedMilliseconds >= 100))
            {
                await FinishEdgeAnimationAsync(generation, _edgeMotion.IsHiding);
                return;
            }
            SetAnimationMesh(frame.Collapse);
            _edgeMaxUpdateMs = Math.Max(_edgeMaxUpdateMs, updating.Elapsed.TotalMilliseconds);
            QueueEdgeFrame(generation);
        }
        catch (Exception error)
        {
            Debug.WriteLine("收纳帧失败: " + error);
            if (DataContext is MainViewModel diagnostics) diagnostics.LogDockAnimation("[dock] frame failed: " + error, failure: true);
            if (_pointerAnimationActive) DisposePointerAnimationWindow();
            await FinishEdgeAnimationAsync(generation, _edgeMotion.IsHiding);
        }
    }

    private async Task FinishEdgeAnimationAsync(int generation, bool hiding)
    {
        if (generation != _edgeAnimationGeneration || _isClosing || _isEdgeFinishing) return;
        _isEdgeFinishing = true;
        var handoff = Stopwatch.StartNew();
        _snapClock.Stop();
        _edgePlaybackClock.Stop();
        _edgeGeometryClock.Stop();
        var renderTiming = _pointerAnimationActive ? _pointerAnimationWindow!.FrameTimingSummary : DockAnimationSurface.FrameTimingSummary;
        var usesPointerSurface = _pointerAnimationActive;
        ClearDockRevealBitmap();
        _dockRevealBitmap = _genieCapturedBitmap;
        _dockRevealBitmapStale = _edgeCapturedSceneRevision != _edgeSceneRevision;
        _genieCapturedBitmap = null;
        if (hiding)
        {
            if (_dockIconWindow is not { Opacity: > 0 }) ShowDockIcon();
            else _dockIconWindow.SetTransition(1);
            _dockIconWindow?.PulseArrival(_dockedEdge);
            Opacity = 0;
            Position = _offscreenParkPos;
            ClearDockAnimationSurface(retainTexture: true, restoreInput: false);
            WindowTaskSwitcher.Configure(this, excluded: true);
        }
        else
        {
            // 分层窗保留最后一帧，等主窗口完成绘制后再隐藏。
            if (usesPointerSurface)
            {
                try { SetAnimationMesh(0); }
                catch (Exception error)
                {
                    DisposePointerAnimationWindow();
                    _animationViewModel?.LogDockAnimation("[dock] final overlay frame failed: " + error, failure: true);
                }
            }
            else
            {
                ClearDockAnimationSurface(retainTexture: true, restoreInput: false);
                Position = DockLivePosition;
            }
            Opacity = 1;
            CloseDockIcon();
        }
        var swapMs = handoff.Elapsed.TotalMilliseconds;
        var liveFrameDrawn = hiding || await WaitForRenderedBatchAsync(this);
        if (generation != _edgeAnimationGeneration || _isClosing) return;
        var desktopHandoff = usesPointerSurface && (hiding
            ? _edgeStartDesktopReady
            : _pointerAnimationActive && liveFrameDrawn && await PointerDockOverlay.WaitForDesktopAsync());
        if (generation != _edgeAnimationGeneration || _isClosing) return;
        if (!hiding && usesPointerSurface)
            ClearDockAnimationSurface(retainTexture: true, restoreInput: false);
        ResetLiveWindowPreparation();
        var report = FormattableString.Invariant(
            $"[dock] renderer={(usesPointerSurface ? "native-layered" : "single-window")} capture_pipeline=visual-brush direction={(hiding ? "hide" : "reveal")} capture={_edgeCaptureSource} capture_ms={_edgeCaptureMs:F1} capture_scale={_captureScale:F2} dock_scale={_dockScaling:F2} window_dip={_edgeWindowSize.Width:F2}x{_edgeWindowSize.Height:F2} texture_px={_dockRevealBitmap?.Width ?? 0}x{_dockRevealBitmap?.Height ?? 0} ready_ms={_edgeReadyMs:F1} first_draw={_edgeFirstDrawConfirmed} prepare_ms={_edgePrepareMs:F1} live_frame={liveFrameDrawn} playback_ms={_edgePlaybackClock.Elapsed.TotalMilliseconds:F1} swap_ms={swapMs:F1} settle_ms={handoff.Elapsed.TotalMilliseconds - swapMs:F1} ui_frames={_frameCount} ui_max_gap_ms={_maxFrameGapMs:F1} ui_gaps_over_25ms={_edgeLongIntervals} ui_update_max_ms={_edgeMaxUpdateMs:F2} {renderTiming}");
        if (DataContext is MainViewModel diagnosticModel)
        {
            var path = usesPointerSurface ? FormattableString.Invariant(
                $" pointer_surface=overlay desktop_handoff={desktopHandoff} travel_dip={_edgeSourceOffset.Length:F0} canvas_dip={_edgeCanvasWidth:F0}x{_edgeCanvasHeight:F0}") : "";
            diagnosticModel.LogDockAnimation(report + path);
        }
        _isSnapAnimating = false;
        _isEdgeFinishing = false;
        _animationViewModel?.SetWindowAnimationActive(false);
        var requestedHiding = _snapTargetPos == _dockHiddenPos;
        if (requestedHiding != hiding)
        {
            StartEdgeAnimation(_snapTargetPos, requestedHiding
                ? DockAnimationMotion.HideDurationMs : DockAnimationMotion.RevealDurationMs);
            return;
        }
        _isDockRevealed = !hiding;
        _dockMustFinishRetraction = false;
        if (hiding)
        {
            _dockRevealedByHotkey = false;
            _dockHadForeground = false;
            _pointerDockPosition = null;
        }
        if (_resummonPointerAfterMotion)
        {
            _resummonPointerAfterMotion = false;
            SummonFromHotkey();
            if (_isSnapAnimating) return;
        }
        QueueForegroundDocumentProbe();
        UpdateStatusAnimationState(restartActiveClocks: false);
        if (_isDocked && _isDockRevealed)
        {
            if (IsDockWindowForeground() && !IsActive)
            {
                Activate();
                Focus();
            }
            _edgeHoverTimer.Start();
            if ((_dockRevealedByHotkey || _dockHadForeground) && !IsDockWindowForeground())
                TryQueueDockRetraction("deactivated");
        }
        _ = SavePlacementAfterDockMotionAsync(generation);
    }
}
