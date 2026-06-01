using System.Runtime.InteropServices;

namespace BlueBubbles.Windows.Helpers;

internal static class BadgeIconRenderer
{
    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int cx, int cy);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr hdc, IntPtr h);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr ho);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateSolidBrush(uint crColor);

    [DllImport("gdi32.dll")]
    private static extern bool Ellipse(IntPtr hdc, int left, int top, int right, int bottom);

    [DllImport("gdi32.dll")]
    private static extern int SetBkMode(IntPtr hdc, int mode);

    [DllImport("gdi32.dll")]
    private static extern uint SetTextColor(IntPtr hdc, uint color);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateFont(
        int cHeight, int cWidth, int cEscapement, int cOrientation,
        int cWeight, uint bItalic, uint bUnderline, uint bStrikeOut,
        uint iCharSet, uint iOutPrecision, uint iClipPrecision,
        uint iQuality, uint iPitchAndFamily, string pszFaceName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int DrawText(IntPtr hdc, string lpchText, int cchText, ref RECT lprc, uint format);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ICONINFO
    {
        public bool fIcon;
        public int xHotspot;
        public int yHotspot;
        public IntPtr hbmMask;
        public IntPtr hbmColor;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr CreateIconIndirect(ref ICONINFO piconinfo);

    private const int TRANSPARENT = 1;
    private const uint DT_CENTER = 0x01;
    private const uint DT_VCENTER = 0x04;
    private const uint DT_SINGLELINE = 0x20;
    private const int FW_BOLD = 700;

    // RGB packed as 0x00BBGGRR for GDI
    private const uint RedBrush = 0x002020E0; // #E02020 in BGR
    private const uint WhiteText = 0x00FFFFFF;

    private static readonly object _cacheLock = new();
    private static readonly Dictionary<int, IntPtr> _iconCache = new();

    public static IntPtr GetBadgeIcon(int count)
    {
        if (count <= 0) return IntPtr.Zero;
        var key = Math.Min(count, 10);

        lock (_cacheLock)
        {
            if (_iconCache.TryGetValue(key, out var cached))
                return cached;

            var icon = RenderBadgeIcon(key > 9 ? "9+" : key.ToString());
            _iconCache[key] = icon;
            return icon;
        }
    }

    public static void ClearCache()
    {
        lock (_cacheLock)
        {
            foreach (var icon in _iconCache.Values)
                DestroyIcon(icon);
            _iconCache.Clear();
        }
    }

    private static IntPtr RenderBadgeIcon(string text)
    {
        const int size = 16;

        var screenDc = GetDC(IntPtr.Zero);
        var memDc = CreateCompatibleDC(screenDc);
        var hBitmap = CreateCompatibleBitmap(screenDc, size, size);
        var hMask = CreateCompatibleBitmap(screenDc, size, size);
        var oldBitmap = SelectObject(memDc, hBitmap);

        // Fill background with red circle
        var brush = CreateSolidBrush(RedBrush);
        var oldBrush = SelectObject(memDc, brush);
        Ellipse(memDc, 0, 0, size, size);
        SelectObject(memDc, oldBrush);
        DeleteObject(brush);

        // Draw white number text
        SetBkMode(memDc, TRANSPARENT);
        SetTextColor(memDc, WhiteText);
        var fontSize = text.Length > 1 ? 9 : 11;
        var hFont = CreateFont(fontSize, 0, 0, 0, FW_BOLD, 0, 0, 0, 0, 0, 0, 0, 0, "Segoe UI");
        var oldFont = SelectObject(memDc, hFont);

        var rect = new RECT { Left = 0, Top = 0, Right = size, Bottom = size };
        DrawText(memDc, text, text.Length, ref rect, DT_CENTER | DT_VCENTER | DT_SINGLELINE);

        SelectObject(memDc, oldFont);
        DeleteObject(hFont);

        // Create mask (all opaque — the circle-on-square shape handles itself visually)
        var maskDc = CreateCompatibleDC(screenDc);
        var oldMask = SelectObject(maskDc, hMask);
        var blackBrush = CreateSolidBrush(0x00000000);
        var maskRect = new RECT { Left = 0, Top = 0, Right = size, Bottom = size };
        // Fill mask with black (opaque) via ellipse too
        var oldMaskBrush = SelectObject(maskDc, blackBrush);
        Ellipse(maskDc, 0, 0, size, size);
        SelectObject(maskDc, oldMaskBrush);
        DeleteObject(blackBrush);
        SelectObject(maskDc, oldMask);
        DeleteDC(maskDc);

        SelectObject(memDc, oldBitmap);
        DeleteDC(memDc);
        ReleaseDC(IntPtr.Zero, screenDc);

        var iconInfo = new ICONINFO
        {
            fIcon = true,
            xHotspot = 0,
            yHotspot = 0,
            hbmMask = hMask,
            hbmColor = hBitmap
        };
        var hIcon = CreateIconIndirect(ref iconInfo);

        DeleteObject(hBitmap);
        DeleteObject(hMask);

        return hIcon;
    }
}
