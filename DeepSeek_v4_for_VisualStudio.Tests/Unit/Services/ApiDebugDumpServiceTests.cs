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
}
