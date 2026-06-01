using System.Collections.Concurrent;
using System.Text;

namespace BlueBubbles.Core.Utils;

public record VCardContact(
    string? FormattedName,
    string? FamilyName,
    string? GivenName,
    List<string> Phones,
    List<string> Emails,
    byte[]? Photo);

public static class VCardParser
{
    public static List<VCardContact> Parse(string vcfContent)
    {
        var contacts = new List<VCardContact>();
        var lines = UnfoldLines(vcfContent);

        string? fn = null;
        string? familyName = null;
        string? givenName = null;
        var phones = new List<string>();
        var emails = new List<string>();
        byte[]? photo = null;
        bool inCard = false;

        foreach (var line in lines)
        {
            if (line.Equals("BEGIN:VCARD", StringComparison.OrdinalIgnoreCase))
            {
                inCard = true;
                fn = null;
                familyName = null;
                givenName = null;
                phones = [];
                emails = [];
                photo = null;
                continue;
            }

            if (line.Equals("END:VCARD", StringComparison.OrdinalIgnoreCase))
            {
                if (inCard && (fn is not null || phones.Count > 0 || emails.Count > 0))
                {
                    contacts.Add(new VCardContact(fn, familyName, givenName, phones, emails, photo));
                }
                inCard = false;
                continue;
            }

            if (!inCard) continue;

            var (name, parameters, value) = ParseProperty(line);
            if (value is null) continue;

            switch (name)
            {
                case "FN":
                    fn = value;
                    break;

                case "N":
                    var parts = value.Split(';');
                    if (parts.Length >= 1) familyName = parts[0];
                    if (parts.Length >= 2) givenName = parts[1];
                    break;

                case "TEL":
                    if (!string.IsNullOrWhiteSpace(value))
                        phones.Add(value.Trim());
                    break;

                case "EMAIL":
                    if (!string.IsNullOrWhiteSpace(value))
                        emails.Add(value.Trim());
                    break;

                case "PHOTO":
                    photo = DecodePhoto(parameters, value);
                    break;
            }
        }

        return contacts;
    }

    public static async Task<List<VCardContact>> ParseFileAsync(string filePath)
    {
        var content = await File.ReadAllTextAsync(filePath);
        return Parse(content);
    }

    private static List<string> UnfoldLines(string content)
    {
        var result = new List<string>();
        var sb = new StringBuilder();

        foreach (var rawLine in content.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');

            if (line.Length > 0 && (line[0] == ' ' || line[0] == '\t'))
            {
                sb.Append(line[1..]);
            }
            else
            {
                if (sb.Length > 0)
                    result.Add(sb.ToString());
                sb.Clear();
                sb.Append(line);
            }
        }

        if (sb.Length > 0)
            result.Add(sb.ToString());

        return result;
    }

    private static (string Name, string Parameters, string? Value) ParseProperty(string line)
    {
        var colonIdx = line.IndexOf(':');
        if (colonIdx < 0)
            return (string.Empty, string.Empty, null);

        var nameAndParams = line[..colonIdx];
        var value = line[(colonIdx + 1)..];

        var semiIdx = nameAndParams.IndexOf(';');
        string name;
        string parameters;
        if (semiIdx >= 0)
        {
            name = nameAndParams[..semiIdx].ToUpperInvariant();
            parameters = nameAndParams[(semiIdx + 1)..].ToUpperInvariant();
        }
        else
        {
            name = nameAndParams.ToUpperInvariant();
            parameters = string.Empty;
        }

        return (name, parameters, value);
    }

    private static byte[]? DecodePhoto(string parameters, string value)
    {
        var isBase64 = parameters.Contains("ENCODING=B", StringComparison.OrdinalIgnoreCase)
                    || parameters.Contains("ENCODING=BASE64", StringComparison.OrdinalIgnoreCase)
                    || parameters.Contains("BASE64", StringComparison.OrdinalIgnoreCase);

        if (!isBase64 || string.IsNullOrWhiteSpace(value))
            return null;

        try
        {
            var cleaned = value.Replace(" ", "").Replace("\t", "");
            return Convert.FromBase64String(cleaned);
        }
        catch
        {
            return null;
        }
    }
}
