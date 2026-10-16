using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;

namespace SmartHdrManager;

public record LiveDisplay(string Key, string Name, string Source, bool Primary, bool Virtual,
    bool QueryOk, bool Supported, bool Enabled, uint Width, uint Height, double Hertz, uint Connection);

public static partial class HdrController
{
    // Device interface paths are persisted; adapter LUIDs and target IDs are resolved afresh on every operation.
    public static List<LiveDisplay> Inspect()
    {
        if (QueryActivePaths(out var paths, out var modes) != 0)
            throw new InvalidOperationException("Display topology query failed; no targets may be controlled.");
        return paths.Select(p => {
            var n = GetTargetName(p.targetInfo.adapterId, p.targetInfo.id);
            bool ok = TryGetHdrState(p.targetInfo.adapterId, p.targetInfo.id, out bool supported, out bool enabled);
            uint width = 0, height = 0;
            if (p.sourceInfo.modeInfoIdx < modes.Length) {
                var m = modes[p.sourceInfo.modeInfoIdx];
                width = (uint)m.u0; height = (uint)(m.u0 >> 32);
            }
            uint tech = p.targetInfo.outputTechnology;
            return new LiveDisplay(n.DevicePath, n.Name, GetSourceGdiName(p), IsPrimaryDisplayPath(p, modes),
                tech == 16 || tech == 17 || n.DevicePath.Length == 0 || n.Name.Contains("virtual", StringComparison.OrdinalIgnoreCase),
                ok, supported, enabled, width, height,
                p.targetInfo.refreshRateDenominator == 0 ? 0 : (double)p.targetInfo.refreshRateNumerator / p.targetInfo.refreshRateDenominator, tech);
        }).ToList();
    }

    public static bool SetVerified(string key, bool desired)
    {
        LastError = "";
        // No retry of the write: a failed or ambiguous write must not cause flicker. Readback is bounded.
        if (string.IsNullOrEmpty(key) || QueryActivePaths(out var paths, out _) != 0) { LastError = "Topology unavailable"; return false; }
        var matches = DisplaySelection.Match(paths, key, p => GetTargetName(p.targetInfo.adapterId, p.targetInfo.id).DevicePath);
        if (matches.Length != 1) { LastError = "Missing or ambiguous selected display"; return false; }
        var p = matches[0];
        if (p.targetInfo.outputTechnology == 16 || p.targetInfo.outputTechnology == 17) return false;
        if (!TryGetHdrState(p.targetInfo.adapterId, p.targetInfo.id, out bool supported, out bool actual, out bool api) || !supported) { LastError = "HDR query failed or unsupported"; return false; }
        if (actual == desired) return true;
        int result = SetHdrOnDisplay(p.targetInfo.adapterId, p.targetInfo.id, desired, api);
        if (result != 0) { LastError = "DisplayConfigSetDeviceInfo: " + result; return false; }
        for (int i = 0; i < 10; i++) {
            Thread.Sleep(200);
            var live = Inspect().Where(d => string.Equals(d.Key, key, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (live.Length != 1 || !live[0].QueryOk) { LastError = "Display disappeared during readback"; return false; }
            if (live[0].Enabled == desired) return true;
        }
        LastError = "HDR state not confirmed within 2 seconds";
        return false;
    }

    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hwnd);
    public static bool PlaceGameWindow(IntPtr hwnd, string key)
    {
        if (hwnd == IntPtr.Zero || IsIconic(hwnd)) return false;
        var ds = Inspect().Where(d => d.Key == key && !d.Virtual).ToArray();
        if (ds.Length != 1) return false;
        if (WindowDisplay(hwnd) == key) return true;
        var screen = System.Windows.Forms.Screen.AllScreens.SingleOrDefault(s => s.DeviceName == ds[0].Source);
        if (screen == null) return false;
        // Move only: no activation, Z-order, size, desktop layout or resolution changes.
        return SetWindowPos(hwnd, IntPtr.Zero, screen.Bounds.X, screen.Bounds.Y, 0, 0, 0x15);
    }
    public static string? WindowDisplay(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return null;
        var info = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>(), szDevice = string.Empty };
        if (!GetMonitorInfo(MonitorFromWindow(hwnd, 0), ref info)) return null;
        var matches = Inspect().Where(d => d.Source == info.szDevice).ToArray();
        return matches.Length == 1 ? matches[0].Key : null; // cloned output is ambiguous
    }
}
