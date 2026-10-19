using DeepSeek_v4_for_VisualStudio.Models;
using DeepSeek_v4_for_VisualStudio.Utils;
using Microsoft.VisualStudio.Shell;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace DeepSeek_v4_for_VisualStudio.Services.Agents
{
    /// <summary>
    /// Explore Agent — 深度代码库检索子代理。
    /// 
    /// 职责：
    /// - 执行多步骤系统化代码库分析（跨文件架构分析、依赖追踪、模式发现）
    /// - 使用多种搜索策略（glob、grep、语义搜索、符号搜索）进行深度检索
    /// - 回答关于代码结构、依赖关系、实现模式的复杂问题
    /// - 被 Ask Agent / Plan Agent 作为子代理并行调用
    /// - 绝不修改工作区文件（可通过 memory 工具维护扩展自己的仓库记忆）
    /// 
    ///  定位：深度检索引擎。简单的单文件查找应由调用方（Ask/Plan Agent）
    /// 直接使用内置工具完成，只有需要跨文件多步骤综合分析时才调用 Explore。
    /// 
    /// 参考: VS Code Copilot Chat Explore Agent
    /// </summary>
    public class ExploreAgent : BaseAgent
    {
        public ExploreAgent(DeepSeekApiService apiService) : base(apiService, AgentType.Explore) { }

        // ═══════════════════════════════════════════════════════════════
        // 缓存策略 — 以后会被 RAG 替代
        // ═══════════════════════════════════════════════════════════════

        /// <summary>
        /// 解决方案文件列表缓存（solutionPath → 文件路径列表）。
        /// 避免同一会话内多次调用 DiscoverSolutionFilesAsync 重复枚举 DTE 项目。
        /// 以后会被 RAG 向量检索替代。
        /// </summary>
        private readonly Dictionary<string, List<string>> _discoveredFilesCache = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// 标准化缓存 key：将 solutionPath 转为目录路径（.sln 文件取其目录）。
        /// 以后会被 RAG 替代。
        /// </summary>
        private static string NormalizeCacheKey(string solutionPath)
        {
            if (string.IsNullOrEmpty(solutionPath)) return "__empty__";
            try
            {
                if (System.IO.File.Exists(solutionPath))
                    return System.IO.Path.GetDirectoryName(solutionPath) ?? solutionPath;
                return solutionPath;
            }
            catch { return solutionPath; }
        }

        /// <summary>
        /// 获取缓存的文件列表（不触发新扫描）。
        /// 以后会被 RAG 替代。
        /// </summary>
        public List<string>? GetCachedDiscoveredFiles(string solutionPath)
        {
            string cacheKey = NormalizeCacheKey(solutionPath);
            lock (_discoveredFilesCache)
            {
                _discoveredFilesCache.TryGetValue(cacheKey, out var cached);
                return cached;
            }
        }

        #region Agent Definition

        /// <summary>
        /// Explore Agent 默认只读工具集。
        /// 这些工具只能检查工作区；memory 工具仅写入扩展记忆库，不属于工作区文件修改。
        /// </summary>
        public static readonly string[] DefaultReadTools = new[]
        {
            "file_search",      // glob 文件搜索
            "grep_search",      // 文本/正则搜索
            "symbol_search",    // 符号搜索（类/方法/接口/属性）
            "get_file_symbols", // 单文件符号定义列表
            "read_file",        // 读取文件内容
            "capture_window",   // 捕获窗口截图（视觉模型直接查看）
            "capture_webpage",  // 离屏渲染 URL 截图（视觉模型直接查看）
            "list_dir",         // 列出目录内容
            "get_errors",       // 获取编译错误
            "fetch_webpage",    // 获取网页内容
            "web_search",       // 联网搜索（需在设置中开启）
            "memory",           // 记忆管理
            "git",              // Git 版本控制（仅 status/diff/log 只读操作）
            "run_in_terminal",  // 终端只读命令
            "get_terminal_output", // 获取异步终端输出
        };

        protected override AgentDefinition CreateDefinition(AgentType agentType)
        {
            return new AgentDefinition
            {
                Type = AgentType.Explore,
                Name = "Explore",
                AllowedTools = new List<string>(DefaultReadTools),
                SystemPrompt = BuildSystemPrompt(),
            };
        }

        private static string BuildSystemPrompt()
        {
            return AiPrompts.ExploreAgentSystemPrompt;
        }

        #endregion

        #region Execute

        /// <summary>
        /// Explore Agent 执行入口。
        /// 接收搜索任务描述，返回代码库分析结果。
        /// </summary>
        public override async Task<AgentResult> ExecuteAsync(string userMessage, AgentContext context)
        {
            // ── 清理上次执行的移交状态 ──
            PendingHandoffRequest = null;

            // 日志只显示用户消息的第一行（实际任务描述），
            // 后续行是 PlanAgent 注入的结构上下文等元数据，无需在日志中展示
            string logMessage = userMessage;
            int firstNewline = userMessage.IndexOf('\n');
            if (firstNewline > 0)
                logMessage = userMessage.Substring(0, firstNewline).TrimEnd('\r');
            AddLog("INFO", LocalizationService.Instance.Format("agent.log.exploreStarted", logMessage));

            var result = new AgentResult
            {
                Success = true,
            };

            try
            {
                var ct = context.CancellationToken;

                // ── 构建消息列表（含对话历史，保持前缀缓存稳定）──
                var messages = BuildContextAwareMessages(
                    Definition.SystemPrompt,
                    BuildExplorePrompt(userMessage, context));

                // ── 解析工作区根目录 ──
                // 对 .sln 文件取目录；对文件夹项目保持目录原样。
                // 注意：不能无条件调用 Path.GetDirectoryName()，否则文件夹项目
                // 的路径会被错误解析为父目录（如 "F:\Proj" → "F:\"）。
                string workspaceRoot = context.SolutionPath ?? string.Empty;
                if (!string.IsNullOrEmpty(workspaceRoot))
                {
                    try
                    {
                        if (File.Exists(workspaceRoot))
                            workspaceRoot = Path.GetDirectoryName(workspaceRoot) ?? workspaceRoot;
                        // 目录路径保持原样，由 BuiltInToolService.NormalizeWorkspaceRoot 最终处理
                    }
                    catch { }
                }

                AddLog("INFO", LocalizationService.Instance.Format("agent.log.explorePromptBuilt", messages.Last().Content?.Length ?? 0, workspaceRoot));

                // ── 使用工具调用循环 ──
                string thinkingContent = string.Empty;
                string fullContent = string.Empty;
                var internalToolCalls = new List<string>();  // 收集内部工具调用，用于展示探索过程

                string aiResponse = await CallAiWithToolLoopAsync(
                    messages,
                    workspaceRoot,
                    ct,
                    onThinking: (thinking) =>
                    {
                        thinkingContent += thinking;
                        context.OnThinkingChunk?.Invoke(thinking);
                    },
                    onContent: (content) =>
                    {
                        fullContent += content;
                        context.OnContentChunk?.Invoke(content);
                    },
                    onToolCall: (toolSummary) =>
                    {
                        AddLog("TOOL", $"{toolSummary}");
                        internalToolCalls.Add(toolSummary);
                    });

                // ── 构建探索过程追踪（纯 Markdown blockquote，[explore-trace] 标记供主流程提取）──
                string explorationTraceMd = string.Empty;
                if (internalToolCalls.Count > 0)
                {
                    var traceBuilder = new StringBuilder();
                    traceBuilder.AppendLine("[explore-trace]");
                    traceBuilder.AppendLine($"> {LocalizationService.Instance.Format("agent.log.exploreTrace", internalToolCalls.Count)}");
                    foreach (var call in internalToolCalls)
                    {
                        traceBuilder.AppendLine($"> - {call}");
                    }
                    traceBuilder.AppendLine("[/explore-trace]");
                    traceBuilder.AppendLine();
                    explorationTraceMd = traceBuilder.ToString();
                }

                // ── 如果工具调用后有思考内容，通过 ReasoningContent 传递，让 UI 渲染原生思考面板 ──
                if (!string.IsNullOrEmpty(thinkingContent))
                {
                    result.ReasoningContent = thinkingContent;
                }

                // ── 最终内容 = 探索追踪 + AI 回答
                result.Content = string.IsNullOrEmpty(explorationTraceMd)
                    ? aiResponse
                    : $"{explorationTraceMd}\n\n{aiResponse}";

                result.Logs.AddRange(_logs);

                AddLog("INFO", LocalizationService.Instance.Format("agent.log.exploreDone", aiResponse.Length));
            }
            catch (OperationCanceledException)
            {
                result.Success = false;
                result.ErrorMessage = LocalizationService.Instance["agent.log.exploreCancelled"];
                AddLog("WARN", LocalizationService.Instance["agent.log.exploreCancelled"]);
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.ErrorMessage = ex.Message;
                AddLog("ERROR", LocalizationService.Instance.Format("agent.log.exploreFailed", ex.Message));
            }

            return result;
        }

        /// <summary>
        /// 构建 Explore prompt，包含 workspace 上下文和搜索指导。
        /// </summary>
        private static string BuildExplorePrompt(string userMessage, AgentContext context)
        {
            var sb = new StringBuilder();

            // ── 缓存优化：稳定性高的内容放前面 ──

            // 第1层：工作区路径（会话内不变，最稳定）
            if (!string.IsNullOrEmpty(context.SolutionPath))
            {
                string workspaceRoot = context.SolutionPath;
                bool isSlnFile = false;
                try
                {
                    if (System.IO.File.Exists(workspaceRoot))
                    {
                        workspaceRoot = System.IO.Path.GetDirectoryName(workspaceRoot) ?? workspaceRoot;
                        isSlnFile = true;
                    }
                }
                catch { }

                sb.AppendLine($"## 工作区");
                sb.AppendLine($"工作区根目录: `{workspaceRoot}`");
                if (isSlnFile)
                    sb.AppendLine($"解决方案文件: {context.SolutionPath}");
                else
                    sb.AppendLine($"项目类型: 文件夹项目（无 .sln 文件，如 CMake / Open Folder）");
                sb.AppendLine();
                sb.AppendLine(">  所有路径必须使用 Windows 绝对路径格式（如 `F:\\project\\src\\file.cs`）。");
                sb.AppendLine("> 不要使用 Linux 风格路径（如 `/home/user/...`）。");
                sb.AppendLine("> 使用 `list_dir` 从工作区根目录开始探索，用 `file_search` 和 `grep_search` 定位文件。");
                sb.AppendLine();
            }

            // 第2层：附加文件上下文（用户提供，会话内稳定）
            if (!string.IsNullOrEmpty(context.FileContext))
            {
                // RAG-MARK: no-truncate — 不再截断文件上下文，完整提供给 ExploreAgent
                // RAG-SOURCE: file-read 用户附加的文件上下文（ExploreAgent 概览）
                sb.AppendLine("## 附加文件上下文");
                sb.AppendLine(context.FileContext);
                sb.AppendLine();
            }

            // 第3层：当前计划信息（同步骤内不变）
            if (context.ActivePlan != null)
            {
                sb.AppendLine("## 当前计划信息");
                sb.AppendLine($"{LocalizationService.Instance["explore.task"]}: {context.ActivePlan.Title}");
                sb.AppendLine($"当前步骤: {context.ActivePlan.CurrentStepIndex}/{context.ActivePlan.Steps.Count}");
                sb.AppendLine();
            }

            // 第4层：探索任务（每问必变，放最后）
            // ── 打「当前任务目标」标记 ──
            //   与 Build/Edit 同理：该消息由 BuildContextAwareMessages 追加为新 user
            //   （deduplicateCurrentUser: false），不经过 Handoff 分支的前缀逻辑。
            sb.AppendLine("## 探索任务");
            sb.Append(LocalizationService.Instance["system.agent.handoffTaskGoalPrefix"]);
            sb.AppendLine(userMessage);
            sb.AppendLine();

            sb.AppendLine(AiPrompts.ExploreAgentInstructions);

            return sb.ToString();
        }

        #endregion

        #region Solution Discovery

        /// <summary>
        /// 可自动发现的源代码文件扩展名集合（引用 SharedConstants）。
        /// </summary>
        private static readonly HashSet<string> SourceFileExtensions = SharedConstants.SourceFileExtensions;

        /// <summary>
        /// 自动发现解决方案中的源代码文件。
        /// 所有 DTE COM 操作集中在一次主线程切换中完成，避免多次切换导致 UI 卡顿。
        /// 结果按 solutionPath 缓存，同一会话内重复调用直接返回缓存（以后会被 RAG 替代）。
        /// </summary>
        public async Task<List<string>> DiscoverSolutionFilesAsync(
            string solutionPath, int maxFiles = 200)
        {
            // ── 缓存策略：同一会话内重复调用直接返回缓存，避免重复枚举 DTE 项目 ──
            // 以后会被 RAG 向量检索替代
            string cacheKey = NormalizeCacheKey(solutionPath);
            lock (_discoveredFilesCache)
            {
                if (_discoveredFilesCache.TryGetValue(cacheKey, out var cached))
                {
                    AddLog("INFO", LocalizationService.Instance.Format("agent.log.discoverCacheHit", cached.Count, solutionPath));
                    // 如果缓存的文件数 >= 请求数，直接返回；否则返回全部缓存
                    if (cached.Count >= maxFiles)
                        return cached.Take(maxFiles).ToList();
                    return cached;
                }
            }

            var discoveredFiles = new List<string>();

            try
            {
                // ── 所有 DTE 操作集中在一次 SwitchToMainThreadAsync 中完成 ──
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                var dte = (EnvDTE.DTE)Microsoft.VisualStudio.Shell.Package
                    .GetGlobalService(typeof(EnvDTE.DTE));
                if (dte?.Solution == null || !dte.Solution.IsOpen)
                {
                    AddLog("INFO", LocalizationService.Instance["agent.log.discoverNoSolution"]);
                    return discoveredFiles;
                }

                AddLog("INFO", LocalizationService.Instance.Format("agent.log.discoverScanStart", solutionPath));

                // 遍历解决方案中的所有项目（同步，在主线程上）
                foreach (EnvDTE.Project project in dte.Solution.Projects)
                {
                    if (discoveredFiles.Count >= maxFiles) break;

                    try
                    {
                        CollectProjectFilesRecursive(
                            project.ProjectItems, discoveredFiles, maxFiles);
                    }
                    catch (Exception ex)
                    {
                        AddLog("WARN", LocalizationService.Instance.Format("agent.log.discoverProjectError", project.Name, ex.Message));
                    }
                }

                AddLog("INFO", LocalizationService.Instance.Format("agent.log.discoverDone", discoveredFiles.Count));
            }
            catch (Exception ex)
            {
                AddLog("ERROR", LocalizationService.Instance.Format("agent.log.discoverFailed", ex.Message));
            }

            // ── 回退：CMake / Open Folder 项目中 DTE ProjectItems 可能为空 ──
            // 使用目录扫描作为兜底方案，确保非 .sln 项目也能发现文件
            if (discoveredFiles.Count == 0 && !string.IsNullOrEmpty(solutionPath))
            {
                try
                {
                    string workspaceDir = solutionPath;
                    if (System.IO.File.Exists(workspaceDir))
                        workspaceDir = System.IO.Path.GetDirectoryName(workspaceDir) ?? workspaceDir;

                    if (System.IO.Directory.Exists(workspaceDir))
                    {
                        AddLog("INFO", "[Discover] " + LocalizationService.Instance.Format(
                    "agent.log.discoverDteFallback", workspaceDir));

                        var excludeDirs = SharedConstants.ExcludedDirectories;

                        await Task.Run(() =>
                        {
                            try
                            {
                                var allFiles = System.IO.Directory.GetFiles(workspaceDir, "*.*", System.IO.SearchOption.AllDirectories);
                                foreach (var file in allFiles)
                                {
                                    if (discoveredFiles.Count >= maxFiles) break;

                                    string dir = System.IO.Path.GetDirectoryName(file) ?? "";
                                    bool excluded = false;
                                    foreach (var excludeDir in excludeDirs)
                                    {
                                        if (dir.IndexOf(excludeDir, StringComparison.OrdinalIgnoreCase) >= 0)
                                        {
                                            excluded = true;
                                            break;
                                        }
                                    }
                                    if (excluded) continue;

                                    string ext = System.IO.Path.GetExtension(file);
                                    if (SourceFileExtensions.Contains(ext))
                                        discoveredFiles.Add(file);
                                }
                            }
                            catch (Exception ex)
                            {
                                AddLog("WARN", "[Discover] " + LocalizationService.Instance.Format(
                                    "agent.log.discoverDirScanFailed", ex.Message));
                            }
                        });

                        AddLog("INFO", "[Discover] " + LocalizationService.Instance.Format(
                            "agent.log.discoverDirScanDone", discoveredFiles.Count));
                    }
                }
                catch (Exception ex)
                {
                    AddLog("WARN", "[Discover] " + LocalizationService.Instance.Format(
                        "agent.log.discoverDirScanError", ex.Message));
                }
            }

            // ── 存入缓存（以后会被 RAG 替代）──
            if (discoveredFiles.Count > 0)
            {
                lock (_discoveredFilesCache)
                {
                    _discoveredFilesCache[cacheKey] = discoveredFiles;
                }
            }

            return discoveredFiles;
        }

        /// <summary>
        /// 同步递归遍历项目项，收集源代码文件路径。
        /// 必须在主线程上调用（访问 EnvDTE COM 对象）。
        /// </summary>
        private static void CollectProjectFilesRecursive(
            EnvDTE.ProjectItems projectItems,
            List<string> discoveredFiles,
            int maxFiles)
        {
            if (projectItems == null || discoveredFiles.Count >= maxFiles)
                return;

            foreach (EnvDTE.ProjectItem item in projectItems)
            {
                if (discoveredFiles.Count >= maxFiles) return;

                try
                {
                    // 递归处理子文件夹
                    if (item.ProjectItems != null && item.ProjectItems.Count > 0)
                    {
                        CollectProjectFilesRecursive(
                            item.ProjectItems, discoveredFiles, maxFiles);
                    }

                    // 获取文件路径
                    string? filePath = null;
                    try { filePath = item.FileNames[0]; } catch { }

                    if (!string.IsNullOrEmpty(filePath) && File.Exists(filePath))
                    {
                        string ext = Path.GetExtension(filePath);
                        bool isSourceFile = SourceFileExtensions.Contains(ext);

                        if (isSourceFile && !discoveredFiles.Contains(filePath, StringComparer.OrdinalIgnoreCase))
                        {
                            discoveredFiles.Add(filePath);
                        }
                    }
                }
                catch
                {
                    // 跳过无法访问的项
                }
            }
        }

        #endregion
    }
}
