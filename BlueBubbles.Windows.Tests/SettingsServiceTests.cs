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
            // The password belongs in the DPAPI credential store, never on disk in cleartext.
            Assert.DoesNotContain("test-pass", File.ReadAllText(filePath));

            var appSettings2 = new AppSettings();
            var serverConfig2 = new ServerConfiguration();
            var svc2 = new SettingsService(appSettings2, serverConfig2, filePath);
            svc2.Load();

            Assert.True(appSettings2.FinishedSetup);
            Assert.Equal("http://10.0.0.1:1234", appSettings2.ServerAddress);
            Assert.Equal("http://10.0.0.1:1234", serverConfig2.ServerUrl);
            Assert.Equal(string.Empty, serverConfig2.Password);
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
                ColorfulAvatars = false,
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
            // ColorfulAvatars defaults to true, so a round-tripped false proves the value travelled.
            Assert.False(appSettings2.ColorfulAvatars);
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
    public void SaveAndLoad_RoundTripsNotificationSettings()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "bb_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var filePath = Path.Combine(tempDir, "settings.json");

        try
        {
            var appSettings = new AppSettings
            {
                NotifyOnChatList = true,
                NotifyReactions = false, // flip the on-by-default to prove it persists, not defaults
                NotificationSound = "custom",
                NotificationSoundCustomPath = @"D:\sounds\my alert.mp3",
                FilterUnknownSenders = true
            };

            var svc = new SettingsService(appSettings, new ServerConfiguration(), filePath);
            svc.Save();

            var appSettings2 = new AppSettings();
            var svc2 = new SettingsService(appSettings2, new ServerConfiguration(), filePath);
            svc2.Load();

            Assert.True(appSettings2.NotifyOnChatList);
            Assert.False(appSettings2.NotifyReactions);
            Assert.Equal("custom", appSettings2.NotificationSound);
            Assert.Equal(@"D:\sounds\my alert.mp3", appSettings2.NotificationSoundCustomPath);
            Assert.True(appSettings2.FilterUnknownSenders);
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
            Assert.True(appSettings.ColorfulAvatars);
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
    public void Load_FileWithRemovedSettingKeys_StillAppliesSurvivingSettings()
    {
        // Upgrade path: settings.json written by <= 0.22.x still carries colorfulBubbles /
        // denseChatTiles / hideDividers / avatarScale / statusIndicatorsOnChats / scrollToLastUnread.
        // JsonOpts leaves UnmappedMemberHandling at the Skip default, so those keys must be ignored
        // without failing the whole load.
        var tempFile = Path.GetTempFileName();
        File.WriteAllText(tempFile, """
            {
              "finishedSetup": true,
              "settingsVersion": 1,
              "theme": 2,
              "colorfulAvatars": false,
              "use24HrFormat": true,
              "showDeliveryTimestamps": true,
              "sendDelay": 3,
              "colorfulBubbles": true,
              "denseChatTiles": true,
              "hideDividers": true,
              "avatarScale": 1.5,
              "statusIndicatorsOnChats": true,
              "scrollToLastUnread": true
            }
            """);

        try
        {
            var appSettings = new AppSettings();
            var svc = new SettingsService(appSettings, new ServerConfiguration(), tempFile);
            svc.Load();

            Assert.True(appSettings.FinishedSetup);
            Assert.Equal(2, appSettings.Theme);
            Assert.False(appSettings.ColorfulAvatars);
            Assert.True(appSettings.Use24HrFormat);
            Assert.True(appSettings.ShowDeliveryTimestamps);
            Assert.Equal(3, appSettings.SendDelay);
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

    [Fact]
    public void Load_LegacyPlaintextPassword_MovesItToCredentialStoreAndStripsItFromDisk()
    {
        var tempFile = Path.GetTempFileName();
        File.WriteAllText(tempFile,
            "{ \"finishedSetup\": true, \"serverUrl\": \"http://10.0.0.1:1234\", \"password\": \"legacy-pass\" }");

        try
        {
            var serverConfig = new ServerConfiguration();
            var credentials = new FakeCredentialService();
            var svc = new SettingsService(new AppSettings(), serverConfig, tempFile, credentials);

            svc.Load();

            // The connection still works this launch...
            Assert.Equal("legacy-pass", serverConfig.Password);
            // ...because the password moved into the encrypted store...
            Assert.Equal("legacy-pass", credentials.Stored);
            // ...and the field is gone from disk entirely, not just blanked.
            Assert.DoesNotContain("\"password\"", File.ReadAllText(tempFile));
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public void Load_LegacyPlaintextPassword_DoesNotOverwriteExistingCredential()
    {
        // A stale cleartext copy must not clobber the credential store, which is authoritative.
        var tempFile = Path.GetTempFileName();
        File.WriteAllText(tempFile, "{ \"password\": \"stale-pass\" }");

        try
        {
            var credentials = new FakeCredentialService { Stored = "current-pass" };
            var svc = new SettingsService(new AppSettings(), new ServerConfiguration(), tempFile, credentials);

            svc.Load();

            Assert.Equal("current-pass", credentials.Stored);
            Assert.DoesNotContain("stale-pass", File.ReadAllText(tempFile));
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    private sealed class FakeCredentialService : ICredentialService
    {
        public string? Stored { get; set; }

        public void SavePassword(string password) => Stored = password;
        public string? GetPassword() => Stored;
        public void DeletePassword() => Stored = null;
    }
}
