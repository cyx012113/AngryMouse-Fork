using AngryMouse.Util;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AngryMouse.Cursors
{
    internal static class CursorVisualCache
    {
        private const int RuntimeTargetHeight = (int)CursorVisualLoader.BuiltInCursorHeight;
        private const int PreviewTargetHeight = 32;
        private const string CacheVersion = "v2";
        private const int MaxRuntimeCacheSize = 100;
        private const int MaxPreviewCacheSize = 100;
        private const int DiskCacheCleanupDays = 30;

        private static readonly object LockObject = new object();
        private static readonly object RuntimeLock = new object();
        private static readonly object PreviewLock = new object();
        private static readonly LinkedList<string> RuntimeCacheOrder = new LinkedList<string>();
        private static readonly Dictionary<string, CursorVisualInfo> RuntimeCache =
            new Dictionary<string, CursorVisualInfo>(StringComparer.OrdinalIgnoreCase);
        private static readonly LinkedList<string> PreviewCacheOrder = new LinkedList<string>();
        private static readonly Dictionary<string, BitmapSource> PreviewCache =
            new Dictionary<string, BitmapSource>(StringComparer.OrdinalIgnoreCase);

        static CursorVisualCache()
        {
            // Off the startup path: recursive EnumerateFiles must not block launch.
            Task.Run(() => CleanupDiskCache());
        }

        private static void CleanupDiskCache()
        {
            try
            {
                var cacheRoot = GetDiskCacheRoot();
                if (Directory.Exists(cacheRoot))
                {
                    var cutoffDate = DateTime.UtcNow.AddDays(-DiskCacheCleanupDays);
                    var files = Directory.EnumerateFiles(cacheRoot, "*.png", SearchOption.AllDirectories);

                    foreach (var file in files)
                    {
                        try
                        {
                            var lastWriteTime = File.GetLastWriteTimeUtc(file);
                            if (lastWriteTime < cutoffDate)
                            {
                                File.Delete(file);
                                // Also delete associated metadata file
                                var metadataFile = file + ".txt";
                                if (File.Exists(metadataFile))
                                {
                                    File.Delete(metadataFile);
                                }
                            }
                        }
                        catch (IOException)
                        {
                            // File might be in use, skip it
                        }
                        catch (UnauthorizedAccessException)
                        {
                            // Skip files we can't access
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                DebugLog.WriteException("Cursor disk cache cleanup failed", ex);
                // If cleanup fails, continue without it
            }
        }

        public static CursorVisualInfo GetRuntimeVisual(string roleKey)
        {
            return GetRuntimeVisual(Properties.Settings.Default.CursorCollectionName, roleKey);
        }

        public static CursorVisualInfo GetRuntimeVisual(string collectionName, string roleKey)
        {
            var role = CursorCollectionManager.GetRole(roleKey);
            var path = CursorCollectionManager.ResolveRoleFilePath(collectionName, role.Key);
            var roleSettings = CursorCollectionManager.GetRoleSettings(collectionName, role.Key);

            return GetRuntimeVisual(collectionName, roleKey, path, roleSettings);
        }

        public static CursorVisualInfo GetRuntimeVisual(
            string collectionName,
            string roleKey,
            string path,
            CursorRoleRenderSettings roleSettings)
        {
            var role = CursorCollectionManager.GetRole(roleKey);

            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                return CursorVisualLoader.BuiltIn("Cursor collection file unavailable. Using built-in cursor.");
            }

            var settings = roleSettings ?? new CursorRoleRenderSettings();
            var key = "runtime|" + collectionName + "|" + role.Key + "|" +
                      CreateVisualCacheKey(path, RuntimeTargetHeight, role.Key, settings);

            lock (RuntimeLock)
            {
                CursorVisualInfo cached;
                if (RuntimeCache.TryGetValue(key, out cached))
                {
                    // Move to end of LRU list
                    RuntimeCacheOrder.Remove(key);
                    RuntimeCacheOrder.AddLast(key);
                    return cached;
                }
            }

            try
            {
                var cachedBitmap = LoadRuntimeBitmap(path, collectionName, role, settings);
                if (cachedBitmap == null || cachedBitmap.Bitmap == null)
                {
                    return CursorVisualLoader.BuiltIn("Cursor PNG failed to load. Using built-in cursor.");
                }

                // Apply user's hotspot offset on top of the precomputed hotspot.
                Point hotspot;
                if (cachedBitmap.HasHotspot)
                {
                    hotspot = new Point(
                        cachedBitmap.HotspotX + settings.HotspotOffsetX,
                        cachedBitmap.HotspotY + settings.HotspotOffsetY);
                }
                else
                {
                    hotspot = ScaleHotspot(path, role.Hotspot, settings, cachedBitmap);
                }

                var visual = new CursorVisualInfo(
                    cachedBitmap.Bitmap,
                    hotspot,
                    "Using cursor collection: " + Path.GetFileName(path));

                lock (RuntimeLock)
                {
                    // Evict if cache is full
                    if (RuntimeCache.Count >= MaxRuntimeCacheSize)
                    {
                        var oldestKey = RuntimeCacheOrder.First.Value;
                        RuntimeCache.Remove(oldestKey);
                        RuntimeCacheOrder.RemoveFirst();
                    }

                    RuntimeCache[key] = visual;
                    RuntimeCacheOrder.AddLast(key);
                }

                return visual;
            }
            catch (Exception ex)
            {
                DebugLog.WriteException("Cursor runtime visual failed: " + path, ex);
                return CursorVisualLoader.BuiltIn("Cursor PNG failed to load. Using built-in cursor.");
            }
        }

        public static BitmapSource GetPreview(string path)
        {
            return GetPreview(path, "preview", new CursorRoleRenderSettings());
        }

        public static BitmapSource GetPreview(string collectionName, string roleKey, string path)
        {
            return GetPreview(path, roleKey, CursorCollectionManager.GetRoleSettings(collectionName, roleKey));
        }

        public static CursorVisualInfo GetPreviewVisual(
            string collectionName,
            string roleKey,
            string path,
            CursorRoleRenderSettings roleSettings)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                return CursorVisualLoader.BuiltIn("Cursor collection file unavailable. Using built-in cursor.");
            }

            var role = CursorCollectionManager.GetRole(roleKey);
            var settings = roleSettings ?? new CursorRoleRenderSettings();
            var cachedBitmap = GetCachedSvgBitmap(path, PreviewTargetHeight, role.Key, settings);
            var hotspot = ScaleHotspot(path, role.Hotspot, settings, cachedBitmap);
            return new CursorVisualInfo(cachedBitmap.Bitmap, hotspot, "Preview");
        }

        public static CursorCachedBitmap GetPreviewBitmap(string path, bool trimTransparentPadding)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                return null;
            }

            // trimTransparentPadding is ignored; kept for CursorEditorWindow compatibility.
            return GetCachedSvgBitmap(path, PreviewTargetHeight, "preview", new CursorRoleRenderSettings());
        }

        public static Point GetPreviewHotspot(
            string path,
            Point roleHotspot,
            CursorRoleRenderSettings roleSettings,
            CursorCachedBitmap cachedBitmap)
        {
            return ScaleHotspot(path, roleHotspot, roleSettings ?? new CursorRoleRenderSettings(), cachedBitmap);
        }

        public static void ClearMemory()
        {
            // Use the same locks the readers use, and clear the LRU order lists too, otherwise
            // eviction removes keys that are no longer in the dictionary and the cache can grow
            // past its cap. Called from NotifyCollectionChanged, possibly off the UI thread.
            lock (RuntimeLock)
            {
                RuntimeCache.Clear();
                RuntimeCacheOrder.Clear();
            }

            lock (PreviewLock)
            {
                PreviewCache.Clear();
                PreviewCacheOrder.Clear();
            }
        }

        private static BitmapSource GetPreview(string path, string roleKey, CursorRoleRenderSettings roleSettings)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                return null;
            }

            var key = "preview|" + CreateBitmapCacheKey(path, PreviewTargetHeight, roleSettings);

            lock (PreviewLock)
            {
                BitmapSource cached;
                if (PreviewCache.TryGetValue(key, out cached))
                {
                    // Move to end of LRU list
                    PreviewCacheOrder.Remove(key);
                    PreviewCacheOrder.AddLast(key);
                    return cached;
                }
            }

            try
            {
                var bitmap = GetCachedSvgBitmap(path, PreviewTargetHeight, roleKey, roleSettings).Bitmap;

                lock (PreviewLock)
                {
                    // Evict if cache is full
                    if (PreviewCache.Count >= MaxPreviewCacheSize)
                    {
                        var oldestKey = PreviewCacheOrder.First.Value;
                        PreviewCache.Remove(oldestKey);
                        PreviewCacheOrder.RemoveFirst();
                    }

                    PreviewCache[key] = bitmap;
                    PreviewCacheOrder.AddLast(key);
                }

                return bitmap;
            }
            catch (Exception ex)
            {
                DebugLog.WriteException("Cursor preview failed: " + path, ex);
                return null;
            }
        }

        private static CursorCachedBitmap LoadRuntimeBitmap(
            string path,
            string collectionName,
            CursorRoleDefinition role,
            CursorRoleRenderSettings roleSettings)
        {
            var bundledPng = GetBundledRenderedPng(path, collectionName, role);
            if (!string.IsNullOrWhiteSpace(bundledPng) && File.Exists(bundledPng))
            {
                var bitmap = LoadPng(bundledPng);
                // Compute hotspot for bundled PNGs too
                var hotspot = CalculatePngHotspot(bitmap, role.Key);
                return new CursorCachedBitmap(bitmap, 0, 0, bitmap.PixelWidth, bitmap.PixelHeight,
                    hotspot.X, hotspot.Y);
            }

            return GetCachedSvgBitmap(path, RuntimeTargetHeight, role.Key, roleSettings);
        }

        private static CursorCachedBitmap GetCachedSvgBitmap(
            string path,
            int targetHeight,
            string roleKey,
            CursorRoleRenderSettings roleSettings)
        {
            var cachePath = GetDiskCachePath(path, targetHeight, roleKey, roleSettings);
            var metadataPath = cachePath + ".txt";
            lock (LockObject)
            {
                if (File.Exists(cachePath) && File.Exists(metadataPath))
                {
                    CursorCachedBitmap cachedBitmap;
                    if (TryLoadCachedPng(cachePath, metadataPath, out cachedBitmap))
                    {
                        TouchCacheFile(cachePath);
                        return cachedBitmap;
                    }
                }
            }

            BitmapSource bitmap = null;
            int cropLeft = 0;
            int cropTop = 0;
            int uncroppedWidth = 0;
            int uncroppedHeight = 0;

            try
            {
                using (var stream = File.OpenRead(path))
                {
                    var decoder = BitmapDecoder.Create(
                        stream,
                        BitmapCreateOptions.PreservePixelFormat,
                        BitmapCacheOption.OnLoad);

                    var frame = decoder.Frames.FirstOrDefault();
                    if (frame == null)
                    {
                        return null;
                    }

                    if (targetHeight <= 0)
                    {
                        bitmap = frame;
                        uncroppedWidth = frame.PixelWidth;
                        uncroppedHeight = frame.PixelHeight;
                    }
                    else
                    {
                        var scale = targetHeight / (double)frame.PixelHeight;
                        var width = Math.Max(1, (int)Math.Round(frame.PixelWidth * scale));
                        var height = Math.Max(1, (int)Math.Round(frame.PixelHeight * scale));

                        bitmap = new TransformedBitmap(
                            frame,
                            new ScaleTransform(scale, scale));
                        uncroppedWidth = width;
                        uncroppedHeight = height;
                    }

                    bitmap.Freeze();
                }
            }
            catch (Exception ex)
            {
                DebugLog.WriteException("Cursor bitmap load failed: " + path, ex);
                return null;
            }

            if (bitmap == null)
            {
                return null;
            }

            lock (LockObject)
            {
                if (File.Exists(cachePath) && File.Exists(metadataPath))
                {
                    CursorCachedBitmap cachedBitmap;
                    if (TryLoadCachedPng(cachePath, metadataPath, out cachedBitmap))
                    {
                        return cachedBitmap;
                    }
                }

                SavePng(cachePath, bitmap);

                // Compute hotspot for this role on first load
                double hotspotX = 0;
                double hotspotY = 0;
                try
                {
                    var hotspot = CalculatePngHotspot(bitmap, roleKey);
                    hotspotX = hotspot.X;
                    hotspotY = hotspot.Y;
                }
                catch (Exception ex)
                {
                    DebugLog.WriteException("Hotspot calculation failed for: " + path, ex);
                }

                SaveMetadata(metadataPath, cropLeft, cropTop, uncroppedWidth, uncroppedHeight, hotspotX, hotspotY);

                return new CursorCachedBitmap(
                    bitmap,
                    cropLeft,
                    cropTop,
                    uncroppedWidth,
                    uncroppedHeight,
                    hotspotX,
                    hotspotY);
            }
        }

        private static Point ScaleHotspot(
            string path,
            Point hotspot,
            CursorRoleRenderSettings roleSettings,
            CursorCachedBitmap cachedBitmap)
        {
            // Role hotspots are normalized (0-1) fractions of the image. Scale them to the runtime
            // bitmap's pixel size, then apply the user offset which is stored in bitmap pixels.
            if (cachedBitmap == null || cachedBitmap.UncroppedWidth <= 0 || cachedBitmap.UncroppedHeight <= 0)
            {
                return hotspot;
            }

            var settings = roleSettings ?? new CursorRoleRenderSettings();
            return new Point(
                hotspot.X * cachedBitmap.UncroppedWidth + settings.HotspotOffsetX,
                hotspot.Y * cachedBitmap.UncroppedHeight + settings.HotspotOffsetY);
        }

        /// <summary>
        /// 计算 PNG 图像的 hotspot（中心点），根据18种光标类型使用不同规则：
        /// 
        /// 非透明区域中心：hand, crosshair, ibeam, no, wait, sizewe, sizenwse,
        ///                 sizenesw, sizens, sizeall, help (默认)
        /// 左上角第一个非透明像素：appstarting, arrow
        /// 最上面有效像素行的横向中心：uparrow
        /// </summary>
        private static Point CalculatePngHotspot(BitmapSource bitmap, string roleKey)
        {
            if (bitmap == null || bitmap.PixelWidth == 0 || bitmap.PixelHeight == 0)
            {
                return new Point(0, 0);
            }

            var width = bitmap.PixelWidth;
            var height = bitmap.PixelHeight;
            var stride = (width * bitmap.Format.BitsPerPixel + 7) / 8;
            var pixels = new byte[height * stride];
            bitmap.CopyPixels(pixels, stride, 0);

            var bytesPerPixel = bitmap.Format.BitsPerPixel / 8;

            // Determine the hotspot strategy based on role
            var role = (roleKey ?? string.Empty).ToLowerInvariant();

            // One-pass scan: collect bounding box, first non-transparent pixel,
            // and per-row non-transparent info
            int minX = width, minY = height, maxX = 0, maxY = 0;
            int firstX = -1, firstY = -1;
            int topRowFirstX = -1, topRowLastX = -1, topRowY = -1;
            bool foundAny = false;

            for (var y = 0; y < height; y++)
            {
                int rowFirstX = -1, rowLastX = -1;
                for (var x = 0; x < width; x++)
                {
                    var pixelIndex = y * stride + x * bytesPerPixel;
                    if (pixelIndex + 3 >= pixels.Length) continue;
                    // "有效像素" = alpha >= 128 (exclude anti-aliased semi-transparent edges)
                    if (pixels[pixelIndex + 3] < 128) continue;

                    foundAny = true;
                    if (x < minX) minX = x;
                    if (x > maxX) maxX = x;
                    if (y < minY) minY = y;
                    if (y > maxY) maxY = y;

                    if (firstX < 0)
                    {
                        firstX = x;
                        firstY = y;
                    }

                    if (rowFirstX < 0) rowFirstX = x;
                    rowLastX = x;
                }

                // Track the topmost row with non-transparent pixels
                if (rowFirstX >= 0 && topRowY < 0)
                {
                    topRowFirstX = rowFirstX;
                    topRowLastX = rowLastX;
                    topRowY = y;
                }
            }

            if (!foundAny)
            {
                return new Point(width / 2.0, height / 2.0);
            }

            // Group B: Top-left first non-transparent pixel
            if (IsTopLeftHotspotRole(role))
            {
                return new Point(firstX, firstY);
            }

            // Group C: Topmost row horizontal center
            if (role == "uparrow")
            {
                var centerX = (topRowFirstX + topRowLastX) / 2.0;
                return new Point(centerX, topRowY);
            }

            // Group A (default): Center of non-transparent bounding box
            return new Point((minX + maxX) / 2.0, (minY + maxY) / 2.0);
        }

        /// <summary>
        /// Roles that use top-left first non-transparent pixel as hotspot.
        /// </summary>
        private static bool IsTopLeftHotspotRole(string role)
        {
            switch (role)
            {
                case "appstarting":
                case "arrow":
                    return true;
                default:
                    return false;
            }
        }

        private static string GetBundledRenderedPng(
            string path,
            string collectionName,
            CursorRoleDefinition role)
        {
            if (!string.Equals(collectionName, CursorCollectionManager.BundledAdwaitaName, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            if (!string.Equals(Path.GetFileName(path), role.DefaultFileName, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            var bundledSvg = CursorCollectionManager.GetBundledCollectionFilePath(
                CursorCollectionManager.BundledAdwaitaName,
                role.DefaultFileName);
            if (!File.Exists(bundledSvg) ||
                new FileInfo(path).Length != new FileInfo(bundledSvg).Length)
            {
                return null;
            }

            return CursorCollectionManager.GetBundledRenderedPngPath(role.DefaultFileName);
        }

        private static CursorCachedBitmap LoadCachedPng(string cachePath, string metadataPath)
        {
            var bitmap = LoadPng(cachePath);
            var metadata = LoadMetadata(metadataPath, bitmap.PixelWidth, bitmap.PixelHeight);
            return new CursorCachedBitmap(
                bitmap,
                metadata.CropLeft,
                metadata.CropTop,
                metadata.UncroppedWidth,
                metadata.UncroppedHeight,
                metadata.HotspotX,
                metadata.HotspotY);
        }

        private static bool TryLoadCachedPng(
            string cachePath,
            string metadataPath,
            out CursorCachedBitmap cachedBitmap)
        {
            try
            {
                cachedBitmap = LoadCachedPng(cachePath, metadataPath);
                return true;
            }
            catch (Exception ex) when (
                ex is IOException ||
                ex is UnauthorizedAccessException ||
                ex is InvalidOperationException ||
                ex is ArgumentException ||
                ex is FormatException ||
                ex is NotSupportedException)
            {
                DebugLog.WriteException("Corrupt cursor disk cache removed: " + cachePath, ex);
                cachedBitmap = null;
                try
                {
                    File.Delete(cachePath);
                    File.Delete(metadataPath);
                }
                catch (Exception deleteException) when (
                    deleteException is IOException ||
                    deleteException is UnauthorizedAccessException)
                {
                    DebugLog.WriteException("Cursor disk cache removal failed: " + cachePath, deleteException);
                }

                return false;
            }
        }

        private static void TouchCacheFile(string cachePath)
        {
            // The disk-cache cleanup evicts by last-write time. Touch on every hit so cursors in
            // active use are not deleted after DiskCacheCleanupDays.
            try
            {
                File.SetLastWriteTimeUtc(cachePath, DateTime.UtcNow);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        private static BitmapSource LoadPng(string path)
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.UriSource = new Uri(path, UriKind.Absolute);
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }

        private static void SavePng(string cachePath, BitmapSource bitmap)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(cachePath));

            var tempPath = cachePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));

                using (var stream = File.Create(tempPath))
                {
                    encoder.Save(stream);
                }

                if (File.Exists(cachePath))
                {
                    File.Delete(cachePath);
                }

                File.Move(tempPath, cachePath);
            }
            finally
            {
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }
            }
        }

        private static void SaveMetadata(
            string metadataPath,
            int cropLeft,
            int cropTop,
            int uncroppedWidth,
            int uncroppedHeight,
            double hotspotX = 0,
            double hotspotY = 0)
        {
            var lines = new[]
            {
                "cropLeft=" + cropLeft.ToString(CultureInfo.InvariantCulture),
                "cropTop=" + cropTop.ToString(CultureInfo.InvariantCulture),
                "uncroppedWidth=" + uncroppedWidth.ToString(CultureInfo.InvariantCulture),
                "uncroppedHeight=" + uncroppedHeight.ToString(CultureInfo.InvariantCulture),
                "hotspotX=" + hotspotX.ToString("R", CultureInfo.InvariantCulture),
                "hotspotY=" + hotspotY.ToString("R", CultureInfo.InvariantCulture)
            };

            File.WriteAllLines(metadataPath, lines);
        }

        private static CacheMetadata LoadMetadata(string metadataPath, int fallbackWidth, int fallbackHeight)
        {
            var metadata = new CacheMetadata
            {
                CropLeft = 0,
                CropTop = 0,
                UncroppedWidth = fallbackWidth,
                UncroppedHeight = fallbackHeight,
                HotspotX = 0,
                HotspotY = 0
            };

            foreach (var line in File.ReadAllLines(metadataPath))
            {
                var separatorIndex = line.IndexOf('=');
                if (separatorIndex <= 0)
                {
                    continue;
                }

                var key = line.Substring(0, separatorIndex).Trim();
                var value = line.Substring(separatorIndex + 1).Trim();

                if (string.Equals(key, "cropLeft", StringComparison.OrdinalIgnoreCase))
                {
                    int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out metadata.CropLeft);
                }
                else if (string.Equals(key, "cropTop", StringComparison.OrdinalIgnoreCase))
                {
                    int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out metadata.CropTop);
                }
                else if (string.Equals(key, "uncroppedWidth", StringComparison.OrdinalIgnoreCase))
                {
                    int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out metadata.UncroppedWidth);
                }
                else if (string.Equals(key, "uncroppedHeight", StringComparison.OrdinalIgnoreCase))
                {
                    int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out metadata.UncroppedHeight);
                }
                else if (string.Equals(key, "hotspotX", StringComparison.OrdinalIgnoreCase))
                {
                    double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out metadata.HotspotX);
                }
                else if (string.Equals(key, "hotspotY", StringComparison.OrdinalIgnoreCase))
                {
                    double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out metadata.HotspotY);
                }
            }

            return metadata;
        }

        private static string GetDiskCachePath(
            string path,
            int targetHeight,
            string roleKey,
            CursorRoleRenderSettings roleSettings)
        {
            return Path.Combine(GetDiskCacheRoot(), CreateBitmapCacheKey(path, targetHeight, roleSettings) + "_" + (roleKey ?? "generic") + ".png");
        }

        private static string GetDiskCacheRoot()
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "AngryMouse",
                "CursorBitmapCache",
                CacheVersion);
        }

        private static string CreateBitmapCacheKey(
            string path,
            int targetHeight,
            CursorRoleRenderSettings roleSettings)
        {
            var info = new FileInfo(path);
            var settings = roleSettings ?? new CursorRoleRenderSettings();
            var rawKey = string.Join(
                "|",
                Path.GetFullPath(path).ToLowerInvariant(),
                info.Length.ToString(CultureInfo.InvariantCulture),
                info.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture),
                targetHeight.ToString(CultureInfo.InvariantCulture));

            return ComputeHash(rawKey);
        }

        private static string CreateVisualCacheKey(
            string path,
            int targetHeight,
            string roleKey,
            CursorRoleRenderSettings roleSettings)
        {
            var info = new FileInfo(path);
            var settings = roleSettings ?? new CursorRoleRenderSettings();
            var rawKey = string.Join(
                "|",
                Path.GetFullPath(path).ToLowerInvariant(),
                info.Length.ToString(CultureInfo.InvariantCulture),
                info.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture),
                targetHeight.ToString(CultureInfo.InvariantCulture),
                roleKey ?? string.Empty,
                settings.HotspotOffsetX.ToString("R", CultureInfo.InvariantCulture),
                settings.HotspotOffsetY.ToString("R", CultureInfo.InvariantCulture));

            // In-memory key: use the composed string directly. No SHA256 on every lookup
            // (cleared on collection change via NotifyCollectionChanged), unlike the disk key.
            return rawKey;
        }

        private static string ComputeHash(string rawKey)
        {
            using (var sha256 = SHA256.Create())
            {
                var hash = sha256.ComputeHash(Encoding.UTF8.GetBytes(rawKey));
                return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
            }
        }

        private struct CacheMetadata
        {
            public int CropLeft;
            public int CropTop;
            public int UncroppedWidth;
            public int UncroppedHeight;
            public double HotspotX;
            public double HotspotY;
        }
    }
}
