using System.Runtime.InteropServices;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using PsExporter.Models;
using PsExporter.Services;
using PsExporter.ViewModels;
using SkiaSharp;

namespace PsExporter.Views;

public partial class MainWindow
{
    private const uint RawInputMessage = 0x00FF; // WM_INPUT
    private const ushort GenericDesktopPage = 1, MouseUsage = 2, KeyboardUsage = 6;
    private bool _isPickingSummonProgram;
    private WindowState _summonProgramRestoreState;
    private IDisposable? _summonProgramPickTimeout;
    private IDisposable? _summonPickHoverTimer;
    private WindowPickOutline? _summonPickOutline;
    private IntPtr _summonPickHoveredWindow;
    private bool _summonPickHoveredWindowIsBindable;
    private RawInputDevice[] _previousSummonPickDevices = [];
    private string? _summonPickCandidate;
    private ushort _summonPickMouseDown, _summonPickMouseUp;

    private void OnChooseSummonHotkeyProgramClick(object? sender, RoutedEventArgs e)
    {
        if (!OperatingSystem.IsWindows() || _isPickingSummonProgram || _isSnapAnimating
            || DataContext is not MainViewModel model) return;
        if (_summonHotkeyWindow == IntPtr.Zero || !RegisterSummonPickDevices(_summonHotkeyWindow))
        {
            ReportSummonHotkeyStatus(model, "无法开始选择窗口，请重试");
            return;
        }

        _summonProgramRestoreState = WindowState;
        _isPickingSummonProgram = true;
        var swappedButtons = GetSystemMetrics(23) != 0; // SM_SWAPBUTTON
        _summonPickMouseDown = swappedButtons ? (ushort)4 : (ushort)1;
        _summonPickMouseUp = swappedButtons ? (ushort)8 : (ushort)2;
        _edgeHoverTimer.Stop();
        ApplySummonHotkeyRegistration();
        WindowState = WindowState.Minimized;
        PropertyChanged += OnSummonProgramPickerWindowChanged;
        _summonPickHoverTimer = DispatcherTimer.Run(UpdateSummonPickOutline, TimeSpan.FromMilliseconds(32));
        _summonProgramPickTimeout = DispatcherTimer.RunOnce(() =>
        {
            FinishSummonProgramPick();
            ReportSummonHotkeyStatus(model, "选择窗口超时，已保留原绑定");
        }, TimeSpan.FromSeconds(45));
    }

    private void OnSummonProgramPickerWindowChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == WindowStateProperty && WindowState != WindowState.Minimized)
            FinishSummonProgramPick(restoreWindow: false);
    }

    private void ObserveSummonProgramPickInput(IntPtr input)
    {
        uint size = (uint)Marshal.SizeOf<RawPickInput>();
        var read = GetRawInputData(input, 0x10000003, out var data, ref size, (uint)Marshal.SizeOf<RawInputHeader>());
        if (read == uint.MaxValue || read < Marshal.SizeOf<RawInputHeader>() + 8) return;
        if (data.Header.Type == 1 && data.Body.VirtualKey == 0x1B && (data.Body.KeyFlags & 1) != 0)
        {
            Dispatcher.UIThread.Post(() => FinishSummonProgramPick());
            return;
        }
        if (data.Header.Type != 0) return;
        if ((data.Body.MouseButtonFlags & _summonPickMouseDown) != 0)
            _summonPickCandidate = GetSummonPickProgram(GetSummonPickWindowAtPointer());
        if ((data.Body.MouseButtonFlags & _summonPickMouseUp) != 0 && _summonPickCandidate is { } program)
        {
            _summonPickCandidate = null;
            // 等点击松开后再恢复窗口，让目标应用照常收到这次点击。
            Dispatcher.UIThread.Post(() => FinishSummonProgramPick(program));
        }
    }

    private bool UpdateSummonPickOutline()
    {
        if (!_isPickingSummonProgram) return false;
        var window = GetSummonPickWindowAtPointer();
        if (window != _summonPickHoveredWindow)
        {
            _summonPickHoveredWindow = window;
            _summonPickHoveredWindowIsBindable = GetSummonPickProgram(window) is not null;
        }
        if (!_summonPickHoveredWindowIsBindable)
        {
            _summonPickOutline?.Hide();
            return true;
        }
        try
        {
            if (_summonPickOutline is null)
            {
                var accent = this.TryFindResource("Color.Accent", out var value) && value is Color color
                    ? new SKColor(color.R, color.G, color.B)
                    : SKColors.DodgerBlue;
                _summonPickOutline = new WindowPickOutline(accent);
            }
            _summonPickOutline.Follow(window);
            return true;
        }
        catch (Exception error)
        {
            _summonPickOutline?.Dispose();
            _summonPickOutline = null;
            (DataContext as MainViewModel)?.AppendLog("Error", "无法显示选窗高亮：" + error.Message);
            return false;
        }
    }

    private static IntPtr GetSummonPickWindowAtPointer()
        => GetCursorPos(out var cursor) ? GetAncestor(WindowFromPoint(cursor), 2) : IntPtr.Zero; // GA_ROOT

    private static string? GetSummonPickProgram(IntPtr window)
    {
        if (window == IntPtr.Zero) return null;
        var name = new StringBuilder(128);
        if (GetClassName(window, name, name.Capacity) == 0
            || name.ToString() is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd")
            return null;
        return GetWindowExecutable(window, excludeThisProcess: true);
    }

    private void FinishSummonProgramPick(string? program = null, bool restoreWindow = true)
    {
        if (!_isPickingSummonProgram) return;
        _isPickingSummonProgram = false;
        _summonPickCandidate = null;
        PropertyChanged -= OnSummonProgramPickerWindowChanged;
        _summonProgramPickTimeout?.Dispose();
        _summonProgramPickTimeout = null;
        _summonPickHoverTimer?.Dispose();
        _summonPickHoverTimer = null;
        _summonPickOutline?.Dispose();
        _summonPickOutline = null;
        _summonPickHoveredWindow = IntPtr.Zero;
        _summonPickHoveredWindowIsBindable = false;
        SetSummonPickDevices(GetSummonPickDevices(IntPtr.Zero, 1)); // RIDEV_REMOVE
        if (_previousSummonPickDevices.Length > 0) SetSummonPickDevices(_previousSummonPickDevices);
        _previousSummonPickDevices = [];
        if (_isClosing) return;

        if (restoreWindow)
        {
            WindowState = _summonProgramRestoreState;
            BringToForeground();
        }
        if (program is not null && DataContext is MainViewModel model)
        {
            model.SummonHotkeyProgram = SummonHotkey.MatchesProgram(AppSettings.DefaultSummonHotkeyProgram, program)
                ? AppSettings.DefaultSummonHotkeyProgram : program;
            ReportSummonHotkeyStatus(model, $"快捷键已绑定到 {model.SummonHotkeyProgramDisplay}");
        }
        ApplySummonHotkeyRegistration();
        if (_isDocked && _isDockRevealed)
        {
            _pointerOutsideDockSince = DateTime.MinValue;
            _edgeHoverTimer.Start();
        }
    }

    private bool RegisterSummonPickDevices(IntPtr window)
    {
        uint count = 0;
        var size = (uint)Marshal.SizeOf<RawInputDevice>();
        if (GetRegisteredRawInputDevices(null, ref count, size) == uint.MaxValue
            && Marshal.GetLastPInvokeError() != 122) return false; // ERROR_INSUFFICIENT_BUFFER reports the required count.
        var previous = new RawInputDevice[count];
        var read = count == 0 ? 0 : GetRegisteredRawInputDevices(previous, ref count, size);
        if (read == uint.MaxValue) return false;
        if (!SetSummonPickDevices(GetSummonPickDevices(window, 0x100))) return false; // RIDEV_INPUTSINK
        _previousSummonPickDevices = previous.Take((int)read).Where(device => device.Page == GenericDesktopPage
            && device.Usage is MouseUsage or KeyboardUsage).ToArray();
        return true;
    }

    private static RawInputDevice[] GetSummonPickDevices(IntPtr window, uint flags)
        => [new(GenericDesktopPage, MouseUsage, flags, window), new(GenericDesktopPage, KeyboardUsage, flags, window)];

    private static bool SetSummonPickDevices(RawInputDevice[] devices)
        => RegisterRawInputDevices(devices, (uint)devices.Length, (uint)Marshal.SizeOf<RawInputDevice>());

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct RawInputDevice(ushort Page, ushort Usage, uint Flags, IntPtr Window);

    [StructLayout(LayoutKind.Sequential)]
    private struct RawInputHeader
    {
        internal uint Type, Size;
        internal IntPtr Device, WParam;
    }

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct RawPickBody
    {
        [FieldOffset(2)] internal ushort KeyFlags;
        [FieldOffset(4)] internal ushort MouseButtonFlags;
        [FieldOffset(6)] internal ushort VirtualKey;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RawPickInput
    {
        internal RawInputHeader Header;
        internal RawPickBody Body;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterRawInputDevices([In] RawInputDevice[] devices, uint count, uint size);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetRegisteredRawInputDevices([Out] RawInputDevice[]? devices, ref uint count, uint size);

    [DllImport("user32.dll")]
    private static extern uint GetRawInputData(IntPtr input, uint command, out RawPickInput data, ref uint size, uint headerSize);

    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(NativePoint point);
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr window, uint flags);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll", EntryPoint = "GetClassNameW", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr window, StringBuilder name, int count);
}
