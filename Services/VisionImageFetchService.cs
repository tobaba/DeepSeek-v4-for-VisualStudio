using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using DeepSeek_v4_for_VisualStudio.Utils;

namespace DeepSeek_v4_for_VisualStudio.Services
{
    /// <summary>
    /// 视觉图片获取服务 —— 把网页图片的远程 URL 下载为 data URI。
    ///
    /// 背景（线上事故）：fetch_webpage 会把网页图片的**远程 URL** 原样作为
    /// image_url 直传给模型，由**服务端**自行下载。服务端的网络/代理环境与客户端
    /// 不一致，一旦它取不到图就返回
    ///   HTTP 400 "An exception occurred while loading IMAGE data at index N ..."
    /// 且 400 属客户端错误、不重试，整轮对话直接失败。
    ///
    /// 因此改为**客户端下载 → 转 base64 data URI** 再上传：
    /// 服务端不再需要访问外网取图，从根上消除该类 400。
    /// 下载失败（超时/非 2xx/代理不可达/格式不支持）时**丢弃该图**，
    /// 绝不让一张配图打断整轮对话。
    /// </summary>
    public static class VisionImageFetchService
    {
        /// <summary>单张图片下载超时。</summary>
        private static readonly TimeSpan PerImageTimeout = TimeSpan.FromSeconds(10);

        /// <summary>单次 fetch_webpage 内所有图片下载的总预算（避免多图串行拖慢整轮回复）。</summary>
        private static readonly TimeSpan TotalDownloadBudget = TimeSpan.FromSeconds(20);

        /// <summary>单张图片体积上限（base64 后约 +33%）。超过则丢弃，防止撑爆上下文与请求体。</summary>
        public const int MaxImageBytes = 4 * 1024 * 1024;

        /// <summary>
        /// 单批图片的**总**字节上限。
        /// 图片以 base64 内联进请求体，且该工具结果会在后续每一轮工具循环中被重复发送，
        /// 因此必须限制单批总量，否则一张大图会让每轮请求体成倍膨胀。
        /// </summary>
        public const long MaxTotalBytes = 8 * 1024 * 1024;

        /// <summary>一次工具结果最多直传的图片数（与 WebSearchService.MaxImagesPerPage 对齐）。</summary>
        public const int MaxImages = 8;

        /// <summary>
        /// 视觉模型支持的图片 MIME。以**响应内容嗅探**为准，而非 URL 扩展名——
        /// 网页图床常用无扩展名或伪装扩展名的地址，按扩展名预过滤既会误杀也会漏放。
        /// </summary>
        private static readonly Dictionary<string, string> SupportedMimeToExtension =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["image/png"] = ".png",
                ["image/jpeg"] = ".jpg",
                ["image/jpg"] = ".jpg",
                ["image/gif"] = ".gif",
                ["image/webp"] = ".webp",
            };

        /// <summary>
        /// 下载远程图片并转为 data URI 列表。
        /// 逐张下载，任一环节失败即跳过该图；返回结果可能为空（调用方据此不附加图片块）。
        /// </summary>
        /// <param name="urls">远程图片 URL（可含 data URI，将原样保留）。</param>
        /// <param name="httpClient">复用的 HttpClient；为 null 时使用内部默认实例。</param>
        /// <param name="batchToken">本次下载批次令牌，用于「传输成功后删除图片」的登记与清理。</param>
        /// <param name="cancellationToken">取消令牌。</param>
        public static async Task<List<string>> ToDataUrisAsync(
            IEnumerable<string>? urls,
            HttpClient? httpClient = null,
            string? batchToken = null,
            CancellationToken cancellationToken = default)
        {
            var result = new List<string>();
            if (urls == null)
                return result;

            var deadline = DateTime.UtcNow + TotalDownloadBudget;
            var client = httpClient ?? SharedHttpClient;
            long totalBytes = 0;

            foreach (string url in urls)
            {
                if (result.Count >= MaxImages)
                    break;

                if (string.IsNullOrWhiteSpace(url))
                    continue;

                // 已是 data URI：直接使用，无需下载。
                if (url.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase))
                {
                    result.Add(url);
                    continue;
                }

                if (DateTime.UtcNow >= deadline)
                {
                    Logger.Warn($"[VisionImage] 下载总预算已用尽，剩余图片不再尝试（已成功 {result.Count} 张）");
                    break;
                }

                if (totalBytes >= MaxTotalBytes)
                {
                    Logger.Warn($"[VisionImage] 单批图片总量已达上限（{MaxTotalBytes / 1024 / 1024}MB），" +
                                $"剩余图片不再下载（已成功 {result.Count} 张）");
                    break;
                }

                var downloaded = await TryDownloadAsDataUriAsync(client, url, batchToken, cancellationToken)
                    .ConfigureAwait(false);
                if (downloaded is { } ok)
                {
                    result.Add(ok.DataUri);
                    totalBytes += ok.ByteCount;
                }
            }

            return result;
        }

        /// <summary>
        /// 下载单张图片并转 data URI。任何失败都返回 null（丢弃该图），不抛出。
        /// 返回值同时携带原始字节数，供调用方累计单批总量。
        /// </summary>
        private static async Task<(string DataUri, int ByteCount)?> TryDownloadAsDataUriAsync(
            HttpClient client,
            string url,
            string? batchToken,
            CancellationToken cancellationToken)
        {
            // ── 临时文件登记：先落盘再读回 base64，避免大图长时间占用内存；
            //     传输成功后由 CleanupBatch 统一删除。 ──
            try
            {
                byte[]? bytes;
                if (DownloadOverrideForTests != null)
                {
                    bytes = await DownloadOverrideForTests(url, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    timeoutCts.CancelAfter(PerImageTimeout);

                    using var response = await client
                        .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token)
                        .ConfigureAwait(false);

                    if (!response.IsSuccessStatusCode)
                    {
                        Logger.Info($"[VisionImage] 下载失败 HTTP {(int)response.StatusCode}，丢弃该图: {url}");
                        return null;
                    }

                    // ── 体积预检：Content-Length 超限直接丢弃 ──
                    long? declaredLength = response.Content.Headers.ContentLength;
                    if (declaredLength is > MaxImageBytes)
                    {
                        Logger.Info($"[VisionImage] 图片过大（{declaredLength} 字节）丢弃: {url}");
                        return null;
                    }

                    bytes = await response.Content.ReadAsByteArrayAsync()
                        .ConfigureAwait(false);
                }

                if (bytes == null || bytes.Length == 0)
                {
                    Logger.Info($"[VisionImage] 图片为空，丢弃: {url}");
                    return null;
                }

                if (bytes.Length > MaxImageBytes)
                {
                    Logger.Info($"[VisionImage] 图片过大（{bytes.Length} 字节）丢弃: {url}");
                    return null;
                }

                // ── 按文件头嗅探真实格式：不支持的格式（如 SVG/HTML 错误页）丢弃 ──
                string? mime = SniffImageMime(bytes);
                if (mime == null)
                {
                    Logger.Info($"[VisionImage] 非受支持图片格式（按内容嗅探），丢弃: {url}");
                    return null;
                }

                // 落盘留痕，便于「传输成功后删除图片」的清理链路统一处理。
                SaveTempImage(bytes, mime, batchToken);

                return ($"data:{mime};base64,{Convert.ToBase64String(bytes)}", bytes.Length);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                Logger.Info($"[VisionImage] 下载异常，丢弃该图: {url} - {ex.GetType().Name}: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// 按文件头判断图片真实格式，返回 MIME；非受支持格式返回 null。
        /// 不依赖 URL 扩展名与响应 Content-Type（网页图床两者都不可信）。
        /// </summary>
        public static string? SniffImageMime(byte[] bytes)
        {
            if (bytes == null || bytes.Length < 12)
                return null;

            // PNG: 89 50 4E 47 0D 0A 1A 0A
            if (bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47)
                return "image/png";

            // JPEG: FF D8 FF
            if (bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF)
                return "image/jpeg";

            // GIF: "GIF87a" / "GIF89a"
            if (bytes[0] == 0x47 && bytes[1] == 0x49 && bytes[2] == 0x46 && bytes[3] == 0x38)
                return "image/gif";

            // WebP: "RIFF" .... "WEBP"
            if (bytes[0] == 0x52 && bytes[1] == 0x49 && bytes[2] == 0x46 && bytes[3] == 0x46
                && bytes[8] == 0x57 && bytes[9] == 0x45 && bytes[10] == 0x42 && bytes[11] == 0x50)
                return "image/webp";

            return null;
        }

        /// <summary>取得 MIME 对应的文件扩展名（用于临时文件命名）。</summary>
        public static string GetExtensionForMime(string? mime)
            => mime != null && SupportedMimeToExtension.TryGetValue(mime, out string? ext) ? ext : ".png";

        #region 临时文件与清理

        /// <summary>
        /// 视觉图片临时目录（%TEMP%\DeepSeekVS_VisionImages\&lt;批次令牌&gt;\）。
        /// 图片下载后落盘，**传输成功后**整批删除。
        /// </summary>
        internal static string GetBatchDirectory(string batchToken)
            => Path.Combine(Path.GetTempPath(), "DeepSeekVS_VisionImages", batchToken);

        /// <summary>创建批次令牌（一次 fetch_webpage 结果一个）。</summary>
        public static string NewBatchToken()
            => Guid.NewGuid().ToString("N");

        /// <summary>把图片字节写入批次临时目录，返回文件路径；失败返回 null（不影响直传）。</summary>
        private static string? SaveTempImage(byte[] bytes, string mime, string? batchToken)
        {
            if (string.IsNullOrWhiteSpace(batchToken))
                return null;

            try
            {
                string dir = GetBatchDirectory(batchToken);
                Directory.CreateDirectory(dir);
                string path = Path.Combine(
                    dir, Guid.NewGuid().ToString("N") + GetExtensionForMime(mime));
                File.WriteAllBytes(path, bytes);
                return path;
            }
            catch (Exception ex)
            {
                Logger.Info($"[VisionImage] 写临时图片失败（不影响直传）: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// 传输成功后删除该批次的临时图片目录。
        /// 幂等：目录不存在或删除失败都只记日志，不抛出。
        /// </summary>
        public static void CleanupBatch(string? batchToken)
        {
            if (string.IsNullOrWhiteSpace(batchToken))
                return;

            try
            {
                string dir = GetBatchDirectory(batchToken);
                if (!Directory.Exists(dir))
                    return;

                Directory.Delete(dir, recursive: true);
                Logger.Info($"[VisionImage] 传输成功，已清理临时图片: {batchToken}");
            }
            catch (Exception ex)
            {
                Logger.Warn($"[VisionImage] 清理临时图片失败: {batchToken} - {ex.Message}");
            }
        }

        /// <summary>
        /// 清理视觉图片根目录下的**遗留批次**（上次进程崩溃/异常退出留下的）。
        /// 只删除空闲超过 <paramref name="idleThreshold"/> 的批次目录，避免误删正在进行的批次。
        /// </summary>
        public static void CleanupStaleBatches(TimeSpan? idleThreshold = null)
        {
            TimeSpan idle = idleThreshold ?? TimeSpan.FromHours(6);
            try
            {
                string root = Path.Combine(Path.GetTempPath(), "DeepSeekVS_VisionImages");
                if (!Directory.Exists(root))
                    return;

                DateTime cutoff = DateTime.UtcNow - idle;
                int removed = 0;
                foreach (string dir in Directory.GetDirectories(root))
                {
                    try
                    {
                        if (Directory.GetLastWriteTimeUtc(dir) > cutoff)
                            continue;
                        Directory.Delete(dir, recursive: true);
                        removed++;
                    }
                    catch { /* 单个批次清理失败不影响其它 */ }
                }

                if (removed > 0)
                    Logger.Info($"[VisionImage] 已清理 {removed} 个遗留图片批次目录");
            }
            catch (Exception ex)
            {
                Logger.Warn($"[VisionImage] 清理遗留批次失败: {ex.Message}");
            }
        }

        private static readonly HttpClient SharedHttpClient = CreateHttpClient();

        private static HttpClient CreateHttpClient()
        {
            var handler = new HttpClientHandler
            {
                AutomaticDecompression = System.Net.DecompressionMethods.GZip
                                       | System.Net.DecompressionMethods.Deflate,
            };
            var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
            client.DefaultRequestHeaders.TryAddWithoutValidation(
                "User-Agent",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) DeepSeekVS/1.0");
            return client;
        }

        #endregion

        /// <summary>测试注入点：替换图片下载实现，避免单测访问真实网络。</summary>
        internal static Func<string, CancellationToken, Task<byte[]?>>? DownloadOverrideForTests { get; set; }
    }
}