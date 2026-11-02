using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using Avalonia.Threading;
using PsExporter.Models;
using PsExporter.Services;
using PsExporter.ViewModels;

namespace PsExporter.Views;

public partial class MainWindow
{
    private bool _placementPrepared;
    private bool _placementRestorationCompleted;
    private bool _isRestoringPlacement;
    private bool _isHiddenToTray;
    private bool _activateAfterPlacementRestore;
    private bool _restoreDocked;
    private PixelPoint _restoredNormalPosition;
    private DockEdge _restoredEdge;
    private Screen? _restoredScreen;

    // Called before the lifetime first shows MainWindow: a docked launch stays transparent.
    public void PrepareSavedPlacement()
    {
        if (_placementPrepared) return;
        _placementPrepared = true;
        if (DataContext is not MainViewModel model || model.GetSavedWindowPlacement() is not { } saved) return;
        _isRestoringPlacement = true;
        try
        {
            _restoredScreen = Screens.All.FirstOrDefault(screen => screen.WorkingArea ==
                new PixelRect(saved.WorkX, saved.WorkY, saved.WorkWidth, saved.WorkHeight))
                ?? Screens.ScreenFromPoint(new PixelPoint(saved.X, saved.Y)) ?? Screens.Primary;
            if (_restoredScreen is not { } screen) return;
            WindowStartupLocation = WindowStartupLocation.Manual;
            var size = model.GetSavedWindowSize();
            ApplyScreenConstraints(size.Width, size.Height, targetScreen: screen);
            var scale = Math.Max(screen.Scaling, 0.1);
            var area = screen.WorkingArea;
            var width = Math.Max(1, (int)Math.Round(Width * scale));
            var height = Math.Max(1, (int)Math.Round(Height * scale));
            var x = saved.X;
            var y = saved.Y;
            var sameArea = area == new PixelRect(saved.WorkX, saved.WorkY, saved.WorkWidth, saved.WorkHeight);
            if (!sameArea || !NearlyEqual(scale, saved.Scaling))
            {
                var oldScale = double.IsFinite(saved.Scaling) && saved.Scaling > 0 ? saved.Scaling : 1;
                if (saved.WorkWidth > 0)
                    x = area.X + (int)Math.Round(Math.Clamp((saved.X - (double)saved.WorkX) /
                        Math.Max(1, saved.WorkWidth - size.Width * oldScale), 0, 1) * Math.Max(0, area.Width - width));
                if (saved.WorkHeight > 0)
                    y = area.Y + (int)Math.Round(Math.Clamp((saved.Y - (double)saved.WorkY) /
                        Math.Max(1, saved.WorkHeight - size.Height * oldScale), 0, 1) * Math.Max(0, area.Height - height));
            }
            // Preserve legitimate cross-monitor placement when the display geometry is unchanged.
            var titleAccessible = Screens.All.Any(display =>
                Math.Min((long)x + width, display.WorkingArea.Right) - Math.Max(x, display.WorkingArea.X) >= Math.Min(width, 100 * scale)
                && y >= display.WorkingArea.Y && (long)y + 36 * scale <= display.WorkingArea.Bottom);
            if (!sameArea || !NearlyEqual(scale, saved.Scaling) || !titleAccessible)
            {
                x = Math.Clamp(x, area.X, Math.Max(area.X, area.Right - width));
                y = Math.Clamp(y, area.Y, Math.Max(area.Y, area.Bottom - height));
            }
            _restoreDocked = saved.IsDocked && model.EnableEdgeSnap
                && Enum.TryParse(saved.Edge, out _restoredEdge) && Enum.IsDefined(_restoredEdge) && _restoredEdge != DockEdge.None
                && EdgeSnapHelper.IsOuterScreenEdge(screen.Bounds, Screens.All.Select(s => s.Bounds).ToArray(), _restoredEdge);
            if (_restoreDocked)
            {
                if (_restoredEdge == DockEdge.Left) x = area.X;
                if (_restoredEdge == DockEdge.Right) x = area.Right - width;
                if (_restoredEdge == DockEdge.Top) y = area.Y;
                if (_restoredEdge == DockEdge.Bottom) y = area.Bottom - height;
                Opacity = 0;
                ShowActivated = false;
                WindowTaskSwitcher.Configure(this, excluded: true);
            }
            else
            {
                Opacity = 1;
                ShowActivated = true;
                WindowTaskSwitcher.Configure(this, excluded: false);
            }
            _restoredNormalPosition = new PixelPoint(x, y);
            Position = _restoredNormalPosition;
        }
        catch (Exception error)
        {
            _restoreDocked = false;
            if ((Screens.Primary ?? Screens.All.FirstOrDefault()) is { } fallback)
            {
                var area = fallback.WorkingArea;
                Position = new PixelPoint(area.X + 16, area.Y + 16);
                _restoredNormalPosition = Position;
            }
            Opacity = 1;
            ShowActivated = true;
            WindowTaskSwitcher.Configure(this, excluded: false);
            model.LogPlacementRestoreFailure(error);
        }
    }

    private void CompleteSavedPlacementRestore()
    {
        if (_placementRestorationCompleted || _isClosing) return;
        try
        {
            if (_restoreDocked && _restoredScreen is { } screen && DataContext is MainViewModel model)
            {
                _dockScaling = Math.Max(screen.Scaling, 0.1);
                _dockedEdge = _restoredEdge;
                _dockExpandedPos = _restoredNormalPosition;
                var width = Math.Max(1, (int)Math.Round(Bounds.Width * _dockScaling));
                var height = Math.Max(1, (int)Math.Round(Bounds.Height * _dockScaling));
                var area = screen.WorkingArea;
                var peek = (int)Math.Round(12 * _dockScaling);
                _dockHiddenPos = _dockedEdge switch
                {
                    DockEdge.Left => new PixelPoint(area.X - width + peek, _dockExpandedPos.Y),
                    DockEdge.Right => new PixelPoint(area.Right - peek, _dockExpandedPos.Y),
                    DockEdge.Top => new PixelPoint(_dockExpandedPos.X, area.Y - height + peek),
                    DockEdge.Bottom => new PixelPoint(_dockExpandedPos.X, area.Bottom - peek),
                    _ => _dockExpandedPos
                };
                _dockIconPlacement = EdgeSnapHelper.CalculateDockIconPlacement(area, _dockExpandedPos,
                    width, height, _dockedEdge, (int)Math.Round(56 * _dockScaling), (int)Math.Round(28 * _dockScaling));
                _isDocked = true;
                _isDockRevealed = false;
                model.IsEdgeDocked = true;
                Opacity = 0;
                Position = _offscreenParkPos;
                WindowTaskSwitcher.Configure(this, excluded: true);
                ShowDockIcon();
            }
        }
        catch (Exception error)
        {
            _isDocked = false;
            _isDockRevealed = false;
            _dockedEdge = DockEdge.None;
            CloseDockIcon();
            Position = _restoredNormalPosition;
            WindowTaskSwitcher.Configure(this, excluded: false);
            Opacity = 1;
            if (DataContext is MainViewModel model)
            {
                model.IsEdgeDocked = false;
                model.LogPlacementRestoreFailure(error);
            }
        }
        finally
        {
            _isRestoringPlacement = false;
            _placementRestorationCompleted = true;
            ShowActivated = true;
        }
        UpdateStatusAnimationState(restartActiveClocks: true);
        PersistWindowPlacement();
        QueueForegroundDocumentProbe();
        if (_activateAfterPlacementRestore)
        {
            _activateAfterPlacementRestore = false;
            ActivateFromExternalRequest();
        }
    }

    private void PersistWindowPlacement()
    {
        if (!_placementRestorationCompleted || _isRestoringPlacement || _isHiddenToTray
            || WindowState != WindowState.Normal || DataContext is not MainViewModel model) return;
        var position = _isDocked ? _dockExpandedPos : Position;
        if (position == _offscreenParkPos) return;
        var screen = (_isDocked ? Screens.ScreenFromPoint(position) : Screens.ScreenFromWindow(this)) ?? Screens.Primary;
        if (screen is null || Bounds.Width <= 0 || Bounds.Height <= 0) return;
        var area = screen.WorkingArea;
        var size = _edgeLayoutPrepared ? _edgeWindowSize : Bounds.Size;
        model.SaveWindowPlacement(new WindowPlacementState
        {
            X = position.X, Y = position.Y, WorkX = area.X, WorkY = area.Y,
            WorkWidth = area.Width, WorkHeight = area.Height, Scaling = screen.Scaling,
            IsDocked = _isDocked, Edge = _isDocked ? _dockedEdge.ToString() : DockEdge.None.ToString()
        }, size.Width, size.Height);
    }

    private void RestoreDockAfterScreenChange()
    {
        if (DataContext is not MainViewModel model || model.GetSavedWindowPlacement() is null) return;
        _isRestoringPlacement = true;
        _edgeAnimationGeneration++;
        _isSnapAnimating = false;
        _isEdgeFinishing = false;
        model.SetWindowAnimationActive(false);
        _snapClock.Stop();
        _edgeHoverTimer.Stop();
        Opacity = 0;
        ClearDockAnimationSurface();
        CloseDockIcon();
        ClearDockRevealBitmap();
        _isDocked = false;
        _isDockRevealed = false;
        model.IsEdgeDocked = false;
        _placementPrepared = false;
        _placementRestorationCompleted = false;
        PrepareSavedPlacement();
        Dispatcher.UIThread.Post(CompleteSavedPlacementRestore, DispatcherPriority.Loaded);
    }

    private void OnWindowMoveFinished()
    {
        if (_isSnapAnimating || EvaluateAndStartEdgeSnap()) return;
        PersistWindowPlacement();
    }
}
