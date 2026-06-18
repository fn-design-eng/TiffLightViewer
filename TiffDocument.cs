using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace TiffLightViewer;

public sealed class TiffDocument : IDisposable
{
    private const int CacheRadius = 1;
    private const int MaxPreviewPixels = 16_000_000;
    private readonly Dictionary<int, TiffPage> _cache = [];
    private readonly object _cacheLock = new();
    private readonly string _path;
    private bool _disposed;

    public TiffDocument(string path)
    {
        _path = path;
        PageCount = ReadPageCount(path);
    }

    public int PageCount { get; }

    public Task<TiffPage> LoadPageAsync(int pageIndex, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        if (pageIndex < 0 || pageIndex >= PageCount)
        {
            throw new ArgumentOutOfRangeException(nameof(pageIndex));
        }

        lock (_cacheLock)
        {
            if (_cache.TryGetValue(pageIndex, out var cached))
            {
                return Task.FromResult(cached);
            }
        }

        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var page = LoadPage(pageIndex);
            cancellationToken.ThrowIfCancellationRequested();
            lock (_cacheLock)
            {
                _cache[pageIndex] = page;
                TrimCache(pageIndex);
            }

            _ = Task.Run(() => PreloadNeighbors(pageIndex), CancellationToken.None);
            return page;
        }, cancellationToken);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        lock (_cacheLock)
        {
            _cache.Clear();
        }
    }

    private static int ReadPageCount(string path)
    {
        using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var decoder = new TiffBitmapDecoder(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
        return decoder.Frames.Count;
    }

    private TiffPage LoadPage(int pageIndex)
    {
        using var stream = File.Open(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var decoder = new TiffBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        var frame = decoder.Frames[pageIndex];
        var originalWidth = frame.PixelWidth;
        var originalHeight = frame.PixelHeight;
        var source = DownscaleIfHuge(frame);
        source.Freeze();
        return new TiffPage(source, originalWidth, originalHeight);
    }

    private static BitmapSource DownscaleIfHuge(BitmapSource source)
    {
        var pixelCount = source.PixelWidth * (long)source.PixelHeight;
        if (pixelCount <= MaxPreviewPixels)
        {
            return source;
        }

        var scale = Math.Sqrt(MaxPreviewPixels / (double)pixelCount);
        var transformed = new TransformedBitmap(source, new ScaleTransform(scale, scale));
        transformed.Freeze();
        return transformed;
    }

    private void PreloadNeighbors(int centerPageIndex)
    {
        for (var pageIndex = centerPageIndex - CacheRadius; pageIndex <= centerPageIndex + CacheRadius; pageIndex++)
        {
            if (pageIndex < 0 || pageIndex >= PageCount || pageIndex == centerPageIndex)
            {
                continue;
            }

            lock (_cacheLock)
            {
                if (_cache.ContainsKey(pageIndex))
                {
                    continue;
                }
            }

            try
            {
                var page = LoadPage(pageIndex);
                lock (_cacheLock)
                {
                    _cache.TryAdd(pageIndex, page);
                    TrimCache(centerPageIndex);
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private void TrimCache(int centerPageIndex)
    {
        var staleKeys = _cache.Keys
            .Where(key => Math.Abs(key - centerPageIndex) > CacheRadius)
            .ToArray();

        foreach (var key in staleKeys)
        {
            _cache.Remove(key);
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}
