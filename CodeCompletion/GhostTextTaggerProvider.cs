using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Tagging;
using Microsoft.VisualStudio.Utilities;
using System.ComponentModel.Composition;

namespace DeepSeek_v4_for_VisualStudio.CodeCompletion
{
    /// <summary>
    /// MEF 导出：为可编辑文本视图创建 <see cref="GhostTextTagger"/> 实例，
    /// 启用内联幽灵文本代码补全功能。
    ///
    /// 内容类型：<c>code</c> 覆盖 C#/VB/F#/Python 等继承自 code 的语言；
    /// 其余 [ContentType] 覆盖 C/C++（不继承 code）与 JSON/XML/YAML 等
    /// 配置文件类型。清单集中在 <see cref="DeepSeekCompletionContentTypes"/>。
    /// Markdown/纯文本等文档类型刻意不支持。
    /// </summary>
    [Export(typeof(IViewTaggerProvider))]
    [ContentType("code")]
    [ContentType(DeepSeekCompletionContentTypes.Cpp)]
    [ContentType(DeepSeekCompletionContentTypes.Java)]
    [ContentType(DeepSeekCompletionContentTypes.Sql)]
    [ContentType(DeepSeekCompletionContentTypes.Cmake)]
    [ContentType(DeepSeekCompletionContentTypes.Json)]
    [ContentType(DeepSeekCompletionContentTypes.Xml)]
    [ContentType(DeepSeekCompletionContentTypes.Yaml)]
    [ContentType(DeepSeekCompletionContentTypes.Ini)]
    [ContentType(DeepSeekCompletionContentTypes.Html)]
    [ContentType(DeepSeekCompletionContentTypes.Css)]
    [ContentType(DeepSeekCompletionContentTypes.Razor)]
    [TagType(typeof(IntraTextAdornmentTag))]
    [TextViewRole(PredefinedTextViewRoles.Editable)]
    internal sealed class GhostTextTaggerProvider : IViewTaggerProvider
    {
        #region Public Methods

        /// <summary>
        /// 为给定文本视图创建或获取 <see cref="GhostTextTagger"/> 实例。
        /// </summary>
        public ITagger<T> CreateTagger<T>(ITextView textView, ITextBuffer buffer) where T : ITag
        {
            if (textView is not IWpfTextView wpfView)
            {
                return null;
            }

            if (buffer != textView.TextBuffer)
            {
                return null;
            }

            GhostTextTagger tagger = wpfView.Properties.GetOrCreateSingletonProperty(
                GhostTextTagger.TaggerKey,
                () => new GhostTextTagger(wpfView));

            return tagger as ITagger<T>;
        }

        #endregion
    }
}
