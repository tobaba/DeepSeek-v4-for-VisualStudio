using DeepSeek_v4_for_VisualStudio.Models;
using DeepSeek_v4_for_VisualStudio.Services.BuiltInTools;
using DeepSeek_v4_for_VisualStudio.Settings;
using System.ComponentModel;
using System.Threading.Tasks;

namespace DeepSeek_v4_for_VisualStudio.Tests.Unit.Settings;

/// <summary>
/// P1-5b：LegacySettingsMigrated 一次性迁移标志必须被 DialogPage 序列化持久化。
/// 修复前缺少 DesignerSerializationVisibility(Visible)，每次启动复位 false → 迁移反复执行、
/// 反复用旧值覆盖用户新改设置。
/// </summary>
public class LegacySettingsMigratedPersistenceTests
{
    [Fact]
    public void LegacySettingsMigrated_HasDesignerSerializationVisibilityVisible()
    {
        var prop = typeof(DeepSeekOptionsPage).GetProperty(nameof(DeepSeekOptionsPage.LegacySettingsMigrated));

        prop.Should().NotBeNull();

        var attr = prop!.GetCustomAttributes(typeof(DesignerSerializationVisibilityAttribute), inherit: true)
            .Cast<DesignerSerializationVisibilityAttribute>()
            .SingleOrDefault();

        attr.Should().NotBeNull("必须声明 DesignerSerializationVisibility 才能被 DialogPage 序列化");
        attr!.Visibility.Should().Be(DesignerSerializationVisibility.Visible);
    }

    /// <summary>
    /// 旧版默认值 DuckDuckGo → DeepSeek 的迁移标志同样必须被序列化，
    /// 否则每次启动都会重置搜索偏好，覆盖用户后来的选择。
    /// </summary>
    [Fact]
    public void WebSearchDefaultMigrated_HasDesignerSerializationVisibilityVisible()
    {
        var prop = typeof(DeepSeekOptionsPage).GetProperty(nameof(DeepSeekOptionsPage.WebSearchDefaultMigrated));

        prop.Should().NotBeNull();

        var attr = prop!.GetCustomAttributes(typeof(DesignerSerializationVisibilityAttribute), inherit: true)
            .Cast<DesignerSerializationVisibilityAttribute>()
            .SingleOrDefault();

        attr.Should().NotBeNull("必须声明 DesignerSerializationVisibility 才能被 DialogPage 序列化");
        attr!.Visibility.Should().Be(DesignerSerializationVisibility.Visible);
    }

    [Fact]
    public void DefaultConstants_MatchNewOfficialFirstBehaviour()
    {
        // 新装默认：官方 API 来源使用 DeepSeek 官方搜索 + 官方 Fetch 抓取。
        // 自定义端点仍由运行时回退 DuckDuckGo 与本地解析。
        DeepSeekOptionsPage.DefaultSearchProvider.Should().Be("DeepSeek");
        DeepSeekOptionsPage.DefaultWebFetchMode.Should().Be("official");
    }
}

/// <summary>
/// P1-7：RunInTerminalTool / GitTool 的 CurrentAgentType 必须是 AsyncLocal 线程/异步流隔离，
/// 避免并发 Agent 相互覆盖导致只读判定被静默绕过或反向误拦。
/// </summary>
public class CurrentAgentTypeAsyncLocalIsolationTests
{
    [Fact]
    public async Task ConcurrentFlows_DoNotSeeEachOthersValue()
    {
        // 基线：两个并发异步流各自设置不同 Agent 类型
        var askSees = string.Empty;
        var exploreSees = string.Empty;

        var t1 = Task.Run(async () =>
        {
            RunInTerminalTool.CurrentAgentType = AgentType.Ask;
            await Task.Yield();
            askSees = RunInTerminalTool.CurrentAgentType?.ToString() ?? "null";
            RunInTerminalTool.CurrentAgentType = null;
        });

        var t2 = Task.Run(async () =>
        {
            RunInTerminalTool.CurrentAgentType = AgentType.Explore;
            await Task.Yield();
            exploreSees = RunInTerminalTool.CurrentAgentType?.ToString() ?? "null";
            RunInTerminalTool.CurrentAgentType = null;
        });

        await Task.WhenAll(t1, t2);

        askSees.Should().Be("Ask", "Ask 流内应读到自己设置的 Agent 类型");
        exploreSees.Should().Be("Explore", "Explore 流内应读到自己设置的 Agent 类型");
    }

    [Fact]
    public async Task ChildFlowSetting_DoesNotLeakBackToParent()
    {
        RunInTerminalTool.CurrentAgentType = null;

        await Task.Run(async () =>
        {
            RunInTerminalTool.CurrentAgentType = AgentType.Edit;
            await Task.Yield();
        });

        // 子异步流内设置的值不会回传父流（AsyncLocal 沿执行上下文向下传播，不向上回写）
        RunInTerminalTool.CurrentAgentType.Should().BeNull("子流设置不应泄漏回父流");
    }

    [Fact]
    public void GitTool_CurrentAgentType_IsAsyncLocalBacked()
    {
        GitTool.CurrentAgentType = null;

        GitTool.CurrentAgentType = AgentType.Ask;
        GitTool.CurrentAgentType.Should().Be(AgentType.Ask);

        GitTool.CurrentAgentType = null;
        GitTool.CurrentAgentType.Should().BeNull();
    }
}

/// <summary>
/// web_search 通过 BuiltInToolBase.CurrentUserQuestion 读取当前轮用户原始问题
/// （作为官方 Search 的 ori_question）。该值必须按异步流隔离，
/// 否则并发 Agent 的搜索请求会互相带上对方的用户问题。
/// </summary>
public class CurrentUserQuestionAsyncLocalIsolationTests
{
    [Fact]
    public async Task ConcurrentFlows_DoNotSeeEachOthersQuestion()
    {
        var first = string.Empty;
        var second = string.Empty;

        var t1 = Task.Run(async () =>
        {
            BuiltInToolBase.CurrentUserQuestion = "第一个问题";
            await Task.Yield();
            first = BuiltInToolBase.CurrentUserQuestion ?? "null";
        });

        var t2 = Task.Run(async () =>
        {
            BuiltInToolBase.CurrentUserQuestion = "第二个问题";
            await Task.Yield();
            second = BuiltInToolBase.CurrentUserQuestion ?? "null";
        });

        await Task.WhenAll(t1, t2);

        first.Should().Be("第一个问题");
        second.Should().Be("第二个问题");
    }

    [Fact]
    public async Task ChildFlowSetting_DoesNotLeakBackToParent()
    {
        BuiltInToolBase.CurrentUserQuestion = null;

        await Task.Run(async () =>
        {
            BuiltInToolBase.CurrentUserQuestion = "子问题";
            await Task.Yield();
        });

        BuiltInToolBase.CurrentUserQuestion.Should().BeNull("子流设置不应泄漏回父流");
    }
}
