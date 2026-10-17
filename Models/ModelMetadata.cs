using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace DeepSeek_v4_for_VisualStudio.Models
{
    /// <summary>
    /// 官方 GET /models 返回的单个模型元数据。
    /// 这些字段是服务端的权威声明：上下文窗口、输出上限、输入模态与推理强度档位，
    /// 设置界面直接展示它们，用户无需也无法手填。
    /// </summary>
    public sealed record ModelMetadata(
        string Id,
        string? DisplayName = null,
        int? ContextWindow = null,
        int? MaxOutputTokens = null,
        bool SupportsImageInput = false,
        bool HasModalityInfo = false,
        IReadOnlyList<string>? EffortLevels = null,
        string? DefaultEffortLevel = null)
    {
        /// <summary>接口未给出展示名称时回退到模型标识符。</summary>
        public string Display => string.IsNullOrWhiteSpace(DisplayName) ? Id : DisplayName!.Trim();

        /// <summary>推理强度档位；接口未声明时为空集合。</summary>
        public IReadOnlyList<string> EffortLevelsOrEmpty => EffortLevels ?? Array.Empty<string>();

        /// <summary>是否声明了推理强度档位。</summary>
        public bool HasEffortInfo => EffortLevelsOrEmpty.Count > 0;
    }

    /// <summary>
    /// 解析 GET /models 响应为 <see cref="ModelMetadata"/> 列表。
    /// 兼容 {"data":[...]} 与裸数组两种形态；缺少元数据的旧服务端仍能返回 id。
    /// </summary>
    public static class ModelMetadataParser
    {
        /// <summary>解析模型元数据，按 id 排序去重（同 id 保留首次出现）。</summary>
        public static IReadOnlyList<ModelMetadata> Parse(string json)
        {
            var collected = new List<ModelMetadata>();
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                if (root.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in root.EnumerateArray())
                        Add(collected, item);
                }
                else if (root.ValueKind == JsonValueKind.Object &&
                         root.TryGetProperty("data", out var data) &&
                         data.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in data.EnumerateArray())
                        Add(collected, item);
                }
            }
            catch (JsonException)
            {
                // 非 JSON 响应（如 404 HTML 页）→ 空列表，由调用方回退下一个候选端点。
            }

            return collected
                .Where(model => !string.IsNullOrWhiteSpace(model.Id))
                .GroupBy(model => model.Id, StringComparer.Ordinal)
                .Select(group => group.First())
                .OrderBy(model => model.Id, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static void Add(List<ModelMetadata> models, JsonElement item)
        {
            if (item.ValueKind != JsonValueKind.Object)
                return;

            string? id = ReadString(item, "id");
            if (string.IsNullOrWhiteSpace(id))
                return;

            var modalities = ReadStringArray(item, "input_modalities");
            bool hasModalityInfo = item.TryGetProperty(
                "input_modalities", out var modalityNode) &&
                modalityNode.ValueKind == JsonValueKind.Array;

            var effortNode = ReadObject(item, "effort");
            var effortLevels = effortNode.HasValue
                ? ReadStringArray(effortNode.Value, "supported_levels")
                : Array.Empty<string>();
            string? defaultLevel = effortNode.HasValue
                ? ReadString(effortNode.Value, "default_level")
                : null;

            models.Add(new ModelMetadata(
                Id: id!.Trim(),
                DisplayName: ReadString(item, "name"),
                ContextWindow: ReadPositiveInt(item, "context_window"),
                MaxOutputTokens: ReadPositiveInt(item, "max_output_tokens"),
                SupportsImageInput: modalities.Any(m =>
                    string.Equals(m, "image", StringComparison.OrdinalIgnoreCase)),
                HasModalityInfo: hasModalityInfo,
                EffortLevels: effortLevels,
                DefaultEffortLevel: defaultLevel));
        }

        private static string? ReadString(JsonElement element, string propertyName)
            => element.TryGetProperty(propertyName, out var node) &&
               node.ValueKind == JsonValueKind.String
                ? node.GetString()
                : null;

        private static JsonElement? ReadObject(JsonElement element, string propertyName)
            => element.TryGetProperty(propertyName, out var node) &&
               node.ValueKind == JsonValueKind.Object
                ? node
                : null;

        private static int? ReadPositiveInt(JsonElement element, string propertyName)
        {
            if (!element.TryGetProperty(propertyName, out var node))
                return null;
            if (node.ValueKind == JsonValueKind.Number && node.TryGetInt32(out int value) && value > 0)
                return value;
            return null;
        }

        private static IReadOnlyList<string> ReadStringArray(JsonElement element, string propertyName)
        {
            if (!element.TryGetProperty(propertyName, out var node) ||
                node.ValueKind != JsonValueKind.Array)
            {
                return Array.Empty<string>();
            }

            var items = new List<string>();
            foreach (var entry in node.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.String)
                    continue;
                var text = entry.GetString();
                if (!string.IsNullOrWhiteSpace(text))
                    items.Add(text!.Trim());
            }
            return items;
        }
    }
}
