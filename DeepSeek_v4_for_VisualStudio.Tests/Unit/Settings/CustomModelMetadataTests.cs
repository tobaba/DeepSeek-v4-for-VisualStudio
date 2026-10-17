using DeepSeek_v4_for_VisualStudio.Settings;

namespace DeepSeek_v4_for_VisualStudio.Tests.Unit.Settings;

/// <summary>
/// 自定义端点模型扩展元数据的持久化格式：模型名=展示名称=输出上限=推理档位。
/// </summary>
public class CustomModelMetadataTests
{
    [Fact]
    public void Parse_ReadsAllFields()
    {
        var parsed = DeepSeekOptionsPage.ParseCustomModelMetadata(
            "glm-5.3-flash=GLM 5.3 Flash=64K=low/high/max");

        var entry = parsed["glm-5.3-flash"];
        entry.DisplayName.Should().Be("GLM 5.3 Flash");
        entry.MaxOutputTokens.Should().Be(64_000);
        entry.EffortLevels.Should().Equal("low", "high", "max");
    }

    [Fact]
    public void Parse_BlankFields_LeaveDefaults()
    {
        var parsed = DeepSeekOptionsPage.ParseCustomModelMetadata("m1===");

        // 三个字段全空 → 视为无扩展元数据，不产生条目。
        parsed.Should().BeEmpty();
    }

    [Fact]
    public void Parse_SupportsSeveralSeparators()
    {
        var parsed = DeepSeekOptionsPage.ParseCustomModelMetadata("m1=Name=1M=low|high,medium；max");

        parsed["m1"].EffortLevels.Should().Equal("low", "high", "medium", "max");
    }

    [Fact]
    public void Parse_IsCaseInsensitiveAndSkipsComments()
    {
        var parsed = DeepSeekOptionsPage.ParseCustomModelMetadata(
            "# comment\r\nM1=Name=32K=low" + Environment.NewLine + "invalid-line");

        parsed.Keys.Should().ContainSingle();
        parsed["m1"].MaxOutputTokens.Should().Be(32_000);
    }

    [Fact]
    public void Format_RoundTripsThroughParse()
    {
        var text = DeepSeekOptionsPage.FormatCustomModelMetadata(new[]
        {
            ("glm-5.3-flash", new DeepSeekOptionsPage.ModelExtraMetadata(
                "GLM 5.3 Flash", 64_000, new[] { "low", "high" })),
            // 空元数据不落盘。
            ("plain-model", new DeepSeekOptionsPage.ModelExtraMetadata(
                string.Empty, 0, Array.Empty<string>())),
        });

        text.Should().Contain("glm-5.3-flash=GLM 5.3 Flash=64000=low/high");
        text.Should().NotContain("plain-model");

        var parsed = DeepSeekOptionsPage.ParseCustomModelMetadata(text);
        parsed.Should().HaveCount(1);
        parsed["glm-5.3-flash"].EffortLevels.Should().Equal("low", "high");
    }

    [Fact]
    public void FormatModelChoice_ShowsDisplayNameOrFallsBackToModel()
    {
        DeepSeekOptionsPage.FormatModelChoice("glm-5.3-flash", "GLM 5.3 Flash")
            .Should().Be("GLM 5.3 Flash");

        // 展示名称等于模型名（或为空）时回退到模型名。
        DeepSeekOptionsPage.FormatModelChoice("glm-5.3-flash", "glm-5.3-flash")
            .Should().Be("glm-5.3-flash");
        DeepSeekOptionsPage.FormatModelChoice("glm-5.3-flash", null)
            .Should().Be("glm-5.3-flash");
    }

    [Fact]
    public void ResolveModelFromChoice_RestoresRealModelIdFromDisplayName()
    {
        var known = new[] { "glm-5.3-flash" };
        var displayNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["GLM 5.3 Flash"] = "glm-5.3-flash",
        };

        // 下拉框只显示展示名称 → 按展示名称还原模型 id。
        DeepSeekOptionsPage.ResolveModelFromChoice("GLM 5.3 Flash", known, displayNames)
            .Should().Be("glm-5.3-flash");
        // 纯模型名（旧格式）仍精确命中。
        DeepSeekOptionsPage.ResolveModelFromChoice("glm-5.3-flash", known, displayNames)
            .Should().Be("glm-5.3-flash");
        // 兼容旧的“展示名称 · 模型名”格式。
        DeepSeekOptionsPage.ResolveModelFromChoice("GLM 5.3 Flash · glm-5.3-flash", known)
            .Should().Be("glm-5.3-flash");
        // 未知模型名原样返回，保持“手动填入新模型”的既有行为。
        DeepSeekOptionsPage.ResolveModelFromChoice("brand-new-model", known, displayNames)
            .Should().BeNull();
    }

}
