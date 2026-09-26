using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace TexturesBaker
{
    /// <summary>One cell of an atlas: a unique (shader, texture, tint) combination, possibly shared by several materials.</summary>
    public sealed class AtlasItem
    {
        public Texture texture;
        public Color tint;
        public Shader shader;
        public int width;
        public int height;
        public readonly List<Material> materials = new List<Material>();

        public long Area => (long)width * height;
        public string DisplayName => materials.Count == 1 ? materials[0].name : $"{materials[0].name} (+{materials.Count - 1})";
    }

    public sealed class PlacedItem
    {
        public AtlasItem item;

        /// <summary>Pixel rect of the texture content (padding excluded), bottom-left origin like UVs.</summary>
        public RectInt contentRect;
    }

    public sealed class AtlasPlan
    {
        public Shader shader;
        public Material templateMaterial;
        public int width;
        public int height;
        public float scale = 1f;
        public readonly List<PlacedItem> placed = new List<PlacedItem>();
    }

    public sealed class BakePlan
    {
        public readonly List<AtlasPlan> atlases = new List<AtlasPlan>();
        public readonly List<string> errors = new List<string>();
        public int itemCount;
        public int minMaterialCount;
        public long sourceTexels;
        public long atlasTexels;
    }

    /// <summary>
    /// Turns "these materials into N materials" into a concrete layout: splits cells by shader
    /// (different shaders can't share a material), balances the rest by texel area, then packs
    /// each atlas at the largest uniform scale that fits the max atlas size. The same plan object
    /// drives both the window preview and the bake, so what you see is what gets baked.
    /// </summary>
    public static class AtlasPlanner
    {
        public const int SolidColorSize = 16;
        private const int MinAtlasSize = 64;
        private const float MinUsefulScale = 0.02f;

        public static BakePlan Build(IReadOnlyList<MaterialUsageInfo> included, int requestedCount, int maxAtlasSize, int padding, Shader forcedShader)
        {
            var plan = new BakePlan();
            List<AtlasItem> items = BuildItems(included, forcedShader);
            plan.itemCount = items.Count;

            if (items.Count == 0)
            {
                plan.errors.Add("Нет материалов для объединения.");
                return plan;
            }

            List<List<AtlasItem>> groups = items
                .GroupBy(i => i.shader)
                .Select(g => g.ToList())
                .OrderByDescending(g => g.Sum(i => i.Area))
                .ToList();

            plan.minMaterialCount = groups.Count;
            if (requestedCount < groups.Count)
            {
                string shaders = string.Join(", ", groups.Select(g => $"'{g[0].shader.name}'"));
                plan.errors.Add($"Материалы используют {groups.Count} разных шейдера ({shaders}) — материалов на выходе должно быть не меньше {groups.Count}. " +
                                "Либо включите «Приводить к одному шейдеру», либо исключите лишние материалы.");
                return plan;
            }

            int[] slots = AllocateSlots(groups, Mathf.Min(requestedCount, items.Count));
            for (int g = 0; g < groups.Count; g++)
            {
                foreach (List<AtlasItem> bucket in Distribute(groups[g], slots[g]))
                {
                    AtlasPlan atlas = Pack(bucket, maxAtlasSize, padding);
                    if (atlas == null)
                    {
                        plan.errors.Add($"Текстуры шейдера '{groups[g][0].shader.name}' не помещаются в атлас {maxAtlasSize}px — увеличьте число материалов или размер атласа.");
                        continue;
                    }

                    atlas.shader = bucket[0].shader;
                    atlas.templateMaterial = bucket.OrderByDescending(i => i.Area).First().materials[0];
                    plan.atlases.Add(atlas);
                }
            }

            plan.sourceTexels = items.Sum(i => i.Area);
            plan.atlasTexels = plan.atlases.Sum(a => (long)a.width * a.height);
            return plan;
        }

        public static Vector2Int ContentSize(AtlasItem item, float scale)
        {
            if (item.texture == null)
            {
                return new Vector2Int(SolidColorSize, SolidColorSize);
            }

            return new Vector2Int(RoundTo4(item.width * scale), RoundTo4(item.height * scale));
        }

        private static List<AtlasItem> BuildItems(IReadOnlyList<MaterialUsageInfo> included, Shader forcedShader)
        {
            var byKey = new Dictionary<(Shader, Texture, Color), AtlasItem>();
            var items = new List<AtlasItem>();

            foreach (MaterialUsageInfo info in included)
            {
                Shader shader = forcedShader != null ? forcedShader : info.material.shader;
                var key = (shader, info.texture, info.tint);
                if (!byKey.TryGetValue(key, out AtlasItem item))
                {
                    item = new AtlasItem
                    {
                        texture = info.texture,
                        tint = info.tint,
                        shader = shader,
                        width = info.SourceWidth,
                        height = info.SourceHeight
                    };
                    byKey[key] = item;
                    items.Add(item);
                }

                item.materials.Add(info.material);
            }

            return items;
        }

        /// <summary>Every shader group gets one atlas; the remaining ones go to whichever group is most crowded per atlas.</summary>
        private static int[] AllocateSlots(List<List<AtlasItem>> groups, int total)
        {
            int[] slots = Enumerable.Repeat(1, groups.Count).ToArray();
            double[] areas = groups.Select(g => (double)g.Sum(i => i.Area)).ToArray();

            for (int left = total - groups.Count; left > 0; left--)
            {
                int best = -1;
                double bestLoad = -1;
                for (int g = 0; g < groups.Count; g++)
                {
                    if (slots[g] >= groups[g].Count) continue;

                    double load = areas[g] / slots[g];
                    if (load > bestLoad)
                    {
                        bestLoad = load;
                        best = g;
                    }
                }

                if (best < 0) break;
                slots[best]++;
            }

            return slots;
        }

        /// <summary>Longest-processing-time balancing: biggest textures first, each into the least loaded atlas.</summary>
        private static List<List<AtlasItem>> Distribute(List<AtlasItem> items, int bucketCount)
        {
            var buckets = Enumerable.Range(0, bucketCount).Select(_ => new List<AtlasItem>()).ToList();
            var loads = new long[bucketCount];

            foreach (AtlasItem item in items.OrderByDescending(i => i.Area).ThenBy(i => i.DisplayName, StringComparer.Ordinal))
            {
                int target = 0;
                for (int b = 1; b < bucketCount; b++)
                {
                    if (loads[b] < loads[target]) target = b;
                }

                buckets[target].Add(item);
                loads[target] += item.Area;
            }

            return buckets.Where(b => b.Count > 0).ToList();
        }

        private static AtlasPlan Pack(List<AtlasItem> items, int maxSize, int padding)
        {
            float scale = 1f;
            if (!TryPack(items, 1f, padding, maxSize, maxSize, out _))
            {
                float lo = 0f;
                float hi = 1f;
                for (int i = 0; i < 18; i++)
                {
                    float mid = (lo + hi) * 0.5f;
                    if (TryPack(items, mid, padding, maxSize, maxSize, out _)) lo = mid;
                    else hi = mid;
                }

                scale = lo;
                if (scale < MinUsefulScale || !TryPack(items, scale, padding, maxSize, maxSize, out _))
                {
                    return null;
                }
            }

            foreach (Vector2Int size in CandidateSizes(maxSize))
            {
                if (TryPack(items, scale, padding, size.x, size.y, out List<PlacedItem> placed))
                {
                    var atlas = new AtlasPlan { width = size.x, height = size.y, scale = scale };
                    atlas.placed.AddRange(placed);
                    return atlas;
                }
            }

            return null;
        }

        private static bool TryPack(List<AtlasItem> items, float scale, int padding, int width, int height, out List<PlacedItem> placed)
        {
            placed = new List<PlacedItem>(items.Count);
            var packer = new MaxRectsPacker(width, height);

            foreach (AtlasItem item in items
                         .OrderByDescending(i => Mathf.Max(i.width, i.height))
                         .ThenByDescending(i => i.Area)
                         .ThenBy(i => i.DisplayName, StringComparer.Ordinal))
            {
                Vector2Int content = ContentSize(item, scale);
                if (!packer.TryInsert(content.x + padding * 2, content.y + padding * 2, out RectInt cell))
                {
                    return false;
                }

                placed.Add(new PlacedItem
                {
                    item = item,
                    contentRect = new RectInt(cell.x + padding, cell.y + padding, content.x, content.y)
                });
            }

            return true;
        }

        /// <summary>Power-of-two sizes up to the max, at most 2:1, smallest area first.</summary>
        private static IEnumerable<Vector2Int> CandidateSizes(int maxSize)
        {
            var sizes = new List<Vector2Int>();
            for (int w = MinAtlasSize; w <= maxSize; w *= 2)
            {
                for (int h = MinAtlasSize; h <= maxSize; h *= 2)
                {
                    if (Mathf.Max(w, h) <= Mathf.Min(w, h) * 2)
                    {
                        sizes.Add(new Vector2Int(w, h));
                    }
                }
            }

            return sizes.OrderBy(s => (long)s.x * s.y).ThenBy(s => Mathf.Abs(s.x - s.y));
        }

        private static int RoundTo4(float value)
        {
            return Mathf.Max(4, Mathf.RoundToInt(value / 4f) * 4);
        }
    }
}
