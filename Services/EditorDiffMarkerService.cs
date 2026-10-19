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
            }

            // 显示：先登记宿主，待编辑器首帧渲染后再挂载覆盖层，避免阻塞视图创建
            var editorHost = new EditorDiffHost(textView);

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

            lock (_editorHostsLock)
            {
                if (_editorHosts.TryGetValue(buffer, out var editorHost))
                    editorHost.RefreshSession(session);
            }
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

        #endregion
    }
}
