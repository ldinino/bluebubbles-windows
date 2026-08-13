using BlueBubbles.Core.Services;

namespace BlueBubbles.Windows.Tests;

public class AppLogRedirectTests
{
    [Fact]
    public void LogDirectory_ResolvesUnderTheTempRedirect()
    {
        Assert.False(string.IsNullOrEmpty(TestLogRedirect.TempDir));
        Assert.Equal(TestLogRedirect.TempDir, AppLog.LogDirectory);

        var real = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "BlueBubbles", "logs");
        Assert.NotEqual(real, AppLog.LogDirectory);
    }

    [Fact]
    public void WrittenLine_LandsInTheRedirectedFile()
    {
        var marker = "redirect-probe-" + Guid.NewGuid().ToString("N");
        var now = DateTime.Now;

        AppLog.Info(LogCategory.App, marker);

        var path = Path.Combine(AppLog.LogDirectory, $"bluebubbles-{now:yyyy-MM-dd}.log");
        Assert.True(File.Exists(path), $"expected log file at {path}");
        Assert.Contains(marker, File.ReadAllText(path));
    }
}
