using System.IO.Compression;
using BlueBubbles.Core.Services;

namespace BlueBubbles.Windows.Tests;

public class LogFormatTests
{
    [Fact]
    public void FormatLine_HasTimestampLevelAndCategoryTags()
    {
        var ts = new DateTime(2026, 6, 1, 14, 3, 12, 123);

        var line = AppLog.FormatLine(ts, LogLevel.Info, LogCategory.Socket, "connected");

        Assert.Equal("[2026-06-01 14:03:12.123] [INFO ] [Socket] connected", line);
    }

    [Fact]
    public void FormatLine_TagsAreParseableByCategoryFilter()
    {
        // The viewer filters with entry.Contains($"[{category}]"); every category must round-trip.
        foreach (LogCategory category in Enum.GetValues<LogCategory>())
        {
            var line = AppLog.FormatLine(DateTime.Now, LogLevel.Warn, category, "msg");
            Assert.Contains($"[{category}]", line);
        }
    }

    [Theory]
    [InlineData(LogLevel.Debug, "DEBUG")]
    [InlineData(LogLevel.Info, "INFO ")]
    [InlineData(LogLevel.Warn, "WARN ")]
    [InlineData(LogLevel.Error, "ERROR")]
    public void FormatLine_LevelTagIsFixedWidth(LogLevel level, string expectedTag)
    {
        var line = AppLog.FormatLine(DateTime.Now, level, LogCategory.App, "x");
        Assert.Contains($"[{expectedTag}]", line);
        Assert.Equal(5, expectedTag.Length);
    }
}

public class LogRetentionTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "bb-logtest-" + Guid.NewGuid().ToString("N"));

    public LogRetentionTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private string WriteLog(string name, int sizeBytes, DateTime lastWrite)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, new byte[sizeBytes]);
        File.SetLastWriteTime(path, lastWrite);
        return path;
    }

    [Fact]
    public void Prune_DeletesFilesOlderThanRetentionWindow()
    {
        var stale1 = WriteLog("bluebubbles-old1.log", 10, DateTime.Now.AddDays(-10));
        var stale2 = WriteLog("bluebubbles-old2.log", 10, DateTime.Now.AddDays(-8));
        var fresh = WriteLog("bluebubbles-new.log", 10, DateTime.Now);

        LogRetention.Prune(_dir, retentionDays: 7, maxTotalBytes: long.MaxValue);

        Assert.False(File.Exists(stale1));
        Assert.False(File.Exists(stale2));
        Assert.True(File.Exists(fresh));
    }

    [Fact]
    public void Prune_EnforcesSizeCap_DroppingOldestButKeepingNewest()
    {
        // Three 1 KB files, all within the retention window, oldest -> newest.
        var oldest = WriteLog("bluebubbles-1.log", 1024, DateTime.Now.AddHours(-3));
        var middle = WriteLog("bluebubbles-2.log", 1024, DateTime.Now.AddHours(-2));
        var newest = WriteLog("bluebubbles-3.log", 1024, DateTime.Now.AddHours(-1));

        // Cap of 1500 bytes forces dropping the two oldest (3072 -> 2048 -> 1024).
        LogRetention.Prune(_dir, retentionDays: 365, maxTotalBytes: 1500);

        Assert.False(File.Exists(oldest));
        Assert.False(File.Exists(middle));
        Assert.True(File.Exists(newest));
    }

    [Fact]
    public void Prune_MissingDirectory_DoesNotThrow()
    {
        var ex = Record.Exception(() =>
            LogRetention.Prune(Path.Combine(_dir, "nope"), 7, 1000));
        Assert.Null(ex);
    }
}

public class LogExportTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bb-export-" + Guid.NewGuid().ToString("N"));
    private readonly string _srcDir;
    private readonly string _zipPath;

    public LogExportTests()
    {
        _srcDir = Path.Combine(_root, "logs");
        _zipPath = Path.Combine(_root, "out.zip");
        Directory.CreateDirectory(_srcDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public void WriteZip_IncludesHeaderAndEveryLogFile()
    {
        File.WriteAllText(Path.Combine(_srcDir, "bluebubbles-a.log"), "alpha-content");
        File.WriteAllText(Path.Combine(_srcDir, "bluebubbles-b.log"), "bravo-content");

        LogExport.WriteZip(_srcDir, _zipPath, header: "HEADER_MARKER");

        using var zip = ZipFile.OpenRead(_zipPath);

        var info = zip.GetEntry("_export-info.txt");
        Assert.NotNull(info);
        Assert.Equal("HEADER_MARKER", ReadEntry(info!));

        Assert.Equal("alpha-content", ReadEntry(zip.GetEntry("bluebubbles-a.log")!));
        Assert.Equal("bravo-content", ReadEntry(zip.GetEntry("bluebubbles-b.log")!));
    }

    [Fact]
    public void WriteZip_OverwritesExistingDestination()
    {
        File.WriteAllText(_zipPath, "not a real zip");
        File.WriteAllText(Path.Combine(_srcDir, "bluebubbles-a.log"), "x");

        LogExport.WriteZip(_srcDir, _zipPath, header: "H");

        using var zip = ZipFile.OpenRead(_zipPath); // throws if still the bogus file
        Assert.NotNull(zip.GetEntry("_export-info.txt"));
    }

    [Fact]
    public void WriteZip_MissingSourceDir_StillWritesHeaderOnly()
    {
        LogExport.WriteZip(Path.Combine(_root, "absent"), _zipPath, header: "H");

        using var zip = ZipFile.OpenRead(_zipPath);
        Assert.NotNull(zip.GetEntry("_export-info.txt"));
        Assert.Empty(zip.Entries.Where(e => e.Name.EndsWith(".log")));
    }

    [Fact]
    public void BuildHeader_ContainsVersionAndExportTimestamp()
    {
        var header = LogExport.BuildHeader();
        Assert.Contains("App version :", header);
        Assert.Contains("Exported    :", header);
    }

    [Fact]
    public void SuggestedFileName_IsAZipWithThePrefix()
    {
        var name = LogExport.SuggestedFileName();
        Assert.Matches(@"^bluebubbles-logs-\d+\.\d+\.\d+-\d{4}-\d{2}-\d{2}\.zip$", name);
    }

    private static string ReadEntry(ZipArchiveEntry entry)
    {
        using var reader = new StreamReader(entry.Open());
        return reader.ReadToEnd();
    }
}
