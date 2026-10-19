using DeepSeek_v4_for_VisualStudio.Models;
using DeepSeek_v4_for_VisualStudio.Services;
using DeepSeek_v4_for_VisualStudio.Services.Providers;

namespace DeepSeek_v4_for_VisualStudio.Tests.Unit.Services;

/// <summary>
/// 网页图片视觉直传的回归测试。
///
/// 锁定线上事故（2026-10-19）：
///   fetch_webpage 把网页图片的**远程 URL** 作为 image_url 直传，由服务端自行下载；
///   服务端代理不可达 → HTTP 400 "An exception occurred while loading IMAGE data at index 3"
///   → 400 不重试 → 整轮对话失败。
///
/// 修复后必须满足：
///   1. 只上传 data URI，绝不上传远程 URL（服务端无需联网取图）；
///   2. 单张图片下载失败只丢弃该图，不影响其它图片与整轮对话；
///   3. 格式按内容嗅探，扩展名不可信；
///   4. 兜底：万一仍收到图片加载类 400，剥离 image_url 后重试一次。
/// </summary>
public class VisionImageFetchTests : IDisposable
{
    private readonly List<string> _batchTokens = new();

    public void Dispose()
    {
        // 清理测试产生的批次目录，避免污染 %TEMP%
        foreach (string token in _batchTokens)
            VisionImageFetchService.CleanupBatch(token);
        VisionImageFetchService.DownloadOverrideForTests = null;
    }

    private string NewToken()
    {
        string token = VisionImageFetchService.NewBatchToken();
        _batchTokens.Add(token);
        return token;
    }

    #region 内容嗅探

    [Fact]
    public void SniffImageMime_Png_IsDetected()
    {
        byte[] png = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0 };
        VisionImageFetchService.SniffImageMime(png).Should().Be("image/png");
    }

    [Fact]
    public void SniffImageMime_Jpeg_IsDetected()
    {
        byte[] jpeg = { 0xFF, 0xD8, 0xFF, 0xE0, 0, 0, 0, 0, 0, 0, 0, 0 };
        VisionImageFetchService.SniffImageMime(jpeg).Should().Be("image/jpeg");
    }

    [Fact]
    public void SniffImageMime_Gif_IsDetected()
    {
        byte[] gif = { 0x47, 0x49, 0x46, 0x38, 0x39, 0x61, 0, 0, 0, 0, 0, 0 };
        VisionImageFetchService.SniffImageMime(gif).Should().Be("image/gif");
    }

    [Fact]
    public void SniffImageMime_Webp_IsDetected()
    {
        byte[] webp =
        {
            0x52, 0x49, 0x46, 0x46, 0x00, 0x00, 0x00, 0x00,
            0x57, 0x45, 0x42, 0x50
        };
        VisionImageFetchService.SniffImageMime(webp).Should().Be("image/webp");
    }

    /// <summary>SVG 是文本格式，视觉模型不支持；必须按内容识别出来并丢弃。</summary>
    [Fact]
    public void SniffImageMime_Svg_IsRejected()
    {
        byte[] svg = System.Text.Encoding.UTF8.GetBytes(
            "<svg xmlns=\"http://www.w3.org/2000/svg\"></svg>");
        VisionImageFetchService.SniffImageMime(svg).Should().BeNull();
    }

    /// <summary>伪装成 .png 的 HTML 错误页（图床 404 页面）必须被拒绝。</summary>
    [Fact]
    public void SniffImageMime_HtmlErrorPage_IsRejected()
    {
        byte[] html = System.Text.Encoding.UTF8.GetBytes(
            "<!DOCTYPE html><html><body>404 Not Found</body></html>");
        VisionImageFetchService.SniffImageMime(html).Should().BeNull();
    }

    [Fact]
    public void SniffImageMime_TooShort_IsRejected()
    {
        VisionImageFetchService.SniffImageMime(new byte[] { 0x89, 0x50 }).Should().BeNull();
    }

    [Fact]
    public void SniffImageMime_Null_IsRejected()
    {
        VisionImageFetchService.SniffImageMime(null!).Should().BeNull();
    }

    #endregion

    #region 下载转 data URI

    /// <summary>核心断言：上传的必须是 data URI，远程 URL 绝不外传。</summary>
    [Fact]
    public async Task ToDataUrisAsync_RemotePng_BecomesDataUri()
    {
        byte[] png = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3, 4 };
        VisionImageFetchService.DownloadOverrideForTests = (_, _) => Task.FromResult<byte[]?>(png);

        var result = await VisionImageFetchService.ToDataUrisAsync(
            new[] { "https://example.com/a.gif" }, batchToken: NewToken());

        result.Should().ContainSingle();
        result[0].Should().Be("data:image/png;base64," + Convert.ToBase64String(png));
        // 关键：不得出现原始远程 URL
        result[0].Should().NotContain("example.com");
    }

    /// <summary>单张失败只丢弃该图，其余图片照常直传（不打断整轮对话）。</summary>
    [Fact]
    public async Task ToDataUrisAsync_OneFailure_DoesNotDropOthers()
    {
        byte[] png = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 9, 9, 9, 9 };
        VisionImageFetchService.DownloadOverrideForTests = (url, _) =>
            Task.FromResult<byte[]?>(url.Contains("bad") ? null : png);

        var result = await VisionImageFetchService.ToDataUrisAsync(
            new[] { "https://example.com/bad.gif", "https://example.com/good.gif" },
            batchToken: NewToken());

        result.Should().ContainSingle();
        result[0].Should().StartWith("data:image/png;base64,");
    }

    /// <summary>服务端代理不可达场景：下载抛异常时必须静默丢弃，不能向上冒泡。</summary>
    [Fact]
    public async Task ToDataUrisAsync_DownloadThrows_IsSwallowedAndImageDropped()
    {
        VisionImageFetchService.DownloadOverrideForTests = (_, _) =>
            throw new System.Net.Http.HttpRequestException(
                "ProxyError('Unable to connect to proxy', OSError('Tunnel connection failed: 502 Bad Gateway'))");

        var result = await VisionImageFetchService.ToDataUrisAsync(
            new[] { "https://example.com/a.gif" }, batchToken: NewToken());

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task ToDataUrisAsync_EmptyBytes_IsDropped()
    {
        VisionImageFetchService.DownloadOverrideForTests = (_, _) => Task.FromResult<byte[]?>(Array.Empty<byte>());

        var result = await VisionImageFetchService.ToDataUrisAsync(
            new[] { "https://example.com/a.gif" }, batchToken: NewToken());

        result.Should().BeEmpty();
    }

    /// <summary>已有的 data URI 直接透传，不重复下载。</summary>
    [Fact]
    public async Task ToDataUrisAsync_ExistingDataUri_PassesThroughWithoutDownload()
    {
        bool downloaded = false;
        VisionImageFetchService.DownloadOverrideForTests = (_, _) =>
        {
            downloaded = true;
            return Task.FromResult<byte[]?>(null);
        };

        const string dataUri = "data:image/png;base64,AAAA";
        var result = await VisionImageFetchService.ToDataUrisAsync(
            new[] { dataUri }, batchToken: NewToken());

        result.Should().ContainSingle().Which.Should().Be(dataUri);
        downloaded.Should().BeFalse();
    }

    [Fact]
    public async Task ToDataUrisAsync_NullInput_ReturnsEmpty()
    {
        var result = await VisionImageFetchService.ToDataUrisAsync(null);
        result.Should().BeEmpty();
    }

    /// <summary>
    /// 单批图片总量超过上限时停止下载：图片是 base64 内联进请求体的，
    /// 且会在后续每轮工具循环中重复发送，必须有总量闸门。
    /// 用 3MB/张（低于单张 4MB 上限）证明是**总量**在起约束作用，
    /// 而不是 MaxImages 或单张上限。
    /// </summary>
    [Fact]
    public async Task ToDataUrisAsync_TotalSizeBudgetExceeded_StopsDownloadingMore()
    {
        const int threeMb = 3 * 1024 * 1024;
        byte[] big = new byte[threeMb];
        big[0] = 0x89; big[1] = 0x50; big[2] = 0x4E; big[3] = 0x47;

        int downloads = 0;
        VisionImageFetchService.DownloadOverrideForTests = (_, _) =>
        {
            downloads++;
            return Task.FromResult<byte[]?>(big);
        };

        string[] urls = Enumerable.Range(0, 8)
            .Select(i => $"https://example.com/{i}.png")
            .ToArray();

        var result = await VisionImageFetchService.ToDataUrisAsync(urls, batchToken: NewToken());

        // 8MB 上限 / 3MB ≈ 2 张即达上限，第 4 次循环开始时被拦下
        result.Count.Should().BeLessThan(8, "总量上限必须先于 MaxImages 生效");
        downloads.Should().Be(result.Count, "被拦下后不得再发起下载");
    }

    #endregion

    #region 传输成功后清理临时图片

    /// <summary>
    /// 「传完成功之后删除图片」：批次目录在传输成功后必须被删除。
    /// </summary>
    [Fact]
    public async Task CleanupBatch_AfterSuccessfulTransfer_RemovesTempDirectory()
    {
        byte[] png = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 5, 5, 5, 5 };
        VisionImageFetchService.DownloadOverrideForTests = (_, _) => Task.FromResult<byte[]?>(png);

        string token = NewToken();
        await VisionImageFetchService.ToDataUrisAsync(
            new[] { "https://example.com/a.gif" }, batchToken: token);

        // 下载后落盘留痕
        string dir = VisionImageFetchService.GetBatchDirectory(token);
        Directory.Exists(dir).Should().BeTrue();
        Directory.GetFiles(dir).Should().NotBeEmpty();

        // 传输成功后清理
        VisionImageFetchService.CleanupBatch(token);
        Directory.Exists(dir).Should().BeFalse();
    }

    /// <summary>清理必须幂等：重复调用或目录不存在都不能抛。</summary>
    [Fact]
    public void CleanupBatch_IsIdempotent()
    {
        string token = NewToken();
        var act = () =>
        {
            VisionImageFetchService.CleanupBatch(token);
            VisionImageFetchService.CleanupBatch(token);
            VisionImageFetchService.CleanupBatch(null);
            VisionImageFetchService.CleanupBatch(string.Empty);
        };
        act.Should().NotThrow();
    }

    #endregion

    #region 方案4：图片加载类 400 的剥离重试

    [Fact]
    public void IsImageLoadingError_RealServerMessage_IsDetected()
    {
        const string details =
            "响应体: {\"error\":{\"message\":\"An exception occurred while loading IMAGE data " +
            "at index 3: Error while loading data https://x.cn/a.gif HTTPSConnectionPool" +
            "(host='x.cn', port=443): Max retries exceeded ... ProxyError('Unable to connect " +
            "to proxy', OSError('Tunnel connection failed: 502 Bad Gateway'))\"}}";

        OpenAiCompatibleProvider.IsImageLoadingError(details).Should().BeTrue();
    }

    [Fact]
    public void IsImageLoadingError_UnrelatedBadRequest_IsNotDetected()
    {
        const string details = "响应体: {\"error\":{\"message\":\"Invalid value for 'temperature'\"}}";
        OpenAiCompatibleProvider.IsImageLoadingError(details).Should().BeFalse();
    }

    [Fact]
    public void IsImageLoadingError_NullOrEmpty_IsNotDetected()
    {
        OpenAiCompatibleProvider.IsImageLoadingError(null).Should().BeFalse();
        OpenAiCompatibleProvider.IsImageLoadingError(string.Empty).Should().BeFalse();
    }

    /// <summary>剥离图片块，保留文本块。</summary>
    [Fact]
    public void TryStripImageBlocks_RemovesImagesKeepsText()
    {
        var messages = new List<ChatApiMessage>
        {
            new()
            {
                Role = "tool",
                MultimodalContent = new List<ChatContentPart>
                {
                    new() { Type = "text", Text = "网页正文" },
                    new() { Type = "image_url", ImageUrl = new ChatImageUrl { Url = "https://x.cn/a.gif" } },
                    new() { Type = "image_url", ImageUrl = new ChatImageUrl { Url = "https://x.cn/b.gif" } },
                }
            }
        };

        bool stripped = OpenAiCompatibleProvider.TryStripImageBlocks(messages, out int count);

        stripped.Should().BeTrue();
        count.Should().Be(2);
        messages[0].MultimodalContent.Should().ContainSingle()
            .Which.Type.Should().Be("text");
        // 关键：远程 URL 必须彻底消失
        messages[0].MultimodalContent!.Should().NotContain(p => p.Type == "image_url");
    }

    /// <summary>纯图片消息剥离后不能变成空 content，否则会引发新的 400。</summary>
    [Fact]
    public void TryStripImageBlocks_ImageOnlyMessage_GetsPlaceholderText()
    {
        var messages = new List<ChatApiMessage>
        {
            new()
            {
                Role = "user",
                MultimodalContent = new List<ChatContentPart>
                {
                    new() { Type = "image_url", ImageUrl = new ChatImageUrl { Url = "https://x.cn/a.gif" } },
                }
            }
        };

        OpenAiCompatibleProvider.TryStripImageBlocks(messages, out int count).Should().BeTrue();
        count.Should().Be(1);
        messages[0].MultimodalContent.Should().BeNull();
        messages[0].Content.Should().NotBeNullOrWhiteSpace();
    }

    /// <summary>没有图片时不应误报剥离（避免无谓重发）。</summary>
    [Fact]
    public void TryStripImageBlocks_NoImages_ReturnsFalse()
    {
        var messages = new List<ChatApiMessage>
        {
            new() { Role = "user", Content = "纯文本" }
        };

        OpenAiCompatibleProvider.TryStripImageBlocks(messages, out int count).Should().BeFalse();
        count.Should().Be(0);
    }

    /// <summary>
    /// 图片消息同时带字符串 Content：剥离后降级为纯文本消息，文本不得丢失。
    /// </summary>
    [Fact]
    public void TryStripImageBlocks_StringContentWithOnlyImage_KeepsTextAsPlainContent()
    {
        var messages = new List<ChatApiMessage>
        {
            new()
            {
                Role = "user",
                Content = "用户问题",
                MultimodalContent = new List<ChatContentPart>
                {
                    new() { Type = "image_url", ImageUrl = new ChatImageUrl { Url = "https://x.cn/a.gif" } },
                }
            }
        };

        OpenAiCompatibleProvider.TryStripImageBlocks(messages, out int count).Should().BeTrue();
        count.Should().Be(1);
        messages[0].MultimodalContent.Should().BeNull("只剩文本时应降级为普通文本消息");
        messages[0].Content.Should().Be("用户问题", "文本内容不得丢失");
    }

    /// <summary>
    /// 多模态消息同时带字符串 Content 且仍有文本块时，Content 必须收敛进内容块，
    /// 否则序列化时字符串与数组同时存在会产生冲突。
    /// </summary>
    [Fact]
    public void TryStripImageBlocks_MultimodalWithTextPart_ConvergesStringContentIntoParts()
    {
        var messages = new List<ChatApiMessage>
        {
            new()
            {
                Role = "user",
                Content = "用户问题",
                MultimodalContent = new List<ChatContentPart>
                {
                    new() { Type = "text", Text = "网页正文" },
                    new() { Type = "image_url", ImageUrl = new ChatImageUrl { Url = "https://x.cn/a.gif" } },
                }
            }
        };

        OpenAiCompatibleProvider.TryStripImageBlocks(messages, out int count).Should().BeTrue();
        count.Should().Be(1);
        messages[0].Content.Should().BeNull("有内容块时字符串必须收敛，避免序列化冲突");
        // 已有的文本块被保留，未重复追加字符串 Content
        messages[0].MultimodalContent.Should().ContainSingle()
            .Which.Text.Should().Be("网页正文");
    }

    /// <summary>
    /// 回归：前面的消息剥离了图片后，后面**不含图片**的多模态消息不得被误改
    ///（strippedCount 曾是累计值，会错误地让后续消息进入修改分支）。
    /// </summary>
    [Fact]
    public void TryStripImageBlocks_LaterMessageWithoutImages_IsLeftUntouched()
    {
        var laterParts = new List<ChatContentPart>
        {
            new() { Type = "text", Text = "后续消息" },
        };
        var messages = new List<ChatApiMessage>
        {
            new()
            {
                Role = "tool",
                MultimodalContent = new List<ChatContentPart>
                {
                    new() { Type = "image_url", ImageUrl = new ChatImageUrl { Url = "https://x.cn/a.gif" } },
                }
            },
            new() { Role = "user", Content = "保留", MultimodalContent = laterParts }
        };

        OpenAiCompatibleProvider.TryStripImageBlocks(messages, out int count).Should().BeTrue();
        count.Should().Be(1);
        messages[1].Content.Should().Be("保留", "无图片的消息不应被改写");
        messages[1].MultimodalContent.Should().BeSameAs(laterParts);
    }

    #endregion
}