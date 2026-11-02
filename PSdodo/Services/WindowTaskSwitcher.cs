using System.Runtime.InteropServices;
using Avalonia.Controls;
namespace PsExporter.Services;
internal static class WindowTaskSwitcher
{
    internal static void Configure(Window window, bool excluded, bool noActivate = false)
    {
        if (window.ShowInTaskbar != !excluded) window.ShowInTaskbar = !excluded;
        if (!OperatingSystem.IsWindows() || window.TryGetPlatformHandle() is not { } handle) return;
        var originalStyle=GetWindowLongPtr(handle.Handle,-20).ToInt64();
        var style=originalStyle;
        style=excluded ? (style|0x80L)&~0x40000L : (style&~0x80L)|0x40000L;
        if (noActivate) style|=0x08000000L;
        if (style == originalStyle) return;
        SetWindowLongPtr(handle.Handle,-20,new IntPtr(style));
        SetWindowPos(handle.Handle,IntPtr.Zero,0,0,0,0,0x0001|0x0002|0x0004|0x0010|0x0020);
    }
    internal static void RaiseWithoutActivation(Window window)
    {
        if (!OperatingSystem.IsWindows() || window.TryGetPlatformHandle() is not { } handle) return;
        // 仅在展开起步前提升层级；尊重置顶设置，不激活、不改位置尺寸。
        SetWindowPos(handle.Handle, window.Topmost ? new IntPtr(-1) : IntPtr.Zero, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010 | 0x0200);
    }

    [DllImport("user32.dll",EntryPoint="GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr hwnd,int index);
    [DllImport("user32.dll",EntryPoint="SetWindowLongPtrW")] private static extern IntPtr SetWindowLongPtr(IntPtr hwnd,int index,IntPtr value);
    [DllImport("user32.dll",EntryPoint="SetWindowPos")] private static extern bool SetWindowPos(IntPtr hwnd,IntPtr after,int x,int y,int width,int height,uint flags);
}
