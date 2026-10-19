using DeepSeek_v4_for_VisualStudio.Models;
using DeepSeek_v4_for_VisualStudio.Services.Editing;
using DeepSeek_v4_for_VisualStudio.Utils;
using DeepSeek_v4_for_VisualStudio.View.Hosts;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

namespace DeepSeek_v4_for_VisualStudio.Services
{
    /// <summary>
    /// 编辑器内嵌 Diff 预览管理服务（preview-then-commit）。
    ///
    /// 单文件、单会话：预览阶段不修改 sourceBuffer，用户在 <see cref="View.Hosts.EditorDiffHost"/>
    /// 上逐块「保留 / 撤销」，或整体接受/撤销；写穿模式下通过
    /// <see cref="Editing.StagedEditWorkspace"/> 恢复/确认磁盘 Baseline。
    /// </summary>
    public class EditorDiffMarkerService
    {
        #region Singleton

        private static EditorDiffMarkerService? _instance;
        private static readonly object _instanceLock = new();

        public static EditorDiffMarkerService Instance
        {
            get
            {
                if (_instance == null)
                {
                    lock (_instanceLock)
                    {
                        _instance ??= new EditorDiffMarkerService();
                    }
                }
                return _instance;
            }
        }

        #endregion

        #region Fields

        private readonly InlineDiffSessionManager _sessionManager = new();
        private readonly Dictionary<ITextBuffer, EditorDiffHost> _editorHosts = new();
        private readonly object _editorHostsLock = new();

        // ── 待处理改动追踪（供聊天区「文件改动列表」驱动）──
        // 一轮编辑的多个文件共用同一个 StagedEditWorkspace；列表按 Workspace 的
        // 实时追踪集合渲染，某文件被保留/撤销后自然从列表消失。
        private readonly object _reviewLock = new();
        private Editing.StagedEditWorkspace? _reviewWorkspace;
        private int _reviewTurnUserMsgIndex = -1;

        /// <summary>
        /// 待处理改动集合发生变化（文件新增/确认/撤销）时触发。
        /// 供聊天区刷新文件改动列表；触发线程不固定，订阅方需自行切回 UI 线程。
        /// </summary>
        public event EventHandler? PendingChangesChanged;

        #endregion

        #region Public API — Diff Preview

        /// <summary>
        /// 创建 Inline Diff 预览会话（preview-then-commit）。
        /// 预览阶段不修改 sourceBuffer，用户确认后才通过 ProposalCommitCoordinator 提交。
        /// </summary>
        /// <param name="textView">目标 WPF 文本视图（缓冲区应仍为原始内容）</param>
        /// <param name="originalContent">修改前的原始代码</param>
        /// <param name="proposedContent">AI 建议的新代码</param>
        /// <param name="textChanges">结构化改动（可选，逐块撤销用）</param>
        /// <param name="workspace">写穿编辑工作区（可选，撤销时恢复磁盘 Baseline）</param>
        public InlineDiffSession? CreateInlineDiffPreview(
            IWpfTextView textView, string originalContent, string proposedContent,
            IReadOnlyList<ProposedTextChange>? textChanges = null,
            Editing.StagedEditWorkspace? workspace = null)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (textView == null)
                throw new ArgumentNullException(nameof(textView));

            if (string.IsNullOrEmpty(proposedContent) || originalContent == proposedContent)
                return null;

            // 获取文件路径
            string filePath = GetFilePathFromBuffer(textView.TextBuffer)
                ?? textView.TextBuffer.GetHashCode().ToString();

            // 构建 PreparedChangeSet
            var change = new PreparedChangeSet
            {
                FilePath = filePath,
                Operation = ProposedFileOperation.Modify,
                BaselineText = originalContent,
                ProposedText = proposedContent,
                TextChanges = textChanges ?? Array.Empty<ProposedTextChange>(),
                ContentTypeName = textView.TextBuffer.ContentType.TypeName,
                SaveBehavior = ProposalSaveBehavior.KeepDocumentDirty,
            };

            // ── 同一文档已有活跃写穿会话（AI 在预览期间再次编辑同一文件）→ 原地刷新 ──
            // 旧会话的 Baseline / Hunks 已过期，直接创建会被单文档单 Session 约束拒绝。
            if (workspace != null &&
                _sessionManager.TryGetSession(textView.TextBuffer, out var existingSession) &&
                existingSession != null)
            {
                RefreshWriteThroughSession(existingSession, change, workspace, textView.TextBuffer);
                Logger.Info($"[EditorDiff] Inline Diff Session 已刷新: {existingSession.SessionId.Substring(0, 8)} ({Path.GetFileName(filePath)})");
                return existingSession;
            }

            // 创建 Session
            var session = _sessionManager.CreateSession(textView, change);

            if (session == null)
            {
                Logger.Warn($"[EditorDiff] 无法创建 Session: {Path.GetFileName(filePath)}");
                return null;
            }

            // ── 写穿模式：关联 Workspace，撤销时恢复磁盘 Baseline ──
            if (workspace != null)
            {
                session.Workspace = workspace;
                AttachReviewWorkspace(workspace);
            }

            // 显示：先登记宿主，待编辑器首帧渲染后再挂载覆盖层，避免阻塞视图创建
            var editorHost = new EditorDiffHost(textView);
            editorHost.Closed += (_, __) =>
            {
                lock (_editorHostsLock)
                {
                    if (_editorHosts.TryGetValue(textView.TextBuffer, out var current) &&
                        ReferenceEquals(current, editorHost))
                    {
                        _editorHosts.Remove(textView.TextBuffer);
                    }
                }
            };

            lock (_editorHostsLock)
            {
                _editorHosts[textView.TextBuffer] = editorHost;
            }

            textView.VisualElement.Dispatcher.BeginInvoke(
                new Action(() =>
                {
                    if (!textView.IsClosed && session.State == InlineDiffSessionState.Showing)
                        editorHost.Show(session);
                }),
                System.Windows.Threading.DispatcherPriority.Loaded);

            Logger.Info($"[EditorDiff] Inline Diff Session 已创建: {session.SessionId.Substring(0, 8)} ({Path.GetFileName(filePath)})");

            return session;
        }

        /// <summary>
        /// 原地刷新已有写穿会话：把撤销追踪移交新 Workspace、替换变更内容，
        /// 并通过 EditorDiffHost 重建只读视图与 hunk 按钮。
        /// </summary>
        private void RefreshWriteThroughSession(
            InlineDiffSession session,
            PreparedChangeSet change,
            Editing.StagedEditWorkspace workspace,
            ITextBuffer buffer)
        {
            var oldWorkspace = session.Workspace;
            if (oldWorkspace != null && !ReferenceEquals(oldWorkspace, workspace))
                oldWorkspace.DiscardFile(change.FilePath);

            session.ReplaceChange(change);
            session.Workspace = workspace;

            lock (_reviewLock)
            {
                if (ReferenceEquals(_reviewWorkspace, workspace))
                    _reviewWorkspace = null;
            }

            lock (_editorHostsLock)
            {
                if (_editorHosts.TryGetValue(buffer, out var editorHost))
                    editorHost.RefreshSession(session);
            }
            AttachReviewWorkspace(workspace);
        }

        /// <summary>
        /// 编辑器内嵌挂件：获取指定 buffer 的活跃 Session 及差异块（Hunks）。
        /// 返回 null 表示该 buffer 无活跃 Session。
        /// </summary>
        public InlineDiffSession? GetActiveSessionForBuffer(ITextBuffer buffer)
        {
            if (_sessionManager.TryGetSession(buffer, out var session))
                return session;
            return null;
        }

        /// <summary>
        /// 编辑器内嵌挂件：撤销指定 buffer 的某个差异块，并刷新编辑器。
        /// </summary>
        public bool RevertHunkForBuffer(ITextBuffer buffer, int hunkIndex)
        {
            var session = GetActiveSessionForBuffer(buffer);
            if (session?.Workspace == null) return false;

            bool ok = session.Workspace.RestoreSingleHunk(session.Change.FilePath, hunkIndex);
            if (ok)
            {
                Logger.Info($"[EditorDiff] 编辑器内嵌：已撤销块 [{hunkIndex}] ({System.IO.Path.GetFileName(session.Change.FilePath)})");
            }
            return ok;
        }

        /// <summary>
        /// 编辑器内嵌挂件：保留指定 buffer 的某个差异块（磁盘不变，仅确认该块）。
        /// </summary>
        public bool AcceptHunkForBuffer(ITextBuffer buffer, int hunkIndex)
        {
            var session = GetActiveSessionForBuffer(buffer);
            if (session?.Workspace == null) return false;

            bool ok = session.Workspace.AcceptSingleHunk(session.Change.FilePath, hunkIndex);
            if (ok)
            {
                Logger.Info($"[EditorDiff] 编辑器内嵌：已保留块 [{hunkIndex}] ({System.IO.Path.GetFileName(session.Change.FilePath)})");
            }
            return ok;
        }

        /// <summary>
        /// 编辑器内嵌挂件：保留全部。写穿模式下磁盘即最终内容，
        /// 只确认并清除撤销追踪（不重写文件）；非写穿模式走正常 Commit 流程。
        /// </summary>
        public void ConfirmAllForBuffer(ITextBuffer buffer)
        {
            var session = GetActiveSessionForBuffer(buffer);
            if (session == null) return;

            CloseEditorHost(buffer);

            if (session.Workspace != null)
            {
                session.ConfirmWriteThrough();
            }
            else
            {
                _ = session.CommitAsync(System.Threading.CancellationToken.None);
            }
        }

        /// <summary>
        /// 编辑器内嵌挂件：撤销全部（恢复 Baseline 并关闭内嵌 Diff 覆盖层）。
        /// </summary>
        public void DismissSessionForBuffer(ITextBuffer buffer)
        {
            var session = GetActiveSessionForBuffer(buffer);
            if (session == null) return;

            CloseEditorHost(buffer);
            session.Dismiss();
            RaisePendingChangesChanged();
        }

        #endregion

        #region Public API — Pending Change Tracking

        /// <summary>
        /// 登记本轮编辑的待处理 Workspace（Agent 每个文件完成写入时调用）。
        /// 同一轮的多个文件共用一个 Workspace，重复登记幂等。
        /// </summary>
        public void AttachReviewWorkspace(Editing.StagedEditWorkspace workspace)
        {
            if (workspace == null) return;

            Editing.StagedEditWorkspace? previous;
            lock (_reviewLock)
            {
                if (ReferenceEquals(_reviewWorkspace, workspace)) return;
                previous = _reviewWorkspace;
                DetachReviewWorkspaceLocked();
                _reviewWorkspace = workspace;
                workspace.FileTracked += OnReviewFileTracked;
            }
            // 新一轮编辑接管后，关闭上一轮残留的预览覆盖层（磁盘内容保持不变）。
            CloseReviewSessions(previous);
            // 上一轮未处理完的改动默认保留（磁盘已是最终内容），同时清理其撤销备份。
            try { previous?.ConfirmAll(); } catch (Exception ex) { Logger.Warn($"[EditorDiff] 清理上一轮 Review Workspace 失败: {ex.Message}"); }
            RaisePendingChangesChanged();
        }

        /// <summary>
        /// 记录本轮待处理改动归属的用户消息索引（会话切换清空时使用）。
        /// </summary>
        public void SetReviewTurn(int userMsgIndex)
        {
            lock (_reviewLock) { _reviewTurnUserMsgIndex = userMsgIndex; }
        }

        /// <summary>本轮待处理改动归属的用户消息索引；无活动轮时为 -1。</summary>
        public int ReviewTurnUserMsgIndex
        {
            get { lock (_reviewLock) return _reviewTurnUserMsgIndex; }
        }

        /// <summary>
        /// 当前仍有待处理改动的文件路径快照（相对/绝对取决于写入方，通常为绝对路径）。
        /// </summary>
        public IReadOnlyList<string> GetPendingFilePaths()
        {
            lock (_reviewLock)
            {
                return _reviewWorkspace != null
                    ? _reviewWorkspace.GetTrackedFilePaths()
                    : (IReadOnlyList<string>)Array.Empty<string>();
            }
        }

        /// <summary>指定文件当前是否仍有待处理改动。</summary>
        public bool HasPendingFile(string filePath)
        {
            lock (_reviewLock)
            {
                return _reviewWorkspace != null && _reviewWorkspace.HasTrackedFile(filePath);
            }
        }

        /// <summary>
        /// 指定文件「待处理差异」的真实增删行数（仅统计未保留/未撤销的块）。
        /// </summary>
        public (int Added, int Removed) GetPendingLineStats(string filePath)
        {
            lock (_reviewLock)
            {
                return _reviewWorkspace != null
                    ? _reviewWorkspace.GetPendingLineStats(filePath)
                    : (0, 0);
            }
        }

        /// <summary>
        /// 为指定文件打开（或聚焦）内嵌 Diff 预览。
        /// 从「文件改动列表」点击条目时调用：已打开的文件直接复用其活跃会话，
        /// 未打开的文件按需打开编辑器后再创建会话。
        /// </summary>
        public async Task<bool> OpenDiffPreviewForFileAsync(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath)) return false;

            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            Editing.StagedEditWorkspace? workspace;
            lock (_reviewLock) { workspace = _reviewWorkspace; }

            // 只显示「尚未处理」的块：已保留/已撤销的块并入基线，不再重复出现在预览里。
            string baseline = workspace != null && workspace.HasTrackedFile(filePath)
                ? workspace.BuildPendingOnlyBaseline(filePath)
                : (workspace?.GetBaseline(filePath) ?? string.Empty);
            string proposed = File.Exists(filePath)
                ? Utils.FileEncodingHelper.ReadAllText(filePath)
                : string.Empty;

            IWpfTextView? view = FindWpfTextViewForFile(filePath);
            if (view == null)
            {
                view = await OpenDocumentAndGetViewAsync(filePath);
            }
            if (view == null) return false;

            // 已有活跃会话 → 复用，但先确认覆盖层还挂在编辑器上
            // （切换文档标签会把可视树卸载/重载，Adorner 可能已被 WPF 丢弃）。
            if (_sessionManager.TryGetSession(view.TextBuffer, out var existing) && existing != null)
            {
                EnsureEditorHostOverlay(view.TextBuffer);
                return true;
            }

            var session = CreateInlineDiffPreview(
                view, baseline, proposed, textChanges: null, workspace: workspace);
            return session != null;
        }

        /// <summary>
        /// 全部应用：确认所有待处理文件（清除撤销追踪，保留磁盘内容）并收起预览。
        /// </summary>
        public void ApplyAllPending()
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            Editing.StagedEditWorkspace? workspace;
            lock (_reviewLock) { workspace = _reviewWorkspace; }

            // 先关闭本轮所有编辑器覆盖层，再确认文件，避免覆盖层挂在已确认的文件上。
            CloseReviewSessions(workspace);
            workspace?.ConfirmAll();
            RaisePendingChangesChanged();
        }

        /// <summary>
        /// 全部撤销：把所有待处理文件恢复到 Baseline（写回磁盘）并收起预览。
        /// </summary>
        public void RevertAllPending()
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            Editing.StagedEditWorkspace? workspace;
            lock (_reviewLock) { workspace = _reviewWorkspace; }

            CloseReviewSessions(workspace);
            workspace?.RestoreToBaseline();
            RaisePendingChangesChanged();
        }

        /// <summary>
        /// 清空待处理追踪（切换会话 / 新建 / 回退整轮后调用）。
        /// 不修改磁盘：调用方需自行决定是否先确认或撤销。
        /// </summary>
        public void ClearPendingTracking()
        {
            Editing.StagedEditWorkspace? workspace;
            lock (_reviewLock)
            {
                workspace = _reviewWorkspace;
                DetachReviewWorkspaceLocked();
            }
            CloseReviewSessions(workspace);
            try { workspace?.ConfirmAll(); } catch (Exception ex) { Logger.Warn($"[EditorDiff] 清理 Review Workspace 失败: {ex.Message}"); }
            RaisePendingChangesChanged();
        }

        /// <summary>
        /// 某文件被单独保留/撤销后主动通知列表刷新（逐块操作收尾时调用）。
        /// </summary>
        public void NotifyPendingChanged()
        {
            RaisePendingChangesChanged();
        }

        #endregion

        #region Private — Pending Change Tracking

        private void OnReviewFileTracked(string filePath)
        {
            RaisePendingChangesChanged();
        }

        private void DetachReviewWorkspaceLocked()
        {
            if (_reviewWorkspace != null)
            {
                _reviewWorkspace.FileTracked -= OnReviewFileTracked;
                _reviewWorkspace = null;
            }
            _reviewTurnUserMsgIndex = -1;
        }

        /// <summary>关闭属于指定 Workspace 的编辑器覆盖层并释放对应 Session。</summary>
        private void CloseReviewSessions(Editing.StagedEditWorkspace? workspace)
        {
            if (workspace == null) return;

            if (!ThreadHelper.CheckAccess())
            {
                // EditorDiffHost.Close 有 UI 线程亲和性，必须切回 UI 线程。
                // 这里用 RunAsync 而不是 Run：调用方可能正持有其它锁（例如会话切换路径），
                // 同步等待 UI 线程会与文档加载 / 编辑器写入互相阻塞，表现为「正在加载文档…」卡死。
                _ = ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
                {
                    try
                    {
                        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                        CloseReviewSessionsCore(workspace);
                    }
                    catch (Exception ex)
                    {
                        Logger.Warn($"[EditorDiff] 异步关闭预览失败: {ex.Message}");
                    }
                });
                return;
            }

            CloseReviewSessionsCore(workspace);
        }

        private void CloseReviewSessionsCore(Editing.StagedEditWorkspace workspace)
        {
            foreach (var session in _sessionManager.GetActiveSessions())
            {
                if (!ReferenceEquals(session.Workspace, workspace)) continue;
                try
                {
                    if (session.SourceBuffer != null)
                        CloseEditorHost(session.SourceBuffer);
                }
                catch (Exception ex) { Logger.Warn($"[EditorDiff] 关闭全部应用/撤销覆盖层失败: {ex.Message}"); }

                try { session.Dispose(); }
                catch (Exception ex) { Logger.Warn($"[EditorDiff] 释放全部应用/撤销会话失败: {ex.Message}"); }
            }
        }

        private void RaisePendingChangesChanged()
        {
            try { PendingChangesChanged?.Invoke(this, EventArgs.Empty); }
            catch (Exception ex) { Logger.Warn($"[EditorDiff] PendingChangesChanged 订阅异常: {ex.Message}"); }
        }

        private static IWpfTextView? FindWpfTextViewForFile(string filePath)
            => ToolWindows.TerminalWindowHelper.FindWpfTextViewForFile(filePath);

        private static async Task<IWpfTextView?> OpenDocumentAndGetViewAsync(string filePath)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            try
            {
                var openDoc = (Microsoft.VisualStudio.Shell.Interop.IVsUIShellOpenDocument?)
                    Package.GetGlobalService(typeof(Microsoft.VisualStudio.Shell.Interop.SVsUIShellOpenDocument));
                if (openDoc == null) return null;

                var logicalView = Microsoft.VisualStudio.VSConstants.LOGVIEWID_Primary;
                openDoc.OpenDocumentViaProject(
                    filePath, ref logicalView, out _, out _, out _,
                    out Microsoft.VisualStudio.Shell.Interop.IVsWindowFrame frame);
                frame?.Show();

                // 等待编辑器首帧装载后再取视图
                for (int i = 0; i < 20; i++)
                {
                    var view = FindWpfTextViewForFile(filePath);
                    if (view != null) return view;
                    await Task.Delay(100);
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                }
            }
            catch (Exception ex)
            {
                Logger.Warn($"[EditorDiff] 打开文档并获取视图失败 ({filePath}): {ex.Message}");
            }
            return null;
        }

        #endregion

        #region Private Methods

        /// <summary>
        /// 从 ITextBuffer 获取关联的文件路径。
        /// </summary>
        private static string? GetFilePathFromBuffer(ITextBuffer buffer)
        {
            if (buffer.Properties.TryGetProperty(typeof(ITextDocument), out ITextDocument textDoc))
                return textDoc.FilePath;
            return null;
        }

        /// <summary>
        /// 关闭编辑器内嵌 Diff 宿主（从 adornment 层移除）。
        /// </summary>
        private void CloseEditorHost(ITextBuffer buffer)
        {
            lock (_editorHostsLock)
            {
                if (_editorHosts.TryGetValue(buffer, out var host))
                {
                    host.Close();
                    _editorHosts.Remove(buffer);
                }
            }
        }

        /// <summary>
        /// 确保指定 buffer 的覆盖层仍在可视树上（标签切回 / 再次点击条目时调用）。
        /// </summary>
        private void EnsureEditorHostOverlay(ITextBuffer buffer)
        {
            try
            {
                lock (_editorHostsLock)
                {
                    if (_editorHosts.TryGetValue(buffer, out var host))
                        host.EnsureOverlayVisible();
                }
            }
            catch (Exception ex)
            {
                Logger.Warn($"[EditorDiff] 重新挂载 Diff 覆盖层失败: {ex.Message}");
            }
        }

        #endregion
    }
}
