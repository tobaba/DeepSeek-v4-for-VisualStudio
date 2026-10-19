using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using DeepSeek_v4_for_VisualStudio.Models;
using DeepSeek_v4_for_VisualStudio.Utils;

namespace DeepSeek_v4_for_VisualStudio.Services
{
    /// <summary>
    /// DeepSeek 官方模型目录缓存。启动时通过 OpenAI 兼容的 GET /models
    /// 获取可用模型；请求失败时保留最近一次成功获取的远程列表。
    /// </summary>
    public static class OfficialModelCatalogService
    {
        private static readonly object StateGate = new();
        private static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(30);

        private static IReadOnlyList<ModelMetadata> _metadata = Array.Empty<ModelMetadata>();
        private static IReadOnlyList<string> _models = Array.Empty<string>();
        private static bool _hasRemoteModels;
        private static string _lastApiKey = string.Empty;
        private static DateTimeOffset _lastFetchUtc = DateTimeOffset.MinValue;
        private static Task<IReadOnlyList<string>>? _inFlight;
        private static Guid _activeFetchId;

        /// <summary>远程模型列表发生变化时触发；回调方需自行切换回 UI 线程。</summary>
        public static event Action? ModelsChanged;

        public static IReadOnlyList<string> GetModels()
        {
            lock (StateGate)
                return _models;
        }

        /// <summary>
        /// 官方模型的完整元数据（上下文窗口、输入模态、effort 档位等）。
        /// 设置界面据此只读展示，无需用户手填。
        /// </summary>
        public static IReadOnlyList<ModelMetadata> GetMetadata()
        {
            lock (StateGate)
                return _metadata;
        }

        /// <summary>按模型 id 查询官方元数据；非官方模型或尚未刷新时返回 null。</summary>
        public static ModelMetadata? TryGetMetadata(string? model)
        {
            if (string.IsNullOrWhiteSpace(model))
                return null;

            lock (StateGate)
            {
                foreach (var item in _metadata)
                {
                    if (string.Equals(item.Id, model.Trim(), StringComparison.OrdinalIgnoreCase))
                        return item;
                }
            }
            return null;
        }

        /// <summary>
        /// 官方模型声明的推理强度档位；接口未提供或非官方模型时返回空列表。
        /// 调用方据此生成 effort 下拉，避免硬编码与接口不一致。
        /// </summary>
        public static IReadOnlyList<string> GetEffortLevels(string? model)
            => TryGetMetadata(model)?.EffortLevelsOrEmpty ?? Array.Empty<string>();

        public static bool HasRemoteModels
        {
            get
            {
                lock (StateGate)
                    return _hasRemoteModels;
            }
        }

        /// <summary>
        /// 获取或刷新官方模型列表。相同 Key 的成功结果在缓存期内直接复用；
        /// Key 变化会强制重新请求，失败时返回当前远程列表。
        /// </summary>
        public static Task<IReadOnlyList<string>> RefreshAsync(
            string? apiKey,
            CancellationToken cancellationToken = default,
            HttpMessageHandler? httpMessageHandler = null)
        {
            var normalizedKey = (apiKey ?? string.Empty).Trim();
            lock (StateGate)
            {
                var keyChanged = !string.Equals(_lastApiKey, normalizedKey, StringComparison.Ordinal);
                if (!keyChanged && _inFlight != null)
                    return _inFlight;

                var isFresh = _hasRemoteModels &&
                    DateTimeOffset.UtcNow - _lastFetchUtc < CacheLifetime;
                if (normalizedKey.Length == 0)
                {
                    _lastApiKey = normalizedKey;
                    return Task.FromResult(GetModels());
                }

                if (!keyChanged && isFresh)
                    return Task.FromResult(GetModels());

                _lastApiKey = normalizedKey;
                var fetchId = Guid.NewGuid();
                _activeFetchId = fetchId;
                _inFlight = FetchCoreAsync(
                    normalizedKey, cancellationToken, httpMessageHandler, fetchId);
                return _inFlight;
            }
        }

        private static async Task<IReadOnlyList<string>> FetchCoreAsync(
            string apiKey,
            CancellationToken cancellationToken,
            HttpMessageHandler? httpMessageHandler,
            Guid fetchId)
        {
            try
            {
                var fetched = await ModelFetchService.FetchModelsWithMetadataAsync(
                    DeepSeekApiService.DefaultBaseUrl,
                    apiKey,
                    cancellationToken,
                    httpMessageHandler);
                var models = fetched.Select(model => model.Id).ToList();

                bool changed;
                bool isActive;
                lock (StateGate)
                {
                    isActive = _activeFetchId == fetchId &&
                        string.Equals(_lastApiKey, apiKey, StringComparison.Ordinal);
                    if (isActive)
                    {
                        // 元数据（上下文窗口、模态、effort 档位）变化同样要通知订阅方，
                        // 否则设置界面与请求参数会停留在旧的接口声明上。
                        changed = !_hasRemoteModels || !MetadataEquals(_metadata, fetched);
                        _metadata = fetched;
                        _models = models;
                        _hasRemoteModels = true;
                        _lastFetchUtc = DateTimeOffset.UtcNow;
                        _inFlight = null;
                    }
                    else
                    {
                        changed = false;
                    }
                }

                if (!isActive)
                {
                    Logger.Info("[Models] 忽略已过期的官方模型列表响应");
                    return GetModels();
                }

                Logger.Info($"[Models] 官方模型列表刷新成功: {models.Count} 个模型");
                if (changed)
                    ModelsChanged?.Invoke();
                return models;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Logger.Warn($"[Models] 官方模型列表刷新失败，保留当前模型列表: {ex.Message}");
                return GetModels();
            }
            finally
            {
                lock (StateGate)
                    if (_activeFetchId == fetchId)
                        _inFlight = null;
            }
        }

        internal static void ResetForTests(IReadOnlyList<string>? models = null)
            => ResetForTests(
                models?.Select(id => new ModelMetadata(id)).ToList());

        /// <summary>测试用：直接注入元数据目录。</summary>
        internal static void ResetForTests(IReadOnlyList<ModelMetadata>? metadata)
        {
            lock (StateGate)
            {
                _metadata = metadata ?? Array.Empty<ModelMetadata>();
                _models = _metadata.Select(model => model.Id).ToList();
                _hasRemoteModels = metadata != null;
                _lastApiKey = string.Empty;
                _lastFetchUtc = DateTimeOffset.MinValue;
                _inFlight = null;
                _activeFetchId = Guid.Empty;
            }
        }

        /// <summary>比较两次接口返回的元数据是否等价（用于判断是否需要触发刷新事件）。</summary>
        private static bool MetadataEquals(
            IReadOnlyList<ModelMetadata> left,
            IReadOnlyList<ModelMetadata> right)
        {
            if (left.Count != right.Count)
                return false;

            for (var i = 0; i < left.Count; i++)
            {
                var previous = left[i];
                var current = right[i];
                if (!string.Equals(previous.Id, current.Id, StringComparison.Ordinal) ||
                    !string.Equals(previous.DisplayName, current.DisplayName, StringComparison.Ordinal) ||
                    previous.ContextWindow != current.ContextWindow ||
                    previous.MaxOutputTokens != current.MaxOutputTokens ||
                    previous.SupportsImageInput != current.SupportsImageInput ||
                    previous.HasModalityInfo != current.HasModalityInfo ||
                    !string.Equals(previous.DefaultEffortLevel, current.DefaultEffortLevel, StringComparison.Ordinal) ||
                    !previous.EffortLevelsOrEmpty.SequenceEqual(
                        current.EffortLevelsOrEmpty, StringComparer.Ordinal))
                {
                    return false;
                }
            }

            return true;
        }
    }
}
