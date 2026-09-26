using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MeshSectionBaker;
using TexturesBaker;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace LodCreator
{
    [Serializable]
    public class LodSettings
    {
        /// <summary>Per generated level (LOD1..LODn): share of the original triangles, 0..1.</summary>
        public List<float> ratios = new List<float>();

        /// <summary>transitions[i] = screen height (0..1) below which LOD i switches to LOD i+1.</summary>
        public List<float> transitions = new List<float>();

        public bool cull;
        public float cullHeight = 0.01f;

        /// <summary>
        /// Largest allowed surface error of a LOD, in pixels of a 1080p screen at the moment that LOD
        /// switches in. Simplification stops early (more triangles than the ratio) rather than exceed it.
        /// 0 = no limit, only the ratio counts.
        /// </summary>
        public float maxPixelError = DefaultPixelError;

        public const float DefaultPixelError = 3f;
        public const float ReferenceScreenHeight = 1080f;

        public LodSettings Clone()
        {
            return new LodSettings
            {
                ratios = new List<float>(ratios),
                transitions = new List<float>(transitions),
                cull = cull,
                cullHeight = cullHeight,
                maxPixelError = maxPixelError
            };
        }

        /// <summary>World-space error allowed for a LOD shown below screen height <paramref name="height"/> on an object of <paramref name="worldSize"/>.</summary>
        public float MaxWorldError(float worldSize, float height)
        {
            if (maxPixelError <= 0f || height <= 0f) return 0f;
            return maxPixelError * worldSize / (height * ReferenceScreenHeight);
        }

        /// <summary>Screen heights for LODGroup.SetLODs: one per LOD (original + generated), strictly decreasing.</summary>
        public float[] BuildHeights()
        {
            int count = ratios.Count + 1;
            var heights = new float[count];
            float previous = 1f;
            for (int i = 0; i < count - 1; i++)
            {
                heights[i] = Mathf.Min(Mathf.Clamp(transitions[i], 0.0005f, 1f), previous * 0.98f);
                previous = heights[i];
            }

            heights[count - 1] = cull ? Mathf.Min(Mathf.Clamp(cullHeight, 0.0001f, 1f), previous * 0.98f) : 0f;
            return heights;
        }
    }

    public enum LodObjectKind
    {
        Object,
        Section
    }

    public sealed class LodLevelInfo
    {
        public float height;
        public int renderers;
        public int triangles;
        public int vertices;
    }

    /// <summary>What the LOD window knows about one selected object.</summary>
    public sealed class LodObjectInfo
    {
        public GameObject root;
        public LodObjectKind kind;
        public readonly List<MeshRenderer> renderers = new List<MeshRenderer>();
        public int triangles;
        public int vertices;
        public float worldSize;
        public LODGroup lodGroup;
        public LodCreatorRecord record;
        public MeshSectionGrid sectionGrid;
        public BakedSection section;
        public bool usesAtlas;
        public bool isBakedPrefab;
        public string lodGroupOrigin;
        public string blockReason;
        public readonly List<LodLevelInfo> levels = new List<LodLevelInfo>();

        public bool CanCreate => blockReason == null && record == null && lodGroup == null;
        public bool CanRemove => record != null && !IsInSectionHolder(root);

        public static LodObjectInfo Build(GameObject go, HashSet<string> atlasMaterialPaths, HashSet<string> bakedPrefabGuids)
        {
            var info = new LodObjectInfo { root = go };
            info.lodGroup = go.GetComponent<LODGroup>();
            info.record = go.GetComponent<LodCreatorRecord>();

            FindSection(go, out info.sectionGrid, out info.section);
            info.kind = info.section != null ? LodObjectKind.Section : LodObjectKind.Object;

            GameObject prefabRoot = PrefabUtility.GetOutermostPrefabInstanceRoot(go);
            if (prefabRoot != null)
            {
                GameObject source = PrefabUtility.GetCorrespondingObjectFromSource(prefabRoot);
                info.isBakedPrefab = source != null && bakedPrefabGuids.Contains(BakeRegistry.Guid(source));
            }

            // LOD0 renderers: the recorded LOD0 when LODs exist, otherwise every active mesh renderer.
            if (info.lodGroup != null && info.lodGroup.lodCount > 0)
            {
                foreach (Renderer r in info.lodGroup.GetLODs()[0].renderers)
                {
                    if (r is MeshRenderer mr && HasMesh(mr)) info.renderers.Add(mr);
                }
            }
            else
            {
                foreach (MeshRenderer r in go.GetComponentsInChildren<MeshRenderer>(false))
                {
                    if (r.enabled && r.gameObject.activeInHierarchy && HasMesh(r)) info.renderers.Add(r);
                }
            }

            Bounds? bounds = null;
            foreach (MeshRenderer r in info.renderers)
            {
                Mesh mesh = r.GetComponent<MeshFilter>().sharedMesh;
                info.triangles += TriangleCount(mesh);
                info.vertices += mesh.vertexCount;
                if (bounds.HasValue) { Bounds b = bounds.Value; b.Encapsulate(r.bounds); bounds = b; }
                else bounds = r.bounds;

                foreach (Material m in r.sharedMaterials)
                {
                    if (m != null && atlasMaterialPaths.Contains(AssetDatabase.GetAssetPath(m))) info.usesAtlas = true;
                }
            }

            if (bounds.HasValue)
            {
                Vector3 size = bounds.Value.size;
                info.worldSize = Mathf.Max(size.x, Mathf.Max(size.y, size.z));
            }

            if (info.lodGroup != null)
            {
                info.lodGroupOrigin = info.record != null ? "создан LOD Creator"
                    : info.kind == LodObjectKind.Section ? "создан при сшивании секции"
                    : "свой LODGroup объекта";

                foreach (LOD lod in info.lodGroup.GetLODs())
                {
                    var level = new LodLevelInfo { height = lod.screenRelativeTransitionHeight };
                    foreach (Renderer r in lod.renderers)
                    {
                        if (!(r is MeshRenderer mr) || !HasMesh(mr)) continue;
                        Mesh mesh = mr.GetComponent<MeshFilter>().sharedMesh;
                        level.renderers++;
                        level.triangles += TriangleCount(mesh);
                        level.vertices += mesh.vertexCount;
                    }

                    info.levels.Add(level);
                }
            }

            info.blockReason = FindBlockReason(info);
            return info;
        }

        private static string FindBlockReason(LodObjectInfo info)
        {
            if (IsInSectionHolder(info.root)) return "Объект убран в сшитую секцию — работайте с секцией или распеките её.";
            if (info.record != null) return null;
            if (info.lodGroup != null) return $"У объекта уже есть LODGroup ({info.lodGroupOrigin}).";
            if (info.renderers.Count == 0) return "Нет включённых MeshRenderer с мешем.";

            foreach (MeshRenderer r in info.renderers)
            {
                LODGroup inner = r.GetComponentInParent<LODGroup>();
                if (inner != null && inner.gameObject != info.root) return $"Внутри объекта уже есть LODGroup («{inner.name}»).";

                Mesh mesh = r.GetComponent<MeshFilter>().sharedMesh;
                if (!mesh.isReadable) return $"Меш «{mesh.name}» без Read/Write — включите Read/Write в импорте модели.";
                for (int s = 0; s < mesh.subMeshCount; s++)
                {
                    if (mesh.GetTopology(s) != MeshTopology.Triangles) return $"Меш «{mesh.name}» не из треугольников.";
                }
            }

            return null;
        }

        public static bool IsInSectionHolder(GameObject go)
        {
            Transform root = go.transform.root;
            foreach (GameObject sceneRoot in go.scene.GetRootGameObjects())
            {
                foreach (MeshSectionGrid grid in sceneRoot.GetComponentsInChildren<MeshSectionGrid>(true))
                {
                    if (grid.sourcesHolder != null && grid.sourcesHolder == root) return true;
                }
            }

            return false;
        }

        private static void FindSection(GameObject go, out MeshSectionGrid grid, out BakedSection section)
        {
            grid = null;
            section = null;
            foreach (GameObject sceneRoot in go.scene.GetRootGameObjects())
            {
                foreach (MeshSectionGrid g in sceneRoot.GetComponentsInChildren<MeshSectionGrid>(true))
                {
                    BakedSection match = g.bakedSections.FirstOrDefault(s => s.output == go);
                    if (match != null)
                    {
                        grid = g;
                        section = match;
                        return;
                    }
                }
            }
        }

        private static bool HasMesh(MeshRenderer r)
        {
            var filter = r.GetComponent<MeshFilter>();
            return filter != null && filter.sharedMesh != null;
        }

        public static int TriangleCount(Mesh mesh)
        {
            long count = 0;
            for (int s = 0; s < mesh.subMeshCount; s++)
            {
                if (mesh.GetTopology(s) == MeshTopology.Triangles) count += mesh.GetIndexCount(s) / 3;
            }

            return (int)count;
        }
    }

    /// <summary>Creates LODs by simplifying the object's meshes, and removes them again.</summary>
    public static class LodBuilder
    {
        public const string OutputRoot = "Assets/LodCreator/Generated";
        public const int MaxLevels = 5;

        /// <summary>
        /// Recommended levels from the object's triangle count and cull distance from its size:
        /// simple props get fewer levels and are culled earlier, big objects/sections are never culled.
        /// </summary>
        public static LodSettings Recommend(LodObjectInfo info)
        {
            int tris = info.triangles;
            float[] ratios = tris < 300 ? new[] { 0.5f }
                : tris < 2000 ? new[] { 0.5f, 0.2f }
                : tris < 20000 ? new[] { 0.5f, 0.25f, 0.1f }
                : new[] { 0.5f, 0.25f, 0.12f, 0.05f };
            float[] transitions = { 0.5f, 0.25f, 0.12f, 0.06f, 0.03f };

            var settings = new LodSettings();
            for (int i = 0; i < ratios.Length; i++)
            {
                settings.ratios.Add(ratios[i]);
                settings.transitions.Add(transitions[i]);
            }

            bool large = info.kind == LodObjectKind.Section || info.worldSize >= 20f;
            settings.cull = !large;
            settings.cullHeight = info.worldSize < 2f ? 0.03f : 0.012f;
            return settings;
        }

        public static int CreateMany(IList<LodObjectInfo> infos, LodSettings settings)
        {
            var cache = new LodMeshCache();
            int created = 0;
            try
            {
                for (int i = 0; i < infos.Count; i++)
                {
                    if (!infos[i].CanCreate) continue;
                    Create(infos[i], settings, cache, (float)i / infos.Count, infos.Count);
                    created++;
                }
            }
            finally
            {
                EditorUtility.ClearProgressBar();
                AssetDatabase.SaveAssets();
            }

            return created;
        }

        public static int RemoveMany(IList<LodObjectInfo> infos)
        {
            int removed = 0;
            foreach (LodObjectInfo info in infos)
            {
                if (!info.CanRemove) continue;
                Remove(info.record);
                removed++;
            }

            return removed;
        }

        private static void Create(LodObjectInfo info, LodSettings settings, LodMeshCache cache, float progress, int total)
        {
            GameObject root = info.root;
            string folder = info.section != null && info.section.meshAssetPaths.Count > 0
                ? Path.GetDirectoryName(info.section.meshAssetPaths[0]).Replace('\\', '/')
                : $"{OutputRoot}/{Sanitize(string.IsNullOrEmpty(root.scene.name) ? "Untitled" : root.scene.name)}";
            EnsureFolder(folder);

            var record = root.AddComponent<LodCreatorRecord>();
            var group = root.AddComponent<LODGroup>();
            record.createdLodGroup = true;
            record.levelTriangles.Add(info.triangles);

            float[] heights = settings.BuildHeights();
            var lods = new LOD[heights.Length];
            var previousMeshes = info.renderers.ToDictionary(r => r, r => r.GetComponent<MeshFilter>().sharedMesh);
            lods[0] = new LOD(heights[0], info.renderers.Cast<Renderer>().ToArray());

            for (int level = 1; level < heights.Length; level++)
            {
                float ratio = settings.ratios[level - 1];
                // LOD level is visible below heights[level - 1]: that is where it is seen biggest.
                float worldError = settings.MaxWorldError(info.worldSize, heights[level - 1]);
                var levelRenderers = new List<Renderer>();
                int levelTriangles = 0;

                foreach (MeshRenderer source in info.renderers)
                {
                    EditorUtility.DisplayProgressBar("Создание LOD", $"{root.name}: LOD{level} ({ratio:P0}) · {source.name}", progress + (float)level / heights.Length / Mathf.Max(1, total));

                    Mesh sourceMesh = source.GetComponent<MeshFilter>().sharedMesh;
                    Vector3 scale = source.transform.lossyScale;
                    float meshScale = Mathf.Max(Mathf.Abs(scale.x), Mathf.Max(Mathf.Abs(scale.y), Mathf.Abs(scale.z)), 1e-6f);
                    Mesh lodMesh = cache.Get(sourceMesh, ratio, worldError / meshScale, folder, out string path);
                    if (!record.meshAssetPaths.Contains(path)) record.meshAssetPaths.Add(path); // tracked even if unused below, so removal cleans it up

                    // Greedy simplification isn't monotonic: never let a farther level be heavier than the previous one.
                    Mesh previous = previousMeshes[source];
                    if (level > 1 && LodObjectInfo.TriangleCount(lodMesh) >= LodObjectInfo.TriangleCount(previous))
                    {
                        lodMesh = previous;
                        path = AssetDatabase.GetAssetPath(previous);
                    }

                    previousMeshes[source] = lodMesh;
                    if (!record.meshAssetPaths.Contains(path)) record.meshAssetPaths.Add(path);

                    var go = new GameObject($"{source.gameObject.name}_LOD{level}");
                    go.transform.SetParent(source.transform, false);
                    go.layer = source.gameObject.layer;
                    go.tag = source.gameObject.tag;
                    GameObjectUtility.SetStaticEditorFlags(go, GameObjectUtility.GetStaticEditorFlags(source.gameObject));

                    go.AddComponent<MeshFilter>().sharedMesh = lodMesh;
                    var renderer = go.AddComponent<MeshRenderer>();
                    renderer.sharedMaterials = source.sharedMaterials;
                    renderer.shadowCastingMode = source.shadowCastingMode;
                    renderer.receiveShadows = source.receiveShadows;
                    renderer.lightProbeUsage = source.lightProbeUsage;
                    renderer.reflectionProbeUsage = source.reflectionProbeUsage;
                    renderer.motionVectorGenerationMode = source.motionVectorGenerationMode;
                    renderer.allowOcclusionWhenDynamic = source.allowOcclusionWhenDynamic;
                    renderer.renderingLayerMask = source.renderingLayerMask;

                    record.createdObjects.Add(go);
                    levelRenderers.Add(renderer);
                    levelTriangles += LodObjectInfo.TriangleCount(lodMesh);
                }

                lods[level] = new LOD(heights[level], levelRenderers.ToArray());
                record.levelTriangles.Add(levelTriangles);
            }

            group.SetLODs(lods);
            group.RecalculateBounds();

            // A section's LOD meshes belong to the section: its unbake deletes them together with its own meshes.
            if (info.section != null)
            {
                foreach (string path in record.meshAssetPaths)
                {
                    if (!info.section.meshAssetPaths.Contains(path)) info.section.meshAssetPaths.Add(path);
                }

                EditorUtility.SetDirty(info.sectionGrid);
            }

            EditorUtility.SetDirty(root);
            EditorSceneManager.MarkSceneDirty(root.scene);
        }

        public static void Remove(LodCreatorRecord record)
        {
            GameObject root = record.gameObject;
            Scene scene = root.scene;
            var paths = new List<string>(record.meshAssetPaths);

            foreach (GameObject go in record.createdObjects)
            {
                if (go != null) Object.DestroyImmediate(go);
            }

            if (record.createdLodGroup && root.TryGetComponent(out LODGroup group))
            {
                Object.DestroyImmediate(group);
            }

            foreach (GameObject sceneRoot in scene.GetRootGameObjects())
            {
                foreach (MeshSectionGrid grid in sceneRoot.GetComponentsInChildren<MeshSectionGrid>(true))
                {
                    BakedSection section = grid.bakedSections.FirstOrDefault(s => s.output == root);
                    if (section == null) continue;
                    section.meshAssetPaths.RemoveAll(paths.Contains);
                    EditorUtility.SetDirty(grid);
                }
            }

            Object.DestroyImmediate(record);

            // Shared LOD meshes (same source mesh on several objects) are deleted only when nothing else uses them.
            HashSet<string> stillUsed = CollectReferencedLodMeshes();
            foreach (string path in paths)
            {
                if (!stillUsed.Contains(path)) AssetDatabase.DeleteAsset(path);
            }

            EditorSceneManager.MarkSceneDirty(scene);
        }

        private static HashSet<string> CollectReferencedLodMeshes()
        {
            var used = new HashSet<string>();
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                Scene scene = SceneManager.GetSceneAt(i);
                if (!scene.isLoaded) continue;

                foreach (GameObject root in scene.GetRootGameObjects())
                {
                    foreach (LodCreatorRecord other in root.GetComponentsInChildren<LodCreatorRecord>(true))
                    {
                        used.UnionWith(other.meshAssetPaths);
                    }
                }
            }

            return used;
        }

        internal static void EnsureFolder(string path)
        {
            path = path.Replace('\\', '/').TrimEnd('/');
            if (AssetDatabase.IsValidFolder(path)) return;

            string parent = Path.GetDirectoryName(path)?.Replace('\\', '/');
            EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, Path.GetFileName(path));
        }

        internal static string Sanitize(string name)
        {
            foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
            return name;
        }
    }

    /// <summary>
    /// One simplified mesh per (source mesh, ratio): shared by every object using that mesh, and
    /// found again by a stable file name on later runs instead of being generated twice.
    /// </summary>
    public sealed class LodMeshCache
    {
        private readonly Dictionary<(Mesh, int, int), (Mesh mesh, string path)> _cache = new Dictionary<(Mesh, int, int), (Mesh, string)>();

        /// <param name="maxError">Allowed surface error in mesh units (0 = none). Quantized relative to the mesh size so instances of similar scale share one mesh.</param>
        public Mesh Get(Mesh source, float ratio, float maxError, string folder, out string path)
        {
            int ratioKey = Mathf.RoundToInt(ratio * 1000f);
            float extent = Mathf.Max(source.bounds.size.x, Mathf.Max(source.bounds.size.y, source.bounds.size.z), 1e-6f);

            // Error as a fraction of the mesh size, in steps of 2^(1/4) (~19%), rounded down (stricter).
            int errorKey = 0;
            if (maxError > 0f)
            {
                errorKey = Mathf.FloorToInt(Mathf.Log(maxError / extent * 1e6f, 2f) * 4f);
                errorKey = Mathf.Max(errorKey, 1);
                maxError = Mathf.Pow(2f, errorKey / 4f) * 1e-6f * extent;
            }

            var key = (source, ratioKey, errorKey);
            if (_cache.TryGetValue(key, out var cached))
            {
                path = cached.path;
                return cached.mesh;
            }

            string baseName = LodBuilder.Sanitize(source.name);
            if (AssetDatabase.TryGetGUIDAndLocalFileIdentifier(source, out string guid, out long localId) && !string.IsNullOrEmpty(guid))
            {
                path = $"{folder}/{baseName}_{guid.Substring(0, 8)}_{localId}_r{ratioKey}_e{errorKey}_v{MeshSimplifier.Version}.asset";
                var existing = AssetDatabase.LoadAssetAtPath<Mesh>(path);
                if (existing != null)
                {
                    _cache[key] = (existing, path);
                    return existing;
                }
            }
            else
            {
                path = AssetDatabase.GenerateUniqueAssetPath($"{folder}/{baseName}_r{ratioKey}_e{errorKey}.asset");
            }

            Mesh mesh = MeshSimplifier.Simplify(source, ratio, maxError, out _, out _);
            mesh.name = Path.GetFileNameWithoutExtension(path);
            AssetDatabase.CreateAsset(mesh, path);
            _cache[key] = (mesh, path);
            return mesh;
        }
    }
}
