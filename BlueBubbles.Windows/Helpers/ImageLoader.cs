using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage;
using Windows.Storage.FileProperties;

namespace BlueBubbles.Windows.Helpers;

/// <summary>
/// Shared, failure-tolerant loading of local images into <see cref="BitmapImage"/>.
/// Centralizes the stream-based load so chat bubbles, the media gallery, and the
/// fullscreen viewer don't each reimplement it. Callers are responsible for
/// recycling guards (generation counters / token checks) around the await.
/// </summary>
public static class ImageLoader
{
    /// <summary>Loads a local image file at full resolution (or capped by
    /// <paramref name="decodePixelWidth"/> when &gt; 0). When <paramref name="decodeLogical"/>
    /// is set the cap is interpreted in logical (effective) pixels, so the decode tracks the
    /// display DPI — the bitmap lands already sized for its on-screen footprint (crisp at any
    /// scale) instead of decoding full-res and letting the GPU downscale it.</summary>
    public static async Task<BitmapImage?> FromFileAsync(string path, int decodePixelWidth = 0, bool decodeLogical = false)
    {
        try
        {
            var bitmap = new BitmapImage();
            if (decodePixelWidth > 0)
            {
                bitmap.DecodePixelWidth = decodePixelWidth;
                if (decodeLogical) bitmap.DecodePixelType = DecodePixelType.Logical;
            }
            using var stream = File.OpenRead(path);
            await bitmap.SetSourceAsync(stream.AsRandomAccessStream());
            return bitmap;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Produces a small thumbnail for a local file. Works for images and
    /// videos (the shell extracts a representative frame), ideal for gallery tiles.</summary>
    public static async Task<BitmapImage?> ThumbnailAsync(string path, uint size = 200)
    {
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(path);
            using var thumb = await file.GetThumbnailAsync(ThumbnailMode.SingleItem, size);
            if (thumb is null) return null;
            var bitmap = new BitmapImage();
            await bitmap.SetSourceAsync(thumb);
            return bitmap;
        }
        catch
        {
            return null;
        }
    }
}
