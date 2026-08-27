using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AngryMouse.HotspotDetection
{
    /// <summary>
    /// 鼠标贴图焦点（热点）智能识别服务。
    /// 优先调用 Python 检测器（OpenCV / scikit-image 等第三方库，跨语言进程调用），
    /// 当 Python 不可用时回退到内置的 C# 启发式分析，保证功能在任意环境均可工作。
    /// </summary>
    public static class HotspotDetectionService
    {
        private const int PythonTimeoutMs = 20000;

        // 254 为 AngryMouse 运行期光标缩放高度（RuntimeTargetHeight）。
        public const double RuntimeTargetHeight = 254.0;

        private static readonly Regex JsonNumber = new Regex(
            "\"(?<key>[A-Za-z_]+)\"\\s*:\\s*(?<val>-?\\d+(?:\\.\\d+)?)",
            RegexOptions.Compiled);

        /// <summary>
        /// 对给定 PNG 进行智能焦点识别。
        /// </summary>
        /// <param name="pngPath">光标 PNG 路径（需含 alpha 通道）。</param>
        /// <param name="roleKey">光标角色键（如 arrow / hand / ibeam）。已知角色优先采用
        /// 约定热点，未知角色回退几何法。可为 null。</param>
        /// <returns>识别结果；Success=false 时 Error 包含原因。</returns>
        public static HotspotDetectionResult Detect(string pngPath, string roleKey = null)
        {
            if (string.IsNullOrWhiteSpace(pngPath) || !File.Exists(pngPath))
            {
                return new HotspotDetectionResult { Success = false, Error = "文件不存在：" + pngPath };
            }

            var pythonResult = RunPython(pngPath, roleKey);
            if (pythonResult != null && pythonResult.Success)
            {
                return pythonResult;
            }

            return RunCSharpFallback(pngPath);
        }

        private static HotspotDetectionResult RunPython(string pngPath, string roleKey)
        {
            var python = ResolvePython();
            var script = ResolveScript();
            if (python == null || script == null)
            {
                return null;
            }

            var arguments = "\"" + script + "\" \"" + pngPath + "\" --target-height " +
                            ((int)RuntimeTargetHeight).ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (!string.IsNullOrWhiteSpace(roleKey))
            {
                arguments += " --role \"" + roleKey.Replace("\"", string.Empty) + "\"";
            }

            var psi = new ProcessStartInfo(python, arguments)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8,
                StandardErrorEncoding = System.Text.Encoding.UTF8,
            };

            try
            {
                using (var process = Process.Start(psi))
                {
                    if (process == null)
                    {
                        return null;
                    }

                    var stdout = process.StandardOutput.ReadToEnd();
                    var stderr = process.StandardError.ReadToEnd();
                    var exited = process.WaitForExit(PythonTimeoutMs);
                    if (!exited)
                    {
                        try { process.Kill(); } catch { }
                        return null;
                    }

                    if (process.ExitCode != 0)
                    {
                        return new HotspotDetectionResult
                        {
                            Success = false,
                            Error = "Python 退出码 " + process.ExitCode + "：" + stderr.Trim()
                        };
                    }

                    var result = ParseJson(stdout);
                    if (result != null && !result.Success && !string.IsNullOrEmpty(result.Error))
                    {
                        return result;
                    }

                    return result;
                }
            }
            catch (Exception ex)
            {
                return new HotspotDetectionResult { Success = false, Error = "Python 调用异常：" + ex.Message };
            }
        }

        private static HotspotDetectionResult ParseJson(string stdout)
        {
            if (string.IsNullOrWhiteSpace(stdout))
            {
                return new HotspotDetectionResult { Success = false, Error = "Python 无输出" };
            }

            var result = new HotspotDetectionResult { Success = true, Method = "python" };
            var captured = false;

            foreach (Match match in JsonNumber.Matches(stdout))
            {
                var key = match.Groups["key"].Value;
                var raw = match.Groups["val"].Value;
                double value;
                if (!double.TryParse(raw, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out value))
                {
                    continue;
                }

                switch (key)
                {
                    case "nx":
                        result.NormalizedX = value;
                        captured = true;
                        break;
                    case "ny":
                        result.NormalizedY = value;
                        captured = true;
                        break;
                    case "sx":
                        result.HotspotX254 = value;
                        break;
                    case "sy":
                        result.HotspotY254 = value;
                        break;
                    case "confidence":
                        result.Confidence = value;
                        break;
                }
            }

            // error 字段（字符串）优先
            var errMatch = Regex.Match(stdout, "\"error\"\\s*:\\s*\"(?<msg>[^\"]*)\"");
            if (errMatch.Success)
            {
                return new HotspotDetectionResult { Success = false, Error = errMatch.Groups["msg"].Value };
            }

            if (!captured)
            {
                return new HotspotDetectionResult { Success = false, Error = "无法解析 Python 输出" };
            }

            // 若未提供 sx/sy，用归一化值换算
            if (result.HotspotX254 == 0 && result.HotspotY254 == 0)
            {
                result.HotspotX254 = result.NormalizedX * RuntimeTargetHeight;
                result.HotspotY254 = result.NormalizedY * RuntimeTargetHeight;
            }

            return result;
        }

        private static string ResolvePython()
        {
            // 1. 设置项（若存在）：Properties.Settings.Default.PythonRuntimePath
            try
            {
                var settings = Properties.Settings.Default;
                var prop = settings.GetType().GetProperty("PythonRuntimePath", BindingFlags.Public | BindingFlags.Instance);
                if (prop != null && prop.PropertyType == typeof(string))
                {
                    var value = prop.GetValue(settings) as string;
                    if (!string.IsNullOrWhiteSpace(value) && File.Exists(value))
                    {
                        return value;
                    }
                }
            }
            catch
            {
                // 忽略：设置项不存在或读取失败
            }

            // 2. PATH 上的 python / python3
            foreach (var candidate in new[] { "python", "python3" })
            {
                if (IsCommandAvailable(candidate))
                {
                    return candidate;
                }
            }

            return null;
        }

        private static bool IsCommandAvailable(string command)
        {
            var psi = new ProcessStartInfo(command, "--version")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            try
            {
                using (var process = Process.Start(psi))
                {
                    if (process == null)
                    {
                        return false;
                    }

                    process.WaitForExit(5000);
                    return true;
                }
            }
            catch
            {
                return false;
            }
        }

        private static string ResolveScript()
        {
            var baseDir = AppDomain.CurrentDomain.BaseDirectory;
            var candidates = new[]
            {
                Path.Combine(baseDir, "HotspotDetector", "hotspot_detector.py"),
                Path.Combine(baseDir, "Tools", "HotspotDetector", "hotspot_detector.py"),
                Path.Combine(baseDir, "..", "Tools", "HotspotDetector", "hotspot_detector.py"),
            };

            foreach (var candidate in candidates)
            {
                var full = Path.GetFullPath(candidate);
                if (File.Exists(full))
                {
                    return full;
                }
            }

            return null;
        }

        /// <summary>
        /// C# 内置回退：基于 PNG alpha 掩码计算热点。
        /// 对称形状（圆/十字/工字）取掩码质心；明显非对称（如箭头）取包围盒左上角作为尖端近似。
        /// </summary>
        private static HotspotDetectionResult RunCSharpFallback(string pngPath)
        {
            try
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.UriSource = new Uri(pngPath, UriKind.Absolute);
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.EndInit();
                if (bitmap.CanFreeze)
                {
                    bitmap.Freeze();
                }

                var width = bitmap.PixelWidth;
                var height = bitmap.PixelHeight;
                var converted = new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
                var stride = ((width * 4) + 3) & ~3;
                var pixels = new byte[stride * height];
                converted.CopyPixels(pixels, stride, 0);

                long sumX = 0, sumY = 0, count = 0;
                double minX = width, minY = height, maxX = 0, maxY = 0;
                for (var y = 0; y < height; y++)
                {
                    for (var x = 0; x < width; x++)
                    {
                        var alpha = pixels[(y * stride) + (x * 4) + 3];
                        if (alpha < 128)
                        {
                            continue;
                        }

                        sumX += x;
                        sumY += y;
                        count++;
                        if (x < minX) minX = x;
                        if (x > maxX) maxX = x;
                        if (y < minY) minY = y;
                        if (y > maxY) maxY = y;
                    }
                }

                if (count == 0)
                {
                    return new HotspotDetectionResult { Success = false, Error = "图像无有效像素" };
                }

                double centroidX = sumX / (double)count;
                double centroidY = sumY / (double)count;
                var bboxCx = (minX + maxX) / 2.0;
                var bboxCy = (minY + maxY) / 2.0;
                var symmetric = Math.Abs(bboxCx - centroidX) < (width * 0.18) &&
                                Math.Abs(bboxCy - centroidY) < (height * 0.18);

                double nx, ny;
                if (symmetric)
                {
                    nx = centroidX / Math.Max(1, width - 1);
                    ny = centroidY / Math.Max(1, height - 1);
                }
                else
                {
                    // 非对称：尖端通常位于包围盒最靠左上（最小 x+y）的角
                    nx = minX / Math.Max(1, width - 1);
                    ny = minY / Math.Max(1, height - 1);
                }

                return new HotspotDetectionResult
                {
                    Success = true,
                    NormalizedX = nx,
                    NormalizedY = ny,
                    HotspotX254 = nx * RuntimeTargetHeight,
                    HotspotY254 = ny * RuntimeTargetHeight,
                    Method = "csharp-fallback",
                    Confidence = symmetric ? 0.6 : 0.45,
                };
            }
            catch (Exception ex)
            {
                return new HotspotDetectionResult { Success = false, Error = "C# 回退异常：" + ex.Message };
            }
        }
    }

    public class HotspotDetectionResult
    {
        public bool Success { get; set; }
        public double NormalizedX { get; set; }
        public double NormalizedY { get; set; }

        /// <summary>254 空间（运行期缩放高度）下的热点像素坐标。</summary>
        public double HotspotX254 { get; set; }
        public double HotspotY254 { get; set; }
        public string Method { get; set; }
        public double Confidence { get; set; }
        public string Error { get; set; }
    }
}
