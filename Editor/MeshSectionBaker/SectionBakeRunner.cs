using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LodCreator;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace MeshSectionBaker
{
    /// <summary>
    /// Bakes cells into combined section objects and reverts them.
    ///
    /// Bake: per cell, renderers are merged into one mesh per render-settings group (layer, tag,
    /// shadow/probe settings) with one submesh per material; MeshColliders of removed objects are
    /// merged into one collider per (layer, physics material). Removable source objects are moved
    /// into the inactive EditorOnly holder, the rest just get their renderers switched off.
    /// Unbake: everything is put back from the section record and the generated meshes are deleted.
    ///
    /// Bake/unbake are deliberately not Undo operations: generated mesh assets live on disk and
    /// Undo can't bring them back, so the pair of buttons is the undo.
    /// </summary>
    public static class SectionBakeRunner
    {
        private const string ProgressTitle = "Mesh Section Baker";
        private const string OutputRoot = "Assets/MeshSectionBaker/Generated";

        public static int Bake(MeshSectionGrid grid, IList<SectionCell> cells)
        {
            if (cells.Count == 0) return 0;

            EnsureRoots(grid);
            string folder = EnsureOutputFolder(grid);
            int baked = 0;

            // Removed objects are deleted from the scene: keep those that something else points at.
            if (grid.sourceMode == SourceObjectMode.RemoveObjects)
            {
                EditorUtility.DisplayProgressBar(ProgressTitle, "Проверка ссылок на объекты", 0f);
                SourceStore.KeepReferencedUnits(grid, cells);
            }

            try
            {
                for (int i = 0; i < cells.Count; i++)
                {
                    EditorUtility.DisplayProgressBar(ProgressTitle, $"Запекание {cells[i].Name} ({i + 1}/{cells.Count})", (float)i / cells.Count);
                    BakeCell(grid, cells[i], folder);
                    baked++;
                }
            }
            finally
            {
                EditorUtility.ClearProgressBar();
                AssetDatabase.SaveAssets();
                Finish(grid);
            }

            return baked;
        }

        public static int Unbake(MeshSectionGrid grid, IList<BakedSection> sections)
        {
            // Reverse bake order restores sibling indices exactly.
            List<BakedSection> ordered = sections.OrderByDescending(s => grid.bakedSections.IndexOf(s)).ToList();
            int missing = 0;

            try
            {
                AssetDatabase.StartAssetEditing();
                for (int i = 0; i < ordered.Count; i++)
                {
                    BakedSection section = ordered[i];
                    EditorUtility.DisplayProgressBar(ProgressTitle, $"Распекание {section.name} ({i + 1}/{ordered.Count})", (float)i / ordered.Count);
                    missing += UnbakeSection(grid, section);
                }
            }
            finally
            {
                AssetDatabase.StopAssetEditing();
                EditorUtility.ClearProgressBar();
                SourceStore.CleanupOrphans(grid);
                CleanupEmptyRoots(grid);
                Finish(grid);
            }

            if (missing > 0)
            {
                Debug.LogWarning($"[MeshSectionBaker] {missing} исходных объектов не найдено при распекании (удалены вручную?).");
            }

            return ordered.Count;
        }

        private static void BakeCell(MeshSectionGrid grid, SectionCell cell, string folder)
        {
            var record = new BakedSection
            {
                name = UniqueSectionName(grid, cell.Name),
                min = cell.min,
                max = cell.max,
                polygon = new List<Vector2>(cell.polygon),
                sourceSectionId = cell.sectionId
            };

            var sectionObject = new GameObject(record.name);
            sectionObject.transform.SetParent(grid.sectionsRoot, false);
            sectionObject.transform.position = new Vector3((cell.min.x + cell.max.x) * 0.5f, 0f, (cell.min.y + cell.max.y) * 0.5f);
            Matrix4x4 worldToLocal = sectionObject.transform.worldToLocalMatrix;
            record.output = sectionObject;

            // Render: one level normally; with LODs inside the objects, one merged mesh set per LOD
            // level plus a LODGroup on the section.
            int maxLevel = cell.units.Count > 0 ? cell.units.Max(u => u.MaxLodLevel) : 0;
            if (maxLevel == 0)
            {
                BuildRenderObjects(record, sectionObject, worldToLocal, cell.units.SelectMany(u => u.renderers).ToList(), "Render", folder);
            }
            else
            {
                var lodRenderers = new List<Renderer>[maxLevel + 1];
                for (int level = 0; level <= maxLevel; level++)
                {
                    List<MeshRenderer> renderers = cell.units.SelectMany(u => u.RenderersForLevel(level)).Distinct().ToList();
                    lodRenderers[level] = BuildRenderObjects(record, sectionObject, worldToLocal, renderers, $"LOD{level}", folder);
                }

                float sectionSize = WorldSize(lodRenderers[0]);
                float[] heights = SectionLodHeights(cell, maxLevel, sectionSize);
                var lods = new LOD[maxLevel + 1];
                for (int level = 0; level <= maxLevel; level++) lods[level] = new LOD(heights[level], lodRenderers[level].ToArray());

                LODGroup lodGroup = sectionObject.AddComponent<LODGroup>();
                lodGroup.SetLODs(lods);
                lodGroup.RecalculateBounds();
            }

            // Collision of the objects that are removed (objects that stay keep their own colliders).
            int colliderIndex = 0;
            foreach (IGrouping<string, MeshCollider> group in cell.units.Where(u => u.removable).SelectMany(u => u.colliders).GroupBy(ColliderKey))
            {
                List<MeshCollider> colliders = group.ToList();
                MeshCollider first = colliders[0];

                Mesh mesh = MeshCombiner.CombineCollision(colliders, worldToLocal);
                mesh.name = $"{record.name}_Collision{colliderIndex}";
                record.meshAssetPaths.Add(SaveMesh(mesh, folder));

                var collisionObject = new GameObject(colliderIndex == 0 ? "Collision" : $"Collision_{colliderIndex}");
                collisionObject.transform.SetParent(sectionObject.transform, false);
                collisionObject.layer = first.gameObject.layer;
                GameObjectUtility.SetStaticEditorFlags(collisionObject, UnionFlags(colliders.Select(c => c.gameObject)));

                var meshCollider = collisionObject.AddComponent<MeshCollider>();
                meshCollider.sharedMaterial = first.sharedMaterial;
                meshCollider.sharedMesh = mesh;
                colliderIndex++;
            }

            // Record where removed objects lived before touching any of them.
            var removedRoots = new List<GameObject>();
            foreach (SourceUnit unit in cell.units)
            {
                record.sourceObjectCount++;
                record.sourceRendererCount += unit.renderers.Count;

                if (unit.removable)
                {
                    Transform transform = unit.root.transform;
                    record.removed.Add(new RemovedObject
                    {
                        parent = transform.parent,
                        siblingIndex = transform.GetSiblingIndex(),
                        storedIndex = removedRoots.Count
                    });
                    removedRoots.Add(unit.root);
                }
                else
                {
                    DisableRenderers(record, unit.renderers);
                }
            }

            // Delete them from the scene, keeping a copy to spawn back on unbake.
            if (removedRoots.Count > 0 && !SourceStore.Store(grid, record, removedRoots, folder))
            {
                record.removed.Clear();
                foreach (SourceUnit unit in cell.units.Where(u => u.removable)) DisableRenderers(record, unit.renderers);
            }

            grid.bakedSections.Add(record);
        }

        private static void DisableRenderers(BakedSection record, IEnumerable<MeshRenderer> renderers)
        {
            foreach (MeshRenderer renderer in renderers)
            {
                renderer.enabled = false;
                record.disabledRenderers.Add(renderer);
            }
        }

        private static int UnbakeSection(MeshSectionGrid grid, BakedSection section)
        {
            int missing = string.IsNullOrEmpty(section.sourcesPrefabPath)
                ? RestoreFromHolder(section)
                : SourceStore.Restore(grid, section);

            foreach (MeshRenderer renderer in section.disabledRenderers)
            {
                if (renderer != null) EnableRenderer(renderer);
                else missing++;
            }

            if (section.output != null)
            {
                // LODs made for the section (or a part of it) go with it, including their meshes.
                foreach (LodCreatorRecord lodRecord in section.output.GetComponentsInChildren<LodCreatorRecord>(true))
                {
                    LodBuilder.Remove(lodRecord);
                }

                Object.DestroyImmediate(section.output);
            }

            foreach (string path in section.meshAssetPaths)
            {
                AssetDatabase.DeleteAsset(path);
            }

            // The sources asset is deleted later by SourceStore.CleanupOrphans: the saved scene may still refer to it.
            grid.bakedSections.Remove(section);
            return missing;
        }

        /// <summary>Switches the renderer back on without leaving an "enabled" override on a prefab instance that didn't have one.</summary>
        private static void EnableRenderer(MeshRenderer renderer)
        {
            renderer.enabled = true;
            if (!PrefabUtility.IsPartOfPrefabInstance(renderer)) return;

            var source = PrefabUtility.GetCorrespondingObjectFromSource(renderer);
            if (source != null && source.enabled)
            {
                SerializedProperty property = new SerializedObject(renderer).FindProperty("m_Enabled");
                if (property != null && property.prefabOverride) PrefabUtility.RevertPropertyOverride(property, InteractionMode.AutomatedAction);
            }
        }

        /// <summary>Sections baked by older versions: the removed objects are parked in the EditorOnly holder.</summary>
        private static int RestoreFromHolder(BakedSection section)
        {
            int missing = 0;
            foreach (RemovedObject removed in section.removed.OrderBy(r => r.siblingIndex))
            {
                if (removed.gameObject == null)
                {
                    missing++;
                    continue;
                }

                Transform transform = removed.gameObject.transform;
                transform.SetParent(removed.parent, true);
                transform.SetSiblingIndex(removed.siblingIndex);
            }

            return missing;
        }

        /// <summary>Merges renderers into one object per render-settings group; returns the created renderers.</summary>
        private static List<Renderer> BuildRenderObjects(BakedSection record, GameObject sectionObject, Matrix4x4 worldToLocal,
            List<MeshRenderer> sourceRenderers, string prefix, string folder)
        {
            var created = new List<Renderer>();
            int groupIndex = 0;
            foreach (IGrouping<string, MeshRenderer> group in sourceRenderers.GroupBy(RenderKey))
            {
                List<MeshRenderer> renderers = group.ToList();
                MeshRenderer first = renderers[0];

                Mesh mesh = MeshCombiner.CombineRender(renderers, worldToLocal, out Material[] materials);
                mesh.name = $"{record.name}_{prefix}{(groupIndex == 0 ? string.Empty : "_" + groupIndex)}";
                record.meshAssetPaths.Add(SaveMesh(mesh, folder));

                var renderObject = new GameObject(groupIndex == 0 ? prefix : $"{prefix}_{groupIndex}");
                renderObject.transform.SetParent(sectionObject.transform, false);
                renderObject.layer = first.gameObject.layer;
                renderObject.tag = first.gameObject.tag;
                GameObjectUtility.SetStaticEditorFlags(renderObject, UnionFlags(renderers.Select(r => r.gameObject)));

                renderObject.AddComponent<MeshFilter>().sharedMesh = mesh;
                var meshRenderer = renderObject.AddComponent<MeshRenderer>();
                meshRenderer.sharedMaterials = materials;
                CopyRendererSettings(first, meshRenderer);
                created.Add(meshRenderer);
                groupIndex++;
            }

            return created;
        }

        /// <summary>
        /// Section LOD switch heights from the objects' own LODGroups. Screen height is relative to
        /// object size, so each object's height is rescaled by (object size / section size) to switch at
        /// the same distance; the median across objects is used. The section is culled only if every
        /// object in it is culled - otherwise objects without LODs would vanish.
        /// </summary>
        private static float[] SectionLodHeights(SectionCell cell, int maxLevel, float sectionSize)
        {
            var heights = new float[maxLevel + 1];
            List<LodSet> sets = cell.units.SelectMany(u => u.lodSets).Where(s => s.heights.Length > 1).ToList();
            float size = Mathf.Max(sectionSize, 0.01f);

            for (int level = 0; level < maxLevel; level++)
            {
                List<float> values = sets
                    .Where(s => s.heights.Length - 1 > level)
                    .Select(s => s.heights[level] * s.worldSize / size)
                    .ToList();
                heights[level] = values.Count > 0 ? Median(values) : (level == 0 ? 0.5f : heights[level - 1] * 0.5f);
            }

            bool everyObjectCulls = cell.units.All(u => u.lodSets.Count > 0 && u.lodFree.Count == 0) &&
                                    sets.Count > 0 && sets.All(s => s.heights[s.heights.Length - 1] > 0f);
            heights[maxLevel] = everyObjectCulls
                ? Median(sets.Select(s => s.heights[s.heights.Length - 1] * s.worldSize / size).ToList())
                : 0f;

            // Strictly decreasing, as LODGroup requires.
            heights[0] = Mathf.Clamp(heights[0], 0.0005f, 1f);
            for (int level = 1; level <= maxLevel; level++)
            {
                float cap = heights[level - 1] * 0.95f;
                heights[level] = level == maxLevel && heights[level] <= 0f ? 0f : Mathf.Clamp(heights[level], 0.0001f, cap);
            }

            return heights;
        }

        private static float Median(List<float> values)
        {
            values.Sort();
            int mid = values.Count / 2;
            return values.Count % 2 == 1 ? values[mid] : (values[mid - 1] + values[mid]) * 0.5f;
        }

        private static float WorldSize(List<Renderer> renderers)
        {
            if (renderers.Count == 0) return 1f;
            Bounds bounds = renderers[0].bounds;
            foreach (Renderer renderer in renderers) bounds.Encapsulate(renderer.bounds);
            return Mathf.Max(bounds.size.x, Mathf.Max(bounds.size.y, bounds.size.z));
        }

        private static string RenderKey(MeshRenderer renderer)
        {
            return string.Join("|",
                renderer.gameObject.layer,
                renderer.gameObject.tag,
                (int)renderer.shadowCastingMode,
                renderer.receiveShadows,
                (int)renderer.lightProbeUsage,
                (int)renderer.reflectionProbeUsage,
                renderer.renderingLayerMask);
        }

        private static string ColliderKey(MeshCollider collider)
        {
            return collider.gameObject.layer + "|" + (collider.sharedMaterial != null ? collider.sharedMaterial.GetHashCode() : 0);
        }

        private static void CopyRendererSettings(MeshRenderer from, MeshRenderer to)
        {
            to.shadowCastingMode = from.shadowCastingMode;
            to.receiveShadows = from.receiveShadows;
            to.lightProbeUsage = from.lightProbeUsage;
            to.reflectionProbeUsage = from.reflectionProbeUsage;
            to.motionVectorGenerationMode = from.motionVectorGenerationMode;
            to.allowOcclusionWhenDynamic = from.allowOcclusionWhenDynamic;
            to.renderingLayerMask = from.renderingLayerMask;
        }

        private static StaticEditorFlags UnionFlags(IEnumerable<GameObject> objects)
        {
            StaticEditorFlags flags = 0;
            foreach (GameObject go in objects) flags |= GameObjectUtility.GetStaticEditorFlags(go);
            return flags;
        }

        private static string SaveMesh(Mesh mesh, string folder)
        {
            string path = AssetDatabase.GenerateUniqueAssetPath($"{folder}/{mesh.name}.asset");
            AssetDatabase.CreateAsset(mesh, path);
            return path;
        }

        private static string UniqueSectionName(MeshSectionGrid grid, string baseName)
        {
            string name = baseName;
            for (int i = 1; grid.bakedSections.Any(s => s.name == name); i++)
            {
                name = $"{baseName}_{i}";
            }

            return name;
        }

        private static void EnsureRoots(MeshSectionGrid grid)
        {
            var scene = grid.gameObject.scene;
            if (grid.sectionsRoot == null)
            {
                var root = new GameObject("[MeshSections]");
                EditorSceneManager.MoveGameObjectToScene(root, scene);
                grid.sectionsRoot = root.transform;
            }
        }

        private static void CleanupEmptyRoots(MeshSectionGrid grid)
        {
            if (grid.bakedSections.Count > 0) return;

            if (grid.sectionsRoot != null && grid.sectionsRoot.childCount == 0)
            {
                Object.DestroyImmediate(grid.sectionsRoot.gameObject);
                grid.sectionsRoot = null;
            }

            if (grid.sourcesHolder != null && grid.sourcesHolder.childCount == 0)
            {
                Object.DestroyImmediate(grid.sourcesHolder.gameObject);
                grid.sourcesHolder = null;
            }

            string folder = OutputFolderPath(grid);
            if (AssetDatabase.IsValidFolder(folder) && AssetDatabase.FindAssets(string.Empty, new[] { folder }).Length == 0)
            {
                AssetDatabase.DeleteAsset(folder);
            }
        }

        internal static string EnsureOutputFolder(MeshSectionGrid grid)
        {
            if (string.IsNullOrEmpty(grid.gridId))
            {
                grid.gridId = Guid.NewGuid().ToString("N").Substring(0, 8);
            }

            string folder = OutputFolderPath(grid);
            EnsureFolder(folder);
            return folder;
        }

        internal static string OutputFolderPath(MeshSectionGrid grid)
        {
            string sceneName = string.IsNullOrEmpty(grid.gameObject.scene.name) ? "Untitled" : grid.gameObject.scene.name;
            foreach (char c in Path.GetInvalidFileNameChars()) sceneName = sceneName.Replace(c, '_');
            return $"{OutputRoot}/{sceneName}_{grid.gridId}";
        }

        private static void EnsureFolder(string path)
        {
            if (AssetDatabase.IsValidFolder(path)) return;

            string parent = Path.GetDirectoryName(path)?.Replace('\\', '/');
            EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, Path.GetFileName(path));
        }

        internal static void Finish(MeshSectionGrid grid)
        {
            // The records must never be rolled back by an unrelated Ctrl+Z on the grid (that would
            // orphan the removed objects), so drop the grid's undo history after bake/unbake.
            Undo.ClearUndo(grid);
            EditorUtility.SetDirty(grid);
            EditorSceneManager.MarkSceneDirty(grid.gameObject.scene);
        }
    }
}
