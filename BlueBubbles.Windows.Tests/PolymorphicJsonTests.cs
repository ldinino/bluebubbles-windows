using System.Text.Json;
using BlueBubbles.Core.Models;
using BlueBubbles.Core.Utils;

namespace BlueBubbles.Windows.Tests;

/// <summary>
/// Regression tests for the shape-tolerant converters. The server/Apple sends
/// <c>editedContent</c> / <c>originalTextRange</c> as either a keyed object or a bare array,
/// and <c>payloadData</c> as either a clean object or a raw NSKeyedArchiver array. Before these
/// converters, a single such message threw and poisoned the whole delta-sync batch (or dropped
/// a live new-message). These tests pin the tolerance down.
/// </summary>
public class PolymorphicJsonTests
{
    private static readonly JsonSerializerOptions Options = JsonDefaults.Options;

    // ── editedContent ──

    [Fact]
    public void EditedContent_AsArray_WrapsUnderKeyZero()
    {
        const string json = """
        {
            "editedContent": [
                { "text": { "values": [] }, "date": 1700000000000.0 },
                { "text": { "values": [] }, "date": 1700000001000.0 }
            ]
        }
        """;

        var info = JsonSerializer.Deserialize<MessageSummaryInfo>(json, Options);

        Assert.NotNull(info);
        Assert.NotNull(info!.EditedContent);
        Assert.True(info.EditedContent!.ContainsKey("0"));
        Assert.Equal(2, info.EditedContent["0"].Count);
        Assert.Equal(1700000000000.0, info.EditedContent["0"][0].Date);
    }

    [Fact]
    public void EditedContent_AsKeyedObject_PreservesKeys()
    {
        const string json = """
        {
            "editedContent": {
                "0": [ { "text": { "values": [] }, "date": 1700000000000.0 } ],
                "1": [ { "text": { "values": [] }, "date": 1700000002000.0 } ]
            }
        }
        """;

        var info = JsonSerializer.Deserialize<MessageSummaryInfo>(json, Options);

        Assert.NotNull(info!.EditedContent);
        Assert.Equal(new[] { "0", "1" }, info.EditedContent!.Keys.OrderBy(k => k));
        Assert.Equal(1700000002000.0, info.EditedContent["1"][0].Date);
    }

    [Fact]
    public void EditedContent_Absent_IsNull()
    {
        var info = JsonSerializer.Deserialize<MessageSummaryInfo>("""{ "retractedParts": [0] }""", Options);
        Assert.NotNull(info);
        Assert.Null(info!.EditedContent);
    }

    // ── originalTextRange ──

    [Fact]
    public void OriginalTextRange_AsArray_WrapsUnderKeyZero()
    {
        var info = JsonSerializer.Deserialize<MessageSummaryInfo>(
            """{ "originalTextRange": [0, 12] }""", Options);

        Assert.NotNull(info!.OriginalTextRange);
        Assert.Equal(new[] { 0, 12 }, info.OriginalTextRange!["0"]);
    }

    [Fact]
    public void OriginalTextRange_AsKeyedObject_PreservesKeys()
    {
        var info = JsonSerializer.Deserialize<MessageSummaryInfo>(
            """{ "originalTextRange": { "0": [0, 5], "1": [6, 9] } }""", Options);

        Assert.Equal(new[] { 6, 9 }, info!.OriginalTextRange!["1"]);
    }

    // ── payloadData ──

    [Fact]
    public void PayloadData_AsObject_Parses()
    {
        const string json = """
        {
            "payloadData": {
                "type": 0,
                "urlData": [ { "title": "Example", "siteName": "example.com" } ]
            }
        }
        """;

        var msg = JsonSerializer.Deserialize<Message>(json, Options);

        Assert.NotNull(msg!.PayloadData);
        Assert.Equal(PayloadType.Url, msg.PayloadData!.Type);
        Assert.Equal("Example", msg.PayloadData.UrlData![0].Title);
    }

    [Fact]
    public void PayloadData_AsRawArchiverArray_FailsSoftToNull()
    {
        // Rich-link / iMessage-app messages arrive as a raw NSKeyedArchiver array. We don't
        // decode that form yet — the message must still deserialize with PayloadData == null.
        const string json = """
        {
            "guid": "ABC-123",
            "payloadData": [
                { "$archiver": "NSKeyedArchiver", "objects": ["$null", { "NS.keys": [], "NS.objects": [] }] }
            ]
        }
        """;

        var ex = Record.Exception(() =>
        {
            var msg = JsonSerializer.Deserialize<Message>(json, Options);
            Assert.NotNull(msg);
            Assert.Equal("ABC-123", msg!.Guid);
            Assert.Null(msg.PayloadData);
        });

        Assert.Null(ex);
    }

    [Fact]
    public void PayloadData_Null_IsNull()
    {
        var msg = JsonSerializer.Deserialize<Message>("""{ "guid": "X", "payloadData": null }""", Options);
        Assert.Null(msg!.PayloadData);
    }

    [Fact]
    public void PayloadData_AsObjectWeDoNotModel_FailsSoftToNull()
    {
        // The object form exists but a nested field has an unexpected shape (here urlData is a
        // string, not an array). This is the $.data[N].payloadData crash seen in the wild — it
        // must degrade to null, not abort the whole Message/batch.
        const string json = """
        {
            "guid": "RICH-1",
            "text": "see link",
            "payloadData": { "type": 0, "urlData": "unexpected-string-not-an-array" }
        }
        """;

        var ex = Record.Exception(() =>
        {
            var msg = JsonSerializer.Deserialize<Message>(json, Options);
            Assert.NotNull(msg);
            Assert.Equal("RICH-1", msg!.Guid);
            Assert.Null(msg.PayloadData);
        });

        Assert.Null(ex);
    }

    [Fact]
    public void EditedContent_WithUnmodelledShape_FailsSoftToNull()
    {
        // editedContent present but its element shape is wrong (text is a string, not an object).
        // Degrade to null rather than poisoning the batch.
        var info = JsonSerializer.Deserialize<MessageSummaryInfo>(
            """{ "editedContent": [ { "text": "oops", "date": 1.0 } ] }""", Options);

        Assert.NotNull(info);
        Assert.Null(info!.EditedContent);
    }

    // ── full-message regression: the exact production crash shape ──

    [Fact]
    public void Message_WithArrayEditedContentAndArrayPayloadData_DeserializesWithoutThrowing()
    {
        const string json = """
        {
            "guid": "POISON-1",
            "text": "edited + rich link",
            "messageSummaryInfo": [
                {
                    "editedContent": [ { "text": { "values": [] }, "date": 1700000000000.0 } ],
                    "originalTextRange": [0, 10]
                }
            ],
            "payloadData": [
                { "$archiver": "NSKeyedArchiver", "objects": ["$null"] }
            ]
        }
        """;

        var msg = JsonSerializer.Deserialize<Message>(json, Options);

        Assert.NotNull(msg);
        Assert.Equal("POISON-1", msg!.Guid);
        Assert.Null(msg.PayloadData);
        Assert.NotNull(msg.MessageSummaryInfo);
        Assert.True(msg.MessageSummaryInfo![0].EditedContent!.ContainsKey("0"));
        Assert.Equal(new[] { 0, 10 }, msg.MessageSummaryInfo[0].OriginalTextRange!["0"]);
    }
}
