using DeepSeek_v4_for_VisualStudio.Utils;
using System;
using System.Collections.Concurrent;
using System.IO;
using System.Text;
using System.Threading;

namespace DeepSeek_v4_for_VisualStudio.Services
{
    /// <summary>
    /// 开发者调试用的 API 报文转储。
    ///
    /// 开启后把每次 API 调用的完整请求体与完整响应（原始 SSE 事件流）落到磁盘，
    /// 供离线分析缓存命中、工具调用、消息清洗结果等。默认关闭：
    /// 报文包含完整 prompt / 代码，体积大且属于敏感内容，只应由开发者按需开启。
    ///
    /// 目录结构（每次调用一对文件）：
    ///   %LocalAppData%\DeepSeekVS\api-dump\{yyyyMMdd}\{HHmmssfff}-{n}-{provider}-request.json
    ///   %LocalAppData%\DeepSeekVS\api-dump\{yyyyMMdd}\{HHmmssfff}-{n}-{provider}-response.log
    /// 请求文件是可直接用 JSON 工具打开的完整请求体；响应文件是逐行原始 SSE `data:` 载荷，
    /// 末尾追加一行人类可读的 summary（usage / finish_reason / 耗时）。
    /// </summary>
    public static class ApiDebugDumpService
    {
        private const string DumpFolderName = "api-dump";

        /// <summary>保留天数，超过后在写入时清理，避免调试开关忘关把磁盘写满。</summary>
        private const int RetentionDays = 7;

        /// <summary>单次调用的转储分配结果；未开启时 <see cref="Enabled"/> 为 false。</summary>
        public readonly struct DumpScope
        {
            internal DumpScope(bool enabled, string? requestPath, string? responsePath)
            {
                Enabled = enabled;
                RequestPath = requestPath;
                ResponsePath = responsePath;
            }

            /// <summary>是否处于转储状态。</summary>
            public bool Enabled { get; }

            /// <summary>本次调用的请求体文件路径（未开启时为 null）。</summary>
            public string? RequestPath { get; }

            /// <summary>本次调用的响应流文件路径（未开启时为 null）。</summary>
            public string? ResponsePath { get; }
        }

        private static readonly object _initLock = new();
        private static int _sequence;
        private static DateTime _lastCleanupDate = DateTime.MinValue;

        private static readonly string DefaultDumpRoot =
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "DeepSeekVS",
                DumpFolderName);

        private static string? _rootOverride;

        /// <summary>
        /// 转储根目录覆盖（仅测试用；生产环境使用 %LocalAppData%\DeepSeekVS\api-dump）。
        /// 置为 null 恢复默认目录。
        /// </summary>
        internal static string? RootOverride
        {
            get => _rootOverride;
            set => _rootOverride = string.IsNullOrWhiteSpace(value) ? null : value;
        }

        private static string DumpRoot => _rootOverride ?? DefaultDumpRoot;

        /// <summary>
        /// 运行期开关。由 <see cref="Settings.DeepSeekOptionsPage"/> 在设置热更新时同步，
        /// 使 API 调用路径不必每次读设置对象。
        /// </summary>
        private static volatile bool _enabled;

        /// <summary>是否已开启 API 报文转储。</summary>
        public static bool Enabled
        {
            get => _enabled;
            set
            {
                if (_enabled == value) return;
                _enabled = value;
                Logger.Info(value
                    ? $"[ApiDump] 已开启 API 报文转储，输出目录: {DumpRoot}"
                    : "[ApiDump] 已关闭 API 报文转储");
            }
        }

        /// <summary>转储根目录（供设置页 / 文档展示路径）。</summary>
        public static string RootDirectory => DumpRoot;

        /// <summary>
        /// 为一次 API 调用分配转储文件。未开启时返回 <see cref="DumpScope.Enabled"/> = false，
        /// 调用方据此跳过所有转储写入，零额外开销。
        /// </summary>
        public static DumpScope BeginCall(string providerName)
        {
            if (!_enabled)
                return new DumpScope(false, null, null);

            try
            {
                EnsureDirectory();

                var now = DateTime.Now;
                int seq = Interlocked.Increment(ref _sequence);
                string safeProvider = SanitizeProviderName(providerName);
                string baseName = $"{now:HHmmssfff}-{seq:D4}-{safeProvider}";
                string dayDir = Path.Combine(DumpRoot, now.ToString("yyyyMMdd"));
                Directory.CreateDirectory(dayDir);

                return new DumpScope(
                    true,
                    Path.Combine(dayDir, baseName + "-request.json"),
                    Path.Combine(dayDir, baseName + "-response.log"));
            }
            catch (Exception ex)
            {
                // 转储失败绝不能影响 API 调用本身。
                Logger.Warn($"[ApiDump] 分配转储文件失败，本次调用不转储: {ex.Message}");
                return new DumpScope(false, null, null);
            }
        }

        /// <summary>写入完整请求体（整个调用只写一次，直接落盘为可解析的 JSON）。</summary>
        public static void WriteRequest(DumpScope scope, string requestJson)
        {
            if (!scope.Enabled || string.IsNullOrEmpty(scope.RequestPath)) return;

            try
            {
                // 每个 DumpScope 的文件路径唯一，单次调用内只有一个写入者（发起请求的线程 /
                // 消费 SSE 的线程），因此不需要跨调用共享锁，也不会在长会话里累积锁对象。
                File.WriteAllText(scope.RequestPath!, requestJson ?? string.Empty, Encoding.UTF8);
            }
            catch (Exception ex)
            {
                Logger.Warn($"[ApiDump] 写入请求体失败: {ex.Message}");
            }
        }

        /// <summary>追加一条原始响应行（逐行 SSE 事件），带毫秒时间戳便于分析流式节奏。</summary>
        public static void AppendResponseLine(DumpScope scope, string line)
        {
            AppendResponseText(scope, $"{DateTime.Now:HH:mm:ss.fff} {line}");
        }

        /// <summary>在响应文件末尾追加一次人类可读的调用摘要（usage / finish_reason / 耗时）。</summary>
        public static void WriteSummary(DumpScope scope, string summary)
        {
            AppendResponseText(scope, $"\n===== SUMMARY =====\n{summary}\n===================");
        }

        private static void AppendResponseText(DumpScope scope, string text)
        {
            if (!scope.Enabled || string.IsNullOrEmpty(scope.ResponsePath)) return;

            try
            {
                // 单次调用的响应流只有一个消费线程顺序写入，且文件路径在各调用间唯一，
                // 因此无需加锁，也不会在长会话里累积锁对象。
                File.AppendAllText(scope.ResponsePath!, text + Environment.NewLine, Encoding.UTF8);
            }
            catch (Exception ex)
            {
                Logger.Warn($"[ApiDump] 写入响应流失败: {ex.Message}");
            }
        }

        private static string SanitizeProviderName(string? provider)
        {
            if (string.IsNullOrWhiteSpace(provider))
                return "provider";

            var sb = new StringBuilder(provider.Length);
            foreach (char c in provider)
            {
                if (char.IsLetterOrDigit(c) || c == '-' || c == '_')
                    sb.Append(c);
            }
            return sb.Length > 0 ? sb.ToString() : "provider";
        }

        private static void EnsureDirectory()
        {
            Directory.CreateDirectory(DumpRoot);
            CleanupOldDumpsIfNeeded();
        }

        private static void CleanupOldDumpsIfNeeded()
        {
            var today = DateTime.Today;
            if (_lastCleanupDate >= today) return;

            lock (_initLock)
            {
                if (_lastCleanupDate >= today) return;
                _lastCleanupDate = today;
            }

            try
            {
                var cutoff = today.AddDays(-RetentionDays);
                foreach (var dir in Directory.GetDirectories(DumpRoot))
                {
                    try
                    {
                        var info = new DirectoryInfo(dir);
                        // 目录名是 yyyyMMdd；解析失败时退回 LastWriteTime 判断。
                        bool stale = DateTime.TryParseExact(
                                info.Name,
                                "yyyyMMdd",
                                System.Globalization.CultureInfo.InvariantCulture,
                                System.Globalization.DateTimeStyles.None,
                                out var day)
                            ? day < cutoff
                            : info.LastWriteTime < cutoff;

                        if (stale)
                            info.Delete(recursive: true);
                    }
                    catch
                    {
                        // 单个目录清理失败不影响其它目录
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Warn($"[ApiDump] 清理过期转储失败: {ex.Message}");
            }
        }
    }
}
