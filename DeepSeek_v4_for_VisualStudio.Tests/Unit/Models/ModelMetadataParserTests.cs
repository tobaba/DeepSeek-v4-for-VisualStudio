namespace DeepSeek_v4_for_VisualStudio.Tests.Unit.Models;

/// <summary>
/// /models 响应元数据解析：官方接口声明的上下文窗口、输出上限、输入模态与 effort 档位。
/// </summary>
public class ModelMetadataParserTests
{
    private const string FullPayload = """
    {
      "object": "list",
      "data": [
        {
          "id": "deepseek-flash",
          "object": "model",
          "owned_by": "deepseek",
          "name": "DeepSeek-V4.1-Flash",
          "context_window": 1048576,
          "max_output_tokens": 393216,
          "input_modalities": ["text", "image"],
          "output_modalities": ["text"],
          "effort": {
            "supported_levels": ["low", "high", "max"],
            "default_level": "high"
          },
          "api_capabilities": {
            "anthropic_messages": { "system_prompt_update": "in-history" }
          }
        },
        {
          "id": "deepseek-v4-pro",
          "object": "model",
          "owned_by": "deepseek",
          "name": "DeepSeek-V4-Pro",
          "context_window": 1048576,
          "max_output_tokens": 393216,
          "input_modalities": ["text"],
          "output_modalities": ["text"],
          "effort": { "supported_levels": ["low", "high", "max"], "default_level": "high" }
        }
      ]
    }
    """;

    [Fact]
    public void Parse_ReadsAllDeclaredMetadata()
    {
        var models = ModelMetadataParser.Parse(FullPayload);

        models.Should().HaveCount(2);
        var flash = models.Single(model => model.Id == "deepseek-flash");
        flash.Display.Should().Be("DeepSeek-V4.1-Flash");
        flash.ContextWindow.Should().Be(1_048_576);
        flash.MaxOutputTokens.Should().Be(393_216);
        flash.SupportsImageInput.Should().BeTrue();
        flash.HasModalityInfo.Should().BeTrue();
        flash.EffortLevelsOrEmpty.Should().Equal("low", "high", "max");
        flash.DefaultEffortLevel.Should().Be("high");

        var pro = models.Single(model => model.Id == "deepseek-v4-pro");
        pro.SupportsImageInput.Should().BeFalse();
        pro.HasModalityInfo.Should().BeTrue();
    }

    [Fact]
    public void Parse_IdOnlyPayload_LeavesMetadataUnknown()
    {
        var models = ModelMetadataParser.Parse("""{"data":[{"id":"legacy-model"}]}""");

        var model = models.Should().ContainSingle().Subject;
        model.Display.Should().Be("legacy-model");
        model.ContextWindow.Should().BeNull();
        model.MaxOutputTokens.Should().BeNull();
        model.HasModalityInfo.Should().BeFalse();
        model.HasEffortInfo.Should().BeFalse();
    }

    [Fact]
    public void Parse_BareArray_SortsAndDedupesById()
    {
        var models = ModelMetadataParser.Parse(
            """[{"id":"b","context_window":100},{"id":"a"},{"id":"b","context_window":999}]""");

        models.Select(model => model.Id).Should().Equal("a", "b");
        models.Single(model => model.Id == "b").ContextWindow.Should().Be(100);
    }

    [Fact]
    public void Parse_NonJson_ReturnsEmpty()
        => ModelMetadataParser.Parse("<html>Not Found</html>").Should().BeEmpty();

    [Fact]
    public void Parse_NonPositiveContextWindow_IsIgnored()
    {
        var models = ModelMetadataParser.Parse(
            """{"data":[{"id":"m","context_window":0,"max_output_tokens":-5}]}""");

        var model = models.Should().ContainSingle().Subject;
        model.ContextWindow.Should().BeNull();
        model.MaxOutputTokens.Should().BeNull();
    }
}
