using System;
using System.Runtime.InteropServices;

using Microsoft.Win32;

using static Dalamud.api;

namespace MidiBard.IPC;

enum WineHost { None, Linux, Mac, Other }

internal static class IPCManagerFactory
{
    /// <summary>
    /// Creates the appropriate IIPCManager for the current runtime environment.
    /// Returns <see cref="LinuxIPCManager"/> when running under Wine on Linux,
    /// and <see cref="WindowsIPCManager"/> on native Windows.
    /// </summary>
    internal static IIPCManager Create()
    {
        if (GetWineHost() == WineHost.Linux)
        {
            PluginLog.Debug("Wine detected – using LinuxIPCManager");
            return new LinuxIPCManager();
        }

        PluginLog.Debug("Native Windows detected – using WindowsIPCManager");
        return new WindowsIPCManager();
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void WineGetHostVersionDelegate(
        out IntPtr sysName,
        out IntPtr releaseName);

    static WineHost GetWineHost()
    {
        var hNTDLL = NativeLibrary.Load("ntdll.dll");
        if (hNTDLL == IntPtr.Zero) return WineHost.None;

        if (!NativeLibrary.TryGetExport(hNTDLL, "wine_get_host_version", out var fnPtr))
            return WineHost.None;

        var wineGetHostVersion = Marshal.GetDelegateForFunctionPointer
            <WineGetHostVersionDelegate>(fnPtr);

        wineGetHostVersion(out var sysNamePtr, out var releaseNamePtr);

        var sysName = Marshal.PtrToStringAnsi(sysNamePtr);

        return sysName?.ToLower() switch
        {
            "linux" => WineHost.Linux,
            "darwin" => WineHost.Mac,
            _ => WineHost.Other
        };
    }
}
