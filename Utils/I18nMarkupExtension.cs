using System;
using System.ComponentModel;
using System.Windows.Data;
using System.Windows.Markup;
using DeepSeek_v4_for_VisualStudio.Services;

namespace DeepSeek_v4_for_VisualStudio.Utils
{
    /// <summary>
    /// WPF MarkupExtension 用于 XAML 中的 i18n 本地化。
    /// 用法：Text="{utils:I18n chat.windowTitle}"
    /// 或：   Content="{utils:I18n general.ok}"
    /// <para>
    /// 返回的是真正的 <see cref="Binding"/>（绑定到 <see cref="I18nValueProvider"/> 的索引器），
    /// 因此**语言切换后会自动刷新**，无需调用方手动重新赋值。
    /// </para>
    /// <para>
    /// 历史说明：早期实现只返回一次性快照字符串（尽管类注释声称支持自动刷新），
    /// 导致用法上必须依赖各处 Update* 方法手动覆盖，属于隐性契约；
    /// 现改为真绑定以消除该陷阱。带 <see cref="Args"/> 的格式化用法无法走索引器绑定，
    /// 仍为快照（当前仓库无此用法）。
    /// </para>
    /// </summary>
    public class I18nExtension : MarkupExtension
    {
        /// <summary>i18n 资源键</summary>
        public string Key { get; set; } = string.Empty;

        /// <summary>格式化参数（可选，用逗号分隔）。注意：带参数的用法为静态快照，不随语言刷新。</summary>
        public string? Args { get; set; }

        public I18nExtension() { }

        public I18nExtension(string key)
        {
            Key = key;
        }

        public override object ProvideValue(IServiceProvider serviceProvider)
        {
            if (string.IsNullOrEmpty(Key))
                return "[missing key]";

            var localization = LocalizationService.Instance;

            // 带格式化参数：索引器绑定无法表达，保持一次性取值（保持既有行为）
            if (!string.IsNullOrEmpty(Args))
            {
                var args = Args.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
                return localization.Format(Key, args);
            }

            // 真绑定：语言变更时由 I18nValueProvider 触发刷新
            return new Binding($"[{Key}]")
            {
                Source = I18nValueProvider.Instance,
                Mode = BindingMode.OneWay,
            }.ProvideValue(serviceProvider);
        }
    }

    /// <summary>
    /// 本地化文案的绑定源：以索引器暴露键值，并在语言变更时通知所有绑定刷新。
    /// <para>
    /// 作为进程级单例订阅 <see cref="LocalizationService.LanguageChanged"/>，
    /// 不持有任何 UI 对象引用，因此不会造成控件泄漏。
    /// 通知使用 <c>"Item[]"</c>（WPF 索引器绑定的约定名称）以刷新全部索引器绑定。
    /// </para>
    /// </summary>
    public sealed class I18nValueProvider : INotifyPropertyChanged
    {
        /// <summary>进程级单例。</summary>
        public static I18nValueProvider Instance { get; } = new();

        private I18nValueProvider()
        {
            LocalizationService.Instance.LanguageChanged += (_, __) =>
            {
                try
                {
                    // "Item[]" 是 WPF 对索引器属性变更的约定通知名
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
                }
                catch
                {
                    // 通知失败不应影响语言切换调用方（事件契约要求不抛出、不阻塞）
                }
            };
        }

        /// <summary>按当前语言取值。</summary>
        public string this[string key] => LocalizationService.Instance[key];

        public event PropertyChangedEventHandler? PropertyChanged;
    }
}
