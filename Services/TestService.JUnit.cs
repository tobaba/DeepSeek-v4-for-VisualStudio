using DeepSeek_v4_for_VisualStudio.Utils;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Xml.Linq;

namespace DeepSeek_v4_for_VisualStudio.Services
{
    /// <summary>
    /// JUnit XML 解析 — 用于 CTest（cmake --test 的 --output-junit）结果。
    /// 与 TRX 解析并列：两种格式都转成同一份 <see cref="TestRunResult"/>，
    /// 上层渲染与失败判定无需感知测试技术栈。
    /// </summary>
    public partial class TestService
    {
        /// <summary>
        /// 解析 JUnit XML，填充计数、失败明细与耗时。
        ///
        /// CTest 生成的 JUnit 结构为 &lt;testsuite&gt; 下扁平排列的 &lt;testcase&gt;；
        /// 其他工具可能嵌套 &lt;testsuite&gt;，因此这里遍历所有 testcase 节点而不假设层级。
        /// 失败的三种表达都要覆盖：&lt;failure&gt; / &lt;error&gt; 子元素，以及 status="fail" 属性。
        /// </summary>
        internal static void ParseJUnitResult(string junitPath, TestRunResult result)
        {
            XDocument doc = XDocument.Load(junitPath);
            var testCases = doc.Descendants("testcase").ToList();

            double totalTime = 0;
            foreach (var testCase in testCases)
            {
                result.Total++;

                bool failed = false;
                string message = string.Empty;

                var failure = testCase.Element("failure");
                var error = testCase.Element("error");
                var skipped = testCase.Element("skipped");

                if (failure != null)
                {
                    failed = true;
                    message = ExtractJUnitMessage(failure, testCase);
                }
                else if (error != null)
                {
                    failed = true;
                    message = ExtractJUnitMessage(error, testCase);
                }
                else if (skipped != null)
                {
                    // 跳过/禁用：计入 skipped，不算失败
                }
                else if (string.Equals((string?)testCase.Attribute("status"), "fail",
                             StringComparison.OrdinalIgnoreCase))
                {
                    // 部分工具只用 status 属性标记失败（没有 <failure> 子元素）
                    failed = true;
                }

                if (failed)
                {
                    result.Failed++;
                    if (result.FailedTests.Count < MaxFailedTests)
                    {
                        result.FailedTests.Add(new FailedTestInfo
                        {
                            Name = ReadTestCaseName(testCase),
                            Message = message,
                        });
                    }
                }
                else if (skipped != null)
                {
                    result.Skipped++;
                }
                else
                {
                    result.Passed++;
                }

                totalTime += ReadSeconds(testCase.Attribute("time")?.Value);
            }

            // 顶层 testsuite 的 time 更接近总耗时；缺失再用单个 testcase 累加值
            double suiteTime = ReadSeconds(doc.Root?.Element("testsuite")?.Attribute("time")?.Value);
            if (suiteTime <= 0)
                suiteTime = ReadSeconds(doc.Root?.Attribute("time")?.Value);
            if (suiteTime <= 0)
                suiteTime = totalTime;

            if (suiteTime > 0)
                result.Duration = TimeSpan.FromSeconds(suiteTime);

            Logger.Info(
                $"[TestService] JUnit results: total {result.Total}, passed {result.Passed}, " +
                $"failed {result.Failed}, skipped {result.Skipped}");
        }

        /// <summary>
        /// 读取用例名：优先 name，其次 classname.name 组合，最后回退到整行文本。
        /// </summary>
        private static string ReadTestCaseName(XElement testCase)
        {
            string name = (string?)testCase.Attribute("name") ?? string.Empty;
            string className = (string?)testCase.Attribute("classname") ?? string.Empty;

            if (string.IsNullOrEmpty(className))
                return string.IsNullOrEmpty(name) ? "(unknown)" : name;

            if (string.IsNullOrEmpty(name) || className.Equals(name, StringComparison.Ordinal))
                return className;

            // 递归 classname 已经包含用例名时（classname="Suite.Case", name="Case"）不再重复拼接
            if (className.EndsWith("." + name, StringComparison.Ordinal))
                return className;

            // 反过来 name 已经是全限定名时直接用 name
            if (name.StartsWith(className + ".", StringComparison.Ordinal))
                return name;

            return className + "." + name;
        }

        /// <summary>
        /// 提取失败消息。
        ///
        /// CTest 生成的 JUnit 里 message 常是占位符（"Failed"），真正的断言详情
        /// 写在 &lt;system-out&gt; 里（gtest 的 "error: Expected equality of these values"）。
        /// 因此这里先看 message 是否有信息量，没有再从输出里摘出失败片段。
        /// </summary>
        private static string ExtractJUnitMessage(XElement failureElement, XElement testCase)
        {
            string message = (string?)failureElement.Attribute("message") ?? string.Empty;
            if (string.IsNullOrWhiteSpace(message))
                message = (string?)failureElement.Attribute("type") ?? string.Empty;
            if (string.IsNullOrWhiteSpace(message))
                message = failureElement.Value;

            message = NormalizeMessage(message);

            // message 只是占位（CTest 固定写 "Failed"）时，改用用例输出里的失败片段
            if (IsGenericFailureMessage(message))
            {
                string excerpt = ExtractFailureExcerpt(
                    (string?)testCase.Element("system-out") ?? string.Empty,
                    (string?)testCase.Element("system-err") ?? string.Empty);

                if (!string.IsNullOrWhiteSpace(excerpt))
                    message = excerpt;
            }

            return message;
        }

        /// <summary>CTest / 部分工具写的占位失败消息，本身不含诊断信息。</summary>
        private static bool IsGenericFailureMessage(string message)
        {
            if (string.IsNullOrWhiteSpace(message))
                return true;

            string trimmed = message.Trim();
            return trimmed.Equals("Failed", StringComparison.OrdinalIgnoreCase) ||
                   trimmed.Equals("Fail", StringComparison.OrdinalIgnoreCase) ||
                   trimmed.Equals("Failure", StringComparison.OrdinalIgnoreCase) ||
                   trimmed.Equals("Error", StringComparison.OrdinalIgnoreCase) ||
                   trimmed.Equals("test failed", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>失败原因关键字，按信息量排序。</summary>
        private static readonly string[] FailureReasonMarkers =
        {
            "error:",              // gtest 断言：error: Expected equality of these values:
            "AddressSanitizer",
            "runtime error:",
            "Assertion",
            "Expected:",
            "[ fatal ]",           // gtest FATAL（例如临时文件无法创建）
            "fatal error",
            "terminate called",
            "unhandled exception",
        };

        /// <summary>
        /// 从测试输出里摘出失败原因片段：命中关键字的行 + 其后若干行上下文
        /// （gtest 把期望值/实际值放在断言行之后，只取一行会丢信息）。
        /// </summary>
        private static string ExtractFailureExcerpt(string systemOut, string systemErr)
        {
            foreach (string source in new[] { systemOut, systemErr })
            {
                if (string.IsNullOrWhiteSpace(source))
                    continue;

                string[] lines = source.Split(new[] { '\r', '\n' }, StringSplitOptions.None);
                for (int i = 0; i < lines.Length; i++)
                {
                    string line = lines[i].Trim();
                    if (line.Length == 0)
                        continue;

                    if (!FailureReasonMarkers.Any(marker =>
                            line.IndexOf(marker, StringComparison.OrdinalIgnoreCase) >= 0))
                    {
                        continue;
                    }

                    var excerpt = new List<string> { line };
                    for (int j = i + 1; j < lines.Length && excerpt.Count < ExcerptLines; j++)
                    {
                        string next = lines[j].Trim();
                        if (next.Length == 0)
                            continue;
                        excerpt.Add(next);
                    }

                    return NormalizeMessage(string.Join(" ", excerpt));
                }
            }

            // 没命中已知关键字时，退一步取输出末尾若干行：
            // 测试框架通常把失败摘要（[ FAILED ] 行 / FAILED TEST 计数）放在最后。
            string fallbackSource = !string.IsNullOrWhiteSpace(systemOut) ? systemOut : systemErr;
            if (string.IsNullOrWhiteSpace(fallbackSource))
                return string.Empty;

            string[] fallbackLines = fallbackSource
                .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(l => l.Trim())
                .Where(l => l.Length > 0)
                .ToArray();

            return fallbackLines.Length == 0
                ? string.Empty
                : NormalizeMessage(string.Join(" ",
                    fallbackLines.Skip(Math.Max(0, fallbackLines.Length - 3))));
        }

        /// <summary>失败片段最多保留的行数（含命中行）。</summary>
        private const int ExcerptLines = 5;

        /// <summary>把多行消息压成单行并截断到上限。</summary>
        private static string NormalizeMessage(string message)
        {
            message = string.Join(" ", message
                .Split(new[] { '\r', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(l => l.Trim())
                .Where(l => l.Length > 0));

            if (message.Length > MaxMessageLength)
                message = message.Substring(0, MaxMessageLength) + "…";

            return message;
        }

        /// <summary>读取 JUnit 的 time 属性（秒，可能带小数）。</summary>
        private static double ReadSeconds(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return 0;

            return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds)
                ? seconds
                : 0;
        }
    }
}
