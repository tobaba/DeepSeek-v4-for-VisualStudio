using DeepSeek_v4_for_VisualStudio.View;

namespace DeepSeek_v4_for_VisualStudio.Tests.Unit.View;

/// <summary>
/// WebView2 链接路由契约。
/// <para>
/// 历史缺陷：回答里的 Markdown 链接渲染为普通 &lt;a href&gt;（无 target），点击走
/// NavigationStarting，而该处理函数只拦截 vs-navigate://，其余一律放行，
/// 导致整个聊天页面被导航到目标网页。现在外部链接改为交给系统默认浏览器。
/// </para>
/// </summary>
public class ExternalLinkRoutingTests
{
    [Theory]
    [InlineData("https://example.com/a")]
    [InlineData("http://example.com")]
    [InlineData("HTTPS://Example.com/Path")]
    [InlineData("mailto:someone@example.com")]
    public void IsExternalLinkUri_WebLinks_ReturnsTrue(string uri)
    {
        DeepSeekChatControl.IsExternalLinkUri(uri).Should().BeTrue();
    }

    [Theory]
    [InlineData("vs-navigate://file?name=a.cs")]
    [InlineData("vs-navigate://symbol?name=IFoo")]
    [InlineData("file:///C:/temp/a.txt")]
    [InlineData("about:blank")]
    [InlineData("data:text/plain,hello")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not a uri")]
    [InlineData(null)]
    public void IsExternalLinkUri_InternalOrInvalid_ReturnsFalse(string? uri)
    {
        DeepSeekChatControl.IsExternalLinkUri(uri).Should().BeFalse();
    }
}
