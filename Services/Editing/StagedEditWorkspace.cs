using DeepSeek_v4_for_VisualStudio.Models;
using DeepSeek_v4_for_VisualStudio.Utils;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace DeepSeek_v4_for_VisualStudio.Services.Editing
{
    /// <summary>
    /// Agent 多步编辑的写穿 + 撤销追踪工作区。
    ///
    /// 设计（v1.1.12 修正）：
    /// 为保证读写一致性（read_file / 构建校验必须看到最新代码），编辑内容【直接落盘】，
    /// 而不是暂存在内存。为保证"裸盘落盘"也能撤销，首次改动每个文件时记录其 Baseline
    /// （原始内容 / 原始哈希），用户撤销时通过 <see cref="RestoreToBaseline"/> 回写磁盘。
    ///
    /// 使用方式：
    /// 1. Agent 开始时创建 Workspace。
    /// 2. 编辑工具通过 Workspace.ReadFile/WriteFile 读写 —— WriteFile 直接落盘并登记 Baseline。
    /// 3. Agent 完成后调用 <see cref="ToPreparedChangeBatch"/> 生成变更 Batch（供 diff 预览）。
    /// 4. 用户保留 → 磁盘已是最终内容，无需额外提交；用户撤销 → <see cref="RestoreToBaseline"/> 回滚。
    ///
    /// 线程安全：所有公开方法通过 _lock 保护。
    /// </summary>
    public sealed class StagedEditWorkspace
    {
        private readonly object _lock = new();
        private readonly Dictionary<string, StagedFile> _trackedFiles
            = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// 已打开文档写入器（可选注入，推荐 EditBufferApplier.TryWriteOpenDocument）。
        /// 签名：(filePath, fullContent) => true 表示文件已在编辑器打开并通过 buffer+编辑器 Save 写入。
        /// 命中时避免 File.WriteAllText 裸写盘在 dirty buffer 场景触发 VS「文件已在磁盘上修改」弹窗；
        /// 返回 false（未打开）时回退裸写盘。磁盘仍是权威内容，写穿设计与 Baseline 追踪不受影响。
        /// </summary>
        public Func<string, string, bool>? OpenDocumentWriter { get; set; }

        /// <summary>
        /// 已打开文档内容读取器（可选注入，推荐 EditBufferApplier.TryGetOpenDocumentContent）。
        /// 首次接触文件时读取当前编辑器 buffer 内容作为 Baseline；不做预保存。
        /// 这样既能保留用户未保存修改用于撤销，又避免在工具执行关键路径中等待 VS 的保存对话框。
        /// </summary>
        public Func<string, string?>? OpenDocumentContentProvider { get; set; }

        /// <summary>
        /// 某个文件首次进入撤销追踪时触发（参数为规范化后的绝对路径）。
        /// 供文件改动列表在「每改完一个文件」时即时登记待处理项；
        /// 回调在 _lock 之外触发，订阅方可以安全地回读 Workspace。
        /// </summary>
        public event Action<string>? FileTracked;

        /// <summary>当前追踪（有改动）的文件数</summary>
        public int StagedCount
        {
            get { lock (_lock) return _trackedFiles.Count; }
        }

        /// <summary>是否有任何被追踪的改动</summary>
        public bool HasAnyTrackedChanges
        {
            get { lock (_lock) return _trackedFiles.Count > 0; }
        }

        /// <summary>
        /// 获取文件在当前工作区中的变更类型；未跟踪时返回 null。
        /// </summary>
        public ProposedFileOperation? GetOperation(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath))
                return null;

            var normalizedPath = NormalizePath(filePath);
            lock (_lock)
            {
                return _trackedFiles.TryGetValue(normalizedPath, out var file)
                    ? file.Operation
                    : null;
            }
        }

        /// <summary>
        /// 读取文件内容。直接读取磁盘（内容已落盘）。
        /// </summary>
        public string ReadFile(string filePath)
        {
            var normalizedPath = NormalizePath(filePath);
            if (OpenDocumentContentProvider != null)
            {
                try
                {
                    var bufferContent = OpenDocumentContentProvider(normalizedPath);
                    if (bufferContent != null)
                        return bufferContent;
                }
                catch (Exception ex)
                {
                    Logger.Warn($"[StagedWorkspace] 读取打开文档失败，回退磁盘: {Path.GetFileName(normalizedPath)} — {ex.Message}");
                }
            }

            return File.Exists(normalizedPath) ? FileEncodingHelper.ReadAllText(normalizedPath) : string.Empty;
        }

        /// <summary>
        /// 写入文件内容（写穿落盘）。
        /// 首次接触此文件时记录 Baseline（原始内容 + 哈希），供用户撤销时恢复。
        ///
        /// 落盘路径：
        /// - 注入 OpenDocumentWriter 且文件已在编辑器打开 → buffer 整体替换 + 编辑器 Save（锁外执行，
        ///   因内部需切换 UI 线程，持 _lock 等待 UI 线程会有死锁风险）；
        /// - 否则 → File.WriteAllText 裸写盘（原有行为）。
        /// </summary>
        public void WriteFile(string filePath, string newContent)
        {
            if (string.IsNullOrWhiteSpace(filePath))
                throw new ArgumentException("文件路径不能为空", nameof(filePath));

            var normalizedPath = NormalizePath(filePath);
            string content = newContent ?? string.Empty;

            // ── 换行风格收口 ──
            // 写穿是编辑链路落盘（含 VS 编辑器 buffer 路径）的必经点，统一在此把行尾
            // 调整为目标文件原本的风格，避免 AI 编辑把 LF 文件整体改写成 CRLF。
            // 新建文件探测不到风格 → LineEndingHelper 回退系统默认（Environment.NewLine）。
            content = LineEndingHelper.ConformToFile(content, normalizedPath);

            // 首次接触时直接读取当前 buffer 作为 Baseline。这里只读不保存，
            // 避免把可能阻塞的 VS Save 调用插进工具执行关键路径。
            string? openBufferContent = null;
            if (OpenDocumentContentProvider != null && File.Exists(normalizedPath))
            {
                try { openBufferContent = OpenDocumentContentProvider(normalizedPath); }
                catch (Exception ex)
                {
                    Logger.Warn($"[StagedWorkspace] 读取打开文档失败，Baseline 回退磁盘: {Path.GetFileName(normalizedPath)} — {ex.Message}");
                }
            }

            bool newlyTracked = false;
            lock (_lock)
            {
                // 确保目录存在
                string? dir = Path.GetDirectoryName(normalizedPath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                bool isNewFile = !File.Exists(normalizedPath);

                // 首次接触 → 登记 Baseline（用于撤销恢复）
                if (!_trackedFiles.ContainsKey(normalizedPath))
                {
                    newlyTracked = true;
                    string baselineContent = isNewFile
                        ? string.Empty
                        : (openBufferContent ?? FileEncodingHelper.ReadAllText(normalizedPath));
                    _trackedFiles[normalizedPath] = new StagedFile
                    {
                        FilePath = normalizedPath,
                        BaselineContent = baselineContent,
                        BaselineHash = !string.IsNullOrEmpty(baselineContent) ? ComputeSha256(baselineContent) : string.Empty,
                        BaselineLastWriteTimeUtc = isNewFile ? (DateTime?)null : File.GetLastWriteTimeUtc(normalizedPath),
                        Operation = isNewFile ? ProposedFileOperation.Add : ProposedFileOperation.Modify,
                        // P0-1：非新建文件首次接触时落一份磁盘备份（崩溃/OOM 后仍可恢复），
                        // 与 BackupService 的"磁盘可恢复"边界保持一致。
                        DiskBackupPath = isNewFile ? null : BackupService.CreateBackup(normalizedPath),
                    };
                }

                // ── 无 writer：直接落盘（原有行为，锁内执行）──
                if (OpenDocumentWriter == null)
                {
                    WriteAllTextWithRetry(normalizedPath, content);
                    Logger.Info($"[StagedWorkspace] 写穿: {Path.GetFileName(normalizedPath)} ({content.Length} chars)");
                    if (newlyTracked) RaiseFileTracked(normalizedPath);
                    return;
                }
            }

            // ── writer 路径：锁外执行（内部切换 UI 线程，持锁等待会有死锁风险）──
            WriteViaOpenDocumentOrDisk(normalizedPath, content);
            Logger.Info($"[StagedWorkspace] 写穿: {Path.GetFileName(normalizedPath)} ({content.Length} chars)");
            if (newlyTracked) RaiseFileTracked(normalizedPath);
        }

        /// <summary>
        /// 统一落盘：优先通过已打开文档 buffer + 编辑器 Save 写入；未打开/失败时回退裸写盘。
        /// 必须在 _lock 外调用（writer 内部可能切换 UI 线程）。
        /// </summary>
        private void WriteViaOpenDocumentOrDisk(string normalizedPath, string content)
        {
            bool written = false;
            try
            {
                if (OpenDocumentWriter != null)
                    written = OpenDocumentWriter(normalizedPath, content);
            }
            catch (Exception ex)
            {
                Logger.Warn($"[StagedWorkspace] buffer 写入异常，回退磁盘: {Path.GetFileName(normalizedPath)} — {ex.Message}");
                written = false;
            }

            if (!written)
                WriteAllTextWithRetry(normalizedPath, content);
        }

        /// <summary>
        /// 写入文件并在遇到瞬时文件锁时短暂重试。VS、索引器或杀毒软件可能短暂持有文件句柄，
        /// 单次 File.WriteAllText 失败并不代表编辑内容无效。
        /// </summary>
        private static void WriteAllTextWithRetry(string filePath, string content)
        {
            const int maxAttempts = 8;
            IOException? lastError = null;

            for (int attempt = 0; attempt < maxAttempts; attempt++)
            {
                try
                {
                    FileEncodingHelper.WriteAllText(filePath, content, null);
                    if (attempt > 0)
                        Logger.Info($"[StagedWorkspace] 文件锁重试成功: {Path.GetFileName(filePath)} (attempt {attempt + 1})");
                    return;
                }
                catch (IOException ex) when (attempt < maxAttempts - 1)
                {
                    lastError = ex;
                    Thread.Sleep(Math.Min(500, 50 * (attempt + 1)));
                }
            }

            throw lastError ?? new IOException($"写入文件失败: {filePath}");
        }

        /// <summary>
        /// 删除文件（直接删除磁盘文件），记录原始内容供撤销恢复。
        /// </summary>
        public void DeleteFile(string filePath)
        {
            var normalizedPath = NormalizePath(filePath);
            bool newlyTracked = false;

            lock (_lock)
            {
                if (!_trackedFiles.ContainsKey(normalizedPath) && File.Exists(normalizedPath))
                {
                    newlyTracked = true;
                    // 首次接触 → 登记 Baseline，并落一份磁盘备份（用于删除操作崩溃后的恢复）
                    _trackedFiles[normalizedPath] = new StagedFile
                    {
                        FilePath = normalizedPath,
                        BaselineContent = FileEncodingHelper.ReadAllText(normalizedPath),
                        BaselineHash = ComputeSha256(FileEncodingHelper.ReadAllText(normalizedPath)),
                        BaselineLastWriteTimeUtc = File.GetLastWriteTimeUtc(normalizedPath),
                        Operation = ProposedFileOperation.Delete,
                        DiskBackupPath = BackupService.CreateBackup(normalizedPath),
                    };
                }
                else if (_trackedFiles.TryGetValue(normalizedPath, out var existing))
                {
                    existing.Operation = ProposedFileOperation.Delete;
                }

                if (File.Exists(normalizedPath))
                    File.Delete(normalizedPath);
            }

            if (newlyTracked) RaiseFileTracked(normalizedPath);
        }

        /// <summary>
        /// 获取当前的文件内容（直接读磁盘）。
        /// </summary>
        public string? GetStagedContent(string filePath)
        {
            var normalizedPath = NormalizePath(filePath);
            return File.Exists(normalizedPath) ? FileEncodingHelper.ReadAllText(normalizedPath) : string.Empty;
        }

        /// <summary>
        /// 将所有被追踪的变更转换为可提交的 Batch（用于 diff 预览 / 冲突检测）。
        /// </summary>
        public PreparedChangeBatch ToPreparedChangeBatch()
        {
            lock (_lock)
            {
                var changes = _trackedFiles.Values
                    .Select(f =>
                    {
                        string currentContent = File.Exists(f.FilePath)
                            ? FileEncodingHelper.ReadAllText(f.FilePath)
                            : string.Empty;
                        return new PreparedChangeSet
                        {
                            FilePath = f.FilePath,
                            Operation = f.Operation,
                            BaselineText = f.BaselineContent,
                            BaselineHash = f.BaselineHash,
                            BaselineLastWriteTimeUtc = f.BaselineLastWriteTimeUtc,
                            ProposedText = currentContent,
                        };
                    })
                    .Where(c => !string.Equals(c.BaselineText, c.ProposedText, StringComparison.Ordinal)
                                || c.Operation == ProposedFileOperation.Add
                                || c.Operation == ProposedFileOperation.Delete)
                    .ToList();

                return new PreparedChangeBatch { Changes = changes };
            }
        }

        /// <summary>
        /// 撤销所有变更：将每个被追踪文件恢复到其 Baseline 内容（写回磁盘）。
        /// 新建文件会被删除，删除的文件会被恢复。
        ///
        /// 恢复写入优先通过已打开文档 buffer + 编辑器 Save（避免 dirty buffer 场景弹窗），
        /// 未打开的文件回退裸写盘。写入在 _lock 外执行（writer 内部可能切换 UI 线程）。
        /// </summary>
        public void RestoreToBaseline()
        {
            List<StagedFile> files;
            lock (_lock)
            {
                files = _trackedFiles.Values.ToList();
            }

            foreach (var file in files)
            {
                try
                {
                    switch (file.Operation)
                    {
                        case ProposedFileOperation.Add:
                            // 新建文件 → 回滚删除
                            if (File.Exists(file.FilePath))
                                File.Delete(file.FilePath);
                            break;
                        case ProposedFileOperation.Delete:
                            // 删除的文件 → 优先磁盘备份恢复（崩溃可恢复），无备份时回退内存 Baseline
                            if (file.DiskBackupPath != null && File.Exists(file.DiskBackupPath))
                            {
                                BackupService.RestoreFromBackup(file.FilePath, file.DiskBackupPath);
                            }
                            else if (!string.IsNullOrEmpty(file.BaselineContent))
                            {
                                EnsureDirectoryExists(file.FilePath);
                                WriteViaOpenDocumentOrDisk(file.FilePath, file.BaselineContent);
                            }
                            break;
                        default:
                            // 修改 → 优先磁盘备份恢复（崩溃可恢复），无备份时回退内存 Baseline
                            if (file.DiskBackupPath != null && File.Exists(file.DiskBackupPath))
                            {
                                BackupService.RestoreFromBackup(file.FilePath, file.DiskBackupPath);
                            }
                            else
                            {
                                WriteViaOpenDocumentOrDisk(file.FilePath, file.BaselineContent);
                            }
                            break;
                    }
                }
                catch (Exception ex)
                {
                    Logger.Warn($"[StagedWorkspace] 撤销失败: {Path.GetFileName(file.FilePath)} — {ex.Message}");
                }
            }

            lock (_lock)
            {
                _trackedFiles.Clear();
            }
            Logger.Info($"[StagedWorkspace] 已将所有改动恢复到 Baseline");
        }

        private static void EnsureDirectoryExists(string filePath)
        {
            string? dir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);
        }

        /// <summary>
        /// 确认所有变更（保留已落盘内容），清除撤销追踪。
        /// 同时清理本工作区在首次接触时创建的磁盘备份（内容已被保留，备份不再需要）。
        /// </summary>
        public void ConfirmAll()
        {
            List<string?> backups;
            lock (_lock)
            {
                backups = _trackedFiles.Values.Select(f => f.DiskBackupPath).ToList();
                _trackedFiles.Clear();
            }
            foreach (var b in backups)
            {
                if (string.IsNullOrEmpty(b)) continue;
                try { BackupService.CleanupBackup(b); }
                catch (Exception ex) { Logger.Warn($"[StagedWorkspace] 清理确认后的磁盘备份失败: {b} — {ex.Message}"); }
            }
            Logger.Info("[StagedWorkspace] 已确认所有改动（清除撤销追踪）");
        }

        /// <summary>
        /// 丢弃撤销追踪（不修改磁盘 —— 落盘内容保持不变）。用于确认后的清理。
        /// </summary>
        public void Discard()
        {
            ConfirmAll();
        }

        /// <summary>
        /// 释放单个文件的撤销追踪（不修改磁盘 —— 落盘内容保持不变）。
        /// 用于会话刷新时把该文件的撤销权移交给新 Workspace，
        /// 避免旧 Workspace 的 RestoreToBaseline 覆盖新内容。
        /// </summary>
        public void DiscardFile(string filePath)
        {
            var normalizedPath = NormalizePath(filePath);
            string? backupPath = null;

            lock (_lock)
            {
                if (_trackedFiles.TryGetValue(normalizedPath, out var file))
                {
                    backupPath = file.DiskBackupPath;
                    _trackedFiles.Remove(normalizedPath);
                }
            }

            if (string.IsNullOrEmpty(backupPath)) return;
            try { BackupService.CleanupBackup(backupPath); }
            catch (Exception ex)
            {
                Logger.Warn($"[StagedWorkspace] 清理刷新前的磁盘备份失败: {backupPath} — {ex.Message}");
            }
        }

        /// <summary>
        /// 是否仍在追踪指定文件（即该文件尚有可撤销 / 待处理的改动）。
        /// </summary>
        public bool HasTrackedFile(string filePath)
        {
            var normalizedPath = NormalizePath(filePath);
            lock (_lock)
            {
                return _trackedFiles.ContainsKey(normalizedPath);
            }
        }

        /// <summary>
        /// 当前仍在追踪（配置了待处理改动）的文件绝对路径快照。
        /// </summary>
        public System.Collections.Generic.IReadOnlyList<string> GetTrackedFilePaths()
        {
            lock (_lock)
            {
                return _trackedFiles.Keys.ToList();
            }
        }

        /// <summary>
        /// 获取指定文件的原始内容（Baseline）；未追踪时返回 null。
        /// </summary>
        public string? GetBaseline(string filePath)
        {
            var normalizedPath = NormalizePath(filePath);
            lock (_lock)
            {
                return _trackedFiles.TryGetValue(normalizedPath, out var file)
                    ? file.BaselineContent
                    : null;
            }
        }

        /// <summary>
        /// 计算指定文件「待处理差异」的真实增删行数：
        /// 以仅含未处理块的显示基线对比当前内容，已保留/已撤销的块不计入。
        /// 未追踪或已无待处理块时返回 (0, 0)。
        /// </summary>
        public (int Added, int Removed) GetPendingLineStats(string filePath)
        {
            var normalizedPath = NormalizePath(filePath);
            lock (_lock)
            {
                if (!_trackedFiles.TryGetValue(normalizedPath, out var file))
                    return (0, 0);

                // 直接复用已算好的差异块：待处理块的 Old/New 行数之和
                // 就是「仅含待处理块的基线 vs 当前内容」的真实增删行数，
                // 不必再拼一遍基线字符串并跑第二遍 diff（那正是 UI 线程卡顿的来源）。
                RecomputeHunksLocked(file, normalizedPath);

                int added = 0, removed = 0;
                foreach (var hunk in file.Hunks)
                {
                    if (hunk.IsReverted || hunk.IsAccepted) continue;
                    added += Math.Max(0, hunk.NewLineCount);
                    removed += Math.Max(0, hunk.OldLineCount);
                }
                return (added, removed);
            }
        }

        /// <summary>
        /// 确认单个文件：磁盘内容即最终结果，仅清除该文件的撤销追踪。
        /// 与 <see cref="ConfirmAll"/> 不同，不影响其它文件。
        /// </summary>
        public void ConfirmFile(string filePath)
        {
            var normalizedPath = NormalizePath(filePath);
            string? backupPath = null;

            lock (_lock)
            {
                if (_trackedFiles.TryGetValue(normalizedPath, out var file))
                {
                    backupPath = file.DiskBackupPath;
                    _trackedFiles.Remove(normalizedPath);
                }
            }

            if (!string.IsNullOrEmpty(backupPath))
            {
                try { BackupService.CleanupBackup(backupPath); }
                catch (Exception ex) { Logger.Warn($"[StagedWorkspace] 清理确认文件备份失败: {backupPath} — {ex.Message}"); }
            }
            Logger.Info($"[StagedWorkspace] 已确认单个文件: {Path.GetFileName(normalizedPath)}");
        }

        /// <summary>
        /// 撤销单个文件：把该文件恢复到 Baseline（写回磁盘），仅清除该文件的追踪。
        /// 新建文件会被删除，被删除文件会被恢复。
        /// </summary>
        public void RestoreFileToBaseline(string filePath)
        {
            var normalizedPath = NormalizePath(filePath);

            StagedFile? file;
            lock (_lock)
            {
                if (!_trackedFiles.TryGetValue(normalizedPath, out file))
                    return;
                _trackedFiles.Remove(normalizedPath);
            }

            try
            {
                switch (file.Operation)
                {
                    case ProposedFileOperation.Add:
                        if (File.Exists(file.FilePath))
                            File.Delete(file.FilePath);
                        break;
                    case ProposedFileOperation.Delete:
                        if (file.DiskBackupPath != null && File.Exists(file.DiskBackupPath))
                            BackupService.RestoreFromBackup(file.FilePath, file.DiskBackupPath);
                        else if (!string.IsNullOrEmpty(file.BaselineContent))
                        {
                            EnsureDirectoryExists(file.FilePath);
                            WriteViaOpenDocumentOrDisk(file.FilePath, file.BaselineContent);
                        }
                        break;
                    default:
                        if (file.DiskBackupPath != null && File.Exists(file.DiskBackupPath))
                            BackupService.RestoreFromBackup(file.FilePath, file.DiskBackupPath);
                        else
                            WriteViaOpenDocumentOrDisk(file.FilePath, file.BaselineContent);
                        break;
                }
            }
            catch (Exception ex)
            {
                Logger.Warn($"[StagedWorkspace] 单文件撤销失败: {Path.GetFileName(file.FilePath)} — {ex.Message}");
            }

            if (!string.IsNullOrEmpty(file.DiskBackupPath))
            {
                try { BackupService.CleanupBackup(file.DiskBackupPath); }
                catch (Exception ex) { Logger.Warn($"[StagedWorkspace] 清理撤销文件备份失败: {file.DiskBackupPath} — {ex.Message}"); }
            }
            Logger.Info($"[StagedWorkspace] 已恢复单个文件到 Baseline: {Path.GetFileName(normalizedPath)}");
        }

        private void RaiseFileTracked(string normalizedPath)
        {
            try { FileTracked?.Invoke(normalizedPath); }
            catch (Exception ex) { Logger.Warn($"[StagedWorkspace] FileTracked 回调异常: {ex.Message}"); }
        }

        private static (int Added, int Removed) CountChangedLines(string oldText, string newText)
        {
            if (string.Equals(oldText, newText, StringComparison.Ordinal))
                return (0, 0);

            int added = 0, removed = 0;
            try
            {
                foreach (var line in CodeDiffService.ComputeDiff(oldText ?? string.Empty, newText ?? string.Empty))
                {
                    if (line.Type == DiffLineType.Added) added++;
                    else if (line.Type == DiffLineType.Deleted) removed++;
                }
            }
            catch
            {
                added = Math.Max(0, SplitLines(newText ?? string.Empty).Length - SplitLines(oldText ?? string.Empty).Length);
                removed = Math.Max(0, SplitLines(oldText ?? string.Empty).Length - SplitLines(newText ?? string.Empty).Length);
            }
            return (added, removed);
        }

        private static string NormalizePath(string path)
            => Path.GetFullPath(path.Trim());

        private static string ComputeSha256(string content)
        {
            using var sha256 = SHA256.Create();
            byte[] hashBytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(content));
            return BitConverter.ToString(hashBytes).Replace("-", "").ToLowerInvariant();
        }

        /// <summary>
        /// 获取指定文件的所有差异块（Hunks）。
        /// 返回的空列表表示文件无变化或已被确认。
        /// </summary>
        public System.Collections.Generic.IReadOnlyList<DiffHunkInfo> GetHunks(string filePath)
        {
            var normalizedPath = NormalizePath(filePath);

            lock (_lock)
            {
                if (!_trackedFiles.TryGetValue(normalizedPath, out var file))
                    return Array.Empty<DiffHunkInfo>();

                RecomputeHunksLocked(file, normalizedPath);
                return file.Hunks;
            }
        }

        /// <summary>
        /// 重算指定文件的差异块并回填已保留/已撤销标记（调用方须持有 _lock）。
        ///
        /// 以「当前内容哈希」为缓存键：内容未变时直接复用上次结果，
        /// 避免「文件改动列表」每次刷新都重跑 LCS。UI 线程会在编辑过程中
        /// 被多次刷新触发，重算全量 diff 正是打开文档卡在「正在加载文档…」的元凶。
        /// </summary>
        private void RecomputeHunksLocked(StagedFile file, string normalizedPath)
        {
            string currentContent = File.Exists(normalizedPath)
                ? FileEncodingHelper.ReadAllText(normalizedPath)
                : string.Empty;

            string contentHash = ComputeSha256(currentContent);
            if (string.Equals(file.HunksContentHash, contentHash, StringComparison.Ordinal))
                return; // 内容未变 → 复用缓存，跳过 LCS

            var previousHunks = file.Hunks;
            file.Hunks = ComputeHunks(file.BaselineContent, currentContent);
            file.HunksContentHash = contentHash;

            // ── 回填已撤销/已保留标记 ──
            // 重算会新建实例，需按 OldText+NewText 精确匹配恢复状态：
            // 已撤销块在重算后自然消失（内容已等于 Baseline），无需回填；
            // 已保留块内容未变，按内容匹配即可恢复其标记。
            foreach (var hunk in file.Hunks)
            {
                foreach (var prev in previousHunks)
                {
                    if (!prev.IsReverted && !prev.IsAccepted)
                        continue;

                    if (HunkTextEquals(prev.OldText, hunk.OldText) &&
                        HunkTextEquals(prev.NewText, hunk.NewText))
                    {
                        hunk.IsReverted = prev.IsReverted;
                        hunk.IsAccepted = prev.IsAccepted;
                        break;
                    }
                }
            }
        }

        /// <summary>
        /// 撤销单个差异块：只将该块的内容恢复到 Baseline，
        /// 其他块保持不变。从当前内容中定位该块并替换。
        ///
        /// 落盘优先通过已打开文档 buffer + 编辑器 Save（写入在 _lock 外执行，
        /// writer 内部可能切换 UI 线程，持锁等待会有死锁风险）。
        /// </summary>
        /// <returns>true 表示撤销成功</returns>
        public bool RestoreSingleHunk(string filePath, int hunkIndex)
        {
            var normalizedPath = NormalizePath(filePath);

            string revertedContent;
            lock (_lock)
            {
                if (!_trackedFiles.TryGetValue(normalizedPath, out var file))
                    return false;

                if (hunkIndex < 0 || hunkIndex >= file.Hunks.Count)
                    return false;

                var hunk = file.Hunks[hunkIndex];
                if (hunk.IsReverted || hunk.IsAccepted)
                    return false;

                string currentContent = File.Exists(normalizedPath)
                    ? FileEncodingHelper.ReadAllText(normalizedPath)
                    : string.Empty;

                revertedContent = ApplyHunkRevert(currentContent, hunk);

                // ── 标记已撤销 ──
                hunk.IsReverted = true;

                Logger.Info($"[StagedWorkspace] 已撤销单块 [{hunkIndex}] ({Path.GetFileName(normalizedPath)})");

                // 若所有块都撤销了，等价于恢复到 Baseline
                if (file.Hunks.All(h => h.IsReverted))
                {
                    // 由调用方决定是否整文件回滚标记
                }
            }

            // ── 落盘（锁外执行）──
            WriteViaOpenDocumentOrDisk(normalizedPath, revertedContent);

            return true;
        }

        /// <summary>
        /// 保留单个差异块：接受该块的修改（磁盘内容不变，写穿模式已落盘），
        /// 该块不再计入待处理，也不再显示撤销/保留按钮。
        /// </summary>
        /// <returns>true 表示保留成功</returns>
        public bool AcceptSingleHunk(string filePath, int hunkIndex)
        {
            var normalizedPath = NormalizePath(filePath);

            lock (_lock)
            {
                if (!_trackedFiles.TryGetValue(normalizedPath, out var file))
                    return false;

                if (hunkIndex < 0 || hunkIndex >= file.Hunks.Count)
                    return false;

                var hunk = file.Hunks[hunkIndex];
                if (hunk.IsReverted || hunk.IsAccepted)
                    return false;

                hunk.IsAccepted = true;

                Logger.Info($"[StagedWorkspace] 已保留单块 [{hunkIndex}] ({Path.GetFileName(normalizedPath)})");
                return true;
            }
        }

        /// <summary>
        /// 是否仍有未撤销的块。
        /// </summary>
        public bool HasPendingHunks(string filePath)
        {
            var normalizedPath = NormalizePath(filePath);
            lock (_lock)
            {
                if (!_trackedFiles.TryGetValue(normalizedPath, out var file))
                    return false;
                return file.Hunks.Any(h => !h.IsReverted && !h.IsAccepted);
            }
        }

        /// <summary>
        /// 构建「仅含待处理块」的显示基线：在当前内容的基础上，
        /// 把所有待处理（未保留且未撤销）块回退为 Baseline 原文。
        /// 以它作为 Diff 左侧时，只有待处理块会显示为差异：
        /// 已保留块并入新基线（不再高亮），已撤销块天然与基线一致（同样不显示）。
        /// </summary>
        public string BuildPendingOnlyBaseline(string filePath)
        {
            var normalizedPath = NormalizePath(filePath);

            // 先刷新 hunks（重算 + 回填已保留/已撤销标记），确保标志位最新
            GetHunks(normalizedPath);

            lock (_lock)
            {
                string currentContent = File.Exists(normalizedPath)
                    ? FileEncodingHelper.ReadAllText(normalizedPath)
                    : string.Empty;

                if (!_trackedFiles.TryGetValue(normalizedPath, out var file))
                    return currentContent;

                string content = currentContent;

                // 从后往前回退：先替换靠后的块，前面块的行号不会被推移
                for (int i = file.Hunks.Count - 1; i >= 0; i--)
                {
                    var hunk = file.Hunks[i];
                    if (hunk.IsReverted || hunk.IsAccepted) continue;
                    content = ApplyHunkRevert(content, hunk);
                }

                return content;
            }
        }

        /// <summary>
        /// 应用单块回滚：定位 hunk 在当前内容中的行范围，替换为 Baseline 对应内容。
        ///
        /// - 纯新增块（OldLineCount == 0）：删掉被插入的那几行；
        /// - 纯删除块（NewLineCount == 0）：把 Baseline 原文插回 NewAnchorLine 位置；
        /// - 修改块：用 Baseline 原文替换当前范围。
        ///
        /// 回写沿用当前文件自身的换行风格（LF/CRLF/CR），避免撤销一个块把整个文件的行尾改写。
        /// </summary>
        private static string ApplyHunkRevert(string currentContent, DiffHunkInfo hunk)
        {
            string[] currentLines = SplitLines(currentContent);
            string lineEnding = LineEndingHelper.Detect(currentContent);

            // 插入锚点：纯删除块在当前文件中没有对应行，位置来自 NewAnchorLine；
            // 老数据/外部构造的 hunk 没有该字段时退回 NewStartLine。
            int insertAt = hunk.NewAnchorLine >= 0
                ? hunk.NewAnchorLine
                : Math.Max(0, hunk.NewStartLine);
            if (insertAt > currentLines.Length) insertAt = currentLines.Length;

            // 待移除的当前行数（纯删除为 0），并夹紧到文件实际范围内
            int removeCount = Math.Max(0, hunk.NewLineCount);
            if (insertAt + removeCount > currentLines.Length)
                removeCount = currentLines.Length - insertAt;

            // 待替换为 Baseline 内容
            string[] oldLines = SplitLines(hunk.OldText);

            var result = new List<string>();

            // 替换前的内容
            for (int i = 0; i < insertAt; i++)
                result.Add(currentLines[i]);

            // 插入 Baseline 原内容
            result.AddRange(oldLines);

            // 替换后的内容
            for (int i = insertAt + removeCount; i < currentLines.Length; i++)
                result.Add(currentLines[i]);

            return string.Join(lineEnding, result);
        }

        /// <summary>
        /// 计算 Baseline 与当前内容之间的行级差异块。
        ///
        /// 算法：先裁掉首尾完全相同的行（O(n)），只对中间差异区做 LCS 动态规划 + 回溯，
        /// 输出连续的变化区域。真实编辑通常只动很小一段，裁剪后 DP 规模与"改动行数"同阶，
        /// 避免大文件按整文件规模建表（整表方案在 5000 行文件上约 95MB）。
        ///
        /// 空间：长度用两行滚动数组，回溯方向另存 byte 表 —— 约为全 int 表的 1/4，
        /// 且裁剪后表尺寸只取决于差异区。
        /// </summary>
        private static List<DiffHunkInfo> ComputeHunks(string baselineText, string currentText)
        {
            var hunks = new List<DiffHunkInfo>();
            if (string.Equals(baselineText, currentText, StringComparison.Ordinal)) return hunks;

            string[] oldLines = SplitLines(baselineText);
            string[] newLines = SplitLines(currentText);

            int n = oldLines.Length, m = newLines.Length;

            // ── 裁掉公共前后缀 ──
            // LCS 的前缀/后缀性质保证：首尾相同行一定存在一个包含它们的最优解，
            // 因此裁掉后再求解不改变结果，只缩小问题规模。
            int prefix = 0;
            while (prefix < n && prefix < m &&
                   string.Equals(oldLines[prefix], newLines[prefix], StringComparison.Ordinal))
                prefix++;

            int suffix = 0;
            while (suffix < n - prefix && suffix < m - prefix &&
                   string.Equals(oldLines[n - 1 - suffix], newLines[m - 1 - suffix], StringComparison.Ordinal))
                suffix++;

            int midN = n - prefix - suffix;
            int midM = m - prefix - suffix;
            if (midN <= 0 && midM <= 0) return hunks;

            var oldMid = new string[midN];
            Array.Copy(oldLines, prefix, oldMid, 0, midN);
            var newMid = new string[midM];
            Array.Copy(newLines, prefix, newMid, 0, midM);

            // ── 中间区 LCS：后缀式 DP（dp[i][j] = LCS(oldMid[i..], newMid[j..])）──
            // 后缀式填表 + 正向回溯，可直接按文件顺序产出 hunk，无需收集后再翻转。
            // back: 0=对角(相等) 1=下(删 old) 2=右(增 new)
            var back = new byte[midN + 1, midM + 1];
            var prev = new int[midM + 1];   // 第 midN 行（全 0，DP 边界）
            var curr = new int[midM + 1];

            for (int i = midN - 1; i >= 0; i--)
            {
                curr[midM] = 0;             // newMid 已耗尽 → LCS 为 0
                for (int j = midM - 1; j >= 0; j--)
                {
                    if (string.Equals(oldMid[i], newMid[j], StringComparison.Ordinal))
                    {
                        curr[j] = prev[j + 1] + 1;
                        back[i, j] = 0;
                    }
                    else if (prev[j] >= curr[j + 1])
                    {
                        curr[j] = prev[j];
                        back[i, j] = 1;     // 平局优先"删 old"，与 unified diff 先 - 后 + 的惯例一致
                    }
                    else
                    {
                        curr[j] = curr[j + 1];
                        back[i, j] = 2;
                    }
                }
                (prev, curr) = (curr, prev);    // prev 变为第 i 行
            }

            // 展示与回写都沿用文件原本的换行风格
            string lineEnding = LineEndingHelper.Detect(currentText, baselineText);

            // ── 正向回溯：收集连续变化块 ──
            int oi = 0, ni = 0;
            var oldBlock = new List<string>();
            var newBlock = new List<string>();
            int oldStart = 0, newStart = 0, anchor = 0;
            bool inChange = false;

            void Flush()
            {
                if (!inChange) return;
                hunks.Add(new DiffHunkInfo
                {
                    OldStartLine = oldBlock.Count > 0 ? oldStart : -1,
                    OldLineCount = oldBlock.Count,
                    NewStartLine = newBlock.Count > 0 ? newStart : -1,
                    NewLineCount = newBlock.Count,
                    // 纯删除时 NewStartLine 为 -1，撤销要靠这个锚点把原文插回原位
                    NewAnchorLine = anchor,
                    OldText = string.Join(lineEnding, oldBlock),
                    NewText = string.Join(lineEnding, newBlock),
                });
                oldBlock.Clear();
                newBlock.Clear();
                inChange = false;
            }

            while (oi < midN && ni < midM)
            {
                if (string.Equals(oldMid[oi], newMid[ni], StringComparison.Ordinal))
                {
                    Flush();
                    oi++; ni++;
                }
                else
                {
                    if (!inChange)
                    {
                        oldStart = prefix + oi;
                        newStart = prefix + ni;
                        anchor = prefix + ni;
                        inChange = true;
                    }

                    if (back[oi, ni] == 1) { oldBlock.Add(oldMid[oi]); oi++; }   // 删 old
                    else { newBlock.Add(newMid[ni]); ni++; }                     // 增 new
                }
            }

            // 尾部残余归入最后一个块
            if (oi < midN || ni < midM)
            {
                if (!inChange)
                {
                    oldStart = prefix + oi;
                    newStart = prefix + ni;
                    anchor = prefix + ni;
                    inChange = true;
                }
                while (oi < midN) { oldBlock.Add(oldMid[oi]); oi++; }
                while (ni < midM) { newBlock.Add(newMid[ni]); ni++; }
            }

            Flush();

            return hunks;
        }

        /// <summary>
        /// 按行切分（三种行尾都支持，保留末尾空元素）—— 委托 <see cref="LineEndingHelper.SplitLines"/>，
        /// 保证与编辑链路其它环节使用同一套切分语义。
        /// </summary>
        private static string[] SplitLines(string text) => LineEndingHelper.SplitLines(text);

        /// <summary>
        /// 比较两个 hunk 文本是否等价，忽略换行符风格差异。
        /// 用于标记回填：OldText/NewText 的分隔符随文件风格而定，
        /// 若期间文件行尾被改写，纯 Ordinal 比较会导致已保留/已撤销标记丢失。
        /// </summary>
        private static bool HunkTextEquals(string a, string b)
        {
            if (string.Equals(a, b, StringComparison.Ordinal)) return true;

            string[] la = SplitLines(a);
            string[] lb = SplitLines(b);
            if (la.Length != lb.Length) return false;

            for (int i = 0; i < la.Length; i++)
            {
                if (!string.Equals(la[i], lb[i], StringComparison.Ordinal)) return false;
            }
            return true;
        }

        /// <summary>
        /// 单个被追踪文件的内部状态（记录 Baseline 供撤销恢复）。
        /// </summary>
        private sealed class StagedFile
        {
            public string FilePath { get; set; } = string.Empty;
            public string BaselineContent { get; set; } = string.Empty;
            public string BaselineHash { get; set; } = string.Empty;
            public DateTime? BaselineLastWriteTimeUtc { get; set; }
            public ProposedFileOperation Operation { get; set; } = ProposedFileOperation.Modify;

            /// <summary>磁盘备份路径（BackupService）。进程崩溃/OOM 后仍可通过它恢复撤销。</summary>
            public string? DiskBackupPath { get; set; }

            /// <summary>差异块列表（Baseline vs 当前，逐块撤销用）</summary>
            public List<DiffHunkInfo> Hunks { get; set; } = new();

            /// <summary>
            /// Hunks 对应的「当前内容」哈希：内容未变时复用 Hunks，跳过 LCS 重算。
            /// 由 <see cref="RecomputeHunksLocked"/> / <see cref="AcceptSingleHunk"/> 维护。
            /// </summary>
            public string? HunksContentHash { get; set; }
        }
    }
}
