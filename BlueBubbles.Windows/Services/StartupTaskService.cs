using Microsoft.Win32;

namespace BlueBubbles.Windows.Services;

/// <summary>
/// Launch-at-sign-in for an unpackaged app, via the per-user
/// <c>HKCU\Software\Microsoft\Windows\CurrentVersion\Run</c> key. (The packaged
/// <c>Windows.ApplicationModel.StartupTask</c> API needs package identity, which an unpackaged
/// .exe does not have.) When "start minimized" is set, a <c>--minimized</c> argument is added to
/// the registered command; <see cref="App"/> reads it on launch to start hidden in the tray.
/// </summary>
public sealed class StartupTaskService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "BlueBubbles";
    public const string MinimizedArg = "--minimized";

    /// <summary>True when a Run entry for this app currently exists.</summary>
    public bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            return key?.GetValue(ValueName) is string v && v.Length > 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Registers or removes the Run entry. When enabling, the command points at the currently
    /// running executable (so it stays correct even if the install location changes) and includes
    /// <see cref="MinimizedArg"/> when <paramref name="startMinimized"/> is set.
    /// </summary>
    /// <returns>True if the resulting state matches what was requested.</returns>
    public bool SetEnabled(bool enable, bool startMinimized)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true)
                            ?? Registry.CurrentUser.CreateSubKey(RunKeyPath);
            if (key is null) return false;

            if (enable)
            {
                var exe = Environment.ProcessPath;
                if (string.IsNullOrEmpty(exe)) return false;
                var command = startMinimized ? $"\"{exe}\" {MinimizedArg}" : $"\"{exe}\"";
                key.SetValue(ValueName, command, RegistryValueKind.String);
                return true;
            }

            if (key.GetValue(ValueName) is not null)
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            return false;
        }
        catch
        {
            return IsEnabled();
        }
    }
}
