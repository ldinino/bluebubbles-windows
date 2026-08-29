using System.Text.Json;
using BlueBubbles.Core.Data.Entities;
using BlueBubbles.Core.Export;
using BlueBubbles.Core.Services;

namespace BlueBubbles.Windows.Tests;

public class ChatExportServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "bb-export-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
        GC.SuppressFinalize(this);
    }

    private sealed class FakeAttachmentCache : ICachedAttachmentLookup
    {
        private readonly Dictionary<string, string> _paths = new(StringComparer.Ordinal);
        public void Add(string guid, string path) => _paths[guid] = path;
        public bool IsCached(string guid) => _paths.ContainsKey(guid);
        public string? GetCachedPath(string guid) => _paths.GetValueOrDefault(guid);
    }

    private const long T0 = 1767322645678;

    private async Task<(TestDbContextFactory Factory, int ChatId)> SeedAsync(
        long? watermark, params MessageEntity[] messages)
    {
        var factory = TestDbContextFactory.Create();
        await using var db = factory.CreateDbContext();

        var handle = new HandleEntity { Address = "+15550001111", Service = "iMessage" };
        var chat = new ChatEntity
        {
            Guid = "iMessage;+;chat999",
            DisplayName = "Beach Trip",
            OldestSyncedMessageDate = watermark,
        };
        db.Handles.Add(handle);
        db.Chats.Add(chat);
        await db.SaveChangesAsync();

        db.ChatParticipants.Add(new ChatParticipant { ChatId = chat.Id, HandleId = handle.Id });
        foreach (var m in messages)
        {
            m.ChatId = chat.Id;
            if (!m.IsFromMe) m.HandleId = handle.Id;
            db.Messages.Add(m);
        }
        await db.SaveChangesAsync();

        return (factory, chat.Id);
    }

    [Fact]
    public async Task Export_WritesJsonlTranscriptAndManifest()
    {
        var (factory, chatId) = await SeedAsync(0,
            new MessageEntity { Guid = "m1", Text = "hello", DateCreated = T0, OriginalRowId = 1 },
            new MessageEntity
            {
                Guid = "t1", Text = "Liked \u201Chello\u201D", IsFromMe = true, DateCreated = T0 + 5,
                OriginalRowId = 2, AssociatedMessageGuid = "m1", AssociatedMessageType = "love",
            });

        var svc = new ChatExportService(factory, new FakeAttachmentCache());
        var result = await svc.ExportAsync([chatId], _dir, new ChatExportOptions());

        var baseName = ExportFileNames.ForChat("iMessage;+;chat999", "Beach Trip");
        var jsonl = await File.ReadAllLinesAsync(Path.Combine(_dir, $"{baseName}.jsonl"));
        var transcript = await File.ReadAllTextAsync(Path.Combine(_dir, $"{baseName}.txt"));
        var manifest = await File.ReadAllTextAsync(Path.Combine(_dir, "manifest.json"));

        Assert.Equal(1, result.ChatCount);
        Assert.Equal(1, result.MessageCount);          // the tapback is folded, not a record
        Assert.Equal(0, result.IncompleteChatCount);   // watermark 0 = reaches the beginning

        Assert.Equal(2, jsonl.Length);                 // header + one message
        Assert.Contains("\"type\":\"header\"", jsonl[0]);
        Assert.DoesNotContain("Liked", jsonl[1]);

        Assert.Contains("(Loved by Me)", transcript);
        Assert.Contains("COVERAGE", transcript);
        Assert.Contains("\"chatCount\": 1", manifest);

        // Manifest is valid JSON, not just a string that looks like it.
        using var doc = JsonDocument.Parse(manifest);
        Assert.Equal(1, doc.RootElement.GetProperty("chats").GetArrayLength());
    }

    [Fact]
    public async Task Export_CopiesCachedAttachmentAndFlagsUncachedOne()
    {
        var source = Path.Combine(Path.GetTempPath(), $"bb-src-{Guid.NewGuid():N}.jpg");
        await File.WriteAllTextAsync(source, "not-a-real-jpeg");

        try
        {
            var msg = new MessageEntity
            {
                Guid = "m1", Text = null, DateCreated = T0, OriginalRowId = 1,
                Attachments =
                [
                    new AttachmentEntity { Guid = "att-cached", TransferName = "beach.jpg", MimeType = "image/jpeg", TotalBytes = 15, OriginalRowId = 1 },
                    new AttachmentEntity { Guid = "att-missing", TransferName = "clip.mov", MimeType = "video/quicktime", TotalBytes = 99, OriginalRowId = 2 },
                ],
            };

            var (factory, chatId) = await SeedAsync(0, msg);
            var cache = new FakeAttachmentCache();
            cache.Add("att-cached", source);

            var svc = new ChatExportService(factory, cache);
            var result = await svc.ExportAsync([chatId], _dir, new ChatExportOptions());

            Assert.Equal(1, result.AttachmentsCopied);
            Assert.Equal(1, result.AttachmentsMissing);

            var copiedName = $"{ExportFileNames.ShortHash("att-cached")}-beach.jpg";
            Assert.True(File.Exists(Path.Combine(_dir, "attachments", copiedName)));

            var baseName = ExportFileNames.ForChat("iMessage;+;chat999", "Beach Trip");
            var transcript = await File.ReadAllTextAsync(Path.Combine(_dir, $"{baseName}.txt"));

            Assert.Contains($"[Attachment: beach.jpg, image/jpeg, 15 bytes -> attachments/{copiedName}]", transcript);
            Assert.Contains("[Attachment NOT INCLUDED: clip.mov", transcript);
        }
        finally
        {
            File.Delete(source);
        }
    }

    [Fact]
    public async Task Export_NeverContactsTheServer()
    {
        // The service takes no API dependency at all, so a mid-export fetch is impossible by
        // construction rather than by discipline.
        var ctor = Assert.Single(typeof(ChatExportService).GetConstructors());
        Assert.DoesNotContain(ctor.GetParameters(),
            p => p.ParameterType == typeof(IBlueBubblesApiService));
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Export_ReportsProgressPerChatAndHonoursCancellation()
    {
        var (factory, chatId) = await SeedAsync(null,
            new MessageEntity { Guid = "m1", Text = "hi", DateCreated = T0, OriginalRowId = 1 });

        var svc = new ChatExportService(factory, new FakeAttachmentCache());

        var seen = new List<ChatExportProgress>();
        var result = await svc.ExportAsync([chatId], _dir, new ChatExportOptions(),
            new Progress<ChatExportProgress>(p => seen.Add(p)));

        Assert.Equal(1, result.IncompleteChatCount);   // NULL watermark is not completeness

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => svc.ExportAsync([chatId], _dir, new ChatExportOptions(), null, cts.Token));
    }

    [Fact]
    public async Task Export_IsDeterministic_ReExportProducesIdenticalFiles()
    {
        var (factory, chatId) = await SeedAsync(0,
            new MessageEntity { Guid = "m1", Text = "hello", DateCreated = T0, OriginalRowId = 1 });

        var svc = new ChatExportService(factory, new FakeAttachmentCache());
        var baseName = ExportFileNames.ForChat("iMessage;+;chat999", "Beach Trip");

        await svc.ExportAsync([chatId], _dir, new ChatExportOptions());
        var first = await File.ReadAllTextAsync(Path.Combine(_dir, $"{baseName}.jsonl"));

        await svc.ExportAsync([chatId], _dir, new ChatExportOptions());
        var second = await File.ReadAllTextAsync(Path.Combine(_dir, $"{baseName}.jsonl"));

        Assert.Equal(first, second);
        Assert.Single(Directory.GetFiles(_dir, "*.jsonl"));
    }
}
