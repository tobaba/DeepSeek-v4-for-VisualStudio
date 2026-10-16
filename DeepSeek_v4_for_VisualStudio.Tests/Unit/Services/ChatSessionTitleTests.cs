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