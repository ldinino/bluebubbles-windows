using System.Runtime.CompilerServices;
using BlueBubbles.Core.Services;

namespace BlueBubbles.Windows.Tests;

/// <summary>
/// Sends the suite's file logging to a disposable temp directory so fixture noise never lands in
/// the real <c>%LocalAppData%\BlueBubbles\logs</c>, which is used as an evidence channel.
/// </summary>
internal static class TestLogRedirect
{
    internal static string TempDir { get; private set; } = string.Empty;

    // [ModuleInitializer] runs at assembly load, before xunit discovers or runs anything in
    // parallel. That ordering is required: AppLog caches its directory on first use.
    [ModuleInitializer]
    internal static void Initialize()
    {
        TempDir = Path.Combine(Path.GetTempPath(), "BlueBubblesTests", "logs-" + Guid.NewGuid().ToString("N"));
        AppLog.RedirectLogDirectory(TempDir);

        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try { Directory.Delete(TempDir, recursive: true); }
            catch { /* best-effort cleanup; never fail a run over temp files */ }
        };
    }
}
