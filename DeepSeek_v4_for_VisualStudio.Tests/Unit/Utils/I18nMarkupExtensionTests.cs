using System;
using System.ComponentModel;
using DeepSeek_v4_for_VisualStudio.Services;
using DeepSeek_v4_for_VisualStudio.Utils;

namespace DeepSeek_v4_for_VisualStudio.Tests.Unit.Utils;

/// <summary>
/// <see cref="I18nExtension"/> / <see cref="I18nValueProvider"/> 的行为契约测试。
/// <para>
/// 历史缺陷：<c>{utils:I18n key}</c> 只返回一次性快照字符串（类注释却声称支持自动刷新），
/// 导致语言切换后这些 XAML 文案不更新，必须依赖各处 Update* 手动覆盖 —— 隐性契约容易漏。
/// 现改为真绑定：提供器在语言变更时发出 <c>"Item[]"</c> 通知，绑定随之刷新。
/// </para>
/// </summary>
public class I18nMarkupExtensionTests : IDisposable
{
    private readonly string _originalLanguage;

    public I18nMarkupExtensionTests()
    {
        _originalLanguage = LocalizationService.Instance.CurrentLanguage;
    }

    public void Dispose()
    {
        // 恢复语言，避免影响其他测试
        try { LocalizationService.Instance.SetLanguage(_originalLanguage); }
        catch { }
    }

    [Fact]
    public void ValueProvider_Indexer_ReturnsCurrentLanguageValue()
    {
        LocalizationService.Instance.SetLanguage("zh-CN");
        string zh = I18nValueProvider.Instance["general.ok"];

        LocalizationService.Instance.SetLanguage("en");
        string en = I18nValueProvider.Instance["general.ok"];

        zh.Should().NotBeNullOrEmpty();
        en.Should().NotBeNullOrEmpty();
        zh.Should().NotBe(en, "不同语言应返回不同文案（该键在两种语言下确实不同）");
    }

    [Fact]
    public void ValueProvider_RaisesItemChanged_OnLanguageSwitch()
    {
        // 这是绑定能自动刷新的关键：语言变更必须发出索引器通知
        var raised = new System.Collections.Generic.List<string?>();
        PropertyChangedEventHandler handler = (_, e) => raised.Add(e.PropertyName);
        I18nValueProvider.Instance.PropertyChanged += handler;

        try
        {
            LocalizationService.Instance.SetLanguage("en");
        }
        finally
        {
            I18nValueProvider.Instance.PropertyChanged -= handler;
        }

        raised.Should().Contain("Item[]", "WPF 依据该名称刷新全部索引器绑定");
    }

    [Fact]
    public void ValueProvider_IsSingleton()
    {
        I18nValueProvider.Instance.Should().BeSameAs(I18nValueProvider.Instance);
    }

    [Fact]
    public void Extension_EmptyKey_ReturnsMarker()
    {
        var ext = new I18nExtension();

        ext.ProvideValue(null!).Should().Be("[missing key]");
    }
}