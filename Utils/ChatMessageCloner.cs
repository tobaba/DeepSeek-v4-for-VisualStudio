using System.Collections.Generic;
using System.Linq;
using DeepSeek_v4_for_VisualStudio.Models;

namespace DeepSeek_v4_for_VisualStudio.Utils
{
    /// <summary>
    /// <see cref="ChatApiMessage"/> 深克隆工具。
    ///
    /// 消息对象在多条链路间流转（对话历史、Handoff 转发快照、Provider 侧清洗副本、
    /// 发送快照），各处都需要「复制一份再改，绝不污染原对象」。
    /// 此前这段逐字段拷贝逻辑在 BaseAgent、OpenAiCompatibleProvider、ConversationContextManager
    /// 内各写了一份（且深浅程度不一致，容易出现「改了副本却影响到原对象」的隐患），
    /// 现统一收敛到这里，保证语义一致。
    ///
    /// 克隆深度：ToolCalls / MultimodalContent 及其内层元素全部新建，
    /// 调用方可安全修改（含替换、置空集合元素）。
    /// </summary>
    internal static class ChatMessageCloner
    {
        /// <summary>
        /// 深克隆单条消息。
        /// </summary>
        public static ChatApiMessage Clone(ChatApiMessage message)
        {
            return new ChatApiMessage
            {
                Role = message.Role,
                Content = message.Content,
                MultimodalContent = CloneContentParts(message.MultimodalContent),
                ReasoningContent = message.ReasoningContent,
                ToolCalls = CloneToolCalls(message.ToolCalls),
                ToolCallId = message.ToolCallId,
                Name = message.Name,
            };
        }

        /// <summary>
        /// 深克隆消息列表。
        /// </summary>
        public static List<ChatApiMessage> CloneAll(IEnumerable<ChatApiMessage> messages)
            => messages.Select(Clone).ToList();

        /// <summary>
        /// 深克隆工具调用列表；空集合归一为 null，与「无工具调用」语义保持一致。
        /// </summary>
        public static List<ToolCall>? CloneToolCalls(List<ToolCall>? toolCalls)
        {
            if (toolCalls == null || toolCalls.Count == 0)
                return null;

            return toolCalls.Select(tc => new ToolCall
            {
                Id = tc.Id,
                Type = tc.Type,
                Function = tc.Function == null
                    ? new ToolCallFunction()
                    : new ToolCallFunction
                    {
                        Name = tc.Function.Name,
                        Arguments = tc.Function.Arguments,
                    },
            }).ToList();
        }

        /// <summary>
        /// 深克隆多模态内容块列表；空集合归一为 null。
        /// </summary>
        public static List<ChatContentPart>? CloneContentParts(List<ChatContentPart>? parts)
        {
            if (parts == null || parts.Count == 0)
                return null;

            return parts.Select(p => new ChatContentPart
            {
                Type = p.Type,
                Text = p.Text,
                ImageUrl = p.ImageUrl == null
                    ? null
                    : new ChatImageUrl { Url = p.ImageUrl.Url, Detail = p.ImageUrl.Detail },
                File = p.File == null
                    ? null
                    : new ChatFilePart
                    {
                        FileId = p.File.FileId,
                        FileData = p.File.FileData,
                        Filename = p.File.Filename,
                    },
            }).ToList();
        }
    }
}
