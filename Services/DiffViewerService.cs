using DeepSeek_v4_for_VisualStudio.Utils;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Differencing;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Utilities;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace DeepSeek_v4_for_VisualStudio.Services
{
    /// <summary>
    /// VS SDK 原生 Diff 查看器服务。
    /// 使用 <see cref="IDifferenceBufferFactoryService"/> + <see cref="IWpfDifferenceViewerFactoryService"/>
    /// 创建 VS 内置的差异对比视图（支持内联/并排模式、自动红绿着色、滚动同步）。
    /// </summary>
    public class DiffViewerService : IDisposable
    {
        #region Singleton

        private static DiffViewerService? _instance;
        private static readonly object _instanceLock = new();

        public static DiffViewerService Instance
        {
            get
            {
                if (_instance == null)
                {
                    lock (_instanceLock)
                    {
                        _instance ??= new DiffViewerService();
                    }
                }
                return _instance;
            }
        }

        #endregion

        #region Fields

        private bool _disposed;

        private IComponentModel? _componentModel;
        private IDifferenceBufferFactoryService? _bufferFactory;
        private IWpfDifferenceViewerFactoryService? _viewerFactory;
        private ITextBufferFactoryService? _textBufferFactory;
        private IContentTypeRegistryService? _contentTypeRegistry;
        private IEditorOptionsFactoryService? _editorOptionsFactory;
        private ITextDocumentFactoryService? _textDocumentFactory;

        #endregion

        #region MEF Service Resolution

        /// <summary>
        /// 延迟解析 MEF 服务（避免在包初始化阶段因 MEF 未就绪而失败）。
        /// </summary>
        private void EnsureServices()
        {
            if (_bufferFactory != null && _viewerFactory != null)
                return;

            ThreadHelper.ThrowIfNotOnUIThread();

            _componentModel = (IComponentModel)Package.GetGlobalService(typeof(SComponentModel));
            if (_componentModel == null)
                throw new InvalidOperationException("IComponentModel 不可用，MEF 容器未初始化。");

            var exportProvider = _componentModel.DefaultExportProvider;

            // 获取 IDifferenceBufferFactoryService（优先 v3 → v2 → v1）
            _bufferFactory = exportProvider.GetExport<IDifferenceBufferFactoryService>()?.Value
                ?? exportProvider.GetExport<IDifferenceBufferFactoryService2>()?.Value
                ?? (IDifferenceBufferFactoryService?)exportProvider.GetExport<IDifferenceBufferFactoryService3>()?.Value;

            _viewerFactory = exportProvider.GetExport<IWpfDifferenceViewerFactoryService>()?.Value;

            _textBufferFactory = exportProvider.GetExport<ITextBufferFactoryService>()?.Value;

            _contentTypeRegistry = exportProvider.GetExport<IContentTypeRegistryService>()?.Value;

            _editorOptionsFactory = exportProvider.GetExport<IEditorOptionsFactoryService>()?.Value;

            _textDocumentFactory = exportProvider.GetExport<ITextDocumentFactoryService>()?.Value;

            if (_bufferFactory == null)
                throw new InvalidOperationException("无法获取 IDifferenceBufferFactoryService。");
            if (_viewerFactory == null)
                throw new InvalidOperationException("无法获取 IWpfDifferenceViewerFactoryService。");
            if (_textBufferFactory == null)
                throw new InvalidOperationException("无法获取 ITextBufferFactoryService。");
            if (_contentTypeRegistry == null)
                throw new InvalidOperationException("无法获取 IContentTypeRegistryService。");
        }

        #endregion

        #region Public API - Diff Viewer

        /// <summary>
        /// 基于纯文本创建只读 Diff 预览（不关联任何真实文件）。
        /// 返回 <see cref="DiffViewerHandle"/>，由调用方管理生命周期。
        /// 两个输入 buffer 均不可编辑，防止用户通过 Diff Viewer 修改内容。
        /// </summary>
        /// <param name="baselineText">原始内容（冻结的 Baseline）</param>
        /// <param name="proposedText">建议修改后的内容</param>
        /// <param name="contentType">内容类型（默认 "code"）</param>
        /// <param name="viewMode">查看模式（Inline 或 SideBySide）</param>
        public DiffViewerHandle CreateReadOnlyPreview(
            string baselineText,
            string proposedText,
            string contentType = "code",
            DifferenceViewMode viewMode = DifferenceViewMode.Inline)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            EnsureServices();

            IContentType ct = _contentTypeRegistry!.GetContentType(contentType);

            // 创建左右两个 buffer（优先文件后端 buffer，兼容 FileEncoding 等第三方 margin 扩展）
            ITextBuffer baselineBuffer;
            ITextBuffer proposalBuffer;
            string? tempBaselineFile = null;
            string? tempProposalFile = null;

            try
            {
                if (_textDocumentFactory != null)
                {
                    // 使用临时文件创建带 ITextDocument 的 buffer
                    tempBaselineFile = WriteTempFile(baselineText ?? string.Empty);
                    tempProposalFile = WriteTempFile(proposedText ?? string.Empty);

                    var baselineDoc = _textDocumentFactory.CreateAndLoadTextDocument(tempBaselineFile, ct);
                    var proposalDoc = _textDocumentFactory.CreateAndLoadTextDocument(tempProposalFile, ct);
                    baselineBuffer = baselineDoc.TextBuffer;
                    proposalBuffer = proposalDoc.TextBuffer;
                }
                else
                {
                    baselineBuffer = _textBufferFactory!.CreateTextBuffer(baselineText ?? string.Empty, ct);
                    proposalBuffer = _textBufferFactory.CreateTextBuffer(proposedText ?? string.Empty, ct);
                }
            }
            catch (Exception ex)
            {
                Logger.Warn($"[DiffViewer] 创建文件后端 buffer 失败，回退内存 buffer: {ex.Message}");
                baselineBuffer = _textBufferFactory!.CreateTextBuffer(baselineText ?? string.Empty, ct);
                proposalBuffer = _textBufferFactory.CreateTextBuffer(proposedText ?? string.Empty, ct);
                tempBaselineFile = null;
                tempProposalFile = null;
            }

            // 配置差异选项
            var diffOptions = new StringDifferenceOptions
            {
                DifferenceType = StringDifferenceTypes.Line | StringDifferenceTypes.Word,
                IgnoreTrimWhiteSpace = false,
                WordSplitBehavior = WordSplitBehavior.Default,
            };

            // 创建只读 DifferenceBuffer（disableEditing: true）
            var differenceBuffer = _bufferFactory!.CreateDifferenceBuffer(
                baselineBuffer,
                proposalBuffer,
                diffOptions,
                disableEditing: true,
                wrapLeftBuffer: true,
                wrapRightBuffer: true);

            // 创建编辑器选项
            IEditorOptions editorOptions = _editorOptionsFactory!.GetOptions(differenceBuffer.InlineBuffer);

            // 创建 Viewer
            IWpfDifferenceViewer viewer;
            try
            {
                viewer = _viewerFactory!.CreateDifferenceView(differenceBuffer, editorOptions);
            }
            catch (Exception ex)
            {
                Logger.Error($"[DiffViewer] CreateReadOnlyPreview 失败: {ex.Message}", ex);
                throw new InvalidOperationException(
                    "无法创建差异查看器。如果安装了 FileEncoding 扩展，请暂时禁用。", ex);
            }

            // 设置查看模式
            try
            {
                if (viewer is IDifferenceViewer3 viewer3)
                    viewer3.ViewMode = viewMode;
                else if (viewer is IDifferenceViewer2 viewer2)
                    viewer2.ViewMode = viewMode;
                else
                    viewer.ViewMode = viewMode;
            }
            catch (Exception ex)
            {
                Logger.Warn($"[DiffViewer] 设置 ViewMode 失败: {ex.Message}");
            }

            Logger.Info($"[DiffViewer] 创建只读预览 (mode={viewMode}, " +
                $"leftLines={baselineBuffer.CurrentSnapshot.LineCount}, " +
                $"rightLines={proposalBuffer.CurrentSnapshot.LineCount})");

            return new DiffViewerHandle(
                baselineBuffer, proposalBuffer, differenceBuffer, viewer,
                tempBaselineFile, tempProposalFile);
        }

        #endregion

        #region Private Helpers

        /// <summary>
        /// 将内容写入临时文件并返回路径（用于创建 <see cref="ITextDocument"/> 后端 buffer）。
        /// </summary>
        private static string WriteTempFile(string content)
        {
            string tempPath = Path.GetTempFileName();
            File.WriteAllText(tempPath, content, System.Text.Encoding.UTF8);
            return tempPath;
        }

        #endregion

        #region IDisposable

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _instance = null;
        }

        #endregion
    }
}
