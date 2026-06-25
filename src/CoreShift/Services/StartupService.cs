using System;
using System.Diagnostics;
using Microsoft.Win32;

namespace CoreShift.Services;

/// <summary>
/// Manages CoreShift's auto-start entry under
/// <c>HKCU\Software\Microsoft\Windows\CurrentVersion\Run</c>.
/// </summary>
public static class StartupService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string AppKeyPath = @"Software\CoreShift";
    private const string ValueName = "CoreShift";

    /// <summary>Full path to the running executable (single-file aware).</summary>
    public static string ExecutablePath =>
        Environment.ProcessPath
        ?? Process.GetCurrentProcess().MainModule?.FileName
        ?? string.Empty;

    public static bool IsEnabled()
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
        return key?.GetValue(ValueName) is string value && !string.IsNullOrEmpty(value);
    }

    public static void Enable()
    {
        string path = ExecutablePath;
        if (string.IsNullOrEmpty(path)) return;

        using RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKeyPath);
        key.SetValue(ValueName, $"\"{path}\"");
    }

    public static void Disable()
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
        key?.DeleteValue(ValueName, throwOnMissingValue: false);
    }

    /// <summary>
    /// On the very first launch, injects the Run entry once and records that it did so,
    /// so a user who later disables auto-start is not re-enabled on the next boot.
    /// </summary>
    public static void EnsureFirstRun()
    {
        using RegistryKey appKey = Registry.CurrentUser.CreateSubKey(AppKeyPath);
        if (appKey.GetValue("Initialized") as string == "1")
            return;

        Enable();
        appKey.SetValue("Initialized", "1");
    }
}
