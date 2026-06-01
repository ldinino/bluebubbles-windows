using BlueBubbles.Core.Configuration;
using BlueBubbles.Core.Services;

namespace BlueBubbles.Windows.Tests;

public class SettingsServiceTests
{
    [Fact]
    public void SaveAndLoad_RoundTrips()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "bb_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var filePath = Path.Combine(tempDir, "settings.json");

        try
        {
            var appSettings = new AppSettings { FinishedSetup = true, ServerAddress = "http://10.0.0.1:1234" };
            var serverConfig = new ServerConfiguration
            {
                ServerUrl = "http://10.0.0.1:1234",
                Password = "test-pass",
                FcmProjectId = "proj-123",
                FcmApiKey = "key-abc"
            };

            var svc = new SettingsService(appSettings, serverConfig, filePath);
            svc.Save();

            Assert.True(File.Exists(filePath));

            var appSettings2 = new AppSettings();
            var serverConfig2 = new ServerConfiguration();
            var svc2 = new SettingsService(appSettings2, serverConfig2, filePath);
            svc2.Load();

            Assert.True(appSettings2.FinishedSetup);
            Assert.Equal("http://10.0.0.1:1234", appSettings2.ServerAddress);
            Assert.Equal("http://10.0.0.1:1234", serverConfig2.ServerUrl);
            Assert.Equal("test-pass", serverConfig2.Password);
            Assert.Equal("proj-123", serverConfig2.FcmProjectId);
            Assert.Equal("key-abc", serverConfig2.FcmApiKey);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void SaveAndLoad_RoundTripsAppearanceMessagingAndPrivateApi()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "bb_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var filePath = Path.Combine(tempDir, "settings.json");

        try
        {
            var appSettings = new AppSettings
            {
                Theme = 2,
                ColorfulBubbles = true,
                HideDividers = true,
                AvatarScale = 1.5,
                Use24HrFormat = true,
                AutoDownload = false,
                SendWithReturn = false,
                SendDelay = 5,
                ServerPrivateAPI = true,
                PrivateManualMarkAsRead = true,
                LaunchAtStartup = true,
                LastSelectedChatGuid = "chat-guid-42"
            };

            var svc = new SettingsService(appSettings, new ServerConfiguration(), filePath);
            svc.Save();

            var appSettings2 = new AppSettings();
            var svc2 = new SettingsService(appSettings2, new ServerConfiguration(), filePath);
            svc2.Load();

            Assert.Equal(2, appSettings2.Theme);
            Assert.True(appSettings2.ColorfulBubbles);
            Assert.True(appSettings2.HideDividers);
            Assert.Equal(1.5, appSettings2.AvatarScale);
            Assert.True(appSettings2.Use24HrFormat);
            Assert.False(appSettings2.AutoDownload);
            Assert.False(appSettings2.SendWithReturn);
            Assert.Equal(5, appSettings2.SendDelay);
            Assert.Equal(true, appSettings2.ServerPrivateAPI);
            Assert.True(appSettings2.PrivateManualMarkAsRead);
            Assert.True(appSettings2.LaunchAtStartup);
            Assert.Equal("chat-guid-42", appSettings2.LastSelectedChatGuid);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void Load_OldFileMissingNewFields_PreservesDefaults()
    {
        // A settings file written before these fields existed must not clobber the
        // AppSettings constructor defaults (e.g. SendWithReturn/AutoDownload default true).
        var tempFile = Path.GetTempFileName();
        File.WriteAllText(tempFile, "{ \"finishedSetup\": true }");

        try
        {
            var appSettings = new AppSettings();
            var svc = new SettingsService(appSettings, new ServerConfiguration(), tempFile);
            svc.Load();

            Assert.True(appSettings.FinishedSetup);
            Assert.True(appSettings.SendWithReturn);
            Assert.True(appSettings.AutoDownload);
            Assert.Equal(1.0, appSettings.AvatarScale);
            Assert.True(appSettings.PrivateSendTypingIndicators);
            Assert.True(appSettings.PrivateMarkChatAsRead);
            Assert.True(appSettings.CloseToTray);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public void Load_MissingFile_DoesNotThrow()
    {
        var appSettings = new AppSettings();
        var serverConfig = new ServerConfiguration();
        var svc = new SettingsService(appSettings, serverConfig,
            Path.Combine(Path.GetTempPath(), "nonexistent_" + Guid.NewGuid() + ".json"));

        var ex = Record.Exception(() => svc.Load());

        Assert.Null(ex);
        Assert.False(appSettings.FinishedSetup);
    }

    [Fact]
    public void Load_PreVersionFile_MigratesAppearanceDefaultsOn()
    {
        // A pre-v1 file (no settingsVersion) persisted the then-unused appearance toggles as false.
        // The v0→v1 migration coerces them to their intended on-by-default values.
        var tempFile = Path.GetTempFileName();
        File.WriteAllText(tempFile, "{ \"colorfulAvatars\": false, \"showDeliveryTimestamps\": false }");

        try
        {
            var appSettings = new AppSettings();
            var svc = new SettingsService(appSettings, new ServerConfiguration(), tempFile);
            svc.Load();

            Assert.True(appSettings.ColorfulAvatars);
            Assert.True(appSettings.ShowDeliveryTimestamps);
            Assert.Equal(AppSettings.CurrentSettingsVersion, appSettings.SettingsVersion);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public void Load_CurrentVersionFile_DoesNotMigrateAppearance()
    {
        // A current-version file's explicit choices must be respected (no re-migration).
        var tempFile = Path.GetTempFileName();
        File.WriteAllText(tempFile,
            $"{{ \"settingsVersion\": {AppSettings.CurrentSettingsVersion}, \"colorfulAvatars\": false, \"showDeliveryTimestamps\": false }}");

        try
        {
            var appSettings = new AppSettings();
            var svc = new SettingsService(appSettings, new ServerConfiguration(), tempFile);
            svc.Load();

            Assert.False(appSettings.ColorfulAvatars);
            Assert.False(appSettings.ShowDeliveryTimestamps);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public void Load_CorruptFile_DoesNotThrow()
    {
        var tempFile = Path.GetTempFileName();
        File.WriteAllText(tempFile, "not valid json {{{");

        try
        {
            var appSettings = new AppSettings();
            var serverConfig = new ServerConfiguration();
            var svc = new SettingsService(appSettings, serverConfig, tempFile);

            var ex = Record.Exception(() => svc.Load());

            Assert.Null(ex);
            Assert.False(appSettings.FinishedSetup);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }
}
