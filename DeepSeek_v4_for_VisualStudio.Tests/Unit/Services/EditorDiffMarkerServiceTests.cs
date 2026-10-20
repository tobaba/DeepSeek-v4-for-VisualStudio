using System.Reflection;
using DeepSeek_v4_for_VisualStudio.Services.Editing;

namespace DeepSeek_v4_for_VisualStudio.Tests.Unit.Services;

/// <summary>
/// 编辑器内嵌 Diff 预览服务的回归测试。
///
/// 背景：写穿编辑每写一个文件都会触发 <see cref="Editing.StagedEditWorkspace.FileTracked"/>，
/// 每个订阅者都会重建一次 Inline Diff 视图（两个临时文件 + DifferenceBuffer +
/// IWpfDifferenceViewer，全部在 UI 线程完成）。一旦挂接路径出现重复订阅，
/// 订阅数会随每次刷新翻倍，随后一次写入就能触发指数级的预览重建，界面直接卡死。
/// 因此「同一 Workspace 只订阅一次」必须被固定下来。
/// </summary>
public class EditorDiffMarkerServiceTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly string _backupRoot;

    public EditorDiffMarkerServiceTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"editor_diff_svc_{Guid.NewGuid():N}");
        _backupRoot = Path.Combine(Path.GetTempPath(), $"editor_diff_svc_bak_{Guid.NewGuid():N}");
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

    [Fact]
    public void AttachReviewWorkspace_SameWorkspaceRepeatedly_SubscribesFileTrackedOnce()
    {
        var file = Path.Combine(_tempRoot, "repeat.txt");
        File.WriteAllText(file, "A\n");

        var workspace = new StagedEditWorkspace();
        var service = new EditorDiffMarkerService();

        // 一轮编辑里挂接会被反复调用：每个步骤开头、以及每次会话刷新收尾。
        service.AttachReviewWorkspace(workspace);
        service.AttachReviewWorkspace(workspace);
        service.AttachReviewWorkspace(workspace);

        CountFileTrackedSubscribers(workspace).Should().Be(1);

        // 仍能正常追踪待处理文件（重复挂接不得把当前 Workspace 丢掉）。
        workspace.WriteFile(file, "A2\n");
        service.GetPendingFilePaths().Should().Contain(Path.GetFullPath(file));
    }

    /// <summary>
    /// 读取 FileTracked 事件背后的委托链长度（字段式事件，直接反射私有字段）。
    /// </summary>
    private static int CountFileTrackedSubscribers(StagedEditWorkspace workspace)
    {
        var field = typeof(StagedEditWorkspace).GetField(
            nameof(StagedEditWorkspace.FileTracked),
            BindingFlags.Instance | BindingFlags.NonPublic);

        field.Should().NotBeNull("FileTracked 是字段式事件，应存在同名私有委托字段");

        var handler = field!.GetValue(workspace) as Delegate;
        return handler?.GetInvocationList().Length ?? 0;
    }
}
