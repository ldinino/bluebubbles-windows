using System.Collections.Concurrent;
using System.Threading.Channels;
using BlueBubbles.Core.Configuration;
using BlueBubbles.Core.Models;

namespace BlueBubbles.Core.Services;

public class OutgoingMessageService : IOutgoingMessageService
{
    private readonly IBlueBubblesApiService _api;
    private readonly IActionHandler _actionHandler;
    private readonly IAttachmentCacheService _attachmentCache;
    private readonly AppSettings _settings;
    private readonly Channel<OutgoingItem> _queue;
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _delayCancellations = new();

    public event EventHandler<OutgoingMessageEvent>? MessageStateChanged;

    public OutgoingMessageService(
        IBlueBubblesApiService api,
        IActionHandler actionHandler,
        IAttachmentCacheService attachmentCache,
        AppSettings settings)
    {
        _api = api;
        _actionHandler = actionHandler;
        _attachmentCache = attachmentCache;
        _settings = settings;
        _queue = Channel.CreateUnbounded<OutgoingItem>(new UnboundedChannelOptions
        {
            SingleReader = true
        });
        _ = Task.Run(ProcessQueueAsync);
    }

    public string EnqueueText(string chatGuid, string text, string? subject = null,
        string? effectId = null, string? selectedMessageGuid = null,
        int? partIndex = null, bool? ddScan = null)
    {
        var tempGuid = GenerateTempGuid();
        _queue.Writer.TryWrite(new OutgoingItem(tempGuid, chatGuid, OutgoingItemType.Text)
        {
            Text = text,
            Subject = subject,
            EffectId = effectId,
            SelectedMessageGuid = selectedMessageGuid,
            PartIndex = partIndex,
            DdScan = ddScan
        });
        return tempGuid;
    }

    public string EnqueueAttachment(string chatGuid, string filePath,
        string? subject = null, string? effectId = null,
        string? selectedMessageGuid = null, int? partIndex = null,
        bool? isAudioMessage = null)
    {
        var tempGuid = GenerateTempGuid();
        _queue.Writer.TryWrite(new OutgoingItem(tempGuid, chatGuid, OutgoingItemType.Attachment)
        {
            FilePath = filePath,
            Subject = subject,
            EffectId = effectId,
            SelectedMessageGuid = selectedMessageGuid,
            PartIndex = partIndex,
            IsAudioMessage = isAudioMessage
        });
        return tempGuid;
    }

    public string EnqueueMultipart(string chatGuid,
        List<Dictionary<string, object?>> parts,
        string? effectId = null, string? subject = null,
        string? selectedMessageGuid = null, int? partIndex = null,
        bool? ddScan = null)
    {
        var tempGuid = GenerateTempGuid();
        _queue.Writer.TryWrite(new OutgoingItem(tempGuid, chatGuid, OutgoingItemType.Multipart)
        {
            Parts = parts,
            EffectId = effectId,
            Subject = subject,
            SelectedMessageGuid = selectedMessageGuid,
            PartIndex = partIndex,
            DdScan = ddScan
        });
        return tempGuid;
    }

    public Task<ApiResponse<Message>> SendTapbackAsync(string chatGuid,
        string selectedMessageText, string selectedMessageGuid,
        string reaction, int? partIndex = null)
    {
        return _api.SendTapbackAsync(chatGuid, selectedMessageText,
            selectedMessageGuid, reaction, partIndex);
    }

    public Task<ApiResponse<Message>> SendEditAsync(string messageGuid,
        string editedMessage, string backwardsCompatMessage, int partIndex = 0)
    {
        return _api.EditMessageAsync(messageGuid, editedMessage,
            backwardsCompatMessage, partIndex);
    }

    public Task<ApiResponse<Message>> SendUnsendAsync(string messageGuid,
        int partIndex = 0)
    {
        return _api.UnsendMessageAsync(messageGuid, partIndex);
    }

    public void CancelPending(string tempGuid)
    {
        if (_delayCancellations.TryRemove(tempGuid, out var cts))
        {
            cts.Cancel();
            cts.Dispose();
        }
    }

    private async Task ProcessQueueAsync()
    {
        await foreach (var item in _queue.Reader.ReadAllAsync())
        {
            if (_settings.SendDelay > 0)
            {
                var cts = new CancellationTokenSource();
                _delayCancellations[item.TempGuid] = cts;
                try
                {
                    await Task.Delay(_settings.SendDelay * 1000, cts.Token);
                }
                catch (OperationCanceledException)
                {
                    MessageStateChanged?.Invoke(this,
                        new OutgoingMessageEvent(item.TempGuid, item.ChatGuid, OutgoingMessageState.Cancelled));
                    continue;
                }
                finally
                {
                    if (_delayCancellations.TryRemove(item.TempGuid, out var removed))
                        removed.Dispose();
                }
            }

            MessageStateChanged?.Invoke(this,
                new OutgoingMessageEvent(item.TempGuid, item.ChatGuid, OutgoingMessageState.Sending));

            try
            {
                ApiResponse<Message> response;

                switch (item.Type)
                {
                    case OutgoingItemType.Text:
                    {
                        response = await _api.SendTextAsync(
                            item.ChatGuid, item.TempGuid, item.Text!,
                            method: "private-api", subject: item.Subject,
                            effectId: item.EffectId,
                            selectedMessageGuid: item.SelectedMessageGuid,
                            partIndex: item.PartIndex, ddScan: item.DdScan);
                        break;
                    }
                    case OutgoingItemType.Attachment:
                    {
                        using var stream = File.OpenRead(item.FilePath!);
                        response = await _api.SendAttachmentAsync(
                            item.ChatGuid, item.TempGuid, stream,
                            Path.GetFileName(item.FilePath!),
                            method: "private-api", effectId: item.EffectId,
                            subject: item.Subject,
                            selectedMessageGuid: item.SelectedMessageGuid,
                            partIndex: item.PartIndex,
                            isAudioMessage: item.IsAudioMessage);
                        break;
                    }
                    case OutgoingItemType.Multipart:
                    {
                        response = await _api.SendMultipartAsync(
                            item.ChatGuid, item.TempGuid, item.Parts!,
                            effectId: item.EffectId, subject: item.Subject,
                            selectedMessageGuid: item.SelectedMessageGuid,
                            partIndex: item.PartIndex, ddScan: item.DdScan);
                        break;
                    }
                    default:
                        continue;
                }

                if (response.Status == 200 && response.Data is not null)
                {
                    _actionHandler.RemoveOutOfOrderGuid(response.Data.Guid);
                    await SeedAttachmentCacheAsync(item, response.Data);
                    MessageStateChanged?.Invoke(this,
                        new OutgoingMessageEvent(item.TempGuid, item.ChatGuid,
                            OutgoingMessageState.Sent, response.Data));
                }
                else
                {
                    var errorMsg = response.Error?.ErrorMessage ?? "Send failed";
                    MessageStateChanged?.Invoke(this,
                        new OutgoingMessageEvent(item.TempGuid, item.ChatGuid,
                            OutgoingMessageState.Failed, ErrorMessage: errorMsg));
                }
            }
            catch (Exception ex)
            {
                MessageStateChanged?.Invoke(this,
                    new OutgoingMessageEvent(item.TempGuid, item.ChatGuid,
                        OutgoingMessageState.Failed, ErrorMessage: ex.Message));
            }
        }
    }

    /// <summary>Copies a just-sent local attachment into the cache under the server-assigned
    /// attachment guid. Without this, a bubble rebuilt from the DB (after navigating away and
    /// back) looks up the real guid, finds no cache entry, and renders nothing until a delta
    /// sync re-downloads the file we already have (B13).</summary>
    private async Task SeedAttachmentCacheAsync(OutgoingItem item, Message serverMessage)
    {
        if (item.Type != OutgoingItemType.Attachment || item.FilePath is null) return;
        if (serverMessage.Attachments is not { Count: > 0 } attachments) return;

        foreach (var att in attachments)
        {
            if (att.Guid is null) continue;
            try
            {
                await _attachmentCache.SeedFromLocalFileAsync(att.Guid, item.FilePath);
            }
            catch (Exception ex)
            {
                // Best-effort: the attachment stays downloadable from the server.
                AppLog.Warn(LogCategory.App,
                    $"Seeding attachment cache for {att.Guid} failed: {ex.Message}");
            }
        }
    }

    public static string GenerateTempGuid()
    {
        return $"temp-{Guid.NewGuid():N}"[..25];
    }
}

internal record OutgoingItem(string TempGuid, string ChatGuid, OutgoingItemType Type)
{
    public string? Text { get; init; }
    public string? Subject { get; init; }
    public string? FilePath { get; init; }
    public string? EffectId { get; init; }
    public string? SelectedMessageGuid { get; init; }
    public int? PartIndex { get; init; }
    public bool? DdScan { get; init; }
    public bool? IsAudioMessage { get; init; }
    public List<Dictionary<string, object?>>? Parts { get; init; }
}

internal enum OutgoingItemType { Text, Attachment, Multipart }
