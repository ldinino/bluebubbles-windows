using System.IO.Compression;
using System.Runtime.InteropServices;

namespace BlueBubbles.Core.Services;

/// <summary>
/// Bundles the rolling log files into a single zip for bug reports, prefixed with an
/// environment header (app version + OS). File-system logic lives here in Core so it stays
/// testable; the Windows layer only supplies the user-chosen destination path.
/// </summary>
public static class LogExport
{
    /// <summary>Human-readable environment block placed at the top of every export.</summary>
    public static string BuildHeader()
    {
        var arch = RuntimeInformation.OSArchitecture;
        return string.Join(Environment.NewLine,
            "BlueBubbles for Windows — diagnostic log export",
            $"App version : {AppInfo.Version}",
            $"OS          : {Environment.OSVersion.VersionString} ({arch})",
            $".NET        : {Environment.Version}",
            $"Exported    : {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC",
            "");
    }

    /// <summary>Default export file name, e.g. <c>bluebubbles-logs-0.18.0-2026-06-01.zip</c>.</summary>
    public static string SuggestedFileName() =>
        $"bluebubbles-logs-{AppInfo.Version}-{DateTime.Now:yyyy-MM-dd}.zip";

    /// <summary>
    /// Write a zip of every <c>*.log</c> file in <paramref name="sourceLogDir"/> to
    /// <paramref name="destZipPath"/>, prepending a <c>_export-info.txt</c> entry with the header.
    /// Overwrites any existing destination. Files are read with a shared handle so an actively
    /// written log doesn't cause a lock conflict.
    /// </summary>
    public static void WriteZip(string sourceLogDir, string destZipPath, string? header = null)
    {
        header ??= BuildHeader();

        if (File.Exists(destZipPath))
            File.Delete(destZipPath);

        using var zip = ZipFile.Open(destZipPath, ZipArchiveMode.Create);

        var infoEntry = zip.CreateEntry("_export-info.txt");
        using (var writer = new StreamWriter(infoEntry.Open()))
            writer.Write(header);

        if (!Directory.Exists(sourceLogDir))
            return;

        foreach (var file in Directory.GetFiles(sourceLogDir, "*.log"))
        {
            var entry = zip.CreateEntry(Path.GetFileName(file));
            using var src = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var dst = entry.Open();
            src.CopyTo(dst);
        }
    }
}
