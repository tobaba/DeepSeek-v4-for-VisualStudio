using DeepSeek_v4_for_VisualStudio.Utils;
using System.Text;

namespace DeepSeek_v4_for_VisualStudio.Tests.Unit.Utils;

/// <summary>
/// <see cref="LineEndingHelper"/> 单元测试 —— 全项目唯一的换行风格判定实现。
/// 重点：探测优先级、探测不到时回退系统默认（Environment.NewLine）、切分语义。
/// </summary>
public class LineEndingHelperTests : IDisposable
{
    private readonly string _tempDir;

    public LineEndingHelperTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"lineending_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, true); } catch { }
    }

    private string NewFile(string name, string content)
    {
        var path = Path.Combine(_tempDir, name);
        File.WriteAllText(path, content, new UTF8Encoding(false));
        return path;
    }

    #region 探测

    [Theory]
    [InlineData("a\r\nb", "\r\n")]
    [InlineData("a\nb", "\n")]
    [InlineData("a\rb", "\r")]
    public void TryDetect_SingleStyle_ReturnsThatStyle(string text, string expected)
    {
        LineEndingHelper.TryDetect(text, out string ending).Should().BeTrue();
        ending.Should().Be(expected);
    }

    [Fact]
    public void TryDetect_MixedStyle_ReturnsDominant()
    {
        LineEndingHelper.TryDetect("a\r\nb\r\nc\nd", out string ending).Should().BeTrue();
        ending.Should().Be("\r\n", "CRLF 占多数应胜出");
    }

    [Fact]
    public void TryDetect_CrlfRunCountedOnce()
    {
        // 一个 \r\n 只能算一次，否则会把「1 个 CRLF」误算成 CRLF+LF 两个计数
        LineEndingHelper.TryDetect("\r\n", out string ending).Should().BeTrue();
        ending.Should().Be("\r\n");
    }

    [Theory]
    [InlineData("")]
    [InlineData("single line without break")]
    [InlineData(null)]
    public void TryDetect_NoLineBreak_ReturnsFalse(string? text)
    {
        LineEndingHelper.TryDetect(text, out _).Should().BeFalse();
    }

    [Fact]
    public void Default_FollowsSystemEnvironment()
    {
        LineEndingHelper.Default.Should().Be(Environment.NewLine);
    }

    [Fact]
    public void Detect_NoLineBreak_FallsBackToSystemDefault()
    {
        LineEndingHelper.Detect("no break here").Should().Be(LineEndingHelper.Default);
    }

    [Fact]
    public void Detect_NoLineBreak_UsesFallbackBeforeDefault()
    {
        LineEndingHelper.Detect("no break here", "x\nf").Should().Be(LineEndingHelper.Lf);
    }

    #endregion

    #region 规范化与转换

    [Fact]
    public void Normalize_ConvertsAllStylesToLf()
    {
        LineEndingHelper.Normalize("a\r\nb\rc\nd").Should().Be("a\nb\nc\nd");
    }

    [Fact]
    public void Conform_RewritesToRequestedStyle()
    {
        LineEndingHelper.Conform("a\r\nb", LineEndingHelper.Lf).Should().Be("a\nb");
        LineEndingHelper.Conform("a\nb", LineEndingHelper.CrLf).Should().Be("a\r\nb");
        LineEndingHelper.Conform("a\nb", LineEndingHelper.Cr).Should().Be("a\rb");
    }

    [Fact]
    public void Conform_IsIdempotent()
    {
        var once = LineEndingHelper.Conform("a\r\nb\rc\n", LineEndingHelper.Lf);
        LineEndingHelper.Conform(once, LineEndingHelper.Lf).Should().Be(once);
    }

    [Fact]
    public void Conform_EmptyInput_StaysEmpty()
    {
        LineEndingHelper.Conform(null, LineEndingHelper.CrLf).Should().BeEmpty();
        LineEndingHelper.Conform(string.Empty, LineEndingHelper.CrLf).Should().BeEmpty();
    }

    #endregion

    #region 按文件解析

    [Fact]
    public void ResolveForFile_ExistingLfFile_ReturnsLf()
    {
        var path = NewFile("lf.txt", "a\nb\n");
        LineEndingHelper.ResolveForFile(path).Should().Be(LineEndingHelper.Lf);
    }

    [Fact]
    public void ResolveForFile_ExistingCrlfFile_ReturnsCrlf()
    {
        var path = NewFile("crlf.txt", "a\r\nb\r\n");
        LineEndingHelper.ResolveForFile(path).Should().Be(LineEndingHelper.CrLf);
    }

    [Fact]
    public void ResolveForFile_MissingFile_ReturnsSystemDefault()
    {
        var path = Path.Combine(_tempDir, $"missing-{Guid.NewGuid():N}.txt");
        LineEndingHelper.ResolveForFile(path).Should().Be(Environment.NewLine);
    }

    [Fact]
    public void ResolveForFile_SingleLineFile_ReturnsSystemDefault()
    {
        // 无换行 → 探测不到 → 系统默认
        var path = NewFile("single.txt", "no break");
        LineEndingHelper.ResolveForFile(path).Should().Be(Environment.NewLine);
    }

    [Fact]
    public void ConformToFile_KeepsExistingFileStyle()
    {
        var lf = NewFile("keep-lf.txt", "a\nb\n");
        LineEndingHelper.ConformToFile("x\r\ny", lf).Should().Be("x\ny");

        var crlf = NewFile("keep-crlf.txt", "a\r\nb\r\n");
        LineEndingHelper.ConformToFile("x\ny", crlf).Should().Be("x\r\ny");
    }

    [Fact]
    public void ConformToFile_NewFile_UsesSystemDefault()
    {
        var path = Path.Combine(_tempDir, $"new-{Guid.NewGuid():N}.txt");
        LineEndingHelper.ConformToFile("x\ny", path)
            .Should().Be("x" + Environment.NewLine + "y");
    }

    #endregion

    #region 按行切分

    [Fact]
    public void SplitLines_PreservesTrailingEmptyElement()
    {
        // 保留末尾空元素，join 回去才能还原尾换行（与 FileEncodingHelper.ReadAllLines 语义不同）
        LineEndingHelper.SplitLines("A\r\nB\r\n").Should().Equal("A", "B", "");
        LineEndingHelper.SplitLines("A\nB").Should().Equal("A", "B");
        LineEndingHelper.SplitLines("A\rB\r").Should().Equal("A", "B", "");
        LineEndingHelper.SplitLines("").Should().BeEmpty();
        LineEndingHelper.SplitLines(null).Should().BeEmpty();
    }

    [Fact]
    public void SplitLines_RoundTripsWithDetectedEnding()
    {
        foreach (var text in new[] { "A\r\nB\r\n", "a\nb\nc", "x\ry" })
        {
            string ending = LineEndingHelper.Detect(text);
            string rejoined = string.Join(ending, LineEndingHelper.SplitLines(text));
            rejoined.Should().Be(text, "切分再按探测到的风格拼回应完全还原原文本");
        }
    }

    #endregion
}
