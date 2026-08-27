using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using AngryMouse.Cursors;
using AngryMouse.Util;

namespace AngryMouse.Animation
{
    class MouseAnimator : IDisposable
    {
        private readonly ScaleTransform _cursorScale;
        private bool _shaking;
        private bool _disposed;

        private double _cursorVisualHeight = CursorVisualLoader.BuiltInCursorHeight;

        // Tracks the current cursor size in WPF device-independent pixels for continuous growth
        private double _currentPixelSize;
        // Last applied scale to avoid redundant property sets that trigger layout
        private double _lastAppliedScale = -1;

        private DispatcherTimer _growthTimer;
        private readonly Stopwatch _growthStopwatch = new Stopwatch();

        // Minimum scale factor when shake starts. The cursor starts at this fraction of natural size.
        private const double StartSizeFraction = 0.25;
        private const int GrowthTimerIntervalMs = 33; // ~30 FPS, smooth enough without overloading UI

        public DpiScale DpiInfo;

        public double CursorVisualHeight
        {
            get => _cursorVisualHeight;
            set
            {
                _cursorVisualHeight = Math.Max(1, value);
            }
        }

        public MouseAnimator(ScaleTransform cursorScale, DpiScale dpiInfo)
        {
            _cursorScale = cursorScale;
            DpiInfo = dpiInfo;
            _currentPixelSize = _cursorVisualHeight * StartSizeFraction;

            Properties.Settings.Default.PropertyChanged += DefaultOnPropertyChanged;
        }

        private void DefaultOnPropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName.Equals("CursorAnimationLength"))
            {
                DebugLog.Write(
                    "Animation duration setting changed: configuredMs=" +
                    Properties.Settings.Default.CursorAnimationLength +
                    ", effectiveMs=" +
                    CursorAnimationSettings.GetEffectiveLength());
            }

            if (e.PropertyName.Equals("CursorGrowthRate") || e.PropertyName.Equals("MaxCursorSize"))
            {
                DebugLog.Write(
                    "Cursor growth settings changed while animator active: shaking=" +
                    (_shaking ? "On" : "Off"));
            }
        }

        public void SetMouseShake(bool shaking, DateTime timestamp)
        {
            if (_shaking == shaking) return;

            if (shaking)
            {
                var currentScale = _cursorScale.ScaleX;
                _cursorScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                _cursorScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);

                if (currentScale > 0.01)
                {
                    _cursorScale.ScaleX = currentScale;
                    _cursorScale.ScaleY = currentScale;
                    _currentPixelSize = _cursorVisualHeight * currentScale;
                }
                else
                {
                    _currentPixelSize = _cursorVisualHeight * StartSizeFraction;
                }

                _currentPixelSize = Math.Max(_cursorVisualHeight * StartSizeFraction,
                    Math.Min(_currentPixelSize, CursorAnimationSettings.GetMaxCursorSize()));

                _shaking = true;
                _lastAppliedScale = -1;
                StartGrowthTimer();
            }
            else
            {
                // Stop growing but keep cursor at current size.
                // The overlay will trigger shrink separately via StartShrink().
                _shaking = false;
                StopGrowthTimer();
            }
        }

        /// <summary>
        /// Shrinks the cursor from current scale toward targetScale.
        /// In always-override mode: targetScale = naturalScale (~32px).
        /// Normal mode: targetScale = 0.
        /// </summary>
        public void StartShrink(double targetScale = 0.0)
        {
            _shaking = false;
            StopGrowthTimer();

            double fromScale = _lastAppliedScale > 0.01 ? _lastAppliedScale : _cursorScale.ScaleX;
            double toScale = targetScale;
            if (fromScale <= toScale + 0.01)
                return;

            int durationMs = CursorAnimationSettings.GetEffectiveLength();

            var anim = CreateScaleAnimation(fromScale, toScale, durationMs);
            anim.Completed += (s, e) =>
            {
                _cursorScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                _cursorScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
                _cursorScale.ScaleX = toScale;
                _cursorScale.ScaleY = toScale;
            };

            _cursorScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            _cursorScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            _cursorScale.ScaleX = toScale;
            _cursorScale.ScaleY = toScale;

            _cursorScale.BeginAnimation(ScaleTransform.ScaleXProperty, anim);
            _cursorScale.BeginAnimation(ScaleTransform.ScaleYProperty, anim);
            _lastAppliedScale = -1;
        }

        private void StartGrowthTimer()
        {
            StopGrowthTimer();

            _growthStopwatch.Restart();
            _growthTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(GrowthTimerIntervalMs)
            };
            _growthTimer.Tick += OnGrowthTick;
            _growthTimer.Start();
        }

        private void StopGrowthTimer()
        {
            if (_growthTimer != null)
            {
                _growthTimer.Stop();
                _growthTimer.Tick -= OnGrowthTick;
                _growthTimer = null;
            }

            _growthStopwatch.Reset();
        }

        private void OnGrowthTick(object sender, EventArgs e)
        {
            if (!_shaking || _disposed)
            {
                StopGrowthTimer();
                return;
            }

            var deltaSeconds = _growthStopwatch.Elapsed.TotalSeconds;
            _growthStopwatch.Restart();

            // Clamp delta to prevent huge jumps
            if (deltaSeconds <= 0 || deltaSeconds > 0.2)
            {
                deltaSeconds = GrowthTimerIntervalMs / 1000.0;
            }

            var growthRate = CursorAnimationSettings.GetGrowthRate();
            var maxSize = CursorAnimationSettings.GetMaxCursorSize();

            _currentPixelSize += growthRate * deltaSeconds;

            if (_currentPixelSize >= maxSize)
            {
                _currentPixelSize = maxSize;
                // Stop timer at max size — no more growth needed
                // Cursor stays at max scale until shake stops, then shrink animation runs
                StopGrowthTimer();
            }

            var newScale = _currentPixelSize / _cursorVisualHeight;

            // Only update if scale changed to avoid unnecessary layout invalidation
            if (Math.Abs(newScale - _lastAppliedScale) > 0.001)
            {
                _lastAppliedScale = newScale;
                _cursorScale.ScaleX = newScale;
                _cursorScale.ScaleY = newScale;
            }
        }

        private static DoubleAnimation CreateScaleAnimation(double from, double to, int durationMs)
        {
            return new DoubleAnimation
            {
                From = from,
                To = to,
                Duration = new Duration(TimeSpan.FromMilliseconds(durationMs)),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut },
                FillBehavior = FillBehavior.HoldEnd
            };
        }

        public void Dispose()
        {
            if (_disposed) return;
            _shaking = false;
            StopGrowthTimer();
            Properties.Settings.Default.PropertyChanged -= DefaultOnPropertyChanged;
            _disposed = true;
        }

        // Static scale helpers used by CursorRoleAdjustWindow for the live hotspot preview.
        // Computes the runtime scale that maps a cursor bitmap of the given height to the
        // configured maximum cursor size at the requested DPI (mirrors the runtime rendering).
        internal static double GetTargetScale(double cursorVisualHeight, DpiScale dpiInfo)
        {
            return GetTargetScale(cursorVisualHeight, dpiInfo.PixelsPerDip);
        }

        internal static double GetTargetScale(double cursorVisualHeight, double pixelsPerDip)
        {
            var maxSize = CursorAnimationSettings.GetMaxCursorSize();
            return maxSize / Math.Max(1, cursorVisualHeight) * Math.Max(0.01, pixelsPerDip);
        }
    }
}
