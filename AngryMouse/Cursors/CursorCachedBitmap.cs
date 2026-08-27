using System.Windows.Media.Imaging;

namespace AngryMouse.Cursors
{
    internal sealed class CursorCachedBitmap
    {
        public CursorCachedBitmap(
            BitmapSource bitmap,
            int cropLeft,
            int cropTop,
            int uncroppedWidth,
            int uncroppedHeight)
            : this(bitmap, cropLeft, cropTop, uncroppedWidth, uncroppedHeight, 0, 0)
        {
        }

        public CursorCachedBitmap(
            BitmapSource bitmap,
            int cropLeft,
            int cropTop,
            int uncroppedWidth,
            int uncroppedHeight,
            double hotspotX,
            double hotspotY)
        {
            Bitmap = bitmap;
            CropLeft = cropLeft;
            CropTop = cropTop;
            UncroppedWidth = uncroppedWidth;
            UncroppedHeight = uncroppedHeight;
            HotspotX = hotspotX;
            HotspotY = hotspotY;
        }

        public BitmapSource Bitmap { get; }

        public int CropLeft { get; }

        public int CropTop { get; }

        public int UncroppedWidth { get; }

        public int UncroppedHeight { get; }

        /// <summary>Pre-computed hotspot X coordinate in unscaled image space. 0 if not computed.</summary>
        public double HotspotX { get; }

        /// <summary>Pre-computed hotspot Y coordinate in unscaled image space. 0 if not computed.</summary>
        public double HotspotY { get; }

        public bool HasHotspot => HotspotX != 0 || HotspotY != 0;
    }
}
