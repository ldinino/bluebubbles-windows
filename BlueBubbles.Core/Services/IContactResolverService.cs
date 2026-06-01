namespace BlueBubbles.Core.Services;

public record ContactSearchResult(string DisplayName, List<string> Phones, List<string> Emails, byte[]? Avatar);

public interface IContactResolverService
{
    Task LoadContactsAsync();
    Task LoadFromVCardAsync(string vcfFilePath);
    string GetDisplayName(string address);
    string GetInitials(string displayName);
    byte[]? GetAvatar(string address);
    string GetChatDisplayName(IEnumerable<string> participantAddresses, string? chatDisplayName);
    IReadOnlyList<ContactSearchResult> SearchContacts(string query, int limit = 25);
    int ContactCount { get; }
    string? LoadedFilePath { get; }
    event EventHandler? ContactsChanged;
}
