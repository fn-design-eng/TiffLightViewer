using System.Windows.Media.Imaging;

namespace TiffLightViewer;

public sealed record TiffPage(BitmapSource Bitmap, int OriginalPixelWidth, int OriginalPixelHeight)
{
    public double PreviewScaleX => Bitmap.PixelWidth / (double)Math.Max(1, OriginalPixelWidth);
    public double PreviewScaleY => Bitmap.PixelHeight / (double)Math.Max(1, OriginalPixelHeight);
}
