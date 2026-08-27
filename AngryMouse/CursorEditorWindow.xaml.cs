using AngryMouse.Cursors;
using AngryMouse.Util;
using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace AngryMouse
{
    public partial class CursorEditorWindow
    {
        private readonly string _collectionName;
        private readonly string _roleKey;
        private readonly CursorRoleRenderSettings _originalSettings;
        private CursorRoleRenderSettings _currentSettings;
        private bool _loading;
        private Image _cursorImage;
        private Line _hotspotLineH;
        private Line _hotspotLineV;

        public CursorEditorWindow(string collectionName, string roleKey)
        {
            InitializeComponent();
            _collectionName = collectionName;
            _roleKey = roleKey;
            _originalSettings = CursorCollectionManager.GetRoleSettings(collectionName, roleKey)
                ?? new CursorRoleRenderSettings();
            _currentSettings = new CursorRoleRenderSettings(
                _originalSettings.HotspotOffsetX,
                _originalSettings.HotspotOffsetY);

            LoadCursorPreview();
            LoadSettingsToControls();
            UpdateInfoText();
        }

        private void LoadCursorPreview()
        {
            try
            {
                var role = CursorCollectionManager.GetRole(_roleKey);
                var path = CursorCollectionManager.ResolveRoleFilePath(_collectionName, role.Key);

                if (string.IsNullOrWhiteSpace(path) || !System.IO.File.Exists(path))
                {
                    return;
                }

                var previewVisual = CursorVisualCache.GetPreviewVisual(
                    _collectionName, _roleKey, path, _currentSettings);

                if (previewVisual == null || previewVisual.Bitmap == null)
                {
                    return;
                }

                // Display the cursor bitmap
                _cursorImage = new Image
                {
                    Source = previewVisual.Bitmap,
                    Stretch = Stretch.None
                };
                Canvas.SetLeft(_cursorImage, 20);
                Canvas.SetTop(_cursorImage, 20);
                CursorCanvas.Children.Clear();
                CursorCanvas.Children.Add(_cursorImage);

                // Draw hotspot crosshair
                DrawHotspotCrosshair(previewVisual.Hotspot);
            }
            catch (Exception ex)
            {
                DebugLog.WriteException("Cursor editor preview failed", ex);
            }
        }

        private void DrawHotspotCrosshair(Point hotspot)
        {
            HotspotOverlay.Children.Clear();

            var crossSize = 8;
            var color = new SolidColorBrush(Colors.Red);
            color.Freeze();

            // Horizontal line
            var x = 20 + hotspot.X;
            var y = 20 + hotspot.Y;

            _hotspotLineH = new Line
            {
                X1 = x - crossSize,
                Y1 = y,
                X2 = x + crossSize,
                Y2 = y,
                Stroke = color,
                StrokeThickness = 1,
                SnapsToDevicePixels = true
            };
            HotspotOverlay.Children.Add(_hotspotLineH);

            _hotspotLineV = new Line
            {
                X1 = x,
                Y1 = y - crossSize,
                X2 = x,
                Y2 = y + crossSize,
                Stroke = color,
                StrokeThickness = 1,
                SnapsToDevicePixels = true
            };
            HotspotOverlay.Children.Add(_hotspotLineV);
        }

        private void LoadSettingsToControls()
        {
            _loading = true;
            HotspotXSlider.Value = _currentSettings.HotspotOffsetX;
            HotspotXTextBox.Text = _currentSettings.HotspotOffsetX.ToString(CultureInfo.InvariantCulture);
            HotspotYSlider.Value = _currentSettings.HotspotOffsetY;
            HotspotYTextBox.Text = _currentSettings.HotspotOffsetY.ToString(CultureInfo.InvariantCulture);
            _loading = false;
        }

        private void UpdateInfoText()
        {
            var role = CursorCollectionManager.GetRole(_roleKey);
            InfoTextBlock.Text = string.Format(CultureInfo.InvariantCulture,
                "Role: {0}\nCollection: {1}\nHotspot X offset: {2} px\nHotspot Y offset: {3} px\nDefault hotspot: ({4:F1}, {5:F1})",
                _roleKey,
                _collectionName,
                _currentSettings.HotspotOffsetX,
                _currentSettings.HotspotOffsetY,
                role.Hotspot.X,
                role.Hotspot.Y);
        }

        private void HotspotSlider_OnValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_loading) return;
            _currentSettings = new CursorRoleRenderSettings(
                (int)HotspotXSlider.Value,
                (int)HotspotYSlider.Value);
            LoadSettingsToControls();
            LoadCursorPreview();
            UpdateInfoText();
        }

        private void HotspotTextBox_OnTextChanged(object sender, TextChangedEventArgs e)
        {
            if (_loading) return;

            if (sender == HotspotXTextBox && int.TryParse(HotspotXTextBox.Text, out var x))
            {
                x = Math.Max(-100, Math.Min(100, x));
                HotspotXSlider.Value = x;
            }
            else if (sender == HotspotYTextBox && int.TryParse(HotspotYTextBox.Text, out var y))
            {
                y = Math.Max(-100, Math.Min(100, y));
                HotspotYSlider.Value = y;
            }
        }

        private void ResetButton_OnClick(object sender, RoutedEventArgs e)
        {
            _currentSettings = new CursorRoleRenderSettings(0, 0);
            LoadSettingsToControls();
            LoadCursorPreview();
            UpdateInfoText();
        }

        private void ApplyButton_OnClick(object sender, RoutedEventArgs e)
        {
            CursorCollectionManager.SaveRoleSettings(
                _collectionName, _roleKey, _currentSettings);
            DebugLog.Write("Cursor editor applied: " + _roleKey +
                ", hotspot=(" + _currentSettings.HotspotOffsetX + "," + _currentSettings.HotspotOffsetY + ")");
            Close();
        }

        private void CancelButton_OnClick(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private void Window_Closed(object sender, EventArgs e)
        {
            _cursorImage = null;
            HotspotOverlay.Children.Clear();
            CursorCanvas.Children.Clear();
        }
    }
}
