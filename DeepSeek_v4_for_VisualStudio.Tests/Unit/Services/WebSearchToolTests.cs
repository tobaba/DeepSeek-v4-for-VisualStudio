using DeepSeek_v4_for_VisualStudio.Services.BuiltInTools;
using System.Text.Json;

namespace DeepSeek_v4_for_VisualStudio.Tests.Unit.Services;

/// <summary>
/// web_search 工具测试：定义、展示文本与参数校验。
/// 执行路径依赖真实搜索服务，网络调用由 WebSearchService 自身测试覆盖。
/// </summary>
public class WebSearchToolTests
{
    [Fact]
    public void Name_IsWebSearch()
    {
        new WebSearchTool().Name.Should().Be("web_search");
    }

    [Fact]
    public void Definition_RequiresQuery()
    {
        var def = new WebSearchTool().GetDefinition();

        def.Type.Should().Be("function");
        def.Function.Name.Should().Be("web_search");
        def.Function.Description.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void GetDisplayText_IncludesQuery()
    {
        var args = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
            "{\"query\":\"DeepSeek V4\"}")!;

        var text = new WebSearchTool().GetDisplayText(args);

        text.Should().Contain("DeepSeek V4");
    }

    [Fact]
    public async Task ExecuteAsync_MissingQuery_ReturnsError()
    {
        var args = new Dictionary<string, JsonElement>();

        var result = await new WebSearchTool().ExecuteAsync(args, workspaceRoot: null);

        result.Should().StartWith("Error:");
    }

    [Fact]
    public async Task ExecuteAsync_NoService_ReturnsError()
    {
        var args = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
            "{\"query\":\"DeepSeek V4\"}")!;

        var result = await new WebSearchTool(webSearchService: null).ExecuteAsync(args, workspaceRoot: null);

        result.Should().StartWith("Error:");
    }

    [Fact]
    public void GetResultSummary_ErrorResult_PassesThrough()
    {
        var summary = new WebSearchTool().GetResultSummary("Error: web_search: boom");

        summary.Should().Be("Error: web_search: boom");
    }

    [Fact]
    public void GetResultSummary_SearchResults_ListsTitlesAndUrls()
    {
        const string toolResult =
            "=== 联网搜索结果（共 2 条）===\n" +
            "\n" +
            "1. DeepSeek V4 发布\n" +
            "   URL: https://example.com/a\n" +
            "   2026-06-01T00:00:00+00:00\n" +
            "   摘要 A\n" +
            "\n" +
            "2. [官方] DeepSeek 文档\n" +
            "   URL: https://example.com/b(c)\n" +
            "   摘要 B";

        var summary = new WebSearchTool().GetResultSummary(toolResult);

        summary.Should().Contain("2");
        summary.Should().Contain("[DeepSeek V4 发布](https://example.com/a)");
        summary.Should().Contain("\\[官方\\] DeepSeek 文档");
        summary.Should().Contain("<https://example.com/b(c)>");
    }

    [Fact]
    public void GetResultSummary_NoResultsMessage_PassesThrough()
    {
        const string toolResult = "web_search: 未找到与「test」相关的网页。";

        var summary = new WebSearchTool().GetResultSummary(toolResult);

        summary.Should().Be(toolResult);
    }
}
