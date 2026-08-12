using Windows.UI;

namespace BlueBubbles.Windows.Helpers;

/// <summary>Deterministic per-contact color palette used by the colorful avatar fallbacks.</summary>
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
}
