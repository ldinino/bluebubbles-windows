using System.Collections.Concurrent;
using BlueBubbles.Core.Configuration;
using BlueBubbles.Core.Utils;

namespace BlueBubbles.Core.Services;

public class ContactResolverService : IContactResolverService
{
    private readonly AppSettings _settings;
    private readonly ConcurrentDictionary<string, string> _nameCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte[]?> _avatarCache = new(StringComparer.OrdinalIgnoreCase);
    private volatile List<ContactSearchResult> _allContacts = [];

    public int ContactCount { get; private set; }
    public string? LoadedFilePath { get; private set; }
    public event EventHandler? ContactsChanged;

    public ContactResolverService(AppSettings settings)
    {
        _settings = settings;
    }

    public async Task LoadContactsAsync()
    {
        var path = _settings.VCardFilePath;
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
            return;

        await LoadFromVCardAsync(path);
    }

    public async Task LoadFromVCardAsync(string vcfFilePath)
    {
        var contacts = await VCardParser.ParseFileAsync(vcfFilePath);

        _nameCache.Clear();
        _avatarCache.Clear();
        var searchable = new List<ContactSearchResult>(contacts.Count);

        foreach (var contact in contacts)
        {
            var displayName = contact.FormattedName
                ?? $"{contact.GivenName} {contact.FamilyName}".Trim();

            if (string.IsNullOrWhiteSpace(displayName))
                continue;

            foreach (var phone in contact.Phones)
            {
                var normalized = NormalizeAddress(phone);
                if (string.IsNullOrEmpty(normalized)) continue;
                _nameCache.TryAdd(normalized, displayName);
                _avatarCache.TryAdd(normalized, contact.Photo);
            }

            foreach (var email in contact.Emails)
            {
                var normalized = NormalizeAddress(email);
                if (string.IsNullOrEmpty(normalized)) continue;
                _nameCache.TryAdd(normalized, displayName);
                _avatarCache.TryAdd(normalized, contact.Photo);
            }

            if (contact.Phones.Count > 0 || contact.Emails.Count > 0)
                searchable.Add(new ContactSearchResult(displayName, contact.Phones, contact.Emails, contact.Photo));
        }

        _allContacts = searchable;
        ContactCount = contacts.Count;
        LoadedFilePath = vcfFilePath;
        ContactsChanged?.Invoke(this, EventArgs.Empty);
    }

    public string GetDisplayName(string address)
    {
        var normalized = NormalizeAddress(address);
        return _nameCache.TryGetValue(normalized, out var name) ? name : FormatAddress(address);
    }

    public string GetInitials(string displayName)
    {
        if (string.IsNullOrWhiteSpace(displayName)) return "?";

        var parts = displayName.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length switch
        {
            0 => "?",
            1 => parts[0][..1].ToUpperInvariant(),
            _ => $"{parts[0][..1]}{parts[^1][..1]}".ToUpperInvariant()
        };
    }

    public byte[]? GetAvatar(string address)
    {
        var normalized = NormalizeAddress(address);
        return _avatarCache.TryGetValue(normalized, out var avatar) ? avatar : null;
    }

    public string GetChatDisplayName(IEnumerable<string> participantAddresses, string? chatDisplayName)
    {
        if (!string.IsNullOrWhiteSpace(chatDisplayName))
            return chatDisplayName;

        var names = participantAddresses
            .Select(GetDisplayName)
            .Where(n => !string.IsNullOrEmpty(n))
            .ToList();

        return names.Count switch
        {
            0 => "Unknown",
            1 => names[0],
            2 => $"{names[0]} & {names[1]}",
            _ => $"{names[0]}, {names[1]} & {names.Count - 2} other{(names.Count - 2 > 1 ? "s" : "")}"
        };
    }

    public IReadOnlyList<ContactSearchResult> SearchContacts(string query, int limit = 25)
    {
        if (string.IsNullOrWhiteSpace(query))
            return [];

        var q = query.Trim();
        var results = new List<ContactSearchResult>();

        foreach (var contact in _allContacts)
        {
            if (contact.DisplayName.Contains(q, StringComparison.OrdinalIgnoreCase)
                || contact.Phones.Any(p => p.Contains(q, StringComparison.OrdinalIgnoreCase))
                || contact.Emails.Any(e => e.Contains(q, StringComparison.OrdinalIgnoreCase)))
            {
                results.Add(contact);
                if (results.Count >= limit) break;
            }
        }

        return results;
    }

    public static string NormalizeAddress(string address)
    {
        var trimmed = address.Trim();
        if (trimmed.Contains('@')) return trimmed.ToLowerInvariant();

        var digits = new string(trimmed.Where(char.IsDigit).ToArray());
        return digits.Length >= 10 ? digits[^10..] : trimmed.ToLowerInvariant();
    }

    public static string FormatAddress(string address)
    {
        var trimmed = address.Trim();
        if (trimmed.Contains('@') || trimmed.StartsWith('+'))
            return trimmed;

        var digits = new string(trimmed.Where(char.IsDigit).ToArray());
        if (digits.Length == 10)
            return $"({digits[..3]}) {digits[3..6]}-{digits[6..]}";
        if (digits.Length == 11 && digits[0] == '1')
            return $"({digits[1..4]}) {digits[4..7]}-{digits[7..]}";

        return trimmed;
    }
}
