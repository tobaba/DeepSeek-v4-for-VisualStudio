using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Utilities;
using System.ComponentModel.Composition;

namespace DeepSeek_v4_for_VisualStudio.CodeCompletion
{
    /// <summary>
    /// MEF 导出：注册内嵌 Inline Diff 所需的编辑器 adornment 图层定义。
    /// Viewer 与逐块按钮的挂载由 <see cref="View.Hosts.EditorDiffHost"/> /
    /// <see cref="View.InlineDiffHostControl"/> 负责，本类仅声明图层。
    /// </summary>
    internal sealed class DiffPreviewAdornmentFactory
    {
        /// <summary>
        /// EditorDiffHost 兜底覆盖层：AdornerLayer 不可用时用于挂载编辑器内嵌 diff 覆盖层。
        /// </summary>
        [Export(typeof(AdornmentLayerDefinition))]
        [Name(View.Hosts.EditorDiffHost.OverlayLayerName)]
        [Order(After = PredefinedAdornmentLayers.Text)]
        internal static AdornmentLayerDefinition DeepSeekEditorDiffOverlayLayer = null!;

        /// <summary>
        /// Inline diff 块级按钮使用独立层，避免被其他装饰器的布局清理移除。
        /// </summary>
        [Export(typeof(AdornmentLayerDefinition))]
        [Name(View.InlineDiffHostControl.HunkButtonsLayerName)]
        [Order(After = PredefinedAdornmentLayers.Text)]
        internal static AdornmentLayerDefinition DeepSeekHunkButtonsLayer = null!;
    }
}
