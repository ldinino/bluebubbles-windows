using BlueBubbles.Core.Services;

namespace BlueBubbles.Windows.Tests;

/// <summary>Covers the pure toast activation routing (F5). The toast *builder* lives in
/// BlueBubbles.Windows and is not reachable from this project (punchlist B2b), so button
/// rendering is human-verified; everything below is the argument plumbing and the
/// foreground/background decision, which are pure and live in Core.</summary>
public class ToastActivationRouterTests
{
    private static Dictionary<string, string> Args(params (string Key, string Value)[] pairs)
        => pairs.ToDictionary(p => p.Key, p => p.Value);

    [Fact]
    public void Resolve_MarkRead_ReturnsChatGuid()
    {
        var result = ToastActivationRouter.Resolve(Args(
            (ToastActivationRouter.ActionKey, ToastActivationRouter.ActionMarkRead),
            (ToastActivationRouter.ChatGuidKey, "iMessage;-;+15551234567")));

        Assert.Equal(ToastActionKind.MarkRead, result.Kind);
        Assert.Equal("iMessage;-;+15551234567", result.ChatGuid);
    }

    /// <summary>The feature's whole point: mark-as-read saves a click, so it must never be
    /// allowed to raise the window. Same for the other inline buttons.</summary>
    [Theory]
    [InlineData(ToastActivationRouter.ActionMarkRead)]
    [InlineData(ToastActivationRouter.ActionReact)]
    [InlineData(ToastActivationRouter.ActionReply)]
    public void Resolve_InlineActions_DoNotActivateWindow(string action)
    {
        var result = ToastActivationRouter.Resolve(
            Args((ToastActivationRouter.ActionKey, action),
                 (ToastActivationRouter.ChatGuidKey, "chat1"),
                 (ToastActivationRouter.MessageGuidKey, "msg1"),
                 (ToastActivationRouter.ReactionKey, "love")),
            Args((ToastActivationRouter.ReplyInputId, "hi")));

        Assert.NotEqual(ToastActionKind.None, result.Kind);
        Assert.False(result.ActivatesWindow);
    }

    [Theory]
    [InlineData(ToastActivationRouter.ActionOpenChat)]
    [InlineData(ToastActivationRouter.ActionOpenApp)]
    public void Resolve_BodyClicks_ActivateWindow(string action)
    {
        var result = ToastActivationRouter.Resolve(Args(
            (ToastActivationRouter.ActionKey, action),
            (ToastActivationRouter.ChatGuidKey, "chat1")));

        Assert.True(result.ActivatesWindow);
    }

    [Fact]
    public void Resolve_MarkRead_WithoutChatGuid_IsNone()
    {
        var result = ToastActivationRouter.Resolve(Args(
            (ToastActivationRouter.ActionKey, ToastActivationRouter.ActionMarkRead)));

        Assert.Equal(ToastActionKind.None, result.Kind);
    }

    /// <summary>markRead must not collide with any other action name, or a toast button would
    /// silently route to the wrong handler.</summary>
    [Fact]
    public void ActionNames_AreDistinct()
    {
        string[] names =
        [
            ToastActivationRouter.ActionMarkRead, ToastActivationRouter.ActionReply,
            ToastActivationRouter.ActionReact, ToastActivationRouter.ActionOpenChat,
            ToastActivationRouter.ActionOpenApp,
        ];

        Assert.Equal(names.Length, names.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void Resolve_Reply_TrimsText()
    {
        var result = ToastActivationRouter.Resolve(
            Args((ToastActivationRouter.ActionKey, ToastActivationRouter.ActionReply),
                 (ToastActivationRouter.ChatGuidKey, "chat1")),
            Args((ToastActivationRouter.ReplyInputId, "  hello  ")));

        Assert.Equal(ToastActionKind.Reply, result.Kind);
        Assert.Equal("hello", result.ReplyText);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Resolve_Reply_BlankText_IsNone(string text)
    {
        var result = ToastActivationRouter.Resolve(
            Args((ToastActivationRouter.ActionKey, ToastActivationRouter.ActionReply),
                 (ToastActivationRouter.ChatGuidKey, "chat1")),
            Args((ToastActivationRouter.ReplyInputId, text)));

        Assert.Equal(ToastActionKind.None, result.Kind);
    }

    [Fact]
    public void Resolve_Reply_WithoutUserInput_IsNone()
    {
        var result = ToastActivationRouter.Resolve(Args(
            (ToastActivationRouter.ActionKey, ToastActivationRouter.ActionReply),
            (ToastActivationRouter.ChatGuidKey, "chat1")));

        Assert.Equal(ToastActionKind.None, result.Kind);
    }

    [Fact]
    public void Resolve_React_CarriesAllArguments()
    {
        var result = ToastActivationRouter.Resolve(Args(
            (ToastActivationRouter.ActionKey, ToastActivationRouter.ActionReact),
            (ToastActivationRouter.ChatGuidKey, "chat1"),
            (ToastActivationRouter.MessageGuidKey, "msg1"),
            (ToastActivationRouter.ReactionKey, "love"),
            (ToastActivationRouter.SelectedTextKey, "hello there")));

        Assert.Equal(ToastActionKind.React, result.Kind);
        Assert.Equal("chat1", result.ChatGuid);
        Assert.Equal("msg1", result.MessageGuid);
        Assert.Equal("love", result.Reaction);
        Assert.Equal("hello there", result.SelectedText);
    }

    [Theory]
    [InlineData(ToastActivationRouter.MessageGuidKey)]
    [InlineData(ToastActivationRouter.ReactionKey)]
    [InlineData(ToastActivationRouter.ChatGuidKey)]
    public void Resolve_React_MissingRequiredArgument_IsNone(string omit)
    {
        var args = Args(
            (ToastActivationRouter.ActionKey, ToastActivationRouter.ActionReact),
            (ToastActivationRouter.ChatGuidKey, "chat1"),
            (ToastActivationRouter.MessageGuidKey, "msg1"),
            (ToastActivationRouter.ReactionKey, "love"));
        args.Remove(omit);

        Assert.Equal(ToastActionKind.None, ToastActivationRouter.Resolve(args).Kind);
    }

    [Fact]
    public void Resolve_React_WithoutSelectedText_UsesEmptyString()
    {
        var result = ToastActivationRouter.Resolve(Args(
            (ToastActivationRouter.ActionKey, ToastActivationRouter.ActionReact),
            (ToastActivationRouter.ChatGuidKey, "chat1"),
            (ToastActivationRouter.MessageGuidKey, "msg1"),
            (ToastActivationRouter.ReactionKey, "love")));

        Assert.Equal(ToastActionKind.React, result.Kind);
        Assert.Equal(string.Empty, result.SelectedText);
    }

    [Fact]
    public void Resolve_UnknownAction_IsNone()
        => Assert.Equal(ToastActionKind.None, ToastActivationRouter.Resolve(Args(
            (ToastActivationRouter.ActionKey, "somethingElse"),
            (ToastActivationRouter.ChatGuidKey, "chat1"))).Kind);

    [Fact]
    public void Resolve_NoActionArgument_IsNone()
        => Assert.Equal(ToastActionKind.None, ToastActivationRouter.Resolve(
            Args((ToastActivationRouter.ChatGuidKey, "chat1"))).Kind);
}
