using DeepSeek_v4_for_VisualStudio.Utils;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;

namespace DeepSeek_v4_for_VisualStudio.Services
{
    /// <summary>
    /// TRX（VSTest 结果日志）解析。
    /// 用 XML 解析而非匹配控制台文本，避免受语言与格式变化影响。
    /// </summary>
    public partial class TestService
    {
        /// <summary>TRX 命名空间（每次日志生成时由 VSTest 写入）。</summary>
        private const string TrxDefaultNamespace =
            "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";

        /// <summary>
        /// 解析 TRX 文件，填充计数、失败明细与耗时。
        /// </summary>
        internal static void ParseTrxResult(string trxPath, TestRunResult result)
        {
            XDocument doc = XDocument.Load(trxPath);
            XNamespace ns = doc.Root?.Name.Namespace ?? TrxDefaultNamespace;

            // ── 结果集合（每条 Outcome 一次）──
            var unitResults = doc.Descendants(ns + "UnitTestResult").ToList();
            foreach (var unitResult in unitResults)
            {
                string outcome = (string?)unitResult.Attribute("outcome") ?? string.Empty;
                result.Total++;

                if (outcome.Equals("Passed", StringComparison.OrdinalIgnoreCase))
                {
                    result.Passed++;
                }
                else if (outcome.Equals("NotExecuted", StringComparison.OrdinalIgnoreCase) ||
                         outcome.Equals("Skipped", StringComparison.OrdinalIgnoreCase) ||
                         outcome.Equals("Inconclusive", StringComparison.OrdinalIgnoreCase))
                {
                    result.Skipped++;
                }
                else
                {
                    // Failed / Timeout / Aborted 等一律计入失败
                    result.Failed++;
                    if (result.FailedTests.Count < MaxFailedTests)
                    {
                        result.FailedTests.Add(new FailedTestInfo
                        {
                            Name = (string?)unitResult.Attribute("testName") ?? "(unknown)",
                            Message = ExtractFailureMessage(unitResult, ns),
                        });
                    }
                }
            }

            // ── 汇总耗时（TRX 里是 "hh:mm:ss.fffffff"）──
            var times = doc.Descendants(ns + "Times").FirstOrDefault();
            string? durationText = (string?)times?.Attribute("duration");
            if (!string.IsNullOrEmpty(durationText) &&
                TimeSpan.TryParse(durationText, out TimeSpan parsed))
            {
                result.Duration = parsed;
            }

            Logger.Info(
                $"[TestService] Results: total {result.Total}, passed {result.Passed}, " +
                $"failed {result.Failed}, skipped {result.Skipped}");
        }

        /// <summary>
        /// 从 <c>UnitTestResult/Output/ErrorInfo</c> 提取失败消息。
        /// 优先 Message，其次 StackTrace；两者都无则回退到 Output 文本。
        /// </summary>
        private static string ExtractFailureMessage(XElement unitResult, XNamespace ns)
        {
            var errorInfo = unitResult.Element(ns + "Output")?.Element(ns + "ErrorInfo");

            string message = (string?)errorInfo?.Element(ns + "Message") ?? string.Empty;
            if (string.IsNullOrWhiteSpace(message))
                message = (string?)errorInfo?.Element(ns + "StackTrace") ?? string.Empty;

            if (string.IsNullOrWhiteSpace(message))
                message = (string?)unitResult.Element(ns + "Output")?.Value ?? string.Empty;

            // 归一化空白，保持单行可读
            message = string.Join(" ", message
                .Split(new[] { '\r', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(l => l.Trim())
                .Where(l => l.Length > 0));

            if (message.Length > MaxMessageLength)
                message = message.Substring(0, MaxMessageLength) + "…";

            return message;
        }
    }
}
