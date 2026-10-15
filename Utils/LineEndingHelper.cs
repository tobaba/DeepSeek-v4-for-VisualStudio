using System;
using System.Collections.Generic;
using System.IO;

namespace DeepSeek_v4_for_VisualStudio.Utils
{
    /// <summary>
    /// 换行风格（行尾符）探测与规范化 —— 全项目唯一的换行判定实现。
    ///
    /// 背景：AI 编辑代码必须保持目标文件原本的换行风格。此前各编辑链路各自硬编码 CRLF，
    /// 导致 LF 文件被整体改写 —— 撤销一个 hunk、替换一次字符串都会产生「整个文件都改了」的 diff。
    ///
    /// 策略：能探测到就沿用文本/文件原有的风格；探测不到（新建文件、空文件、单行无换行文本）
    /// 则回退 <see cref="Default"/>，即跟随系统环境的 <see cref="Environment.NewLine"/>。
    ///
    /// 与编码判定（<see cref="FileEncodingHelper"/>）保持同一思路：
    /// 「读什么编码就写什么编码」→「文件原本什么换行就写什么换行」。
    /// </summary>
    public static class LineEndingHelper
    {
        /// <summary>CRLF —— Windows 惯例。</summary>
        public const string CrLf = "\r\n";

        /// <summary>LF —— Unix / macOS / Git 默认。</summary>
        public const string Lf = "\n";

        /// <summary>CR —— 经典 Mac 惯例（现代已罕见，但旧文件仍可能出现）。</summary>
        public const string Cr = "\r";

        /// <summary>
        /// 探测不到换行风格时使用的默认值 —— 跟随运行环境的系统约定
        /// （Windows 下为 CRLF，类 Unix 下为 LF）。
        /// </summary>
        public static string Default => Environment.NewLine;

        /// <summary>
        /// 统计 \r\n / \n / \r 三种行尾，返回出现次数最多的一种。
        /// 三者数量相同时偏向 CRLF，其次 LF —— 与 Visual Studio 宿主环境的惯例一致。
        /// </summary>
        /// <param name="text">待探测文本。</param>
        /// <param name="lineEnding">探测到的行尾；返回 false 时无意义。</param>
        /// <returns>文本中存在换行时返回 true；否则 false。</returns>
        public static bool TryDetect(string? text, out string lineEnding)
        {
            lineEnding = Default;
            if (string.IsNullOrEmpty(text)) return false;

            int crlf = 0, lf = 0, cr = 0;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (c == '\r')
                {
                    // \r\n 只记一次，不与后面的 \n 重复计数
                    if (i + 1 < text.Length && text[i + 1] == '\n') { crlf++; i++; }
                    else cr++;
                }
                else if (c == '\n')
                {
                    lf++;
                }
            }

            if (crlf == 0 && lf == 0 && cr == 0) return false;

            lineEnding = (crlf >= lf && crlf >= cr) ? CrLf
                       : (lf >= cr ? Lf : Cr);
            return true;
        }

        /// <summary>
        /// 探测文本的主要换行风格。文本自身无换行时退回 <paramref name="fallback"/> 的风格，
        /// 仍无结果则返回 <see cref="Default"/>（系统环境）。
        /// </summary>
        public static string Detect(string? text, string? fallback = null)
        {
            if (TryDetect(text, out string ending)) return ending;
            if (!string.IsNullOrEmpty(fallback) && TryDetect(fallback, out ending)) return ending;
            return Default;
        }

        /// <summary>
        /// 把三种行尾统一成 LF —— 用于比较、拼接等需要「同一口径」的场景。
        /// </summary>
        public static string Normalize(string? text)
        {
            if (string.IsNullOrEmpty(text)) return text ?? string.Empty;
            return text.Replace(CrLf, Lf).Replace(Cr, Lf);
        }

        /// <summary>
        /// 把已规范化为 LF 的文本转换到指定行尾风格。
        /// 入参必须已无 \r，否则会残留（调用方应先经 <see cref="Normalize"/>，或用 <see cref="Conform"/>）。
        /// </summary>
        public static string Apply(string? text, string lineEnding)
        {
            if (string.IsNullOrEmpty(text)) return text ?? string.Empty;
            if (string.IsNullOrEmpty(lineEnding) || lineEnding == Lf) return text;
            return text.Replace(Lf, lineEnding);
        }

        /// <summary>
        /// 用指定行尾风格重写文本行尾（内部先规范化到 LF 再转换），等价于
        /// 「不管原文本是什么风格，统一变成 <paramref name="lineEnding"/>」。
        /// </summary>
        public static string Conform(string? text, string lineEnding)
        {
            return Apply(Normalize(text), lineEnding);
        }

        /// <summary>
        /// 解析写入指定文件时应使用的换行风格：
        /// 文件已存在 → 沿用其原有风格；否则（新建 / 读取失败）→ <see cref="Default"/>（系统环境）。
        ///
        /// 读取失败或文件为空时一律回退默认值，绝不因探测问题阻断写入。
        /// </summary>
        /// <param name="filePath">目标文件路径。</param>
        public static string ResolveForFile(string filePath)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
                    return Default;

                // 走 FileEncodingHelper 读取，保证 UTF-16 / ANSI 等编码下按「字符」而非
                // 「字节」判定行尾（UTF-16 的 \n 是 0A 00，按字节扫描会误判）。
                string existing = FileEncodingHelper.ReadAllText(filePath);
                return Detect(existing);
            }
            catch
            {
                // 探测失败不影响写入：落入系统环境默认
                return Default;
            }
        }

        /// <summary>
        /// 按目标文件原本的换行风格重写文本行尾。
        /// 编辑链路写盘前应统一调用本方法，避免把 LF 文件改成 CRLF。
        /// </summary>
        /// <param name="text">待写入的文本内容。</param>
        /// <param name="filePath">目标文件路径（已存在则沿用其风格）。</param>
        public static string ConformToFile(string? text, string filePath)
        {
            return Conform(text, ResolveForFile(filePath));
        }

        /// <summary>
        /// 按行切分，三种行尾（\r\n / \n / \r）都支持。
        ///
        /// 注意：文本以换行结尾时会**保留**末尾的空元素（"A\r\n" → ["A", ""]），
        /// 这样 join 回去能还原尾换行，行号也与文件实际内容一一对应。
        /// 这与 <see cref="FileEncodingHelper.ReadAllLines"/>（对齐 File.ReadAllLines 语义、
        /// 丢弃末尾空行项）是两套语义，使用前请确认需要哪一种。
        /// </summary>
        public static string[] SplitLines(string? text)
        {
            if (string.IsNullOrEmpty(text)) return Array.Empty<string>();

            var lines = new List<string>();
            int start = 0;

            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (c == '\r')
                {
                    lines.Add(text.Substring(start, i - start));
                    if (i + 1 < text.Length && text[i + 1] == '\n') i++;   // \r\n 记一次
                    start = i + 1;
                }
                else if (c == '\n')
                {
                    lines.Add(text.Substring(start, i - start));
                    start = i + 1;
                }
            }

            lines.Add(text.Substring(start));   // 末尾残余（含"以换行结尾"时的空串）
            return lines.ToArray();
        }
    }
}
