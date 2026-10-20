using DeepSeek_v4_for_VisualStudio.Services;

namespace DeepSeek_v4_for_VisualStudio.Tests.Unit.Services;

/// <summary>
/// 开发者 API 报文转储的回归测试：开关语义、每次调用一对文件、
/// 请求体可解析、响应流完整记录、摘要包含缓存统计。
/// </summary>
public class ApiDebugDumpServiceTests : IDisposable
{
    private readonly string _dumpRoot;

    public ApiDebugDumpServiceTests()
    {
        _dumpRoot = Path.Combine(Path.GetTempPath(), $"api_dump_{Guid.NewGuid():N}");
        ApiDebugDumpService.RootOverride = _dumpRoot;
    }

    public void Dispose()
    {
        ApiDebugDumpService.Enabled = false;
        ApiDebugDumpService.RootOverride = null;
        try { if (Directory.Exists(_dumpRoot)) Directory.Delete(_dumpRoot, true); } catch { }
    }

    [Fact]
    public void BeginCall_WhenDisabled_ReturnsInactiveScopeAndWritesNothing()
    {
        ApiDebugDumpService.Enabled = false;

        var scope = ApiDebugDumpService.BeginCall("DeepSeek");

        scope.Enabled.Should().BeFalse();
        scope.RequestPath.Should().BeNull();
        scope.ResponsePath.Should().BeNull();
    }

    [Fact]
    public void Dump_RoundTripsRequestAndResponseToDisk()
    {
        try
        {
            ApiDebugDumpService.Enabled = true;
            var scope = ApiDebugDumpService.BeginCall("DeepSeek");

            scope.Enabled.Should().BeTrue();
            File.Exists(scope.RequestPath).Should().BeFalse("请求体只在显式写入时落盘");

            const string requestJson = "{\"model\":\"deepseek-chat\",\"stream\":true}";
            ApiDebugDumpService.WriteRequest(scope, requestJson);
            ApiDebugDumpService.AppendResponseLine(scope, "data: {\"choices\":[]}");
            ApiDebugDumpService.AppendResponseLine(scope, "data: [DONE]");
            ApiDebugDumpService.WriteSummary(scope, "outcome=stream-done\ncacheHitTokens=100");

            File.ReadAllText(scope.RequestPath!).Should().Be(requestJson);

            string response = File.ReadAllText(scope.ResponsePath!);
            response.Should().Contain("data: {\"choices\":[]}");
            response.Should().Contain("data: [DONE]");
            response.Should().Contain("outcome=stream-done");
            response.Should().Contain("cacheHitTokens=100");
        }
        finally
        {
            ApiDebugDumpService.Enabled = false;
        }
    }

    [Fact]
    public void BeginCall_ConsecutiveCalls_UseDistinctFilePairs()
    {
        ApiDebugDumpService.Enabled = true;
        try
        {
            var first = ApiDebugDumpService.BeginCall("DeepSeek");
            var second = ApiDebugDumpService.BeginCall("DeepSeek");

            first.RequestPath.Should().NotBe(second.RequestPath);
            first.ResponsePath.Should().NotBe(second.ResponsePath);
        }
        finally
        {
            ApiDebugDumpService.Enabled = false;
        }
    }

    [Fact]
    public void WriteRequest_UnescapesUnicodeToReadableChinese()
    {
        ApiDebugDumpService.Enabled = true;
        try
        {
            var scope = ApiDebugDumpService.BeginCall("DeepSeek");
            ApiDebugDumpService.WriteRequest(scope, "{\"text\":\"\\u4e2d\\u6587\\u6d4b\\u8bd5\"}");

            string content = File.ReadAllText(scope.RequestPath!);
            content.Should().Contain("中文测试");
            content.Should().NotContain("\\u4e2d");
        }
        finally
        {
            ApiDebugDumpService.Enabled = false;
        }
    }

    [Fact]
    public void UnescapeUnicodeForReadability_DecodesChineseAndEmoji()
    {
        ApiDebugDumpService.UnescapeUnicodeForReadability("\\u4e2d\\u6587")
            .Should().Be("中文");

        // emoji 以 UTF-16 代理对 \uD83D\uDE00 传输，必须还原为一个字符而不是两个孤立代理项。
        ApiDebugDumpService.UnescapeUnicodeForReadability("\\uD83D\\uDE00")
            .Should().Be("\U0001F600");
    }

    [Fact]
    public void UnescapeUnicodeForReadability_KeepsJsonEscapesAndControlCharactersIntact()
    {
        // JSON 结构转义保持原样，确保转储仍是合法 JSON。
        ApiDebugDumpService.UnescapeUnicodeForReadability("a\\\"b\\\\c\\nd")
            .Should().Be("a\\\"b\\\\c\\nd");

        // 控制字符（\u000A 换行）不还原，否则会把一行一个 SSE 事件的结构打散。
        ApiDebugDumpService.UnescapeUnicodeForReadability("\\u000a")
            .Should().Be("\\u000a");

        // 转义的反斜杠后跟 u 序列是字面量，不得误判为转义符。
        ApiDebugDumpService.UnescapeUnicodeForReadability("\\\\u4e2d")
            .Should().Be("\\\\u4e2d");
    }

    [Fact]
    public void WriteRequest_ProducesValidJson_AfterUnescaping()
    {
        ApiDebugDumpService.Enabled = true;
        try
        {
            var scope = ApiDebugDumpService.BeginCall("DeepSeek");
            ApiDebugDumpService.WriteRequest(
                scope,
                "{\"messages\":[{\"role\":\"user\",\"content\":\"\\u4f60\\u597d\\n\\u4e16\\u754c\"}]}");

            // 还原中文后仍须能被 JSON 解析器接受（换行仍是 \n 转义，未被还原成真实控制字符）。
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(scope.RequestPath!));
            doc.RootElement
                .GetProperty("messages")[0]
                .GetProperty("content")
                .GetString()
                .Should().Be("你好\n世界");
        }
        finally
        {
            ApiDebugDumpService.Enabled = false;
        }
    }
}
