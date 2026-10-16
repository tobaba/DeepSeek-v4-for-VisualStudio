using System;
using System.Collections.Generic;
using System.IO;

namespace DeepSeek_v4_for_VisualStudio.Services
{
    /// <summary>
    /// 附件候选路径的分类结果。用于把「一批待添加的路径」与「已附加列表」比对后
    /// 分成可直接添加、重复、格式不支持、目录、不存在几类，供调用方统一提示。
    /// </summary>
    internal sealed class AttachmentCollectResult
    {
        /// <summary>可添加的路径（已归一化、去重、且不在既有列表中）。</summary>
        public IReadOnlyList<string> Added { get; }

        /// <summary>已存在于既有列表中的路径（按归一化后的全路径比较）。</summary>
        public IReadOnlyList<string> AlreadyAttached { get; }

        /// <summary>扩展名不受 <see cref="FileParserService.IsSupportedFormat"/> 支持的路径。</summary>
        public IReadOnlyList<string> Unsupported { get; }

        /// <summary>目录路径。当前策略为不递归、直接跳过。</summary>
        public IReadOnlyList<string> Directories { get; }

        /// <summary>不存在（或无法访问）的文件路径。</summary>
        public IReadOnlyList<string> NotFound { get; }

        /// <summary>被忽略的输入项（null 或空白）。</summary>
        public int BlankIgnored { get; }

        internal AttachmentCollectResult(
            IReadOnlyList<string> added,
            IReadOnlyList<string> alreadyAttached,
            IReadOnlyList<string> unsupported,
            IReadOnlyList<string> directories,
            IReadOnlyList<string> notFound,
            int blankIgnored)
        {
            Added = added;
            AlreadyAttached = alreadyAttached;
            Unsupported = unsupported;
            Directories = directories;
            NotFound = notFound;
            BlankIgnored = blankIgnored;
        }

        /// <summary>本次可实际新增的附件数量。</summary>
        public int AddedCount => Added.Count;

        /// <summary>是否存在任何需要用户注意的跳过项。</summary>
        public bool HasSkipped => AlreadyAttached.Count > 0
            || Unsupported.Count > 0
            || Directories.Count > 0
            || NotFound.Count > 0;
    }

    /// <summary>
    /// 附件候选路径的归一化、去重与分类（纯逻辑，无 UI 与 DI 依赖，便于单元测试）。
    /// <para>
    /// 原本「去重 + 格式校验」散落在各添加入口（文件对话框、活动文档、项目文件、
    /// 调试输出、剪贴板等）共 8 处内联实现，且行为不一致（多数路径对重复项静默跳过、
    /// 部分对不支持格式仅写日志）。此类型收敛该逻辑并统一分类，供各入口复用。
    /// </para>
    /// <para>
    /// 注意：与既有实现相比，本类型使用 <see cref="Path.GetFullPath(string)"/> 归一化后再比较，
    /// 因此 <c>C:\a\B.cs</c> 与 <c>c:\A\b.cs</c> 会被视为同一文件（既有实现直接比较原始字符串，
    /// 会视为两个不同文件）。这是有意的行为改进。
    /// </para>
    /// </summary>
    internal static class AttachmentCollector
    {
        /// <summary>
        /// 对候选路径分类。
        /// </summary>
        /// <param name="candidates">待添加的路径集合；可为 null。</param>
        /// <param name="existing">已附加的路径列表，用于判重；可为 null。</param>
        /// <returns>分类结果，各集合互斥且保持输入顺序。</returns>
        internal static AttachmentCollectResult Collect(
            IEnumerable<string>? candidates,
            IEnumerable<string>? existing)
        {
            var existingSet = BuildPathSet(existing);

            var added = new List<string>();
            var alreadyAttached = new List<string>();
            var unsupported = new List<string>();
            var directories = new List<string>();
            var notFound = new List<string>();
            int blankIgnored = 0;

            if (candidates == null)
            {
                return new AttachmentCollectResult(
                    added, alreadyAttached, unsupported, directories, notFound, blankIgnored);
            }

            // 同一批输入内部也要去重（保留首次出现的位置）
            var seenInBatch = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (string? candidate in candidates)
            {
                if (string.IsNullOrWhiteSpace(candidate))
                {
                    blankIgnored++;
                    continue;
                }

                string normalized = NormalizePath(candidate);

                // 归一化失败（非法字符等）时退回原始字符串，避免整批失败
                string key = normalized.Length == 0 ? candidate.Trim() : normalized;

                // 批次内重复：归入 AlreadyAttached，不再重复校验
                if (!seenInBatch.Add(key))
                {
                    alreadyAttached.Add(key);
                    continue;
                }

                if (existingSet.Contains(key))
                {
                    alreadyAttached.Add(key);
                    continue;
                }

                if (Directory.Exists(key))
                {
                    // 目录不递归展开：递归极易被拖入 bin/obj/node_modules 等大目录撑爆上下文
                    directories.Add(key);
                    continue;
                }

                if (!File.Exists(key))
                {
                    notFound.Add(key);
                    continue;
                }

                if (!FileParserService.IsSupportedFormat(key))
                {
                    unsupported.Add(key);
                    continue;
                }

                added.Add(key);
            }

            return new AttachmentCollectResult(
                added, alreadyAttached, unsupported, directories, notFound, blankIgnored);
        }

        /// <summary>
        /// 归一化为绝对全路径。失败时返回空字符串，由调用方决定回退策略。
        /// </summary>
        private static string NormalizePath(string path)
        {
            try
            {
                return Path.GetFullPath(path.Trim());
            }
            catch (Exception)
            {
                // 路径含非法字符、过长等；交由调用方按原始字符串处理
                return string.Empty;
            }
        }

        private static HashSet<string> BuildPathSet(IEnumerable<string>? paths)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (paths == null)
            {
                return set;
            }

            foreach (string? path in paths)
            {
                if (string.IsNullOrWhiteSpace(path))
                {
                    continue;
                }

                string normalized = NormalizePath(path);
                set.Add(normalized.Length == 0 ? path.Trim() : normalized);
            }

            return set;
        }
    }
}