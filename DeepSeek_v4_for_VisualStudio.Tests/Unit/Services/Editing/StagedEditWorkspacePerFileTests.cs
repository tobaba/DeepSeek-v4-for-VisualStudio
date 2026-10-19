using DeepSeek_v4_for_VisualStudio.Services;
using DeepSeek_v4_for_VisualStudio.Services.Editing;

namespace DeepSeek_v4_for_VisualStudio.Tests.Unit.Services.Editing;

/// <summary>
/// 文件改动列表依赖的「单文件」能力回归测试：
/// 每个文件改完即时登记（FileTracked）、单文件确认 / 撤销、真实增删行数统计。
/// </summary>
public class StagedEditWorkspacePerFileTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly string _backupRoot;

    public StagedEditWorkspacePerFileTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"staged_perfile_{Guid.NewGuid():N}");
        _backupRoot = Path.Combine(Path.GetTempPath(), $"staged_perfile_bak_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempRoot);
        Directory.CreateDirectory(_backupRoot);

        BackupService.BaseDirOverride = _backupRoot;
        BackupService.EndSession();
    }

    public void Dispose()
    {
        BackupService.EndSession();
        BackupService.BaseDirOverride = null;
        try { if (Directory.Exists(_tempRoot)) Directory.Delete(_tempRoot, true); } catch { }
        try { if (Directory.Exists(_backupRoot)) Directory.Delete(_backupRoot, true); } catch { }
    }

    private string WriteSourceFile(string name, string content)
    {
        var path = Path.Combine(_tempRoot, name);
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void WriteFile_FirstTouch_RaisesFileTrackedOnce()
    {
        var file = WriteSourceFile("one.txt", "A\nB\nC\n");
        var ws = new StagedEditWorkspace();
        var tracked = new List<string>();
        ws.FileTracked += tracked.Add;

        ws.WriteFile(file, "A\nB2\nC\n");
        ws.WriteFile(file, "A\nB3\nC\n");

        tracked.Should().ContainSingle();
        tracked[0].Should().Be(Path.GetFullPath(file));
    }

    [Fact]
    public void GetPendingLineStats_ReportsRealDiffCounts_NotWholeFile()
    {
        // 20 行的文件只改 1 行：应报告 +1 -1，而不是整文件行数。
        string original = string.Join("\n", Enumerable.Range(1, 20).Select(i => $"line {i}")) + "\n";
        string modified = original.Replace("line 10", "line ten");
        var file = WriteSourceFile("counts.txt", original);

        var ws = new StagedEditWorkspace();
        ws.WriteFile(file, modified);

        var stats = ws.GetPendingLineStats(file);
        stats.Added.Should().Be(1);
        stats.Removed.Should().Be(1);
    }

    [Fact]
    public void ConfirmFile_OnlyClearsTargetFile()
    {
        var a = WriteSourceFile("a.txt", "A\n");
        var b = WriteSourceFile("b.txt", "B\n");
        var ws = new StagedEditWorkspace();
        ws.WriteFile(a, "A2\n");
        ws.WriteFile(b, "B2\n");

        ws.ConfirmFile(a);

        ws.HasTrackedFile(a).Should().BeFalse();
        ws.HasTrackedFile(b).Should().BeTrue();
        File.ReadAllText(a).Should().Be("A2\n"); // 确认保留磁盘内容
    }

    [Fact]
    public void RestoreFileToBaseline_OnlyRevertsTargetFile()
    {
        var a = WriteSourceFile("a.txt", "A\n");
        var b = WriteSourceFile("b.txt", "B\n");
        var ws = new StagedEditWorkspace();
        ws.WriteFile(a, "A2\n");
        ws.WriteFile(b, "B2\n");

        ws.RestoreFileToBaseline(a);

        File.ReadAllText(a).Should().Be("A\n");
        File.ReadAllText(b).Should().Be("B2\n"); // 其它文件不受影响
        ws.HasTrackedFile(a).Should().BeFalse();
        ws.HasTrackedFile(b).Should().BeTrue();
    }

    [Fact]
    public void GetPendingLineStats_AfterAllHunksAccepted_ReturnsZero()
    {
        var file = WriteSourceFile("hunks.txt", "A\nB\nC\n");
        var ws = new StagedEditWorkspace();
        ws.WriteFile(file, "A\nB2\nC\n");

        ws.GetHunks(file).Should().ContainSingle();
        ws.AcceptSingleHunk(file, 0).Should().BeTrue();

        var stats = ws.GetPendingLineStats(file);
        stats.Added.Should().Be(0);
        stats.Removed.Should().Be(0);
        ws.HasPendingHunks(file).Should().BeFalse();
    }
}
