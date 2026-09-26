using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace TexturesBaker
{
    public static class MaterialProps
    {
        /// <summary>Main texture property name (URP "_BaseMap" or legacy/built-in "_MainTex").</summary>
        public static string MainTexture(Material material)
        {
            if (material == null) return null;
            if (material.HasProperty("_BaseMap")) return "_BaseMap";
            if (material.HasProperty("_MainTex")) return "_MainTex";
            return null;
        }

        public static string Color(Material material)
        {
            if (material == null) return null;
            if (material.HasProperty("_BaseColor")) return "_BaseColor";
            if (material.HasProperty("_Color")) return "_Color";
            return null;
        }

        public static bool ShaderHasMainTexture(Shader shader)
        {
            return shader != null && (shader.FindPropertyIndex("_BaseMap") >= 0 || shader.FindPropertyIndex("_MainTex") >= 0);
        }
    }

    public sealed class MaterialUsageInfo
    {
        public Material material;
        public Texture texture;
        public Color tint = UnityEngine.Color.white;
        public int submeshUsages;
        public readonly HashSet<GameObject> prefabs = new HashSet<GameObject>();
        public bool atlasable = true;
        public string reason;
        public string droppedTextures;

        public int SourceWidth => texture != null ? texture.width : AtlasPlanner.SolidColorSize;
        public int SourceHeight => texture != null ? texture.height : AtlasPlanner.SolidColorSize;
    }

    public sealed class PrefabAnalysis
    {
        public readonly List<MaterialUsageInfo> materials = new List<MaterialUsageInfo>();
        public readonly List<string> warnings = new List<string>();
        public int rendererCount;
        public int skippedRendererCount;
    }

    public sealed class UvCache
    {
        private readonly Dictionary<Mesh, List<Vector2>> _uvs = new Dictionary<Mesh, List<Vector2>>();

        public List<Vector2> Get(Mesh mesh)
        {
            if (!_uvs.TryGetValue(mesh, out List<Vector2> list))
            {
                list = new List<Vector2>();
                mesh.GetUVs(0, list);
                _uvs[mesh] = list;
            }

            return list;
        }
    }

    /// <summary>
    /// Inspects the dropped prefabs: which materials they use, and whether each material can be
    /// moved into an atlas at all. The key check is UV range - a submesh whose UVs tile past 0..1
    /// would sample neighbouring atlas cells, so such materials are kept as they are.
    /// </summary>
    public static class BakeAnalyzer
    {
        public const float UvEpsilon = 0.005f;

        public static PrefabAnalysis Analyze(IReadOnlyList<GameObject> prefabs)
        {
            var result = new PrefabAnalysis();
            var byMaterial = new Dictionary<Material, MaterialUsageInfo>();
            var uvCache = new UvCache();
            var skipReasons = new Dictionary<string, int>();

            foreach (GameObject prefab in prefabs)
            {
                if (prefab == null) continue;

                foreach (MeshRenderer renderer in prefab.GetComponentsInChildren<MeshRenderer>(true))
                {
                    if (!TryGetBakeableRenderer(renderer, out Mesh mesh, out Material[] materials, out string skipReason))
                    {
                        if (skipReason != null)
                        {
                            result.skippedRendererCount++;
                            skipReasons.TryGetValue(skipReason, out int count);
                            skipReasons[skipReason] = count + 1;
                        }

                        continue;
                    }

                    result.rendererCount++;
                    for (int submesh = 0; submesh < materials.Length; submesh++)
                    {
                        Material material = materials[submesh];
                        if (material == null) continue;

                        if (!byMaterial.TryGetValue(material, out MaterialUsageInfo info))
                        {
                            info = CreateInfo(material);
                            byMaterial[material] = info;
                            result.materials.Add(info);
                        }

                        info.submeshUsages++;
                        info.prefabs.Add(prefab);
                        if (!info.atlasable) continue;

                        GetMainTextureTransform(material, out Vector2 scale, out Vector2 offset);
                        if (!TryComputeUvShift(mesh, submesh, uvCache.Get(mesh), scale, offset, out _, out string uvReason))
                        {
                            info.atlasable = false;
                            info.reason = $"{uvReason}, меш '{mesh.name}'";
                        }
                    }
                }
            }

            foreach (KeyValuePair<string, int> pair in skipReasons)
            {
                result.warnings.Add($"Пропущено рендереров: {pair.Value} — {pair.Key}");
            }

            result.materials.Sort((a, b) => string.CompareOrdinal(a.material.name, b.material.name));
            return result;
        }

        public static bool TryGetBakeableRenderer(MeshRenderer renderer, out Mesh mesh, out Material[] materials, out string skipReason)
        {
            mesh = null;
            materials = null;
            skipReason = null;

            var filter = renderer.GetComponent<MeshFilter>();
            if (filter == null || filter.sharedMesh == null) return false;

            mesh = filter.sharedMesh;
            materials = renderer.sharedMaterials;

            if (!mesh.isReadable) { skipReason = "меш без Read/Write"; return false; }
            if (mesh.blendShapeCount > 0) { skipReason = "меш с blend shapes"; return false; }
            if (materials.Length != mesh.subMeshCount) { skipReason = "число материалов не совпадает с числом сабмешей"; return false; }
            if (!mesh.HasVertexAttribute(VertexAttribute.TexCoord0)) { skipReason = "у меша нет UV0"; return false; }

            for (int submesh = 0; submesh < mesh.subMeshCount; submesh++)
            {
                if (mesh.GetTopology(submesh) != MeshTopology.Triangles)
                {
                    skipReason = "сабмеш не из треугольников";
                    return false;
                }
            }

            return true;
        }

        public static void GetMainTextureTransform(Material material, out Vector2 scale, out Vector2 offset)
        {
            string prop = MaterialProps.MainTexture(material);
            if (prop == null)
            {
                scale = Vector2.one;
                offset = Vector2.zero;
                return;
            }

            scale = material.GetTextureScale(prop);
            offset = material.GetTextureOffset(prop);
        }

        /// <summary>
        /// UVs (after the material's tiling/offset) must fit into a single 0..1 cell. A whole-unit
        /// offset (e.g. an island living at 2..3) is fine and returned as <paramref name="shift"/>.
        /// </summary>
        public static bool TryComputeUvShift(Mesh mesh, int submesh, List<Vector2> uvs, Vector2 scale, Vector2 offset,
            out Vector2 shift, out string reason)
        {
            shift = Vector2.zero;
            reason = null;

            int[] indices = mesh.GetIndices(submesh);
            if (indices.Length == 0) return true;

            var min = new Vector2(float.MaxValue, float.MaxValue);
            var max = new Vector2(float.MinValue, float.MinValue);
            foreach (int index in indices)
            {
                Vector2 uv = Vector2.Scale(uvs[index], scale) + offset;
                min = Vector2.Min(min, uv);
                max = Vector2.Max(max, uv);
            }

            float cellX = Mathf.Floor(min.x + UvEpsilon);
            float cellY = Mathf.Floor(min.y + UvEpsilon);
            if (max.x - cellX > 1f + UvEpsilon || max.y - cellY > 1f + UvEpsilon)
            {
                reason = $"UV выходят за 0..1 — тайлинг (U {min.x:0.##}..{max.x:0.##}, V {min.y:0.##}..{max.y:0.##})";
                return false;
            }

            shift = new Vector2(cellX, cellY);
            return true;
        }

        private static MaterialUsageInfo CreateInfo(Material material)
        {
            var info = new MaterialUsageInfo { material = material };

            string mainProp = MaterialProps.MainTexture(material);
            if (mainProp == null)
            {
                info.atlasable = false;
                info.reason = "у шейдера нет основной текстуры";
                return info;
            }

            Texture texture = material.GetTexture(mainProp);
            if (texture != null && !(texture is Texture2D))
            {
                info.atlasable = false;
                info.reason = "основная текстура не Texture2D";
                return info;
            }

            info.texture = texture;
            string colorProp = MaterialProps.Color(material);
            info.tint = colorProp != null ? material.GetColor(colorProp) : UnityEngine.Color.white;

            var dropped = new List<string>();
            foreach (string prop in material.GetTexturePropertyNames())
            {
                if (prop != mainProp && material.GetTexture(prop) != null)
                {
                    dropped.Add(prop);
                }
            }

            if (dropped.Count > 0)
            {
                info.droppedTextures = string.Join(", ", dropped);
            }

            return info;
        }
    }
}
