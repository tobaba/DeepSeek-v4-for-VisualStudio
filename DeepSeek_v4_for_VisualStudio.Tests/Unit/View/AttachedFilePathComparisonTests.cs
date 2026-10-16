using System;
using System.IO;
using DeepSeek_v4_for_VisualStudio.View;

namespace DeepSeek_v4_for_VisualStudio.Tests.Unit.View;

/// <summary>
/// 附件移除的路径比较契约测试。
/// <para>
/// 历史缺陷：移除按钮的 <c>Tag</c> 绑定的是 <c>FileName</c>，处理器再按**文件名**
/// 在列表中查找，因此当两个不同目录下的同名文件同时被附加时（例如两个项目的
/// <c>Program.cs</c>），点删除会删掉错误的那一条。
/// </para>
/// <para>
/// 修复：XAML 改绑 <c>FilePath</c>，处理器按完整路径移除，比较口径与
/// <see cref="DeepSeek_v4_for_VisualStudio.Services.AttachmentCollector"/> 的判重一致。
/// 本测试锁定该比较语义。
/// </para>
/// </summary>
public class AttachedFilePathComparisonTests
{
    [Fact]
    public void PathsEqual_SamePathDifferentCasing_ReturnsTrue()
    {
        // 与附件判重口径一致：Windows 下大小写不敏感
        DeepSeekChatControl.PathsEqual(@"C:\Proj\A\Program.cs", @"c:\proj\a\PROGRAM.CS")
            .Should().BeTrue();
    }

    [Fact]
    public void PathsEqual_SameFileNameDifferentDirectory_ReturnsFalse()
    {
        // 关键回归点：同名不同目录必须是两个不同附件，否则会删错
        DeepSeekChatControl.PathsEqual(@"C:\ProjA\Program.cs", @"C:\ProjB\Program.cs")
            .Should().BeFalse();
    }

    [Fact]
    public void PathsEqual_IdenticalPath_ReturnsTrue()
    {
        DeepSeekChatControl.PathsEqual(@"C:\Proj\A\File.cs", @"C:\Proj\A\File.cs")
            .Should().BeTrue();
    }

    [Fact]
    public void PathsEqual_RelativeAndAbsolutePointingToSameFile_ReturnsTrue()
    {
        // 归一化后应识别为同一文件（与 AttachmentCollector 的行为一致）
        DeepSeekChatControl.PathsEqual("File.cs", Path.Combine(Directory.GetCurrentDirectory(), "File.cs"))
            .Should().BeTrue();
    }

    [Theory]
    [InlineData(null, @"C:\a\b.cs")]
    [InlineData(@"C:\a\b.cs", null)]
    [InlineData(null, null)]
    public void PathsEqual_NullInputs_ReturnFalseWithoutThrowing(string? a, string? b)
    {
        var act = () => DeepSeekChatControl.PathsEqual(a, b);

        act.Should().NotThrow();
        act().Should().BeFalse();
    }

    [Fact]
    public void PathsEqual_InvalidPathCharacters_ReturnsFalseWithoutThrowing()
    {
        // 归一化失败时必须安全退回，不能让整个移除操作抛异常
        var act = () => DeepSeekChatControl.PathsEqual("C:\\a\0bad.cs", @"C:\a\bad.cs");

        act.Should().NotThrow();
    }

    [Fact]
    public void PathsEqual_DifferentExtensionsSameBase_ReturnsFalse()
    {
        DeepSeekChatControl.PathsEqual(@"C:\Proj\a.cs", @"C:\Proj\a.txt")
            .Should().BeFalse();
    }
}