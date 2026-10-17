using System;
using System.Collections.Generic;
using DeepSeek_v4_for_VisualStudio.Models;
using DeepSeek_v4_for_VisualStudio.Services;

namespace DeepSeek_v4_for_VisualStudio.Tests.Unit.Services;

/// <summary>
/// 会话标题与语言解耦的回归测试。
/// <para>
/// 历史缺陷：<see cref="ChatSession.Title"/> 曾被初始化为**当前语言的**「新对话」文案
/// （如中文下为「新对话」、英文下为「New Chat」），随后又用该文案与当前语言比较来判断
/// 「标题是否尚未生成」。用户切换语言后比较必然失败，导致：
/// 1) 首条消息后不再自动生成标题，会话标题永久停留在旧语言的默认文案；
/// 2) 空会话被误判为「已使用」而不再被清理，历史列表积累垃圾条目。
/// </para>
/// <para>
/// 修复后：标题初始为空，「尚未生成」由「标题为空」这一与语言无关的事实表达，
/// 展示层再回退为本地化文案。本测试锁定该契约。
/// </para>
/// </summary>
public class ChatSessionTitleTests
{
    private static ChatSession NewSession() => new()
    {
        Id = Guid.NewGuid().ToString("N"),
        Title = string.Empty,
        CreatedAt = DateTime.Now,
        LastActiveAt = DateTime.Now,
    };

    [Fact]
    public void NewSession_HasEmptyTitle()
    {
        var session = NewSession();

        // 关键：新建会话不得把任何语言的文案写进持久化数据
        session.Title.Should().BeEmpty();
        session.HasEmptyTitle.Should().BeTrue();
    }

    [Fact]
    public void NewSession_TitleIsLanguageIndependent()
    {
        // 契约：标题不含本地化内容，因此在任何语言下取值都相同。
        var session = NewSession();

        session.Title.Should().BeEmpty("标题不应绑定到创建时的界面语言");
    }

    /// <summary>
    /// 回归：标题生成请求不得携带 system 提示词。
    /// <para>
    /// 历史缺陷（v1.1.11 引入）：<c>GenerateAiTitleAsync</c> 为了蹭 Prompt Cache
    /// 在 messages[0] 前置了 <c>AiPrompts.SharedImmutablePrefix</c>，导致一个「≤20 字摘要」
    /// 请求实际发送了完整的 Agent system prompt（Windows/PowerShell 终端规则 + Handoff 规则）。
    /// 本测试锁定标题 prompt 自洽、不依赖任何 system 的契约。
    /// </para>
    /// </summary>
    [Fact]
    public void GenerateTitlePrompt_IsSelfContained_NoSystemPromptNeeded()
    {
        // 标题 prompt 必须自带「不要使用工具」约束，否则去掉 system 后模型可能尝试调用工具。
        var template = AiPrompts.SessionTitlePromptTemplate;

        template.Should().NotBeNullOrWhiteSpace();
        template.Should().Contain("工具");
    }

    /// <summary>
    /// 回归：用户消息与助手回复之间必须有换行，不能粘连成
    /// <c>用户：&lt;问题&gt;助手：&lt;回复&gt;</c>。
    /// <para>
    /// 历史缺陷：分段完全依赖资源文件里的 <c>\n\n</c> 转义，转义缺失时两段直接粘在一起。
    /// 修复后由 <see cref="AiPrompts.EnsureBreakAfterPlaceholders"/> 保证占位符后必有换行。
    /// </para>
    /// </summary>
    [Fact]
    public void BuildSessionTitlePrompt_SeparatesUserAndAssistantWithNewline()
    {
        const string user = "输出的所有记忆助手";
        const string assistant = "三层记忆库均已查询完毕，全部为空";

        string prompt = AiPrompts.BuildSessionTitlePrompt(user, assistant);

        // 关键断言：用户正文与紧随其后的助手标签不得粘连
        prompt.Should().NotContain(user + "助手：");
        prompt.Should().NotContain(user + "Assistant:");
        prompt.Should().Contain("\n\n");

        // 用户内容与助手内容必须各自独立存在，且顺序正确
        prompt.Should().Contain(user);
        prompt.Should().Contain(assistant);
        prompt.IndexOf(user, StringComparison.Ordinal)
            .Should().BeLessThan(prompt.IndexOf(assistant, StringComparison.Ordinal));

        // 占位符必须被真实内容替换，不能有残留
        prompt.Should().NotContain("{0}").And.NotContain("{1}");
    }

    /// <summary>
    /// 回归（缺陷复现）：模板缺少分段换行时，构建结果仍必须把两段分开。
    /// 这条用「破损模板」直接覆盖历史缺陷的形态——<c>用户：{0}助手：{1}标题：</c>。
    /// </summary>
    [Fact]
    public void EnsureBreakAfterPlaceholders_RepairsGluedTemplate()
    {
        const string glued = "生成标题：用户：{0}助手：{1}标题：";

        string repaired = AiPrompts.EnsureBreakAfterPlaceholders(glued);

        repaired.Should().Contain("用户：{0}\n\n助手：");
        repaired.Should().Contain("{1}\n\n标题：");

        // 修复后再格式化，不得出现「<用户内容>助手：」这种粘连
        string prompt = string.Format(repaired, "问题", "回复");
        prompt.Should().NotContain("问题助手：");
    }

    [Fact]
    public void EnsureBreakAfterPlaceholders_IsIdempotent_WhenBreaksAlreadyPresent()
    {
        // 当前中英语言包都是这个形态：已有空行分隔，不应被再加一行
        const string healthy = "生成标题：\n\n用户：{0}\n\n助手：{1}\n\n标题：";

        AiPrompts.EnsureBreakAfterPlaceholders(healthy).Should().Be(healthy);

        // 单个换行（\n 或 \r\n）也视作已分隔，不应重复插入
        AiPrompts.EnsureBreakAfterPlaceholders("用户：{0}\n助手：{1}\n标题：")
            .Should().Be("用户：{0}\n助手：{1}\n标题：");
        AiPrompts.EnsureBreakAfterPlaceholders("用户：{0}\r\n助手：{1}\r\n标题：")
            .Should().Be("用户：{0}\r\n助手：{1}\r\n标题：");
    }

    [Fact]
    public void GenerateTitlePromptTemplate_KeepsPlaceholdersForLocalizedLabels()
    {
        // 标签文案（「用户：」「助手：」「标题：」）属于可翻译内容，必须留在语言包里，
        // 不得硬编码进 Services/ 下的 .cs（见 tools/check-cjk-strings.ps1 的 i18n 约束）。
        var template = AiPrompts.SessionTitlePromptTemplate;

        template.Should().Contain("{0}");
        template.Should().Contain("{1}");
    }

    [Fact]
    public void BuildSessionTitlePrompt_WithEmptyReply_StillProducesUsablePrompt()
    {
        // 首轮回复可能为空（Agent 只调用了工具、或返回被截断）。
        // 契约：不抛异常、占位符不残留；空回复只是少一段内容，不影响标题生成。
        string prompt = AiPrompts.BuildSessionTitlePrompt("只有用户消息", string.Empty);

        prompt.Should().Contain("只有用户消息");
        prompt.Should().NotContain("{0}").And.NotContain("{1}");
    }

    [Theory]
    [InlineData("中文标题")]
    [InlineData("English Title")]
    public void SessionWithGeneratedTitle_IsNotConsideredEmpty(string title)
    {
        var session = NewSession();
        session.Title = title;

        session.HasEmptyTitle.Should().BeFalse();
    }

    [Fact]
    public void IsUnusedEmptySession_NewSessionWithNoContent_ReturnsTrue()
    {
        ChatPersistenceService.IsUnusedEmptySession(NewSession()).Should().BeTrue();
    }

    [Fact]
    public void IsUnusedEmptySession_SessionWithApiHistory_ReturnsFalse()
    {
        var session = NewSession();
        session.ApiHistory = new List<ChatApiMessage>
        {
            new() { Role = "user", Content = "hello" },
        };

        ChatPersistenceService.IsUnusedEmptySession(session).Should().BeFalse();
    }

    [Fact]
    public void IsUnusedEmptySession_SessionWithTreeData_ReturnsFalse()
    {
        var session = NewSession();
        session.TreeDataJson = "{\"nodes\":[]}";

        ChatPersistenceService.IsUnusedEmptySession(session).Should().BeFalse();
    }

    [Fact]
    public void IsUnusedEmptySession_SessionWithGeneratedTitle_ReturnsFalse()
    {
        // 标题已生成（无论何种语言）→ 视为使用过
        var session = NewSession();
        session.Title = "重构拖放逻辑";

        ChatPersistenceService.IsUnusedEmptySession(session).Should().BeFalse();
    }

    [Fact]
    public void IsUnusedEmptySession_TitleThatLooksLikeAnotherLanguagesDefault_StillCountsAsUsed()
    {
        // 历史缺陷的回归点：英文界面下新建的会话标题曾存为 "New Conversation"，
        // 在中文环境下与 "新对话" 比较不等 → 被判为「已使用」，空会话不再清理。
        // 修复后标题恒为空、判据与语言无关，因此这类标题只会来自真实生成，
        // 应当被认作「已使用」。
        var session = NewSession();
        session.Title = "New Conversation";

        ChatPersistenceService.IsUnusedEmptySession(session).Should().BeFalse();
    }

    [Fact]
    public void IsUnusedEmptySession_NullSession_ReturnsTrue()
    {
        ChatPersistenceService.IsUnusedEmptySession(null!).Should().BeTrue();
    }

    [Theory]
    [InlineData("新对话")]
    [InlineData("New Conversation")]
    public void MigrateLegacyDefaultTitles_NormalizesLocalizedDefaultToEmpty(string legacyTitle)
    {
        // 旧版本会把当前语言的「新对话」写进标题；两种语言的历史数据都必须被还原，
        // 否则切成另一语言后该会话的标题判等失效（AI 标题不再生成、空会话不再清理）。
        var container = new SessionsContainer
        {
            Sessions =
            {
                new ChatSession { Title = legacyTitle },
            },
        };

        ChatPersistenceService.MigrateLegacyDefaultTitles(container);

        container.Sessions[0].Title.Should().BeEmpty();
        container.Sessions[0].HasEmptyTitle.Should().BeTrue();
    }

    [Fact]
    public void MigrateLegacyDefaultTitles_LeavesRealTitlesUntouched()
    {
        // 迁移只能识别「恰好等于某语言默认文案」的标题，不得误伤真实标题。
        var container = new SessionsContainer
        {
            Sessions =
            {
                new ChatSession { Title = "重构拖放逻辑" },
                new ChatSession { Title = "Fix drag and drop" },
            },
        };

        ChatPersistenceService.MigrateLegacyDefaultTitles(container);

        container.Sessions[0].Title.Should().Be("重构拖放逻辑");
        container.Sessions[1].Title.Should().Be("Fix drag and drop");
    }

    [Fact]
    public void MigrateLegacyDefaultTitles_HandlesNullAndEmptyGracefully()
    {
        var container = new SessionsContainer
        {
            Sessions =
            {
                new ChatSession { Title = string.Empty },
            },
        };

        var act = () => ChatPersistenceService.MigrateLegacyDefaultTitles(container);

        act.Should().NotThrow();
        container.Sessions[0].Title.Should().BeEmpty();
        // 同时确认 null 容器不抛异常
        ((Action)(() => ChatPersistenceService.MigrateLegacyDefaultTitles(null!))).Should().NotThrow();
    }
}