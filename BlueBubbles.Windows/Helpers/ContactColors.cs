using Windows.UI;

namespace BlueBubbles.Windows.Helpers;

/// <summary>
/// Deterministic per-contact color palette shared by avatar fallbacks and (when "Colorful
/// bubbles" is enabled) incoming message bubble tints, so a contact reads the same color in both.
/// </summary>
public static class ContactColors
{
    private static readonly Color[] Palette =
    [
        Color.FromArgb(255, 66, 133, 244),   // blue
        Color.FromArgb(255, 219, 68, 55),    // red
        Color.FromArgb(255, 244, 180, 0),    // yellow
        Color.FromArgb(255, 15, 157, 88),    // green
        Color.FromArgb(255, 171, 71, 188),   // purple
        Color.FromArgb(255, 255, 112, 67),   // orange
        Color.FromArgb(255, 0, 172, 193),    // teal
        Color.FromArgb(255, 124, 77, 255),   // deep purple
    ];

    /// <summary>Picks a stable color for the given key (a contact name, initials, or address)
    /// using a DJB2 hash so the same key always maps to the same swatch.</summary>
    public static Color ForKey(string? key)
    {
        uint hash = 5381;
        if (key is not null)
        {
            foreach (var c in key)
                hash = hash * 33 + c;
        }
        return Palette[hash % (uint)Palette.Length];
    }

    /// <summary>A translucent version of <see cref="ForKey"/> suitable as a bubble background
    /// tint — subtle enough to keep the message text legible over it.</summary>
    public static Color TintForKey(string? key)
    {
        var c = ForKey(key);
        return Color.FromArgb(0x33, c.R, c.G, c.B);
    }
}
