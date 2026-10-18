using DeepSeek_v4_for_VisualStudio.Services;
using DeepSeek_v4_for_VisualStudio.Utils;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace DeepSeek_v4_for_VisualStudio.View
{
    /// <summary>
    /// Diff 预览装饰器。在编辑器视口顶部显示「确认变更」和「撤销」按钮，
        /// 并在每个被修改块的首行右侧浮动「撤销此块」按钮。
        /// 块级操作通过 <see cref="EditorDiffMarkerService"/> 访问 Workspace hunks。
        /// </summary>
        internal sealed class DiffPreviewAdornment
        {
            private readonly IWpfTextView _view;
            private readonly IAdornmentLayer _adornmentLayer;

            /// <summary>纯新增块的高亮色（半透明绿）。</summary>
            private static readonly Color InsertHighlightColor = Color.FromArgb(0x30, 0x2E, 0x9E, 0x58);

            /// <summary>修改块的高亮色（半透明红）。</summary>
            private static readonly Color ModifyHighlightColor = Color.FromArgb(0x30, 0xC7, 0x4E, 0x3A);

            public const string AdornmentLayerName = "DeepSeekDiffPreviewAdornment";

            #region Constructors

            public DiffPreviewAdornment(IWpfTextView view)
            {
                _view = view ?? throw new ArgumentNullException(nameof(view));
                _adornmentLayer = view.GetAdornmentLayer(AdornmentLayerName);

                _view.LayoutChanged += OnLayoutChanged;

                // 订阅 diff 会话变更（新建/确认/撤销时刷新编辑器内嵌按钮）
                EditorDiffMarkerService.Instance.PendingDiffCountChanged += OnPendingDiffChanged;

                RefreshAdornment();
            }

            private void OnPendingDiffChanged()
            {
                if (_view is null) return;
                if (!_view.IsClosed)
                {
                    _view.VisualElement.Dispatcher.BeginInvoke(
                        new Action(RefreshAdornment),
                        System.Windows.Threading.DispatcherPriority.Background);
                }
            }

            #endregion

            #region Event Handlers

            private void OnLayoutChanged(object? sender, TextViewLayoutChangedEventArgs e)
            {
                RefreshAdornment();
            }

            #endregion

            #region Adornment Management

            private void RefreshAdornment()
            {
                // 内嵌 diff 的展示与块级操作由 EditorDiffHost / InlineDiffHostControl 负责，
                // 其挂件挂在独立图层上。此处仅在无活跃 Session 时清理本图层残留。
                var session = EditorDiffMarkerService.Instance.GetActiveSessionForBuffer(_view.TextBuffer);
                if (session != null)
                    return;

                _adornmentLayer.RemoveAllAdornments();
            }

            #endregion
        }
    }
