using System.Collections.Concurrent;
using BlueBubbles.Core.Configuration;
using BlueBubbles.Core.Utils;

namespace BlueBubbles.Core.Services;

public class ContactResolverService : IContactResolverService
{
    private readonly AppSettings _settings;
    // Replaced wholesale by LoadFromVCardAsync (build-then-swap), never mutated in place — so
    // readers always see either the complete old cache or the complete new one, with no
    // half-populated window mid-reload.
    private volatile ConcurrentDictionary<string, string> _nameCache = new(StringComparer.OrdinalIgnoreCase);
    private volatile ConcurrentDictionary<string, byte[]?> _avatarCache = new(StringComparer.OrdinalIgnoreCase);
    // Maps a normalized address to a stable-within-this-load id for the contact card it belongs to, so
    // two addresses on the same card (e.g. an iCloud email and a phone number) can be recognized as the
    // same person — the key for merging "sticky bifurcation" threads. Rebuilt on every load.
    private volatile ConcurrentDictionary<string, string> _contactIdCache = new(StringComparer.OrdinalIgnoreCase);
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

        // Build replacement caches off to the side and swap them in at the end. Mutating the live
        // dictionaries (clear + repopulate) would give concurrent readers a window of missing
        // entries — blank avatars / raw phone numbers on any tile refresh that lands mid-reload,
        // the same flicker class B3 fixed. The old cache stays fully readable until the swap, and
        // doubles as the prior-photo lookup for StablePhoto: handing back the SAME byte[] reference
        // when bytes are unchanged keeps the tile AvatarBytes bindings and the decoded-bitmap cache
        // (both keyed on reference equality) treating an unchanged photo as a no-op (B3).
        var previousAvatars = _avatarCache;
        var newNames = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var newAvatars = new ConcurrentDictionary<string, byte[]?>(StringComparer.OrdinalIgnoreCase);
        var newContactIds = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var searchable = new List<ContactSearchResult>(contacts.Count);
        var contactIndex = 0;

        foreach (var contact in contacts)
        {
            var displayName = contact.FormattedName
                ?? $"{contact.GivenName} {contact.FamilyName}".Trim();

            if (string.IsNullOrWhiteSpace(displayName))
                continue;

            // One id per kept card; shared by every address on the card so two addresses on the same
            // contact (email + phone) collapse to one conversation. First-seen card wins a shared address.
            var contactId = contactIndex.ToString();
            contactIndex++;

            // Resolved once per contact and reused across all its addresses below, so one contact's
            // photo stays a single shared array (one decode) even spanning multiple phones/emails.
            byte[]? photo = contact.Photo;
            var photoResolved = false;

            foreach (var phone in contact.Phones)
            {
                var normalized = NormalizeAddress(phone);
                if (string.IsNullOrEmpty(normalized)) continue;
                newNames.TryAdd(normalized, displayName);
                newContactIds.TryAdd(normalized, contactId);
                if (!photoResolved) { photo = StablePhoto(contact.Photo, previousAvatars.GetValueOrDefault(normalized)); photoResolved = true; }
                newAvatars.TryAdd(normalized, photo);
            }

            foreach (var email in contact.Emails)
            {
                var normalized = NormalizeAddress(email);
                if (string.IsNullOrEmpty(normalized)) continue;
                newNames.TryAdd(normalized, displayName);
                newContactIds.TryAdd(normalized, contactId);
                if (!photoResolved) { photo = StablePhoto(contact.Photo, previousAvatars.GetValueOrDefault(normalized)); photoResolved = true; }
                newAvatars.TryAdd(normalized, photo);
            }

            if (contact.Phones.Count > 0 || contact.Emails.Count > 0)
                searchable.Add(new ContactSearchResult(displayName, contact.Phones, contact.Emails, photo));
        }

        _nameCache = newNames;
        _avatarCache = newAvatars;
        _contactIdCache = newContactIds;
        _allContacts = searchable;
        ContactCount = contacts.Count;
        LoadedFilePath = vcfFilePath;
        ContactsChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Clears all imported contacts (names, avatars, contact ids, search index) and forgets the
    /// loaded file, then raises <see cref="ContactsChanged"/> so the UI falls back to raw addresses and
    /// any merged conversations split apart. Backs the importer's Reset button.</summary>
    public void ClearContacts()
    {
        _nameCache = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        _avatarCache = new ConcurrentDictionary<string, byte[]?>(StringComparer.OrdinalIgnoreCase);
        _contactIdCache = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        _allContacts = [];
        ContactCount = 0;
        LoadedFilePath = null;
        ContactsChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Stable-within-this-load id of the contact card an address belongs to, or null when the
    /// address isn't in any imported contact. Two addresses share an id iff they're on the same card —
    /// the test for merging bifurcated threads.</summary>
    public string? GetContactId(string address)
        => _contactIdCache.TryGetValue(NormalizeAddress(address), out var id) ? id : null;

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

    /// <summary>True when the address resolves to a saved contact name, as opposed to only a raw
    /// (formatted) phone number or email.</summary>
    public bool HasContactName(string address) => _nameCache.ContainsKey(NormalizeAddress(address));

    public string GetAvatarInitials(string address)
        => HasContactName(address) ? GetInitials(GetDisplayName(address)) : string.Empty;

    public string GetChatInitials(IEnumerable<string> participantAddresses, string? chatDisplayName)
    {
        // A custom (group) name wins. Otherwise use the sole 1:1 peer's initials, or an empty
        // string when the peer is an unknown raw address — the avatar then shows a generic person
        // glyph instead of punctuation from a phone number ("+", "(") or a leading digit.
        if (!string.IsNullOrWhiteSpace(chatDisplayName))
            return GetInitials(chatDisplayName);

        var addresses = participantAddresses as IReadOnlyList<string> ?? participantAddresses.ToList();
        // Group avatars are rendered as stacked per-participant circles, not the single avatar, so
        // the single-avatar initials only need to be meaningful for 1:1 chats.
        return addresses.Count == 1 ? GetAvatarInitials(addresses[0]) : string.Empty;
    }

    // Returns the prior array when its bytes match the freshly parsed photo, so an unchanged contact
    // photo keeps a stable reference across reloads (see LoadFromVCardAsync); otherwise the new array.
    private static byte[]? StablePhoto(byte[]? incoming, byte[]? prior)
        => incoming is not null && prior is not null && prior.AsSpan().SequenceEqual(incoming)
            ? prior
            : incoming;

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

    /// <summary>True when an address looks like a phone number rather than an email — the basis for
    /// choosing the phone as a merged conversation's primary identity and ordering "phone / email".</summary>
    public static bool IsPhone(string address)
    {
        var trimmed = address.Trim();
        return !trimmed.Contains('@') && trimmed.Any(char.IsDigit);
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
