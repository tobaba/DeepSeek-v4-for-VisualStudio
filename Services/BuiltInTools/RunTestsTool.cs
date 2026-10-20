using DeepSeek_v4_for_VisualStudio.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace DeepSeek_v4_for_VisualStudio.Services.BuiltInTools
{
    /// <summary>
    /// run_tests 工具 — 运行工作区中的 .NET 单元测试并返回结构化结果。
    /// 委托给 <see cref="ITestService"/> 执行实际测试。
    /// </summary>
    public class RunTestsTool : BuiltInToolBase
    {
        private readonly ITestService? _testService;

        public RunTestsTool(ITestService? testService = null)
        {
            _testService = testService;
        }

        public override string Name => "run_tests";

        public override ToolDefinition GetDefinition()
        {
            return new ToolDefinition
            {
                Type = "function",
                Function = new ToolFunction
                {
                    Name = "run_tests",
                    Description = L["tool.run_tests.desc"],
                    Parameters = new
                    {
                        type = "object",
                        properties = new
                        {
                            project = new
                            {
                                type = "string",
                                description = LocalizationService.Instance["tool.runTests.param.project"]
                            },
                            filter = new
                            {
                                type = "string",
                                description = LocalizationService.Instance["tool.runTests.param.filter"]
                            },
                            configuration = new
                            {
                                type = "string",
                                description = LocalizationService.Instance["tool.runTests.param.configuration"]
                            }
                        },
                        required = new string[] { }
                    }
                }
            };
        }

        public override async Task<string> ExecuteAsync(
            Dictionary<string, JsonElement> args, string? workspaceRoot)
        {
            var testService = _testService;
            if (testService == null)
                return LocalizationService.Instance["tool.runTests.serviceNotInit"];

            string? project = NullIfEmpty(GetStringArg(args, "project"));
            string? filter = NullIfEmpty(GetStringArg(args, "filter"));
            string? configuration = NullIfEmpty(GetStringArg(args, "configuration"));

            TestRunResult result;
            try
            {
                result = await testService.RunTestsAsync(
                    workspaceRoot, project, filter, configuration, CancellationToken);
            }
            catch (Exception ex)
            {
                return LocalizationService.Instance.Format(
                    "tool.runTests.failed", ex.Message);
            }

            return FormatResult(result);
        }

        /// <summary>
        /// 把结构化测试结果渲染成给模型看的文本。
        /// </summary>
        internal static string FormatResult(TestRunResult result)
        {
            var L = LocalizationService.Instance;

            switch (result.Error)
            {
                case TestRunErrorKind.NoTestProject:
                    return L["tool.runTests.noTestProject"];

                case TestRunErrorKind.MultipleProjects:
                    var sb = new StringBuilder(L["tool.runTests.multipleProjects"]);
                    sb.AppendLine();
                    foreach (string p in result.CandidateProjects)
                        sb.AppendLine($"- {p}");
                    return sb.ToString().TrimEnd();

                case TestRunErrorKind.RunnerNotFound:
                    return L["tool.runTests.runnerNotFound"];

                case TestRunErrorKind.LaunchFailed:
                    return L["tool.runTests.launchFailed"];

                case TestRunErrorKind.Timeout:
                    return L["tool.runTests.timeout"];

                case TestRunErrorKind.NoResultProduced:
                    var noResult = new StringBuilder(L["tool.runTests.noResultProduced"]);
                    if (!string.IsNullOrWhiteSpace(result.OutputTail))
                    {
                        noResult.AppendLine();
                        noResult.AppendLine("```");
                        noResult.AppendLine(result.OutputTail);
                        noResult.Append("```");
                    }
                    return noResult.ToString();
            }

            // ── 正常跑完：输出计数 + 失败明细 ──
            var output = new StringBuilder();

            // 首行是稳定的机器可读摘要（不随语言变化），供 GetResultSummary 与模型快速定位
            output.AppendLine(string.Format(
                "total={0} passed={1} failed={2} skipped={3} elapsed={4}s",
                result.Total, result.Passed, result.Failed, result.Skipped,
                result.Duration.TotalSeconds.ToString("F1")));

            output.AppendLine(string.Format(
                L["tool.runTests.summary"],
                result.Total, result.Passed, result.Failed, result.Skipped,
                result.Duration.TotalSeconds.ToString("F1")));

            if (result.Failed > 0)
            {
                output.AppendLine();
                output.AppendLine(L["tool.runTests.failedDetails"]);
                foreach (var failed in result.FailedTests)
                {
                    output.AppendLine($"- {failed.Name}");
                    if (!string.IsNullOrWhiteSpace(failed.Message))
                        output.AppendLine($"  {failed.Message}");
                }

                if (result.Failed > result.FailedTests.Count)
                {
                    output.AppendLine(string.Format(
                        L["tool.runTests.moreFailures"],
                        result.Failed - result.FailedTests.Count));
                }
            }

            return output.ToString().TrimEnd();
        }

        public override string GetDisplayText(Dictionary<string, JsonElement> args)
        {
            string filter = GetStringArg(args, "filter");
            return string.IsNullOrEmpty(filter)
                ? LocalizationService.Instance["tool.runTests.displayText"]
                : LocalizationService.Instance.Format("tool.runTests.displayTextWithFilter", filter);
        }

        public override string GetResultSummary(string toolResult)
        {
            string L(string key) => LocalizationService.Instance[key];

            if (string.IsNullOrEmpty(toolResult)) return L("tool.common.noResult");
            if (toolResult.StartsWith("Error: ")) return toolResult;

            var match = System.Text.RegularExpressions.Regex.Match(
                toolResult, @"passed=(\d+) failed=(\d+) skipped=(\d+)");
            if (match.Success &&
                int.TryParse(match.Groups[1].Value, out int passed) &&
                int.TryParse(match.Groups[2].Value, out int failed) &&
                int.TryParse(match.Groups[3].Value, out int skipped))
            {
                int total = passed + failed + skipped;
                return failed == 0
                    ? L("tool.runTests.summaryAllPassed")
                    : string.Format(L("tool.runTests.summarySomeFailed"), failed, total);
            }

            return L("tool.runTests.complete");
        }

        private static string? NullIfEmpty(string value) =>
            string.IsNullOrWhiteSpace(value) ? null : value;
    }
}
