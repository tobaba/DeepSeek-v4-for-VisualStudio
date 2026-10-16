using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DeepSeek_v4_for_VisualStudio.Services;

namespace DeepSeek_v4_for_VisualStudio.Tests.Unit.Services;

/// <summary>
/// <see cref="AttachmentCollector"/> 单元测试。
/// 覆盖分类（新增/重复/格式不支持/目录/不存在）、去重语义（含大小写与相对路径归一化）、
/// 批次内重复、空白输入容错，以及「已附加列表不被修改」的纯函数契约。
/// 交互层（拖放事件处理器 / 文件对话框）依赖控件实例状态，属 UI 集成范畴，不在本测试覆盖范围内。
/// </summary>
public class AttachmentCollectorTests : IDisposable
{
    /// <summary>测试专用临时目录（每个测试实例独立，避免并行冲突）。</summary>
    private readonly string _testDir;

    public AttachmentCollectorTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "DeepSeekVS_AttachmentCollectorTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_testDir))
            {
                Directory.Delete(_testDir, recursive: true);
            }
        }
        catch
        {
            // 清理失败不影响测试结论（临时目录由系统回收）
        }
    }

    /// <summary>在临时目录下创建一个受支持格式的文件并返回其全路径。</summary>
    private string CreateFile(string fileName, string content = "x")
    {
        string path = Path.Combine(_testDir, fileName);
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void Collect_SupportedNewFile_GoesToAdded()
    {
        string file = CreateFile("a.cs");

        var result = AttachmentCollector.Collect(new[] { file }, Array.Empty<string>());

        result.Added.Should().ContainSingle().Which.Should().Be(file);
        result.AddedCount.Should().Be(1);
        result.AlreadyAttached.Should().BeEmpty();
        result.Unsupported.Should().BeEmpty();
        result.Directories.Should().BeEmpty();
        result.NotFound.Should().BeEmpty();
        result.HasSkipped.Should().BeFalse();
    }

    [Fact]
    public void Collect_FileAlreadyInExisting_GoesToAlreadyAttached()
    {
        string file = CreateFile("dup.cs");

        var result = AttachmentCollector.Collect(new[] { file }, new[] { file });

        result.Added.Should().BeEmpty();
        result.AlreadyAttached.Should().ContainSingle().Which.Should().Be(file);
        result.HasSkipped.Should().BeTrue();
    }

    [Fact]
    public void Collect_DuplicateWithinSameBatch_IsAttachedOnce()
    {
        string file = CreateFile("twice.cs");

        var result = AttachmentCollector.Collect(new[] { file, file }, Array.Empty<string>());

        // 同批次内重复：首次添加、第二次归入已附加，避免产生重复附件
        result.Added.Should().ContainSingle();
        result.AlreadyAttached.Should().ContainSingle();
    }

    [Fact]
    public void Collect_PathCasingDiffers_TreatedAsDuplicate()
    {
        // 归一化后按 OrdinalIgnoreCase 比较：Windows 下同一文件不应重复附加。
        // 注意：这是相对既有内联实现（直接比较原始字符串）的有意行为改进。
        string file = CreateFile("case.cs");
        string upper = file.ToUpperInvariant();

        var result = AttachmentCollector.Collect(new[] { upper }, new[] { file });

        result.Added.Should().BeEmpty();
        result.AlreadyAttached.Should().ContainSingle();
    }

    [Fact]
    public void Collect_ExistingEntryWithDifferentCasing_StillDetectedAsDuplicate()
    {
        string file = CreateFile("case2.cs");

        var result = AttachmentCollector.Collect(new[] { file }, new[] { file.ToUpperInvariant() });

        result.Added.Should().BeEmpty("已附加列表同样应归一化后比较");
        result.AlreadyAttached.Should().ContainSingle();
    }

    [Fact]
    public void Collect_UnsupportedExtension_GoesToUnsupported()
    {
        string file = CreateFile("weird.zzz9");

        var result = AttachmentCollector.Collect(new[] { file }, Array.Empty<string>());

        result.Unsupported.Should().ContainSingle().Which.Should().Be(file);
        result.Added.Should().BeEmpty();
    }

    [Fact]
    public void Collect_NonExistentFile_GoesToNotFound()
    {
        string missing = Path.Combine(_testDir, "nope.cs");

        var result = AttachmentCollector.Collect(new[] { missing }, Array.Empty<string>());

        result.NotFound.Should().ContainSingle().Which.Should().Be(missing);
        result.Added.Should().BeEmpty();
        result.Unsupported.Should().BeEmpty("不存在应先于格式判定，避免误导性的「格式不支持」提示");
    }

    [Fact]
    public void Collect_Directory_GoesToDirectories_WithoutRecursing()
    {
        CreateFile("inside.cs");
        string subDir = Path.Combine(_testDir, "sub");
        Directory.CreateDirectory(subDir);
        CreateFile(Path.Combine("sub", "nested.cs"));

        var result = AttachmentCollector.Collect(new[] { subDir }, Array.Empty<string>());

        result.Directories.Should().ContainSingle().Which.Should().Be(subDir);
        result.Added.Should().BeEmpty("目录不递归展开");
    }

    [Fact]
    public void Collect_BlankAndNullCandidates_AreIgnoredSafely()
    {
        var result = AttachmentCollector.Collect(new[] { null!, "", "   " }, Array.Empty<string>());

        result.BlankIgnored.Should().Be(3);
        result.Added.Should().BeEmpty();
        result.NotFound.Should().BeEmpty();
        result.Unsupported.Should().BeEmpty();
        result.Directories.Should().BeEmpty();
    }

    [Fact]
    public void Collect_NullCandidates_ReturnsEmptyResult()
    {
        var result = AttachmentCollector.Collect(null, null);

        result.Added.Should().BeEmpty();
        result.HasSkipped.Should().BeFalse();
        result.BlankIgnored.Should().Be(0);
    }

    [Fact]
    public void Collect_MixedBatch_ClassifiesEachCategory()
    {
        string ok = CreateFile("ok.cs");
        string dup = CreateFile("dup2.cs");
        string bad = CreateFile("bad.zzz9");
        string missing = Path.Combine(_testDir, "missing.cs");
        string dir = Path.Combine(_testDir, "d");
        Directory.CreateDirectory(dir);

        var result = AttachmentCollector.Collect(
            new[] { ok, dup, bad, missing, dir },
            new[] { dup });

        result.Added.Should().Equal(ok);
        result.AlreadyAttached.Should().Equal(dup);
        result.Unsupported.Should().Equal(bad);
        result.NotFound.Should().Equal(missing);
        result.Directories.Should().Equal(dir);
        result.AddedCount.Should().Be(1);
    }

    [Fact]
    public void Collect_DoesNotMutateExistingCollection()
    {
        // 纯函数契约：调用方负责把 Added 写回自己的列表，Collect 不产生副作用。
        string file = CreateFile("immutable.cs");
        var existing = new List<string> { "placeholder" };
        var snapshot = existing.ToList();

        AttachmentCollector.Collect(new[] { file }, existing);

        existing.Should().Equal(snapshot);
    }

    [Fact]
    public void Collect_ReturnsAbsolutePaths_ForRelativeInput()
    {
        // 相对路径应被归一化为绝对全路径，保证与附件列表中的绝对路径可比对。
        var result = AttachmentCollector.Collect(new[] { "relative.cs" }, Array.Empty<string>());

        result.NotFound.Concat(result.Added).Should().OnlyContain(p => Path.IsPathRooted(p));
    }
}