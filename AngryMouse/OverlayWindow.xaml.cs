using AngryMouse.Animation;
using AngryMouse.Cursors;
using AngryMouse.Screen;
using AngryMouse.Util;
using System;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using static AngryMouse.Util.WindowUtil;

namespace AngryMouse
{
    /// <summary>
    /// Interaction logic for OverlayWindow.xaml
    /// </summary>
    public partial class OverlayWindow
    {
        /// <summary>
        /// Show debug info
        /// </summary>
        private readonly bool _debug;

        /// <summary>
        /// Moves the cursor hotspot around the canvas.
        /// </summary>
        private readonly TranslateTransform _cursorTranslate = new TranslateTransform();

        /// <summary>
        /// Moves the cursor image so its hotspot lands on the mouse position before scaling.
        /// </summary>
        private readonly TranslateTransform _cursorHotspotTranslate = new TranslateTransform();

        /// <summary>
        /// Scales the cursor.
        /// </summary>
        private readonly ScaleTransform _cursorScale = new ScaleTransform
        {
            ScaleX = 0,
            ScaleY = 0
        };

        /// <summary>
        /// The screen this window is open in.
        /// </summary>
        private readonly ScreenInfo _screen;

        /// <summary>
        /// DPI scale info of the current screen.
        /// </summary>
        private DpiScale _dpiInfo;

        /// <summary>
        /// Animates mouse growing and shrinking
        /// </summary>
        private MouseAnimator _mouseAnimator;

        private readonly DispatcherTimer _topmostRefreshTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(50),
            IsEnabled = false
        };

        private bool _systemCursorOverrideActive;

        /// <summary>
        /// Whether the overlay should currently be visible (shaking or role preview).
        /// </summary>
        private bool _shouldBeVisible;

        /// <summary>
        /// Hides the overlay window after the shrink animation finishes so it stops being
        /// composited by DWM while idle.
        /// </summary>
        private readonly DispatcherTimer _idleHideTimer = new DispatcherTimer
        {
            IsEnabled = false
        };

        /// <summary>
        /// Cached current cursor visual dimensions (image pixels) and hotspot.
        /// </summary>
        private double _cursorVisualWidth = CursorVisualLoader.BuiltInCursorWidth;
        private double _cursorVisualHeight = CursorVisualLoader.BuiltInCursorHeight;
        private double _cursorHotspotX;
        private double _cursorHotspotY;

        /// <summary>
        /// Follow-window geometry (physical pixels): size of the small window and the hotspot
        /// offset within it. The window is moved so the hotspot stays under the mouse.
        /// </summary>
        private int _followBoxWidth;
        private int _followBoxHeight;
        private int _followAnchorX;
        private int _followAnchorY;
        private int _lastMouseX;
        private int _lastMouseY;

        /// <summary>
        /// Main constructor.
        /// </summary>
        /// <param name="screen">The window to show the screen in.</param>
        /// <param name="debug">Show debug information on screens</param>
        public OverlayWindow(ScreenInfo screen, bool debug = false)
        {
            InitializeComponent();

            _debug = debug;
            _screen = screen;
            _topmostRefreshTimer.Tick += TopmostRefreshTimer_Tick;
            _idleHideTimer.Tick += IdleHideTimer_Tick;
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            // Do not capture any mouse events
            // TODO I suspect this is the reason we cannot replace the cursor (hide it) since
            // the cursor draws on top of the big cursor.
            // Also hide window from alt-tab menu
            SetWindowStyles(this, ExtendedWindowStyles.WS_EX_TOOLWINDOW | ExtendedWindowStyles.WS_EX_TRANSPARENT);
        }

        protected override void OnDpiChanged(DpiScale oldDpiScaleInfo, DpiScale newDpiScaleInfo)
        {
            _dpiInfo = newDpiScaleInfo;
            DebugLog.Write(
                "DPI changed: " + _screen.Name +
                " " + oldDpiScaleInfo.DpiScaleX + "x" + oldDpiScaleInfo.DpiScaleY +
                " -> " + newDpiScaleInfo.DpiScaleX + "x" + newDpiScaleInfo.DpiScaleY);
            if (_mouseAnimator != null)
            {
                _mouseAnimator.DpiInfo = newDpiScaleInfo;
                ConfigureFollowBox();
            }
        }

        /// <summary>
        /// Called when the window is successfully loaded. Does some view initialization.
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            if (!_debug)
            {
                Root.Children.Remove(DebugInfo);
                OverlayCanvas.Children.Remove(MousePosDebug);
            }

            TransformGroup transformGroup = new TransformGroup();

            transformGroup.Children.Add(_cursorHotspotTranslate);
            transformGroup.Children.Add(_cursorScale);
            transformGroup.Children.Add(_cursorTranslate);

            CursorHost.RenderTransform = transformGroup;

            _dpiInfo = VisualTreeHelper.GetDpi(this);

            if (_debug)
            {
                MousePosDebug.Width = MousePosDebug.Width * _dpiInfo.PixelsPerDip;
                MousePosDebug.Height = MousePosDebug.Height * _dpiInfo.PixelsPerDip;
            }

            _mouseAnimator = new MouseAnimator(_cursorScale, _dpiInfo);
            _mouseAnimator.CursorVisualHeight = _cursorVisualHeight;

            // Size the small follow-window to the current cursor instead of the full screen.
            ConfigureFollowBox();

            // Initialized while shown so DPI/animator are ready; hide now since idle.
            if (!_shouldBeVisible)
            {
                Hide();
            }
        }

        private void Window_Closed(object sender, EventArgs e)
        {
            _topmostRefreshTimer.Stop();
            _topmostRefreshTimer.Tick -= TopmostRefreshTimer_Tick;
            _idleHideTimer.Stop();
            _idleHideTimer.Tick -= IdleHideTimer_Tick;
            _mouseAnimator?.Dispose();
            _mouseAnimator = null;
        }

        public void UpdateMousePosition(int x, int y)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(() => UpdateMousePosition(x, y)));
                return;
            }

            _lastMouseX = x;
            _lastMouseY = y;

            var mouseInScreen = x >= _screen.BoundX && x <= _screen.BoundX + _screen.BoundWidth &&
                                y >= _screen.BoundY && y <= _screen.BoundY + _screen.BoundHeight;
            if (_debug)
            {
                var infoBuilder = new StringBuilder();
                infoBuilder
                    .AppendFormat("Name {0}", _screen.Name).AppendLine()
                    .AppendFormat("Primary {0}", _screen.Primary).AppendLine()
                    .AppendFormat("PixelsPerDip {0}", _dpiInfo.PixelsPerDip).AppendLine()
                    .AppendFormat("Mouse {0},{1}", x, y).AppendLine()
                    .AppendFormat("InScreen {0}", mouseInScreen).AppendLine()
                    .AppendFormat("Draw {0},{1}", x - _screen.BoundX, y - _screen.BoundY);
                DebugInfo.Content = infoBuilder.ToString();

                Canvas.SetTop(MousePosDebug, y - _screen.BoundY);
                Canvas.SetLeft(MousePosDebug, x - _screen.BoundX);
            }

            var shouldShowCursor = mouseInScreen && !_systemCursorOverrideActive;
            CursorHost.Visibility = shouldShowCursor ? Visibility.Visible : Visibility.Hidden;
            if (!shouldShowCursor)
            {
                return;
            }

            // Move the small window so the cursor hotspot stays under the mouse.
            ApplyWindowPosition();
        }

        internal void SetCursorVisual(CursorVisualInfo cursorVisual)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(() => SetCursorVisual(cursorVisual)));
                return;
            }

            if (cursorVisual == null)
            {
                cursorVisual = CursorVisualLoader.BuiltIn();
            }

            try
            {
                ApplyCursorVisual(cursorVisual);
            }
            catch (Exception ex) when (
                ex is IOException ||
                ex is UnauthorizedAccessException ||
                ex is InvalidOperationException ||
                ex is ArgumentException ||
                ex is NotSupportedException)
            {
                DebugLog.WriteException("Overlay cursor visual failed", ex);
                try
                {
                    ApplyCursorVisual(CursorVisualLoader.BuiltIn("Cursor visual failed. Using built-in cursor."));
                }
                catch (Exception fallbackException) when (
                    fallbackException is InvalidOperationException ||
                    fallbackException is ArgumentException ||
                    fallbackException is NotSupportedException)
                {
                    DebugLog.WriteException("Overlay built-in cursor fallback failed", fallbackException);
                }
            }
        }

        private void ApplyCursorVisual(CursorVisualInfo cursorVisual)
        {
            CursorImage.Source = cursorVisual.Bitmap;
            CursorImage.Width = cursorVisual.Width;
            CursorImage.Height = cursorVisual.Height;
            CursorImage.Visibility = cursorVisual.HasBitmap ? Visibility.Visible : Visibility.Hidden;
            BuiltInCursor.Visibility = cursorVisual.HasBitmap ? Visibility.Hidden : Visibility.Visible;

            CursorHost.Width = cursorVisual.Width;
            CursorHost.Height = cursorVisual.Height;

            _cursorHotspotTranslate.X = -cursorVisual.Hotspot.X;
            _cursorHotspotTranslate.Y = -cursorVisual.Hotspot.Y;

            _cursorVisualWidth = cursorVisual.Width;
            _cursorVisualHeight = cursorVisual.Height;
            _cursorHotspotX = cursorVisual.Hotspot.X;
            _cursorHotspotY = cursorVisual.Hotspot.Y;

            if (_mouseAnimator != null)
            {
                _mouseAnimator.CursorVisualHeight = cursorVisual.Height;
                ConfigureFollowBox();
            }
        }

        /// <summary>
        /// Shows the overlay cursor at system-cursor-like size (~32px) for the "always override" mode.
        /// </summary>
        internal void ShowNaturalSize()
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(ShowNaturalSize));
                return;
            }

            _shouldBeVisible = true;
            _idleHideTimer.Stop();

            // Ensure system cursor is hidden while overlay is shown
            if (!Cursors.SystemCursorHider.IsHidden)
            {
                Cursors.SystemCursorHider.Hide();
            }

            if (!IsVisible)
            {
                Show();
            }

            ApplyWindowBounds();
            RefreshTopmost();

            // Target ~32 WPF units (like a normal system cursor), not 1:1 bitmap pixels
            var naturalScale = 32.0 / Math.Max(1, _cursorVisualHeight);

            _cursorScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            _cursorScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            _cursorScale.ScaleX = naturalScale;
            _cursorScale.ScaleY = naturalScale;
        }

        /// <summary>
        /// Hides the always-override cursor and restores system cursor.
        /// </summary>
        internal void HideNaturalSize()
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(HideNaturalSize));
                return;
            }

            _shouldBeVisible = false;
            _cursorScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            _cursorScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            _cursorScale.ScaleX = 0;
            _cursorScale.ScaleY = 0;
            Hide();
        }
        public void SetMouseShake(bool shaking, DateTime timestamp)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(() => SetMouseShake(shaking, timestamp)));
                return;
            }

            _shouldBeVisible = shaking;
            DebugLog.Write(
                "Overlay state requested: screen=" +
                _screen.Name +
                ", shaking=" +
                (shaking ? "On" : "Off") +
                ", isVisible=" +
                (IsVisible ? "On" : "Off") +
                ", animatorReady=" +
                (_mouseAnimator != null ? "On" : "Off") +
                ", systemCursorOverride=" +
                (_systemCursorOverrideActive ? "On" : "Off") +
                ", timestamp=" +
                timestamp.ToString("O", CultureInfo.InvariantCulture));

            if (shaking)
            {
                ShowOverlay();
            }
            else
            {
                ScheduleIdleHide();
            }

            if (_mouseAnimator == null)
            {
                DebugLog.Write("Overlay animator missing: screen=" + _screen.Name);
                return;
            }

            if ((shaking || AngryMouse.Properties.Settings.Default.AlwaysOverrideSystemCursor) && !_systemCursorOverrideActive)
            {
                // 50ms during shake (fast), 250ms during always-override idle (efficient)
                _topmostRefreshTimer.Interval = shaking
                    ? TimeSpan.FromMilliseconds(50)
                    : TimeSpan.FromMilliseconds(250);
                RefreshTopmost();
                if (!_topmostRefreshTimer.IsEnabled)
                {
                    _topmostRefreshTimer.Start();
                }
            }
            else if (!shaking && !AngryMouse.Properties.Settings.Default.AlwaysOverrideSystemCursor)
            {
                _topmostRefreshTimer.Stop();
            }

            _mouseAnimator.SetMouseShake(shaking, timestamp);
        }

        private void ShowOverlay()
        {
            _idleHideTimer.Stop();
            if (!IsVisible)
            {
                DebugLog.Write("Overlay show: screen=" + _screen.Name);
                Show();
            }

            // Re-assert geometry: WPF can reset window position/size across Hide/Show.
            ApplyWindowBounds();
            RefreshTopmost();
        }

        private void ScheduleIdleHide()
        {
            _idleHideTimer.Stop();
            var animationLength = CursorAnimationSettings.GetEffectiveLength();
            _idleHideTimer.Interval = TimeSpan.FromMilliseconds(animationLength + 100);
            _idleHideTimer.Start();

            if (_mouseAnimator != null)
            {
                var naturalScale = AngryMouse.Properties.Settings.Default.AlwaysOverrideSystemCursor
                    ? 32.0 / Math.Max(1, _cursorVisualHeight) : 0.0;
                _mouseAnimator.StartShrink(naturalScale);
            }
        }

        private void IdleHideTimer_Tick(object sender, EventArgs e)
        {
            _idleHideTimer.Stop();
            if (AngryMouse.Properties.Settings.Default.AlwaysOverrideSystemCursor)
            {
                // In always-override mode, never hide — the shrink already went to natural size
                return;
            }
            if (!_shouldBeVisible && IsVisible)
            {
                Hide();
            }
        }

        private void TopmostRefreshTimer_Tick(object sender, EventArgs e)
        {
            RefreshTopmost();

            // In always-override mode, respect app hide-cursor requests
            if (AngryMouse.Properties.Settings.Default.AlwaysOverrideSystemCursor)
            {
                if (IsSystemCursorHidden())
                {
                    if (IsVisible) Hide();
                }
                else if (!IsVisible && _shouldBeVisible)
                {
                    ShowNaturalSize();
                }
            }
        }

        private void RefreshTopmost()
        {
            SetTopmostNoActivate(this);
        }

        /// <summary>
        /// Sizes the small follow-window (and its canvas/viewbox) to the current cursor at full
        /// scale, anchoring the hotspot inside it. Recomputed when the cursor visual, DPI, or
        /// configured size changes. The grow/shrink animation plays within this fixed box.
        /// </summary>
        private void ConfigureFollowBox()
        {
            if (_mouseAnimator == null)
            {
                return;
            }

            var pixelsPerDip = _dpiInfo.PixelsPerDip <= 0 ? 1.0 : _dpiInfo.PixelsPerDip;
            var targetScale = CursorAnimationSettings.GetMaxScale(_cursorVisualHeight, pixelsPerDip);

            var scaledWidth = _cursorVisualWidth * targetScale;
            var scaledHeight = _cursorVisualHeight * targetScale;

            // Extra padding (20% of visual size) to prevent clipping when hotspot is near edges
            var margin = (int)Math.Ceiling(Math.Max(6 * targetScale + 2, scaledHeight * 0.2));

            _followAnchorX = (int)Math.Round(_cursorHotspotX * targetScale) + margin;
            _followAnchorY = (int)Math.Round(_cursorHotspotY * targetScale) + margin;
            _followBoxWidth = (int)Math.Ceiling(scaledWidth) + margin * 2;
            _followBoxHeight = (int)Math.Ceiling(scaledHeight) + margin * 2;

            OverlayCanvas.Width = _followBoxWidth;
            OverlayCanvas.Height = _followBoxHeight;
            Viewbox.Width = _followBoxWidth / pixelsPerDip;
            Viewbox.Height = _followBoxHeight / pixelsPerDip;

            _cursorTranslate.X = _followAnchorX;
            _cursorTranslate.Y = _followAnchorY;

            ApplyWindowBounds();
        }

        private void ApplyWindowBounds()
        {
            if (_followBoxWidth <= 0 || _followBoxHeight <= 0)
            {
                return;
            }

            SetWindowBounds(this, _lastMouseX - _followAnchorX, _lastMouseY - _followAnchorY, _followBoxWidth, _followBoxHeight);
        }

        private void ApplyWindowPosition()
        {
            SetWindowPosition(this, _lastMouseX - _followAnchorX, _lastMouseY - _followAnchorY);
        }

        internal void SetSystemCursorOverrideActive(bool active)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(() => SetSystemCursorOverrideActive(active)));
                return;
            }

            _systemCursorOverrideActive = active;
            DebugLog.Write(
                "Overlay system cursor override: screen=" +
                _screen.Name +
                ", active=" +
                (active ? "On" : "Off"));
            if (active)
            {
                _topmostRefreshTimer.Stop();
                CursorHost.Visibility = Visibility.Hidden;
            }
        }

        [DllImport("user32.dll")]
        private static extern bool GetCursorInfo(ref CURSORINFO pci);

        [StructLayout(LayoutKind.Sequential)]
        private struct CURSORINFO
        {
            public int cbSize;
            public int flags;
            public IntPtr hCursor;
            public System.Drawing.Point ptScreenPos;
        }

        private const int CURSOR_SHOWING = 0x00000001;

        private static bool IsSystemCursorHidden()
        {
            var ci = new CURSORINFO { cbSize = Marshal.SizeOf(typeof(CURSORINFO)) };
            if (!GetCursorInfo(ref ci))
                return false;
            return (ci.flags & CURSOR_SHOWING) == 0;
        }
    }
}
