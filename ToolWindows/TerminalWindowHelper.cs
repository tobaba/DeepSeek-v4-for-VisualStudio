using DeepSeek_v4_for_VisualStudio.Services;
using DeepSeek_v4_for_VisualStudio.Settings;
using DeepSeek_v4_for_VisualStudio.Utils;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.Editor;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.TextManager.Interop;
using System;
using System.IO;
using System.Threading.Tasks;

namespace DeepSeek_v4_for_VisualStudio.ToolWindows
{
    /// <summary>
    /// 工具辅助类：在编辑器中激活内嵌 Inline Diff 预览，以及把 AI 生成的代码写入活动文档。
    /// </summary>
    public static class TerminalWindowHelper
    {
        /// <summary>
        /// 在流程结束时统一显示一次最终 diff 预览。
        /// 由于 Agent 编辑已【直接落盘】，撤销时通过 workspace 恢复磁盘到 Baseline。
        /// </summary>
        /// <param name="oldContent">修改前的内容（新建文件传空字符串）</param>
        /// <param name="newContent">修改后的最终内容（当前磁盘内容）</param>
        /// <param name="filePath">文件完整路径</param>
        /// <param name="workspace">StagedEditWorkspace（直接落盘模式的撤销追踪器，可选）</param>
        public static async Task ShowFinalDiffAsync(
            string oldContent, string newContent, string filePath,
            Services.Editing.StagedEditWorkspace? workspace = null)
        {
            if (oldContent == newContent) return;

            bool showDiff = DeepSeekOptionsPage.Instance?.ShowDiffMarkersInEditor ?? true;
            if (!showDiff) return;

            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            try
            {
                var wpfView = FindWpfTextViewForFile(filePath);
                if (wpfView != null)
                {
                    // 使用 preview-then-commit 路径（只读 diff），撤销时恢复磁盘 Baseline
                    var session = EditorDiffMarkerService.Instance.CreateInlineDiffPreview(
                        wpfView, oldContent, newContent,
                        textChanges: null,
                        workspace: workspace);

                    if (session != null)
                    {
                        Logger.Info($"[WriteCode] 最终 diff 预览已激活: {Path.GetFileName(filePath)}");
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"[WriteCode] 激活最终 diff 预览失败: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// 通过枚举 VS 所有打开的文档窗口，查找指定文件路径对应的 <see cref="IWpfTextView"/>。
        /// </summary>
        internal static IWpfTextView? FindWpfTextViewForFile(string filePath)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            try
            {
                var uiShell = (IVsUIShell?)ServiceProvider.GlobalProvider.GetService(typeof(SVsUIShell));
                if (uiShell == null) return null;

                uiShell.GetDocumentWindowEnum(out IEnumWindowFrames? enumFrames);
                if (enumFrames == null) return null;

                var componentModel = (IComponentModel)ServiceProvider.GlobalProvider.GetService(typeof(SComponentModel));
                var editorAdapter = componentModel?.DefaultExportProvider
                    .GetExport<IVsEditorAdaptersFactoryService>()?.Value;
                if (editorAdapter == null) return null;

                IVsWindowFrame[] frames = new IVsWindowFrame[1];

                while (enumFrames.Next(1, frames, out uint fetched) == VSConstants.S_OK && fetched == 1)
                {
                    var frame = frames[0];

                    // 获取文档的完整路径
                    object pathObj;
                    frame.GetProperty((int)__VSFPROPID.VSFPROPID_pszMkDocument, out pathObj);
                    string? framePath = pathObj as string;

                    if (string.IsNullOrEmpty(framePath) ||
                        !string.Equals(framePath, filePath, StringComparison.OrdinalIgnoreCase))
                        continue;

                    // 路径匹配 → 获取 IVsTextView
                    var vsTextView = VsShellUtilities.GetTextView(frame);
                    if (vsTextView != null)
                    {
                        var wpfView = editorAdapter.GetWpfTextView(vsTextView);
                        if (wpfView != null)
                            return wpfView;
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Warn($"[WriteCode] 查找 IWpfTextView 失败: {ex.Message}");
            }

            return null;
        }

        /// <summary>
        /// 将代码写入 VS 当前活动文档：有选中文本则替换，无选中则在光标处插入。
        /// 使用 ITextEdit 确保整个操作为一次撤销。
        /// 返回 null 表示成功；返回错误字符串表示失败原因（由调用方决定如何展示）。
        /// </summary>
        /// <param name="code">要插入或替换的代码。</param>
        public static async Task<string?> ApplyCodeToActiveDocumentAsync(string code)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(code))
                {
                    return null; // 空代码，静默跳过
                }

                DocumentView? docView = await GetActiveDocumentViewAsync();

                if (docView == null)
                {
                    Logger.Warn("[ApplyCode] 没有活动文档，无法写入代码");
                    return "No active document is open to apply the code.";
                }

                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                ITextBuffer textBuffer = docView.TextView.TextBuffer;
                NormalizedSnapshotSpanCollection selection = docView.TextView.Selection.SelectedSpans;

                using (ITextEdit edit = textBuffer.CreateEdit())
                {
                    if (selection.Count > 0 && !selection[0].IsEmpty)
                    {
                        edit.Replace(selection[0], code);
                    }
                    else
                    {
                        int caretPosition = docView.TextView.Caret.Position.BufferPosition.Position;
                        edit.Insert(caretPosition, code);
                    }

                    edit.Apply();
                }

                Logger.Info("[ApplyCode] 代码已成功写入活动文档");
                return null; // 成功
            }
            catch (Exception ex)
            {
                Logger.Error($"[ApplyCode] Failed: {ex.Message}", ex);
                return $"Failed to apply the code: {ex.Message}";
            }
        }

        #region Private Methods

        /// <summary>
        /// 使用 VS SDK API 获取当前活动的文档视图。
        /// </summary>
        private static async Task<DocumentView?> GetActiveDocumentViewAsync()
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            try
            {
                var textManager = (IVsTextManager)
                    ServiceProvider.GlobalProvider.GetService(typeof(SVsTextManager));

                if (textManager == null)
                {
                    return null;
                }

                textManager.GetActiveView(1, null, out IVsTextView vsTextView);

                if (vsTextView == null)
                {
                    return null;
                }

                // IComponentModel 是 MEF 导出容器，无法通过 GetService 获取
                var componentModel = (IComponentModel)ServiceProvider.GlobalProvider.GetService(typeof(SComponentModel));
                var editorAdapter = componentModel?.DefaultExportProvider.GetExport<IVsEditorAdaptersFactoryService>()?.Value;

                if (editorAdapter == null)
                {
                    return null;
                }

                IWpfTextView? wpfView = editorAdapter.GetWpfTextView(vsTextView);

                if (wpfView == null)
                {
                    return null;
                }

                // Get file path
                string filePath = string.Empty;
                if (wpfView.TextDataModel.DocumentBuffer.Properties.TryGetProperty(
                    typeof(ITextDocument), out ITextDocument textDocument))
                {
                    filePath = textDocument.FilePath;
                }

                return new DocumentView(wpfView, filePath);
            }
            catch (Exception ex)
            {
                Logger.Error($"[GetActiveDocumentView] Failed: {ex.Message}", ex);
                return null;
            }
        }

        #endregion

        #region Nested Types

        /// <summary>
        /// Simple wrapper for an active document's text view and file path.
        /// </summary>
        private class DocumentView
        {
            public IWpfTextView TextView { get; }
            public string FilePath { get; }

            public DocumentView(IWpfTextView textView, string filePath)
            {
                TextView = textView;
                FilePath = filePath;
            }
        }

        #endregion
    }
}
