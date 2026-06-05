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
    /// scale) instead of decoding full-res and letting the GPU downscale it.
    /// <para>When <paramref name="cache"/> is set the decoded bitmap is stored in (and the load
    /// short-circuited by) a shared LRU keyed on (path, decodeWidth). This is what keeps inline
    /// chat images from re-decoding — and flickering blank — every time the virtualizing ListView
    /// recycles their container. See <see cref="TryGetCached"/> for the synchronous hit path.</para></summary>
    public static async Task<BitmapImage?> FromFileAsync(string path, int decodePixelWidth = 0, bool decodeLogical = false, bool cache = false)
    {
        if (cache && TryGetCached(path, decodePixelWidth) is { } hit)
            return hit;

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
            if (cache) Store(CacheKey(path, decodePixelWidth), bitmap);
            return bitmap;
        }
        catch
        {
            return null;
        }
    }

    // ── Decoded-bitmap LRU ──────────────────────────────────────────────────────────────────
    // A BitmapImage can back several Image.Source slots and survive across container recycles,
    // so caching the decoded instance lets a recycled chat bubble re-show its image synchronously
    // (no async disk decode, no blank frame). Bounded so a long photo thread can't grow unbounded.
    // Touched only from the UI thread (callers await on the dispatcher), but locked to stay safe.
    private const int MaxCacheEntries = 80;
    private static readonly object _cacheLock = new();
    private static readonly Dictionary<string, LinkedListNode<KeyValuePair<string, BitmapImage>>> _cache = new();
    private static readonly LinkedList<KeyValuePair<string, BitmapImage>> _lru = new();

    private static string CacheKey(string path, int decodePixelWidth) => $"{path}|{decodePixelWidth}";

    /// <summary>Returns a previously decoded bitmap for (path, decodeWidth), or null on a miss.
    /// Synchronous so a recycled container can assign Source without an intervening blank frame.</summary>
    public static BitmapImage? TryGetCached(string path, int decodePixelWidth)
    {
        var key = CacheKey(path, decodePixelWidth);
        lock (_cacheLock)
        {
            if (_cache.TryGetValue(key, out var node))
            {
                _lru.Remove(node);
                _lru.AddFirst(node);   // most-recently-used
                return node.Value.Value;
            }
        }
        return null;
    }

    private static void Store(string key, BitmapImage bitmap)
    {
        lock (_cacheLock)
        {
            if (_cache.ContainsKey(key)) return;
            var node = new LinkedListNode<KeyValuePair<string, BitmapImage>>(new(key, bitmap));
            _lru.AddFirst(node);
            _cache[key] = node;
            while (_cache.Count > MaxCacheEntries && _lru.Last is { } oldest)
            {
                _lru.RemoveLast();
                _cache.Remove(oldest.Value.Key);
            }
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
