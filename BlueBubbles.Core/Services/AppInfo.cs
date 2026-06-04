using System.Reflection;

namespace BlueBubbles.Core.Services;

/// <summary>
/// Shared, unpackaged-safe access to app identity (version, etc.). Reads from the entry
/// assembly rather than <c>Package.Current</c> so it works in an unpackaged WinUI3 app;
/// the csproj <c>&lt;Version&gt;</c> flows into the assembly version at build time.
/// </summary>
public static class AppInfo
{
    /// <summary>3-part semantic version (Major.Minor.Patch), e.g. "0.18.0".</summary>
    public static string Version
    {
        get
        {
            var asm = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();
            var v = asm.GetName().Version;
            return v is null ? "0.0.0" : $"{v.Major}.{v.Minor}.{v.Build}";
        }
    }
}
