using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace BlueBubbles.Windows.Helpers;

/// <summary>
/// Renders the taskbar unread-count overlay badge (red circle + white count) as an HICON for
/// <c>ITaskbarList3.SetOverlayIcon</c>. Drawn with GDI+ on a 32bpp ARGB surface at 4x
/// supersampling so the circle and digits are anti-aliased, then converted to an icon via
/// CreateIconIndirect with a 32bpp DIB section — Bitmap.GetHicon() must NOT be used here, it
/// collapses the smooth alpha channel to a 1-bit mask and brings back jagged edges.
/// </summary>
internal static class BadgeIconRenderer
{
    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern IntPtr CreateIconIndirect(ref ICONINFO piconinfo);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateDIBSection(
        IntPtr hdc, ref BITMAPINFO pbmi, uint usage, out IntPtr ppvBits, IntPtr hSection, uint offset);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateBitmap(int nWidth, int nHeight, uint nPlanes, uint nBitCount, byte[]? lpBits);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr ho);

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFO
    {
        public int biSize;
        public int biWidth;
        public int biHeight;
        public short biPlanes;
        public short biBitCount;
        public int biCompression;
        public int biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public int biClrUsed;
        public int biClrImportant;
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

    // Windows 11's own taskbar badges use the system critical-fill red.
    private static readonly Color BadgeRed = Color.FromArgb(0xC4, 0x2B, 0x1C);

    private const int SuperSample = 4;
    private const int BaseSizePx = 16; // overlay icon size at 96 DPI (SM_CXSMICON)

    private static readonly object _cacheLock = new();
    private static readonly Dictionary<(int Count, int Size), IntPtr> _iconCache = new();

    public static IntPtr GetBadgeIcon(int count, IntPtr hwnd)
    {
        if (count <= 0) return IntPtr.Zero;
        var capped = Math.Min(count, 10);
        var size = GetBadgeSize(hwnd);

        lock (_cacheLock)
        {
            if (_iconCache.TryGetValue((capped, size), out var cached))
                return cached;

            using var bitmap = RenderBadgeBitmap(capped > 9 ? "9+" : capped.ToString(), size);
            var icon = CreateIconWithAlpha(bitmap);
            if (icon != IntPtr.Zero)
                _iconCache[(capped, size)] = icon;
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

    private static int GetBadgeSize(IntPtr hwnd)
    {
        uint dpi = hwnd != IntPtr.Zero ? GetDpiForWindow(hwnd) : 0;
        if (dpi == 0) dpi = 96;
        return (int)Math.Round(BaseSizePx * dpi / 96.0);
    }

    internal static Bitmap RenderBadgeBitmap(string text, int size)
    {
        var big = size * SuperSample;
        using var bigBitmap = new Bitmap(big, big, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bigBitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            using var fill = new SolidBrush(BadgeRed);
            g.FillEllipse(fill, 1, 1, big - 2, big - 2);

            g.TextRenderingHint = TextRenderingHint.AntiAlias;
            var em = big * (text.Length > 1 ? 0.50f : 0.62f);
            using var font = new Font("Segoe UI", em, FontStyle.Bold, GraphicsUnit.Pixel);
            using var format = new StringFormat(StringFormat.GenericTypographic)
            {
                Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Center,
            };
            g.DrawString(text, font, Brushes.White, new RectangleF(0, 0, big, big), format);
        }

        var result = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(result))
        {
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.DrawImage(bigBitmap, new Rectangle(0, 0, size, size));
        }
        return result;
    }

    private static IntPtr CreateIconWithAlpha(Bitmap bitmap)
    {
        int w = bitmap.Width, h = bitmap.Height;

        var bmi = new BITMAPINFO
        {
            biSize = 40,
            biWidth = w,
            biHeight = -h, // top-down, matching LockBits row order
            biPlanes = 1,
            biBitCount = 32,
        };
        var hColor = CreateDIBSection(IntPtr.Zero, ref bmi, 0, out var bits, IntPtr.Zero, 0);
        if (hColor == IntPtr.Zero) return IntPtr.Zero;

        // The 1bpp AND mask is only consulted by legacy non-alpha renderers; derive it from the
        // alpha channel anyway so the badge stays circular even there. Mono rows are WORD-aligned.
        var maskStride = (w + 15) / 16 * 2;
        var mask = new byte[maskStride * h];

        var data = bitmap.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var row = new byte[w * 4];
            for (var y = 0; y < h; y++)
            {
                Marshal.Copy(data.Scan0 + y * data.Stride, row, 0, row.Length);
                Marshal.Copy(row, 0, bits + y * w * 4, row.Length);
                for (var x = 0; x < w; x++)
                {
                    if (row[x * 4 + 3] == 0)
                        mask[y * maskStride + (x >> 3)] |= (byte)(0x80 >> (x & 7));
                }
            }
        }
        finally
        {
            bitmap.UnlockBits(data);
        }

        var hMask = CreateBitmap(w, h, 1, 1, mask);
        var iconInfo = new ICONINFO
        {
            fIcon = true,
            hbmMask = hMask,
            hbmColor = hColor,
        };
        var hIcon = CreateIconIndirect(ref iconInfo);

        DeleteObject(hColor);
        if (hMask != IntPtr.Zero) DeleteObject(hMask);

        return hIcon;
    }
}
