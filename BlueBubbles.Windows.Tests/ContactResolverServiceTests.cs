using BlueBubbles.Core.Configuration;
using BlueBubbles.Core.Services;
using BlueBubbles.Core.Utils;

namespace BlueBubbles.Windows.Tests;

public class ContactResolverServiceTests : IDisposable
{
    private readonly string _tempDir;
    private readonly AppSettings _settings;

    public ContactResolverServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "bb_test_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
        _settings = new AppSettings();
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); }
        catch { }
    }

    private string WriteVcf(string content)
    {
        var path = Path.Combine(_tempDir, "test.vcf");
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public async Task LoadFromVCard_ParsesMultipleContacts()
    {
        var path = WriteVcf("""
            BEGIN:VCARD
            VERSION:3.0
            FN:John Doe
            TEL:+15551234567
            EMAIL:john@example.com
            END:VCARD
            BEGIN:VCARD
            VERSION:3.0
            FN:Jane Smith
            TEL:(555) 987-6543
            EMAIL:jane@example.com
            END:VCARD
            """);

        var svc = new ContactResolverService(_settings);
        await svc.LoadFromVCardAsync(path);

        Assert.Equal(2, svc.ContactCount);
    }

    [Fact]
    public async Task GetDisplayName_ReturnsContactName_WhenPhoneMatched()
    {
        var path = WriteVcf("""
            BEGIN:VCARD
            VERSION:3.0
            FN:John Doe
            TEL;TYPE=CELL:+1 (555) 123-4567
            END:VCARD
            """);

        var svc = new ContactResolverService(_settings);
        await svc.LoadFromVCardAsync(path);

        Assert.Equal("John Doe", svc.GetDisplayName("+15551234567"));
        Assert.Equal("John Doe", svc.GetDisplayName("5551234567"));
        Assert.Equal("John Doe", svc.GetDisplayName("(555) 123-4567"));
    }

    [Fact]
    public async Task GetDisplayName_ReturnsFormattedNumber_WhenNotMatched()
    {
        var svc = new ContactResolverService(_settings);
        Assert.Equal("(555) 987-6543", svc.GetDisplayName("5559876543"));
    }

    [Fact]
    public async Task GetDisplayName_MatchesEmail_CaseInsensitive()
    {
        var path = WriteVcf("""
            BEGIN:VCARD
            VERSION:3.0
            FN:Jane Smith
            EMAIL:jane@example.com
            END:VCARD
            """);

        var svc = new ContactResolverService(_settings);
        await svc.LoadFromVCardAsync(path);

        Assert.Equal("Jane Smith", svc.GetDisplayName("JANE@EXAMPLE.COM"));
    }

    [Theory]
    [InlineData("John Doe", "JD")]
    [InlineData("Alice", "A")]
    [InlineData("", "?")]
    [InlineData("Mary Jane Watson", "MW")]
    public void GetInitials_ReturnsCorrectInitials(string displayName, string expected)
    {
        var svc = new ContactResolverService(_settings);
        Assert.Equal(expected, svc.GetInitials(displayName));
    }

    [Fact]
    public async Task GetContactId_SameCard_SharesId_AcrossPhoneAndEmail()
    {
        // The "sticky bifurcation" case: one card carrying both a phone and an iCloud email.
        var path = WriteVcf("""
            BEGIN:VCARD
            VERSION:3.0
            FN:Alex Rivera
            TEL:+15550001234
            EMAIL:alex.rivera@example.com
            END:VCARD
            """);

        var svc = new ContactResolverService(_settings);
        await svc.LoadFromVCardAsync(path);

        var phoneId = svc.GetContactId("5550001234");
        var emailId = svc.GetContactId("ALEX.RIVERA@EXAMPLE.COM");

        Assert.NotNull(phoneId);
        Assert.Equal(phoneId, emailId);
    }

    [Fact]
    public async Task GetContactId_DifferentCards_DifferAndUnknownIsNull()
    {
        var path = WriteVcf("""
            BEGIN:VCARD
            VERSION:3.0
            FN:John Doe
            TEL:+15551234567
            END:VCARD
            BEGIN:VCARD
            VERSION:3.0
            FN:Jane Smith
            TEL:+15559876543
            END:VCARD
            """);

        var svc = new ContactResolverService(_settings);
        await svc.LoadFromVCardAsync(path);

        Assert.NotNull(svc.GetContactId("5551234567"));
        Assert.NotEqual(svc.GetContactId("5551234567"), svc.GetContactId("5559876543"));
        Assert.Null(svc.GetContactId("5550000000"));
    }

    [Theory]
    [InlineData("+15550001234", true)]
    [InlineData("(555) 000-1234", true)]
    [InlineData("alex.rivera@example.com", false)]
    public void IsPhone_DistinguishesPhoneFromEmail(string address, bool expected)
    {
        Assert.Equal(expected, ContactResolverService.IsPhone(address));
    }

    [Fact]
    public async Task ClearContacts_ResetsState_AndRaisesEvent()
    {
        var path = WriteVcf("""
            BEGIN:VCARD
            VERSION:3.0
            FN:John Doe
            TEL:+15551234567
            EMAIL:john@example.com
            END:VCARD
            """);

        var svc = new ContactResolverService(_settings);
        await svc.LoadFromVCardAsync(path);
        Assert.Equal(1, svc.ContactCount);

        var raised = false;
        svc.ContactsChanged += (_, _) => raised = true;

        svc.ClearContacts();

        Assert.True(raised);
        Assert.Equal(0, svc.ContactCount);
        Assert.Null(svc.LoadedFilePath);
        Assert.Null(svc.GetContactId("5551234567"));
        // Falls back to the formatted raw number, not the contact name.
        Assert.Equal("(555) 123-4567", svc.GetDisplayName("5551234567"));
    }

    [Fact]
    public async Task HasContactName_TrueForSavedContact_FalseForRawAddress()
    {
        var path = WriteVcf("""
            BEGIN:VCARD
            VERSION:3.0
            FN:John Doe
            TEL:+15551234567
            END:VCARD
            """);

        var svc = new ContactResolverService(_settings);
        await svc.LoadFromVCardAsync(path);

        Assert.True(svc.HasContactName("5551234567"));
        Assert.False(svc.HasContactName("5559876543"));
    }

    [Fact]
    public async Task GetAvatarInitials_ReturnsInitialsForContact_EmptyForRawAddress()
    {
        var path = WriteVcf("""
            BEGIN:VCARD
            VERSION:3.0
            FN:John Doe
            TEL:+15551234567
            END:VCARD
            """);

        var svc = new ContactResolverService(_settings);
        await svc.LoadFromVCardAsync(path);

        Assert.Equal("JD", svc.GetAvatarInitials("5551234567"));
        // Unknown number: empty initials so the avatar shows a generic glyph, not "(" or "+".
        Assert.Equal("", svc.GetAvatarInitials("5559876543"));
        Assert.Equal("", svc.GetAvatarInitials("+15559876543"));
    }

    [Fact]
    public async Task GetChatInitials_PrefersCustomName_ThenContact_ElseEmpty()
    {
        var path = WriteVcf("""
            BEGIN:VCARD
            VERSION:3.0
            FN:John Doe
            TEL:+15551234567
            END:VCARD
            """);

        var svc = new ContactResolverService(_settings);
        await svc.LoadFromVCardAsync(path);

        // Custom (group) name wins.
        Assert.Equal("FC", svc.GetChatInitials(["5551234567", "5559876543"], "Family Chat"));
        // 1:1 with a saved contact → that contact's initials.
        Assert.Equal("JD", svc.GetChatInitials(["5551234567"], null));
        // 1:1 unknown raw address → empty (generic glyph).
        Assert.Equal("", svc.GetChatInitials(["5559876543"], null));
        // Group with no custom name → empty; the single avatar isn't shown for groups.
        Assert.Equal("", svc.GetChatInitials(["5551234567", "5559876543"], null));
    }

    [Fact]
    public void GetChatDisplayName_PrefersExplicitName()
    {
        var svc = new ContactResolverService(_settings);
        Assert.Equal("Family Chat", svc.GetChatDisplayName(["addr1", "addr2"], "Family Chat"));
    }

    [Fact]
    public async Task GetChatDisplayName_JoinsParticipantNames()
    {
        var path = WriteVcf("""
            BEGIN:VCARD
            VERSION:3.0
            FN:Alice
            TEL:1111111111
            END:VCARD
            BEGIN:VCARD
            VERSION:3.0
            FN:Bob
            TEL:2222222222
            END:VCARD
            """);

        var svc = new ContactResolverService(_settings);
        await svc.LoadFromVCardAsync(path);

        Assert.Equal("Alice & Bob", svc.GetChatDisplayName(["1111111111", "2222222222"], null));
    }

    [Fact]
    public async Task GetChatDisplayName_ThreeOrMore_ShowsOthers()
    {
        var path = WriteVcf("""
            BEGIN:VCARD
            VERSION:3.0
            FN:Alice
            TEL:1111111111
            END:VCARD
            BEGIN:VCARD
            VERSION:3.0
            FN:Bob
            TEL:2222222222
            END:VCARD
            BEGIN:VCARD
            VERSION:3.0
            FN:Charlie
            TEL:3333333333
            END:VCARD
            """);

        var svc = new ContactResolverService(_settings);
        await svc.LoadFromVCardAsync(path);

        Assert.Equal("Alice, Bob & 1 other",
            svc.GetChatDisplayName(["1111111111", "2222222222", "3333333333"], null));
    }

    [Fact]
    public async Task GetAvatar_ReturnsBytes_WhenPhotoPresent()
    {
        var photoBytes = new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 };
        var base64 = Convert.ToBase64String(photoBytes);
        var path = WriteVcf($"""
            BEGIN:VCARD
            VERSION:3.0
            FN:Test User
            TEL:5551234567
            PHOTO;ENCODING=b;TYPE=JPEG:{base64}
            END:VCARD
            """);

        var svc = new ContactResolverService(_settings);
        await svc.LoadFromVCardAsync(path);

        Assert.Equal(photoBytes, svc.GetAvatar("5551234567"));
    }

    [Fact]
    public void GetAvatar_ReturnsNull_WhenNoContact()
    {
        var svc = new ContactResolverService(_settings);
        Assert.Null(svc.GetAvatar("unknown"));
    }

    [Fact]
    public async Task GetAvatar_KeepsSameReference_WhenReloadedPhotoUnchanged()
    {
        // Regression for the avatar flicker (B3): a reload that produces byte-identical photo content
        // must hand back the SAME array reference, so the tile binding and decoded-bitmap cache (both
        // keyed on reference equality) treat it as a no-op instead of rebinding + re-decoding.
        var photoBytes = new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0x11, 0x22 };
        var base64 = Convert.ToBase64String(photoBytes);
        var vcf = $"""
            BEGIN:VCARD
            VERSION:3.0
            FN:Test User
            TEL:5551234567
            PHOTO;ENCODING=b;TYPE=JPEG:{base64}
            END:VCARD
            """;
        var path = WriteVcf(vcf);

        var svc = new ContactResolverService(_settings);
        await svc.LoadFromVCardAsync(path);
        var first = svc.GetAvatar("5551234567");

        // Reload the identical file (fresh parse → fresh arrays internally).
        await svc.LoadFromVCardAsync(path);
        var second = svc.GetAvatar("5551234567");

        Assert.NotNull(first);
        Assert.Same(first, second); // reference preserved, not just equal content
    }

    [Fact]
    public async Task GetAvatar_ReturnsNewReference_WhenPhotoChanged()
    {
        var path = Path.Combine(_tempDir, "test.vcf");

        File.WriteAllText(path, $"""
            BEGIN:VCARD
            VERSION:3.0
            FN:Test User
            TEL:5551234567
            PHOTO;ENCODING=b;TYPE=JPEG:{Convert.ToBase64String(new byte[] { 1, 2, 3, 4 })}
            END:VCARD
            """);
        var svc = new ContactResolverService(_settings);
        await svc.LoadFromVCardAsync(path);
        var first = svc.GetAvatar("5551234567");

        File.WriteAllText(path, $"""
            BEGIN:VCARD
            VERSION:3.0
            FN:Test User
            TEL:5551234567
            PHOTO;ENCODING=b;TYPE=JPEG:{Convert.ToBase64String(new byte[] { 9, 8, 7, 6, 5 })}
            END:VCARD
            """);
        await svc.LoadFromVCardAsync(path);
        var second = svc.GetAvatar("5551234567");

        Assert.NotNull(second);
        Assert.NotSame(first, second);
        Assert.Equal(new byte[] { 9, 8, 7, 6, 5 }, second);
    }

    [Fact]
    public async Task LoadContactsAsync_LoadsFromSavedPath()
    {
        var path = WriteVcf("""
            BEGIN:VCARD
            VERSION:3.0
            FN:Auto Load
            TEL:5550001111
            END:VCARD
            """);

        _settings.VCardFilePath = path;
        var svc = new ContactResolverService(_settings);
        await svc.LoadContactsAsync();

        Assert.Equal(1, svc.ContactCount);
        Assert.Equal("Auto Load", svc.GetDisplayName("5550001111"));
    }

    [Fact]
    public async Task LoadContactsAsync_NoOp_WhenNoPathSaved()
    {
        var svc = new ContactResolverService(_settings);
        await svc.LoadContactsAsync();

        Assert.Equal(0, svc.ContactCount);
    }

    [Fact]
    public async Task ContactsChanged_FiresAfterImport()
    {
        var path = WriteVcf("""
            BEGIN:VCARD
            VERSION:3.0
            FN:Event Test
            TEL:5551112222
            END:VCARD
            """);

        var svc = new ContactResolverService(_settings);
        bool fired = false;
        svc.ContactsChanged += (_, _) => fired = true;

        await svc.LoadFromVCardAsync(path);

        Assert.True(fired);
    }

    [Fact]
    public async Task InternationalPhoneNumber_MatchesLast10Digits()
    {
        var path = WriteVcf("""
            BEGIN:VCARD
            VERSION:3.0
            FN:International User
            TEL:+44 7911 123456
            END:VCARD
            """);

        var svc = new ContactResolverService(_settings);
        await svc.LoadFromVCardAsync(path);

        Assert.Equal("International User", svc.GetDisplayName("+447911123456"));
        Assert.Equal("International User", svc.GetDisplayName("7911123456"));
    }

    [Fact]
    public async Task MalformedVCard_HandledGracefully()
    {
        var path = WriteVcf("""
            This is not a valid vCard file.
            It has no BEGIN:VCARD or END:VCARD markers.
            Just random text.
            """);

        var svc = new ContactResolverService(_settings);
        await svc.LoadFromVCardAsync(path);

        Assert.Equal(0, svc.ContactCount);
    }
}

public class VCardParserTests
{
    [Fact]
    public void Parse_HandlesLineFolding()
    {
        var vcf = "BEGIN:VCARD\r\nVERSION:3.0\r\nFN:Long Name That Gets\r\n  Folded\r\nTEL:5551234567\r\nEND:VCARD";
        var contacts = VCardParser.Parse(vcf);

        Assert.Single(contacts);
        Assert.Equal("Long Name That Gets Folded", contacts[0].FormattedName);
    }

    [Fact]
    public void Parse_ExtractsStructuredName()
    {
        var vcf = "BEGIN:VCARD\r\nVERSION:3.0\r\nN:Smith;Jane;;;\r\nTEL:5551234567\r\nEND:VCARD";
        var contacts = VCardParser.Parse(vcf);

        Assert.Single(contacts);
        Assert.Equal("Smith", contacts[0].FamilyName);
        Assert.Equal("Jane", contacts[0].GivenName);
    }

    [Fact]
    public void Parse_FallsBackToStructuredName_WhenNoFN()
    {
        var vcf = "BEGIN:VCARD\r\nVERSION:3.0\r\nN:Doe;John;;;\r\nTEL:5550001111\r\nEND:VCARD";
        var contacts = VCardParser.Parse(vcf);

        Assert.Single(contacts);
        Assert.Null(contacts[0].FormattedName);
        Assert.Equal("Doe", contacts[0].FamilyName);
        Assert.Equal("John", contacts[0].GivenName);
    }

    [Fact]
    public void Parse_MultiplePhoneAndEmail()
    {
        var vcf = "BEGIN:VCARD\r\nVERSION:3.0\r\nFN:Multi\r\nTEL;TYPE=CELL:1111111111\r\nTEL;TYPE=HOME:2222222222\r\nEMAIL;TYPE=WORK:a@b.com\r\nEMAIL;TYPE=HOME:c@d.com\r\nEND:VCARD";
        var contacts = VCardParser.Parse(vcf);

        Assert.Single(contacts);
        Assert.Equal(2, contacts[0].Phones.Count);
        Assert.Equal(2, contacts[0].Emails.Count);
    }

    [Fact]
    public void Parse_EmptyInput_ReturnsEmptyList()
    {
        var contacts = VCardParser.Parse("");
        Assert.Empty(contacts);
    }

    [Fact]
    public void Parse_PhotoBase64Encoding()
    {
        var photoBytes = new byte[] { 1, 2, 3, 4 };
        var b64 = Convert.ToBase64String(photoBytes);
        var vcf = $"BEGIN:VCARD\r\nVERSION:3.0\r\nFN:Photo Test\r\nTEL:5551234567\r\nPHOTO;ENCODING=BASE64;TYPE=JPEG:{b64}\r\nEND:VCARD";
        var contacts = VCardParser.Parse(vcf);

        Assert.Single(contacts);
        Assert.Equal(photoBytes, contacts[0].Photo);
    }
}
