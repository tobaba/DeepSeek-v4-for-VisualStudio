using DeepSeek_v4_for_VisualStudio.Services;
using DeepSeek_v4_for_VisualStudio.Services.Editing;

namespace DeepSeek_v4_for_VisualStudio.Tests.Unit.Services.Editing;

/// <summary>
/// Hunk 计算与逐块撤销的回归测试。
///
/// 覆盖两处修复：
/// 1. 纯删除块撤销必须插回原位置 —— 此前 NewStartLine 为 -1，回滚时被 Math.Max(0, -1) 当成 0，
///    导致被删的行被插到文件开头。
/// 2. 撤销必须沿用文件原本的换行风格 —— 此前固定用 \r\n 拼接，撤销一个块会把 LF 文件整体改写成 CRLF。
/// </summary>
public class StagedEditWorkspaceHunkTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly string _backupRoot;

    public StagedEditWorkspaceHunkTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"staged_hunk_{Guid.NewGuid():N}");
        _backupRoot = Path.Combine(Path.GetTempPath(), $"staged_hunk_bak_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempRoot);
        Directory.CreateDirectory(_backupRoot);

        // 测试隔离：备份根指向临时目录，绝不触碰真实 %LOCALAPPDATA%\DeepSeekVS\backups
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

    /// <summary>File.WriteAllText 不做换行转换，因此测试可以精确控制行尾风格。</summary>
    private string WriteSourceFile(string name, string content)
    {
        var path = Path.Combine(_tempRoot, name);
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void RestoreSingleHunk_PureDelete_InsertsBackAtOriginalPosition()
    {
        var file = WriteSourceFile("pure_delete.txt", "A\r\nB\r\nC\r\n");

        var ws = new StagedEditWorkspace();
        ws.WriteFile(file, "A\r\nC\r\n");                 // 删掉中间一行 B

        var hunks = ws.GetHunks(file);
        hunks.Should().ContainSingle();
        hunks[0].IsPureDelete.Should().BeTrue();

        ws.RestoreSingleHunk(file, 0).Should().BeTrue();

        // 修复前 insertAt 取 Math.Max(0, -1) == 0，结果会变成 "B\r\nA\r\nC\r\n"
        File.ReadAllText(file).Should().Be("A\r\nB\r\nC\r\n");
    }

    [Fact]
    public void RestoreSingleHunk_PureDelete_KeepsLfLineEndings()
    {
        var file = WriteSourceFile("pure_delete_lf.txt", "A\nB\nC\n");

        var ws = new StagedEditWorkspace();
        ws.WriteFile(file, "A\nC\n");

        ws.GetHunks(file).Should().ContainSingle();
        ws.RestoreSingleHunk(file, 0).Should().BeTrue();

        // 修复前固定用 \r\n 拼接，整个文件会被改写成 CRLF
        var result = File.ReadAllText(file);
        result.Should().Be("A\nB\nC\n");
        result.Should().NotContain("\r");
    }

    [Fact]
    public void RestoreSingleHunk_PureInsert_RemovesInsertedLines()
    {
        var file = WriteSourceFile("pure_insert.txt", "A\nC\n");

        var ws = new StagedEditWorkspace();
        ws.WriteFile(file, "A\nB\nC\n");

        var hunks = ws.GetHunks(file);
        hunks.Should().ContainSingle();
        hunks[0].IsPureInsert.Should().BeTrue();

        ws.RestoreSingleHunk(file, 0).Should().BeTrue();
        File.ReadAllText(file).Should().Be("A\nC\n");
    }

    [Fact]
    public void RestoreSingleHunk_Modify_RestoresBaselineText()
    {
        var file = WriteSourceFile("modify.txt", "A\nOLD\nC\n");

        var ws = new StagedEditWorkspace();
        ws.WriteFile(file, "A\nNEW\nC\n");

        var hunks = ws.GetHunks(file);
        hunks.Should().ContainSingle();
        hunks[0].IsModify.Should().BeTrue();

        ws.RestoreSingleHunk(file, 0).Should().BeTrue();
        File.ReadAllText(file).Should().Be("A\nOLD\nC\n");
    }

    [Fact]
    public void RestoreSingleHunk_MultiLineBlock_KeepsPositionAndLf()
    {
        var file = WriteSourceFile("multi_line.txt", "A\nB1\nB2\nC\nD\n");

        var ws = new StagedEditWorkspace();
        ws.WriteFile(file, "A\nX1\nX2\nC\nD\n");

        var hunks = ws.GetHunks(file);
        hunks.Should().ContainSingle();
        hunks[0].OldLineCount.Should().Be(2);
        hunks[0].NewLineCount.Should().Be(2);

        ws.RestoreSingleHunk(file, 0).Should().BeTrue();
        File.ReadAllText(file).Should().Be("A\nB1\nB2\nC\nD\n");
    }

    [Fact]
    public void RestoreSingleHunk_CrlfFile_StaysCrlf()
    {
        var file = WriteSourceFile("crlf.txt", "A\r\nB\r\nC\r\n");

        var ws = new StagedEditWorkspace();
        ws.WriteFile(file, "A\r\nC\r\n");

        ws.GetHunks(file).Should().ContainSingle();
        ws.RestoreSingleHunk(file, 0).Should().BeTrue();

        File.ReadAllText(file).Should().Be("A\r\nB\r\nC\r\n");
    }

    [Fact]
    public void GetHunks_KeepsAcceptedFlag_WhenLineEndingsChangeOnDisk()
    {
        var file = WriteSourceFile("flag.txt", "A\nB1\nB2\nC\n");

        var ws = new StagedEditWorkspace();
        ws.WriteFile(file, "A\nX1\nX2\nC\n");

        ws.GetHunks(file).Should().ContainSingle();
        ws.AcceptSingleHunk(file, 0).Should().BeTrue();

        // 期间文件行尾被外部改写成 CRLF：OldText/NewText 的分隔符随之变化，
        // 标记回填需按"行内容"比较，不能按原始字符串比较，否则已保留标记会丢失。
        File.WriteAllText(file, "A\r\nX1\r\nX2\r\nC\r\n");

        var hunks = ws.GetHunks(file);
        hunks.Should().ContainSingle();
        hunks[0].IsAccepted.Should().BeTrue("换行风格变化不应导致已保留标记丢失");
    }

    [Fact]
    public void GetHunks_LargeFileSmallEdit_ProducesSingleHunk()
    {
        // 内存回归：改动只占很小一段时，裁剪后不应再按整个文件规模建表。
        // 这里用 3000 行文件验证结果正确性与 hunk 收敛性。
        var lines = Enumerable.Range(1, 3000).Select(i => $"line {i}").ToArray();
        var baseline = string.Join("\n", lines) + "\n";

        lines[1500] = "line 1501 CHANGED";
        var edited = string.Join("\n", lines) + "\n";

        var file = WriteSourceFile("large.txt", baseline);

        var ws = new StagedEditWorkspace();
        ws.WriteFile(file, edited);

        var hunks = ws.GetHunks(file);
        hunks.Should().ContainSingle();
        hunks[0].OldStartLine.Should().Be(1500);
        hunks[0].OldLineCount.Should().Be(1);
        hunks[0].NewLineCount.Should().Be(1);

        ws.RestoreSingleHunk(file, 0).Should().BeTrue();
        File.ReadAllText(file).Should().Be(baseline);
    }
}
