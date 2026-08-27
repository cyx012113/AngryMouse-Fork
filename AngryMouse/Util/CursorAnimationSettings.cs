using System;
using System.Windows.Forms;

namespace AngryMouse.Util
{
    internal static class CursorAnimationSettings
    {
        public const int MinimumAnimationLength = 50;
        public const double MinimumGrowthRate = 1.0;
        public const double MaximumGrowthRate = 500.0;
        public const int MinimumMaxCursorSize = 50;
        public const int MaximumMaxCursorSize = 4096;

        public static int NormalizeLength(int value)
        {
            return Math.Max(MinimumAnimationLength, value);
        }

        public static int GetEffectiveLength()
        {
            return NormalizeLength(Properties.Settings.Default.CursorAnimationLength);
        }

        public static double GetGrowthRate()
        {
            var rate = Properties.Settings.Default.CursorGrowthRate;
            if (rate < MinimumGrowthRate) return MinimumGrowthRate;
            if (rate > MaximumGrowthRate) return MaximumGrowthRate;
            return rate;
        }

        public static int GetMaxCursorSize()
        {
            var size = Properties.Settings.Default.MaxCursorSize;
            if (size < MinimumMaxCursorSize) return MinimumMaxCursorSize;
            if (size > MaximumMaxCursorSize) return MaximumMaxCursorSize;
            // Clamp to screen dimension max
            var screen = System.Windows.Forms.Screen.PrimaryScreen;
            var screenMax = Math.Max(screen.Bounds.Width, screen.Bounds.Height);
            return Math.Min(size, screenMax);
        }

        /// <summary>
        /// Compute the maximum scale factor for layout calculations.
        /// Used by OverlayWindow and CursorRoleAdjustWindow.
        /// </summary>
        public static double GetMaxScale(double cursorVisualHeight, double pixelsPerDip)
        {
            var maxSize = GetMaxCursorSize();
            return (maxSize / Math.Max(1, cursorVisualHeight)) * pixelsPerDip;
        }
    }
}
