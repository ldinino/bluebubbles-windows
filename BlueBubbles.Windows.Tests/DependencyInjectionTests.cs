using BlueBubbles.Core.Configuration;
using BlueBubbles.Core.Models;
using BlueBubbles.Core.Services;
using Microsoft.Extensions.DependencyInjection;
using System.Net;
using System.Net.Http;

namespace BlueBubbles.Windows.Tests;

public class DependencyInjectionTests
{
    private static IServiceProvider BuildTestServices()
    {
        var services = new ServiceCollection();
        services.AddSingleton<AppSettings>();
        services.AddSingleton<ServerConfiguration>();
        services.AddSingleton<IBlueBubblesApiService>(sp =>
            new BlueBubblesApiService(
                new HttpClient(new MockHandler(new HttpResponseMessage(HttpStatusCode.OK))),
                sp.GetRequiredService<ServerConfiguration>(),
                sp.GetRequiredService<AppSettings>()));
        services.AddSingleton<IActionHandler, ActionHandler>();
        services.AddSingleton<ICredentialService, StubCredentialService>();
        services.AddSingleton<ISettingsService, StubSettingsService>();
        services.AddSingleton<IFirebaseService, StubFirebaseService>();
        services.AddSingleton<IServerDiscoveryService, StubServerDiscovery>();
        services.AddSingleton<IContactResolverService, StubContactResolver>();
        services.AddSingleton<IChatsService, StubChatsService>();
        services.AddSingleton<IMessagesService, StubMessagesService>();
        services.AddSingleton<IOutgoingMessageService, StubOutgoingMessageService>();
        services.AddSingleton<ISyncService, StubSyncService>();
        services.AddSingleton<ILocalhostDetectionService>(sp =>
            new LocalhostDetectionService(
                sp.GetRequiredService<IBlueBubblesApiService>(),
                sp.GetRequiredService<AppSettings>()));
        services.AddSingleton<ISocketService, SocketService>();
        services.AddSingleton<IWindowStateService, StubWindowStateService>();
        services.AddSingleton<INotificationService, StubNotificationService>();
        services.AddSingleton<IIncomingMessageProcessor, IncomingMessageProcessor>();
        services.AddSingleton<IAttachmentCacheService, StubAttachmentCacheService>();
        return services.BuildServiceProvider();
    }

    [Fact]
    public void AllServices_ResolveWithoutError()
    {
        var sp = BuildTestServices();

        Assert.NotNull(sp.GetRequiredService<AppSettings>());
        Assert.NotNull(sp.GetRequiredService<ServerConfiguration>());
        Assert.NotNull(sp.GetRequiredService<IBlueBubblesApiService>());
        Assert.NotNull(sp.GetRequiredService<IActionHandler>());
        Assert.NotNull(sp.GetRequiredService<ISocketService>());
        Assert.NotNull(sp.GetRequiredService<ICredentialService>());
        Assert.NotNull(sp.GetRequiredService<ISettingsService>());
        Assert.NotNull(sp.GetRequiredService<IFirebaseService>());
        Assert.NotNull(sp.GetRequiredService<IServerDiscoveryService>());
        Assert.NotNull(sp.GetRequiredService<IContactResolverService>());
        Assert.NotNull(sp.GetRequiredService<IChatsService>());
        Assert.NotNull(sp.GetRequiredService<IMessagesService>());
        Assert.NotNull(sp.GetRequiredService<IOutgoingMessageService>());
        Assert.NotNull(sp.GetRequiredService<ISyncService>());
        Assert.NotNull(sp.GetRequiredService<INotificationService>());
        Assert.NotNull(sp.GetRequiredService<IAttachmentCacheService>());
    }

    [Fact]
    public void AppSettings_Singletons_ReturnSameInstance()
    {
        var sp = BuildTestServices();
        var first = sp.GetRequiredService<AppSettings>();
        var second = sp.GetRequiredService<AppSettings>();
        Assert.Same(first, second);
    }

    [Fact]
    public void AppSettings_DefaultValues_AreCorrect()
    {
        var settings = new AppSettings();

        Assert.False(settings.FinishedSetup);
        Assert.Equal(string.Empty, settings.ServerAddress);
        Assert.Equal(30000, settings.ApiTimeout);
        Assert.True(settings.AutoDownload);
        Assert.True(settings.SendWithReturn);
        Assert.True(settings.ColorfulAvatars);
        Assert.True(settings.ShowDeliveryTimestamps);
        Assert.True(settings.CloseToTray);
        Assert.Equal(1.0, settings.AvatarScale);
    }

    [Fact]
    public void AppSettings_PropertyChanged_Fires()
    {
        var settings = new AppSettings();
        string? changedProperty = null;
        settings.PropertyChanged += (_, e) => changedProperty = e.PropertyName;

        settings.FinishedSetup = true;

        Assert.Equal(nameof(AppSettings.FinishedSetup), changedProperty);
    }

    [Fact]
    public void ServerConfiguration_HasValidFcmData_ReturnsFalse_WhenEmpty()
    {
        var config = new ServerConfiguration();
        Assert.False(config.HasValidFcmData);
    }

    [Fact]
    public void ServerConfiguration_HasValidFcmData_ReturnsTrue_WhenPopulated()
    {
        var config = new ServerConfiguration
        {
            FcmProjectId = "test-project",
            FcmApiKey = "test-key",
            FcmApplicationId = "test-app-id"
        };
        Assert.True(config.HasValidFcmData);
    }

    // Stubs
    private class StubCredentialService : ICredentialService
    {
        public void SavePassword(string password) { }
        public string? GetPassword() => null;
        public void DeletePassword() { }
    }
    private class StubSettingsService : ISettingsService
    {
        public void Load() { }
        public void Save() { }
    }
    private class StubSyncService : ISyncService
    {
        public bool IsSyncing => false;
        public event EventHandler<bool>? SyncStateChanged;
        public Task RunFullSyncAsync(bool skipEmptyChats = true, IProgress<BlueBubbles.Core.Models.SyncProgress>? progress = null,
            CancellationToken ct = default) => Task.CompletedTask;
        public Task RunIncrementalSyncAsync(CancellationToken ct = default) => Task.CompletedTask;
    }
    private class StubFirebaseService : IFirebaseService
    {
        public Task FetchAndStoreConfigAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<string?> FetchNewServerUrlAsync(CancellationToken ct = default)
            => Task.FromResult<string?>(null);
    }
    private class StubServerDiscovery : IServerDiscoveryService
    {
        public string BuildGoogleOAuthUrl() => "https://example.com";
        public Task<List<BlueBubbles.Core.Models.DiscoveredServer>> DiscoverServersAsync(
            string accessToken, CancellationToken ct = default)
            => Task.FromResult(new List<BlueBubbles.Core.Models.DiscoveredServer>());
    }
    private class StubContactResolver : IContactResolverService
    {
        public Task LoadContactsAsync() => Task.CompletedTask;
        public Task LoadFromVCardAsync(string vcfFilePath) => Task.CompletedTask;
        public string GetDisplayName(string address) => address;
        public string GetInitials(string displayName) => "?";
        public byte[]? GetAvatar(string address) => null;
        public string GetChatDisplayName(IEnumerable<string> participantAddresses, string? chatDisplayName)
            => chatDisplayName ?? "Chat";
        public int ContactCount => 0;
        public string? LoadedFilePath => null;
        public IReadOnlyList<ContactSearchResult> SearchContacts(string query, int limit = 25) => [];
        public event EventHandler? ContactsChanged;
    }
    private class StubChatsService : IChatsService
    {
        public IReadOnlyList<ChatWithParticipants> Chats => [];
        public IReadOnlyList<ChatWithParticipants> ArchivedChats => [];
        public event EventHandler? ChatsChanged;
        public event EventHandler<string>? ChatUpdated;
        public event EventHandler? ArchivedChatsChanged;
        public Task LoadChatsAsync() => Task.CompletedTask;
        public Task LoadArchivedChatsAsync() => Task.CompletedTask;
        public Task HandleNewMessageAsync(string chatGuid, string? messageText, long dateCreated, bool isFromMe, string? senderAddress = null)
            => Task.CompletedTask;
        public Task MarkChatReadAsync(string chatGuid, bool read, bool notifyServer = true) => Task.CompletedTask;
        public Task TogglePinAsync(string chatGuid) => Task.CompletedTask;
        public Task ReorderPinsAsync(List<string> chatGuids) => Task.CompletedTask;
        public Task ArchiveChatAsync(string chatGuid) => Task.CompletedTask;
        public Task UnarchiveChatAsync(string chatGuid) => Task.CompletedTask;
        public Task DeleteChatAsync(string chatGuid) => Task.CompletedTask;
        public Task<bool> RenameChatAsync(string chatGuid, string newName) => Task.FromResult(true);
        public Task ToggleMuteAsync(string chatGuid) => Task.CompletedTask;
        public Task<bool> AddParticipantAsync(string chatGuid, string address) => Task.FromResult(true);
        public Task<bool> RemoveParticipantAsync(string chatGuid, string address) => Task.FromResult(true);
        public Task<bool> LeaveChatAsync(string chatGuid) => Task.FromResult(true);
        public Task<bool> SetChatIconAsync(string chatGuid, Stream iconStream, string fileName) => Task.FromResult(true);
        public Task<bool> DeleteChatIconAsync(string chatGuid) => Task.FromResult(true);
        public string? FindExistingChatGuid(IEnumerable<string> addresses) => null;
        public Task EnsureChatInDatabaseAsync(Chat chat, string? messageText) => Task.CompletedTask;
    }
    private class StubMessagesService : IMessagesService
    {
        public Task<List<BlueBubbles.Core.Data.Entities.MessageEntity>> LoadMessagesAsync(
            int chatId, int limit = 50, long? beforeDate = null)
            => Task.FromResult(new List<BlueBubbles.Core.Data.Entities.MessageEntity>());
        public Task<List<BlueBubbles.Core.Data.Entities.MessageEntity>> FetchOlderMessagesFromServerAsync(
            int chatId, string chatGuid, int limit = 25, CancellationToken ct = default)
            => Task.FromResult(new List<BlueBubbles.Core.Data.Entities.MessageEntity>());
        public Task SaveIncomingMessageAsync(string chatGuid, BlueBubbles.Core.Models.Message message)
            => Task.CompletedTask;
        public Task UpdateMessageAsync(BlueBubbles.Core.Models.Message message)
            => Task.CompletedTask;
        public Task SoftDeleteMessageAsync(string messageGuid)
            => Task.CompletedTask;
        public Task<List<BlueBubbles.Core.Data.Entities.AttachmentEntity>> LoadMediaAttachmentsAsync(
            int chatId, int limit = 50, int offset = 0)
            => Task.FromResult(new List<BlueBubbles.Core.Data.Entities.AttachmentEntity>());
        public Task<List<BlueBubbles.Core.Data.Entities.MessageEntity>> LoadReactionsAsync(
            IReadOnlyCollection<string> parentGuids)
            => Task.FromResult(new List<BlueBubbles.Core.Data.Entities.MessageEntity>());
        public Task SaveReactionAsync(string chatGuid, BlueBubbles.Core.Models.Message reaction)
            => Task.CompletedTask;
        public Task<List<BlueBubbles.Core.Data.Entities.MessageEntity>> GetMessagesByGuidsAsync(
            IReadOnlyCollection<string> guids)
            => Task.FromResult(new List<BlueBubbles.Core.Data.Entities.MessageEntity>());
    }
    private class StubOutgoingMessageService : IOutgoingMessageService
    {
        public event EventHandler<OutgoingMessageEvent>? MessageStateChanged;
        public string EnqueueText(string chatGuid, string text, string? subject = null,
            string? effectId = null, string? selectedMessageGuid = null,
            int? partIndex = null, bool? ddScan = null) => "temp-stub";
        public string EnqueueAttachment(string chatGuid, string filePath,
            string? subject = null, string? effectId = null,
            string? selectedMessageGuid = null, int? partIndex = null,
            bool? isAudioMessage = null) => "temp-stub";
        public string EnqueueMultipart(string chatGuid,
            List<Dictionary<string, object?>> parts,
            string? effectId = null, string? subject = null,
            string? selectedMessageGuid = null, int? partIndex = null,
            bool? ddScan = null) => "temp-stub";
        public Task<ApiResponse<Message>> SendTapbackAsync(string chatGuid,
            string selectedMessageText, string selectedMessageGuid,
            string reaction, int? partIndex = null) => throw new NotImplementedException();
        public Task<ApiResponse<Message>> SendEditAsync(string messageGuid,
            string editedMessage, string backwardsCompatMessage,
            int partIndex = 0) => throw new NotImplementedException();
        public Task<ApiResponse<Message>> SendUnsendAsync(string messageGuid,
            int partIndex = 0) => throw new NotImplementedException();
        public void CancelPending(string tempGuid) { }
    }
    private class StubWindowStateService : IWindowStateService
    {
        public bool IsWindowFocused => false;
        public string? ActiveChatGuid => null;
        public void SetActiveChatGuid(string? chatGuid) { }
    }
    private class StubNotificationService : INotificationService
    {
        public void HandleNewMessage(NewMessageNotification notification) { }
        public void ClearNotificationsForChat(string chatGuid) { }
        public void ClearAllNotifications() { }
    }
    private class StubAttachmentCacheService : IAttachmentCacheService
    {
        public bool IsCached(string attachmentGuid) => false;
        public string? GetCachedPath(string attachmentGuid) => null;
        public Task<string> DownloadAsync(string attachmentGuid, string? transferName,
            IProgress<double>? progress = null, CancellationToken ct = default)
            => Task.FromResult(string.Empty);
        public Task PurgeCacheAsync(CancellationToken ct = default) => Task.CompletedTask;
        public long GetCacheSizeBytes() => 0;
    }
}
