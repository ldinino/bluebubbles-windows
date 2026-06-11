using System.Collections.ObjectModel;
using System.Text.Json;
using BlueBubbles.Core.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BlueBubbles.Windows.ViewModels;

public partial class SelectedRecipient : ObservableObject
{
    public string Address { get; }
    public string DisplayName { get; }

    [ObservableProperty] public partial bool? IsIMessageAvailable { get; set; }
    [ObservableProperty] public partial bool IsCheckingAvailability { get; set; }

    public SelectedRecipient(string address, string displayName)
    {
        Address = address;
        DisplayName = displayName;
        IsCheckingAvailability = true;
    }
}

public record ContactAddressItem(
    string DisplayName,
    string Address,
    string FormattedAddress,
    bool IsPhone,
    byte[]? Avatar,
    string Initials);

public partial class NewChatViewModel : ObservableObject
{
    private readonly IBlueBubblesApiService _api;
    private readonly IContactResolverService _contacts;
    private readonly IChatsService _chatsService;
    private readonly IAttachmentCacheService _attachmentCache;

    private CancellationTokenSource? _searchCts;

    public ObservableCollection<SelectedRecipient> Recipients { get; } = [];
    public ObservableCollection<ContactAddressItem> SearchResults { get; } = [];
    public ObservableCollection<StagedAttachment> StagedAttachments { get; } = [];

    [ObservableProperty] public partial string SearchQuery { get; set; } = string.Empty;
    [ObservableProperty] public partial bool IsSending { get; set; }
    [ObservableProperty] public partial string? ErrorMessage { get; set; }
    [ObservableProperty] public partial bool HasRecipients { get; set; }
    [ObservableProperty] public partial bool ShowManualAddOption { get; set; }
    [ObservableProperty] public partial string? ManualAddLabel { get; set; }

    public event EventHandler<string>? ChatReady;

    public NewChatViewModel(
        IBlueBubblesApiService api,
        IContactResolverService contacts,
        IChatsService chatsService,
        IAttachmentCacheService attachmentCache)
    {
        _api = api;
        _contacts = contacts;
        _chatsService = chatsService;
        _attachmentCache = attachmentCache;
    }

    public void Reset()
    {
        Recipients.Clear();
        SearchResults.Clear();
        StagedAttachments.Clear();
        SearchQuery = string.Empty;
        IsSending = false;
        ErrorMessage = null;
        HasRecipients = false;
        ShowManualAddOption = false;
        ManualAddLabel = null;
    }

    partial void OnSearchQueryChanged(string value)
    {
        _searchCts?.Cancel();
        _searchCts = new CancellationTokenSource();
        var token = _searchCts.Token;
        _ = DebounceSearchAsync(value, token);
    }

    private async Task DebounceSearchAsync(string query, CancellationToken ct)
    {
        try
        {
            await Task.Delay(200, ct);
        }
        catch (OperationCanceledException) { return; }

        if (ct.IsCancellationRequested) return;
        PerformSearch(query);
    }

    private void PerformSearch(string query)
    {
        SearchResults.Clear();

        if (string.IsNullOrWhiteSpace(query))
        {
            ShowManualAddOption = false;
            ManualAddLabel = null;
            return;
        }

        var trimmed = query.Trim();
        var selectedAddresses = new HashSet<string>(
            Recipients.Select(r => ContactResolverService.NormalizeAddress(r.Address)),
            StringComparer.OrdinalIgnoreCase);

        var results = _contacts.SearchContacts(trimmed);
        foreach (var contact in results)
        {
            foreach (var phone in contact.Phones)
            {
                if (selectedAddresses.Contains(ContactResolverService.NormalizeAddress(phone)))
                    continue;

                SearchResults.Add(new ContactAddressItem(
                    contact.DisplayName,
                    phone,
                    ContactResolverService.FormatAddress(phone),
                    IsPhone: true,
                    contact.Avatar,
                    _contacts.GetInitials(contact.DisplayName)));
            }

            foreach (var email in contact.Emails)
            {
                if (selectedAddresses.Contains(ContactResolverService.NormalizeAddress(email)))
                    continue;

                SearchResults.Add(new ContactAddressItem(
                    contact.DisplayName,
                    email,
                    email,
                    IsPhone: false,
                    contact.Avatar,
                    _contacts.GetInitials(contact.DisplayName)));
            }
        }

        var looksLikeAddress = LooksLikePhoneOrEmail(trimmed);
        if (looksLikeAddress && !selectedAddresses.Contains(ContactResolverService.NormalizeAddress(trimmed)))
        {
            ShowManualAddOption = true;
            ManualAddLabel = $"Message \"{ContactResolverService.FormatAddress(trimmed)}\"";
        }
        else
        {
            ShowManualAddOption = SearchResults.Count == 0 && trimmed.Length >= 3;
            ManualAddLabel = ShowManualAddOption ? $"Message \"{trimmed}\"" : null;
        }
    }

    [RelayCommand]
    private void AddRecipient(ContactAddressItem item)
    {
        var normalized = ContactResolverService.NormalizeAddress(item.Address);
        if (Recipients.Any(r => ContactResolverService.NormalizeAddress(r.Address)
            .Equals(normalized, StringComparison.OrdinalIgnoreCase)))
            return;

        var recipient = new SelectedRecipient(item.Address, item.DisplayName);
        Recipients.Add(recipient);
        HasRecipients = Recipients.Count > 0;

        SearchQuery = string.Empty;
        SearchResults.Clear();
        ShowManualAddOption = false;

        _ = CheckAvailabilityAsync(recipient);
    }

    [RelayCommand]
    private void AddManualRecipient()
    {
        var address = SearchQuery.Trim();
        if (string.IsNullOrWhiteSpace(address)) return;

        var normalized = ContactResolverService.NormalizeAddress(address);
        if (Recipients.Any(r => ContactResolverService.NormalizeAddress(r.Address)
            .Equals(normalized, StringComparison.OrdinalIgnoreCase)))
            return;

        var displayName = _contacts.GetDisplayName(address);
        var recipient = new SelectedRecipient(address, displayName);
        Recipients.Add(recipient);
        HasRecipients = Recipients.Count > 0;

        SearchQuery = string.Empty;
        SearchResults.Clear();
        ShowManualAddOption = false;

        _ = CheckAvailabilityAsync(recipient);
    }

    [RelayCommand]
    private void RemoveRecipient(SelectedRecipient recipient)
    {
        Recipients.Remove(recipient);
        HasRecipients = Recipients.Count > 0;
    }

    private async Task CheckAvailabilityAsync(SelectedRecipient recipient)
    {
        try
        {
            var response = await _api.GetIMessageAvailabilityAsync(recipient.Address);
            if (response.Data.ValueKind == JsonValueKind.True)
                recipient.IsIMessageAvailable = true;
            else if (response.Data.ValueKind == JsonValueKind.False)
                recipient.IsIMessageAvailable = false;
            else if (response.Data.TryGetProperty("available", out var avail))
                recipient.IsIMessageAvailable = avail.GetBoolean();
            else
                recipient.IsIMessageAvailable = null;
        }
        catch
        {
            recipient.IsIMessageAvailable = null;
        }
        finally
        {
            recipient.IsCheckingAvailability = false;
        }
    }

    public void StageAttachment(string filePath)
    {
        StagedAttachments.Add(new StagedAttachment(filePath));
    }

    public void RemoveStagedAttachment(StagedAttachment attachment)
    {
        StagedAttachments.Remove(attachment);
    }

    [RelayCommand]
    private async Task SendAsync(string? messageText)
    {
        if (Recipients.Count == 0) return;
        var hasText = !string.IsNullOrWhiteSpace(messageText);
        var hasAttachments = StagedAttachments.Count > 0;
        if (!hasText && !hasAttachments) return;

        IsSending = true;
        ErrorMessage = null;

        try
        {
            var addresses = Recipients.Select(r => r.Address).ToList();

            // A local participant match can be stale: the row lingers locally after the chat was
            // deleted server-side (FindExistingChatGuid matches by address, not liveness). If the
            // send is rejected, fall through to chat/new below, which creates/returns the
            // canonical chat for these addresses — never silently swallow the failure.
            var existingGuid = _chatsService.FindExistingChatGuid(addresses);
            if (existingGuid is not null)
            {
                if (await TrySendToExistingChatAsync(existingGuid, messageText, hasText))
                {
                    ChatReady?.Invoke(this, existingGuid);
                    return;
                }
                AppLog.Warn(LogCategory.Api,
                    $"Send to existing chat {existingGuid} rejected; retrying via chat/new");
            }

            var service = Recipients.Any(r => r.IsIMessageAvailable == false)
                ? "SMS"
                : "iMessage";

            var response = await _api.CreateChatAsync(
                addresses,
                hasText ? messageText : null,
                service,
                method: "private-api");

            if (response.Data is null)
            {
                ErrorMessage = "Failed to create chat. The server returned no data.";
                return;
            }

            var chatGuid = response.Data.Guid;

            if (hasAttachments)
                await SendAttachmentsAsync(chatGuid);

            await _chatsService.EnsureChatInDatabaseAsync(response.Data, hasText ? messageText : null);
            await _chatsService.LoadChatsAsync();
            ChatReady?.Invoke(this, chatGuid);
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Failed to create chat: {ex.Message}";
        }
        finally
        {
            IsSending = false;
        }
    }

    /// <summary>Sends the draft into an existing chat. Returns false — with nothing delivered —
    /// when the server rejects the first send (typically a chat deleted server-side whose row
    /// lingered locally), so the caller can retry via chat/new without double-sending. On success,
    /// bumps the chat's sort date / resurrects it so the conversation surfaces in the list.</summary>
    private async Task<bool> TrySendToExistingChatAsync(string chatGuid, string? messageText, bool hasText)
    {
        var sentDate = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var anySent = false;
        var sentMimes = new List<string?>();

        if (hasText)
        {
            var tempGuid = $"temp-{Guid.NewGuid():N}";
            var response = await _api.SendTextAsync(chatGuid, tempGuid, messageText!, method: "private-api");
            if (response.Status is < 200 or >= 300) return false;
            sentDate = response.Data?.DateCreated ?? sentDate;
            anySent = true;
        }

        foreach (var attachment in StagedAttachments.ToList())
        {
            var tempGuid = $"temp-{Guid.NewGuid():N}";
            await using var stream = File.OpenRead(attachment.FilePath);
            var response = await _api.SendAttachmentAsync(chatGuid, tempGuid, stream, attachment.FileName,
                method: "private-api");
            if (response.Status is < 200 or >= 300)
            {
                // Nothing delivered yet → safe to retry the whole draft via chat/new. After a
                // partial delivery, retrying would duplicate what already went through — log and
                // let the opened thread show what actually landed.
                if (!anySent) return false;
                AppLog.Warn(LogCategory.Api,
                    $"Attachment '{attachment.FileName}' failed ({response.Status}) after partial send to {chatGuid}");
                continue;
            }
            sentDate = response.Data?.DateCreated ?? sentDate;
            anySent = true;
            sentMimes.AddRange(response.Data?.Attachments?.Select(a => a.MimeType) ?? [null]);
            await SeedAttachmentCacheAsync(response.Data, attachment.FilePath);
        }

        // The socket echo for self-sent REST messages isn't guaranteed; surface the conversation
        // ourselves (bump LatestMessageDate, undo any soft delete, reload the list if needed).
        // Attachment-only drafts get an "Image"/"Video"-style preview instead of a blank tile (B14).
        await _chatsService.HandleNewMessageAsync(
            chatGuid, Core.Utils.MessagePreview.Derive(messageText, sentMimes), sentDate, isFromMe: true);
        return true;
    }

    private async Task SendAttachmentsAsync(string chatGuid)
    {
        foreach (var attachment in StagedAttachments.ToList())
        {
            var tempGuid = $"temp-{Guid.NewGuid():N}";
            await using var stream = File.OpenRead(attachment.FilePath);
            var response = await _api.SendAttachmentAsync(chatGuid, tempGuid, stream, attachment.FileName,
                method: "private-api");
            if (response.Status is < 200 or >= 300)
            {
                AppLog.Warn(LogCategory.Api,
                    $"Attachment '{attachment.FileName}' failed ({response.Status}) for new chat {chatGuid}");
                continue;
            }
            await SeedAttachmentCacheAsync(response.Data, attachment.FilePath);
        }
    }

    /// <summary>Copies a just-sent local attachment into the cache under its server-assigned
    /// guid, so the thread we navigate into renders it without waiting for a delta sync (B13).</summary>
    private async Task SeedAttachmentCacheAsync(Core.Models.Message? serverMessage, string filePath)
    {
        if (serverMessage?.Attachments is not { Count: > 0 } attachments) return;

        foreach (var att in attachments)
        {
            if (att.Guid is null) continue;
            try
            {
                await _attachmentCache.SeedFromLocalFileAsync(att.Guid, filePath);
            }
            catch (Exception ex)
            {
                AppLog.Warn(LogCategory.App,
                    $"Seeding attachment cache for {att.Guid} failed: {ex.Message}");
            }
        }
    }

    private static bool LooksLikePhoneOrEmail(string input)
    {
        if (input.Contains('@') && input.Contains('.'))
            return true;

        var digits = input.Count(char.IsDigit);
        return digits >= 7;
    }
}
