using System;

using Microsoft.Win32;

namespace WebContent.UxTests.Harness;

/// <summary>
/// Ticket #67 (SC-8): the probe-side control of the OS-configured (WinINET) proxy - the exact
/// per-user HKCU Internet Settings that "Internet Options > LAN settings" edits and that the
/// WebView2 system-proxy resolver follows for browsers launched WITHOUT --proxy-server. The
/// snapshot/restore pair keeps the workstation exactly as found: whatever ProxyEnable value,
/// ProxyServer string (this lab's is a socks= entry), ProxyOverride, or AutoConfigURL was
/// present is written back verbatim, and InternetSetOption broadcasts the change so the
/// settings are live for processes started afterwards without any sign-out.
/// </summary>
public static class WinInetProxyControl
{
    private const string InternetSettingsKey =
        @"Software\Microsoft\Windows\CurrentVersion\Internet Settings";

    /// <summary>The proxy settings as HKCU held them, with missing values as null.</summary>
    public sealed class Snapshot
    {
        public bool ProxyEnable;
        public string ProxyServer;
        public string ProxyOverride;
        public string AutoConfigUrl;
    }

    public static Snapshot Capture()
    {
        using var key = Registry.CurrentUser.OpenSubKey(InternetSettingsKey, writable: false)
            ?? throw new InvalidOperationException($"HKCU\\{InternetSettingsKey} is not readable.");
        return new Snapshot
        {
            ProxyEnable = ReadValue(key, "ProxyEnable") is int enabled && enabled != 0,
            ProxyServer = ReadValue(key, "ProxyServer") as string,
            ProxyOverride = ReadValue(key, "ProxyOverride") as string,
            AutoConfigUrl = ReadValue(key, "AutoConfigURL") as string,
        };
    }

    /// <summary>
    /// Points the per-user system proxy at 127.0.0.1:port with "<local>" as the bypass list
    /// (loopback targets like the CDP endpoint stay direct), clears any PAC URL, and
    /// broadcasts INTERNET_OPTION_SETTINGS_CHANGED + REFRESH.
    /// </summary>
    public static void UseLoopbackProxy(int port)
    {
        using (var key = Registry.CurrentUser.OpenSubKey(InternetSettingsKey, writable: true)
            ?? throw new InvalidOperationException($"HKCU\\{InternetSettingsKey} is not writable."))
        {
            key.SetValue("ProxyEnable", 1, RegistryValueKind.DWord);
            key.SetValue("ProxyServer", $"127.0.0.1:{port}", RegistryValueKind.String);
            key.SetValue("ProxyOverride", "<local>", RegistryValueKind.String);
            key.SetValue("AutoConfigURL", string.Empty, RegistryValueKind.String);
        }

        BroadcastChange();
    }

    /// <summary>Writes the captured settings back verbatim (missing values deleted), then broadcasts.</summary>
    public static void Restore(Snapshot snapshot)
    {
        if (snapshot == null) return;
        using (var key = Registry.CurrentUser.OpenSubKey(InternetSettingsKey, writable: true)
            ?? throw new InvalidOperationException($"HKCU\\{InternetSettingsKey} is not writable."))
        {
            WriteValue(key, "ProxyEnable", snapshot.ProxyEnable ? 1 : 0, RegistryValueKind.DWord);
            WriteValue(key, "ProxyServer", snapshot.ProxyServer, RegistryValueKind.String);
            WriteValue(key, "ProxyOverride", snapshot.ProxyOverride, RegistryValueKind.String);
            WriteValue(key, "AutoConfigURL", snapshot.AutoConfigUrl, RegistryValueKind.String);
        }

        BroadcastChange();
    }

    private static object ReadValue(RegistryKey key, string name)
    {
        try { return key.GetValue(name); } catch { return null; }
    }

    private static void WriteValue(RegistryKey key, string name, object value, RegistryValueKind kind)
    {
        try
        {
            if (value == null) key.DeleteValue(name, throwOnMissingValue: false);
            else key.SetValue(name, value, kind);
        }
        catch { /* best-effort restore; the probe log records the snapshot either way */ }
    }

    // wininet InternetSetOptionW: SETTINGS_CHANGED (39) tells every WinINET-aware process the
    // per-user settings moved; REFRESH (37) re-reads them. The exact pair the Control Panel
    // fires when OK closes the LAN-settings dialog.
    [System.Runtime.InteropServices.DllImport("wininet.dll", SetLastError = true)]
    private static extern bool InternetSetOptionW(IntPtr hInternet, int dwOption, IntPtr lpBuffer, int dwBufferLength);

    private static void BroadcastChange()
    {
        try { InternetSetOptionW(IntPtr.Zero, 39, IntPtr.Zero, 0); } catch { }
        try { InternetSetOptionW(IntPtr.Zero, 37, IntPtr.Zero, 0); } catch { }
    }
}
