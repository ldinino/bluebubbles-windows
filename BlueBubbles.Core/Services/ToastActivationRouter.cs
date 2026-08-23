namespace BlueBubbles.Core.Services;

/// <summary>The action a toast interaction resolved to.</summary>
public enum ToastActionKind
{
    /// <summary>Unrecognised, or the arguments the action needs were missing/blank.</summary>
    None,
    Reply,
    React,
    MarkRead,
    OpenChat,
    OpenApp,
}

/// <summary>A validated toast interaction: the action plus the arguments it needs.</summary>
public sealed record ToastActivation(
    ToastActionKind Kind,
    string ChatGuid = "",
    string MessageGuid = "",
    string Reaction = "",
    string SelectedText = "",
    string ReplyText = "")
{
    public static readonly ToastActivation None = new(ToastActionKind.None);

    /// <summary>Whether handling this action is allowed to bring the window to the foreground.
    /// Only the body click and the summary toast may; the inline buttons (reply, tapback,
    /// mark as read) exist precisely to avoid it.</summary>
    public bool ActivatesWindow => Kind is ToastActionKind.OpenChat or ToastActionKind.OpenApp;
}

/// <summary>
/// Pure argument plumbing for toast activations, split out of the platform handler in
/// <c>App.xaml.cs</c> so the routing and its validation are unit-testable without WinRT.
/// </summary>
public static class ToastActivationRouter
{
    // Activation-argument keys and action names, shared with the toast builder.
    public const string ActionKey = "action";
    public const string ChatGuidKey = "chatGuid";
    public const string MessageGuidKey = "messageGuid";
    public const string SelectedTextKey = "selectedText";
    public const string ReactionKey = "reaction";
    public const string ReplyInputId = "replyText";

    public const string ActionOpenChat = "openChat";
    public const string ActionOpenApp = "openApp";
    public const string ActionReply = "reply";
    public const string ActionReact = "react";
    public const string ActionMarkRead = "markRead";

    /// <summary>Resolves a toast activation's raw arguments and user input into a validated action.
    /// Anything unrecognised or missing a required argument resolves to
    /// <see cref="ToastActionKind.None"/> so the caller has a single "do nothing" branch.</summary>
    public static ToastActivation Resolve(
        IDictionary<string, string> args, IDictionary<string, string>? userInput = null)
    {
        if (!args.TryGetValue(ActionKey, out var action)) return ToastActivation.None;

        var chatGuid = Get(args, ChatGuidKey);

        switch (action)
        {
            case ActionReply:
                var text = userInput is not null ? Get(userInput, ReplyInputId) : string.Empty;
                if (chatGuid.Length == 0 || string.IsNullOrWhiteSpace(text)) return ToastActivation.None;
                return new ToastActivation(ToastActionKind.Reply, chatGuid, ReplyText: text.Trim());

            case ActionReact:
                var messageGuid = Get(args, MessageGuidKey);
                var reaction = Get(args, ReactionKey);
                if (chatGuid.Length == 0 || messageGuid.Length == 0 || reaction.Length == 0)
                    return ToastActivation.None;
                return new ToastActivation(
                    ToastActionKind.React, chatGuid, messageGuid, reaction, Get(args, SelectedTextKey));

            case ActionMarkRead:
                return chatGuid.Length == 0
                    ? ToastActivation.None
                    : new ToastActivation(ToastActionKind.MarkRead, chatGuid);

            case ActionOpenChat:
                return chatGuid.Length == 0
                    ? ToastActivation.None
                    : new ToastActivation(ToastActionKind.OpenChat, chatGuid);

            case ActionOpenApp:
                return new ToastActivation(ToastActionKind.OpenApp);

            default:
                return ToastActivation.None;
        }
    }

    private static string Get(IDictionary<string, string> map, string key)
        => map.TryGetValue(key, out var value) && value is not null ? value : string.Empty;
}
