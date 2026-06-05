namespace BlueBubbles.Core.Services;

public record ContactSearchResult(string DisplayName, List<string> Phones, List<string> Emails, byte[]? Avatar);

public interface IContactResolverService
{
    Task LoadContactsAsync();
    Task LoadFromVCardAsync(string vcfFilePath);
    string GetDisplayName(string address);
    string GetInitials(string displayName);
    bool HasContactName(string address);
    /// <summary>Avatar initials for a single address: the contact's initials, or an empty string
    /// when the address has no saved contact name (so the avatar falls back to a person glyph).</summary>
    string GetAvatarInitials(string address);
    /// <summary>Initials for a chat's single (1:1) avatar, derived from the custom chat name or the
    /// sole participant. Empty when only a raw address is known, signalling a generic glyph.</summary>
    string GetChatInitials(IEnumerable<string> participantAddresses, string? chatDisplayName);
    byte[]? GetAvatar(string address);
    string GetChatDisplayName(IEnumerable<string> participantAddresses, string? chatDisplayName);
    IReadOnlyList<ContactSearchResult> SearchContacts(string query, int limit = 25);
    int ContactCount { get; }
    string? LoadedFilePath { get; }
    event EventHandler? ContactsChanged;
}
