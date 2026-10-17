using DeepSeek_v4_for_VisualStudio.View;

namespace DeepSeek_v4_for_VisualStudio.Tests.Unit.View;

/// <summary>
/// 图表导出契约：点击 Mermaid 图后把已渲染的 SVG 写盘并用系统默认程序打开。
/// 只放行单张 &lt;svg&gt; 图，避免把任意 HTML 交给默认浏览器。
/// </summary>
public class DiagramExportTests
{
    [Fact]
    public void IsExportableDiagramSvg_MermaidSvg_ReturnsTrue()
    {
        const string svg = "<svg id=\"mermaid-svg-1\" viewBox=\"0 0 100 50\" "
            + "xmlns=\"http://www.w3.org/2000/svg\"><g><text>a</text></g></svg>";

        DeepSeekChatControl.IsExportableDiagramSvg(svg).Should().BeTrue();
    }

    [Fact]
    public void IsExportableDiagramSvg_LeadingWhitespaceAndUppercase_ReturnsTrue()
    {
        DeepSeekChatControl.IsExportableDiagramSvg("  \n<SVG viewBox=\"0 0 1 1\"></SVG>")
            .Should().BeTrue();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("<html><body>hi</body></html>")]
    [InlineData("<div>not a diagram</div>")]
    [InlineData("plain text")]
    public void IsExportableDiagramSvg_NonDiagramContent_ReturnsFalse(string? content)
    {
        DeepSeekChatControl.IsExportableDiagramSvg(content).Should().BeFalse();
    }

    [Fact]
    public void IsExportableDiagramSvg_EmbeddedScript_ReturnsFalse()
    {
        const string svg = "<svg viewBox=\"0 0 1 1\"><script>alert(1)</script></svg>";

        DeepSeekChatControl.IsExportableDiagramSvg(svg).Should().BeFalse();
    }

    [Fact]
    public void IsExportableDiagramSvg_OversizedPayload_ReturnsFalse()
    {
        string huge = "<svg viewBox=\"0 0 1 1\">" + new string('x', 4 * 1024 * 1024) + "</svg>";

        DeepSeekChatControl.IsExportableDiagramSvg(huge).Should().BeFalse();
    }

    [Fact]
    public void DiagramExportDirectory_IsUnderLocalAppData()
    {
        string expected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DeepSeekVS", "diagrams");

        DeepSeekChatControl.DiagramExportDirectory.Should().Be(expected);
    }

    /// <summary>
    /// 真实缺陷复现：Mermaid 把节点文字放进 foreignObject 的 XHTML 里，
    /// HTML 序列化会把换行写成裸 &lt;br&gt;，独立 .svg 文件按 XML 解析时报
    /// "Opening and ending tag mismatch: br ... span"。
    /// </summary>
    private const string MermaidSvgWithHtmlBr =
        "<svg id=\"mermaid-svg-1\" aria-roledescription=\"flowchart-v2\" "
        + "viewBox=\"0 0 878 1081\" style=\"max-width: none; height: auto;\">"
        + "<g><foreignObject width=\"130\" height=\"51\">"
        + "<div xmlns=\"http://www.w3.org/1999/xhtml\" style=\"display: inline-block;\">"
        + "<span class=\"nodeLabel\">需要跨多轮<br>操作工具 / 仓库？</span></div></foreignObject></g></svg>";

    [Fact]
    public void IsWellFormedXml_MermaidForeignObjectWithHtmlBr_ReturnsFalse()
    {
        DeepSeekChatControl.IsWellFormedXml(MermaidSvgWithHtmlBr).Should().BeFalse();
    }

    [Fact]
    public void NormalizeDiagramSvg_HtmlBrValue_ProducesWellFormedXml()
    {
        string normalized = DeepSeekChatControl.NormalizeDiagramSvg(MermaidSvgWithHtmlBr);

        normalized.Should().StartWith("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
        normalized.Should().Contain("xmlns=\"http://www.w3.org/2000/svg\"");
        normalized.Should().Contain("<br />");
        normalized.Should().NotContain("<br>");
        DeepSeekChatControl.IsWellFormedXml(normalized).Should().BeTrue();
    }

    [Fact]
    public void NormalizeDiagramSvg_VoidElementWithAttributes_SelfCloses()
    {
        DeepSeekChatControl.NormalizeDiagramSvg("<svg><g><img src=\"a.png\"><hr></g></svg>")
            .Should().Contain("<img src=\"a.png\" />").And.Contain("<hr />");
    }

    [Fact]
    public void NormalizeDiagramSvg_ExistingXmlDeclaration_NotDuplicated()
    {
        string source = "<?xml version=\"1.0\" encoding=\"UTF-8\"?>"
            + "<svg xmlns=\"http://www.w3.org/2000/svg\"><g></g></svg>";

        string normalized = DeepSeekChatControl.NormalizeDiagramSvg(source);

        normalized.Split(new[] { "<?xml" }, StringSplitOptions.None).Should().HaveCount(2);
        normalized.Should().NotContain("xmlns=\"http://www.w3.org/2000/svg\" xmlns=");
        DeepSeekChatControl.IsWellFormedXml(normalized).Should().BeTrue();
    }

    [Fact]
    public void NormalizeDiagramSvg_WellFormedSvg_LeftSemanticallyIntact()
    {
        const string source = "<svg viewBox=\"0 0 10 10\"><g><text>a</text></g></svg>";

        string normalized = DeepSeekChatControl.NormalizeDiagramSvg(source);

        normalized.Should().Contain("viewBox=\"0 0 10 10\"");
        normalized.Should().Contain("<text>a</text>");
        DeepSeekChatControl.IsWellFormedXml(normalized).Should().BeTrue();
    }

    [Fact]
    public void IsExportableDiagramSvg_XmlDeclarationPrefixedSvg_ReturnsTrue()
    {
        const string source = "<?xml version=\"1.0\" encoding=\"UTF-8\"?>"
            + "<svg viewBox=\"0 0 1 1\"></svg>";

        DeepSeekChatControl.IsExportableDiagramSvg(source).Should().BeTrue();
    }

    // ── 重复点击去重 ──
    // Mermaid 每次渲染都换 id（mermaid-svg-{时间戳}-{序号}），
    // 若不归一化，同一张图重新渲染后内容不同，去重会失效、文件越堆越多。

    private static string MermaidSvgWithId(string id) =>
        "<svg id=\"" + id + "\" viewBox=\"0 0 10 10\"><style>#" + id
        + " .node{fill:#1f2020;}</style><g><marker id=\"" + id
        + "_flowchart-pointEnd\" marker-end=\"url(#" + id + "_flowchart-pointEnd)\"></marker></g></svg>";

    [Fact]
    public void NormalizeDiagramIdentity_DifferentRenderIds_ProduceSameText()
    {
        string first = MermaidSvgWithId("mermaid-svg-1792252615344-3");
        string second = MermaidSvgWithId("mermaid-svg-1799999999999-12");

        string a = DeepSeekChatControl.NormalizeDiagramIdentity(first);
        string b = DeepSeekChatControl.NormalizeDiagramIdentity(second);

        a.Should().Be(b);
        a.Should().Contain("mermaid-diagram");
        a.Should().NotContain("1792252615344");
        a.Should().NotContain("1799999999999");
    }

    [Fact]
    public void NormalizeDiagramIdentity_RewritesEveryIdReference()
    {
        string normalized = DeepSeekChatControl.NormalizeDiagramIdentity(
            MermaidSvgWithId("mermaid-svg-123-4"));

        // 根 id、样式选择器、marker id、url(#...) 引用都必须同步替换，
        // 否则箭头等引用会指向不存在的 id。
        normalized.Should().NotContain("mermaid-svg-123-4");
        normalized.Should().Contain("id=\"mermaid-diagram\"");
        normalized.Should().Contain("#mermaid-diagram .node");
        normalized.Should().Contain("url(#mermaid-diagram_flowchart-pointEnd)");
    }

    [Fact]
    public void ComputeDiagramHash_SameDiagramRenderedTwice_IsStable()
    {
        string first = DeepSeekChatControl.NormalizeDiagramSvg(
            MermaidSvgWithId("mermaid-svg-1792252615344-3"));
        string second = DeepSeekChatControl.NormalizeDiagramSvg(
            MermaidSvgWithId("mermaid-svg-1799999999999-12"));

        DeepSeekChatControl.ComputeDiagramHash(first)
            .Should().Be(DeepSeekChatControl.ComputeDiagramHash(second));
    }

    [Fact]
    public void ComputeDiagramHash_DifferentDiagrams_Differ()
    {
        string a = DeepSeekChatControl.NormalizeDiagramSvg(
            "<svg viewBox=\"0 0 10 10\"><text>模型 A</text></svg>");
        string b = DeepSeekChatControl.NormalizeDiagramSvg(
            "<svg viewBox=\"0 0 10 10\"><text>模型 B</text></svg>");

        DeepSeekChatControl.ComputeDiagramHash(a)
            .Should().NotBe(DeepSeekChatControl.ComputeDiagramHash(b));
    }

    [Fact]
    public void ComputeDiagramHash_Returns16HexChars()
    {
        DeepSeekChatControl.ComputeDiagramHash("<svg></svg>")
            .Should().MatchRegex("^[0-9A-F]{16}$");
    }

    [Fact]
    public void NormalizeDiagramSvg_StampsStableId()
    {
        string normalized = DeepSeekChatControl.NormalizeDiagramSvg(MermaidSvgWithId("mermaid-svg-1-1"));

        normalized.Should().Contain("mermaid-diagram");
        DeepSeekChatControl.IsWellFormedXml(normalized).Should().BeTrue();
    }
}
