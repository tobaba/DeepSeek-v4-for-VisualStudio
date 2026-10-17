using DeepSeek_v4_for_VisualStudio.Models;
using DeepSeek_v4_for_VisualStudio.Utils;
using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace DeepSeek_v4_for_VisualStudio.Services.BuiltInTools
{
    /// <summary>
    /// web_search 工具 — 联网搜索网页，返回结构化结果（标题/URL/摘要/日期）。
    /// 与 fetch_webpage 配合使用：先用本工具搜索，再按需用 fetch_webpage 抓取正文。
    /// </summary>
    public class WebSearchTool : BuiltInToolBase
    {
        private readonly WebSearchService? _webSearchService;

        public WebSearchTool(WebSearchService? webSearchService = null)
        {
            _webSearchService = webSearchService;
        }

        public override string Name => "web_search";

        public override ToolDefinition GetDefinition()
        {
            return new ToolDefinition
            {
                Type = "function",
                Function = new ToolFunction
                {
                    Name = "web_search",
                    Description = LocalizationService.Instance["tool.webSearch.desc"],
                    Parameters = new
                    {
                        type = "object",
                        properties = new
                        {
                            query = new { type = "string", description = LocalizationService.Instance["tool.webSearch.param.query"] },
                            recency = new { type = "string", description = LocalizationService.Instance["tool.webSearch.param.recency"] },
                            count = new { type = "integer", description = LocalizationService.Instance["tool.webSearch.param.count"] }
                        },
                        required = new[] { "query" }
                    }
                }
            };
        }

        public override string GetDisplayText(Dictionary<string, JsonElement> args)
        {
            string query = GetStringArg(args, "query");
            return string.IsNullOrWhiteSpace(query)
                ? LocalizationService.Instance["tool.webSearch.searching"]
                : LocalizationService.Instance.Format("tool.webSearch.searchingQuery", TruncateText(query, 60));
        }

        public override string GetResultSummary(string toolResult)
        {
            if (string.IsNullOrEmpty(toolResult)) return LocalizationService.Instance["tool.common.noResult"];
            // 错误与"无结果"提示本身已经足够简短，原样展示。
            if (toolResult.StartsWith("Error: ", StringComparison.Ordinal))
                return toolResult;
            if (!toolResult.Contains("URL: ", StringComparison.Ordinal))
                return toolResult;

            // 从结果文本中提取「标题 + 链接」列表，供聊天过程区直接展示搜到了哪些网页。
            var lines = new List<string>();
            string? currentTitle = null;
            foreach (var rawLine in toolResult.Split('\n'))
            {
                string line = rawLine.TrimEnd('\r');

                // 形如 "1. 标题"（顶格编号行，避免把摘要里的编号误判为标题）
                int dotIndex = line.IndexOf(". ", StringComparison.Ordinal);
                if (dotIndex > 0 && int.TryParse(line.Substring(0, dotIndex), out _))
                {
                    currentTitle = line.Substring(dotIndex + 2).Trim();
                    continue;
                }

                // 形如 "   URL: https://..."（固定缩进，由本工具的结果格式保证）
                if (line.StartsWith("   URL: ", StringComparison.Ordinal) && currentTitle != null)
                {
                    string url = line.Substring(8).Trim();
                    lines.Add($"- [{EscapeLinkText(currentTitle)}]({EscapeLinkUrl(url)})");
                    currentTitle = null;
                }
            }

            if (lines.Count == 0)
                return LocalizationService.Instance.Format("tool.webSearch.complete", toolResult.Length);

            var sb = new StringBuilder();
            sb.AppendLine(LocalizationService.Instance.Format("tool.webSearch.foundPages", lines.Count));
            foreach (var line in lines)
                sb.AppendLine(line);
            return sb.ToString().TrimEnd();
        }

        /// <summary>转义 Markdown 链接文本中的方括号，避免标题破坏链接语法。</summary>
        private static string EscapeLinkText(string text)
            => text.Replace("[", "\\[").Replace("]", "\\]");

        /// <summary>URL 含空格或右括号时用尖括号包裹，保证 Markdown 链接可解析。</summary>
        private static string EscapeLinkUrl(string url)
        {
            if (url.IndexOf(' ') >= 0 || url.IndexOf(')') >= 0)
                return "<" + url.Replace(">", "%3E") + ">";
            return url;
        }

        public override async Task<string> ExecuteAsync(Dictionary<string, JsonElement> args, string? workspaceRoot)
        {
            string query = GetStringArg(args, "query");
            string recency = GetStringArg(args, "recency");
            int count = GetIntArg(args, "count", 0);

            if (string.IsNullOrWhiteSpace(query))
                return LocalizationService.Instance["tool.webSearch.missingQuery"];

            if (_webSearchService == null)
                return LocalizationService.Instance["tool.webSearch.serviceNotInit"];

            // 时效过滤只接受约定值，其余一律忽略，避免把无效参数透传给搜索服务。
            string? searchRecency = recency?.Trim().ToLowerInvariant() switch
            {
                "week" or "month" or "semiyear" or "year" => recency!.Trim().ToLowerInvariant(),
                _ => null,
            };

            // count 只接受 1～50；未提供（<=0）时传 null，由各引擎使用自己的默认值。
            int? searchCount = count is >= 1 and <= 50 ? count : null;

            try
            {
                Logger.Info($"[web_search] Searching: \"{query}\""
                    + (searchRecency != null ? $", recency={searchRecency}" : string.Empty)
                    + (searchCount != null ? $", count={searchCount}" : string.Empty));
                // 当前轮用户原始问题随搜索结果一起交给官方引擎，用于更好地理解搜索意图。
                var results = await _webSearchService.SearchAsync(
                    query, CancellationToken, searchRecency, searchCount, CurrentUserQuestion);
                if (results.Count == 0)
                {
                    Logger.Info($"[web_search] No results: \"{query}\"");
                    return LocalizationService.Instance.Format("tool.webSearch.noResults", query);
                }

                string result = FormatResults(results);
                Logger.Info($"[web_search] Returned {results.Count} result(s) ({result.Length} chars)");
                return result;
            }
            catch (ApiKeyInvalidException ex)
            {
                Logger.Error($"[web_search] Invalid API key: {ex.Message}", ex);
                return LocalizationService.Instance.Format("tool.webSearch.failed", ex.Message);
            }
            catch (OperationCanceledException)
            {
                return LocalizationService.Instance["tool.webSearch.cancelled"];
            }
            catch (Exception ex)
            {
                Logger.Error($"[web_search] Search failed: {ex.Message}", ex);
                return LocalizationService.Instance.Format("tool.webSearch.failed", ex.Message);
            }
        }

        /// <summary>
        /// 将搜索结果格式化为模型可读的结构化文本。
        /// </summary>
        private static string FormatResults(List<WebSearchResult> results)
        {
            var L = LocalizationService.Instance;
            var sb = new StringBuilder();
            sb.AppendLine(L.Format("tool.webSearch.header", results.Count));
            sb.AppendLine();

            for (int i = 0; i < results.Count; i++)
            {
                var r = results[i];
                sb.AppendLine($"{i + 1}. {r.Title}");
                if (!string.IsNullOrWhiteSpace(r.Url))
                    sb.AppendLine($"   URL: {r.Url}");
                if (!string.IsNullOrWhiteSpace(r.SiteName))
                {
                    // 官方 Search 给出网页权威性（0～4，>=3 为极权威）；标注以帮助模型判断可信度。
                    string siteLine = r.Authority >= 3
                        ? L.Format("tool.webSearch.siteNameAuthoritative", r.SiteName)
                        : L.Format("tool.webSearch.siteName", r.SiteName);
                    sb.AppendLine($"   {siteLine}");
                }
                if (!string.IsNullOrWhiteSpace(r.Date))
                    sb.AppendLine($"   {r.Date}");
                if (!string.IsNullOrWhiteSpace(r.Snippet))
                    sb.AppendLine($"   {r.Snippet}");
                sb.AppendLine();
            }

            return sb.ToString().TrimEnd();
        }
    }
}
