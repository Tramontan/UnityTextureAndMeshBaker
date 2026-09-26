using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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

            // Render groups: renderers that may share one MeshRenderer.
            int groupIndex = 0;
            foreach (IGrouping<string, MeshRenderer> group in cell.units.SelectMany(u => u.renderers).GroupBy(RenderKey))
            {
                List<MeshRenderer> renderers = group.ToList();
                MeshRenderer first = renderers[0];

                Mesh mesh = MeshCombiner.CombineRender(renderers, worldToLocal, out Material[] materials);
                mesh.name = $"{record.name}_Render{groupIndex}";
                record.meshAssetPaths.Add(SaveMesh(mesh, folder));

                var renderObject = new GameObject(groupIndex == 0 ? "Render" : $"Render_{groupIndex}");
                renderObject.transform.SetParent(sectionObject.transform, false);
                renderObject.layer = first.gameObject.layer;
                renderObject.tag = first.gameObject.tag;
                GameObjectUtility.SetStaticEditorFlags(renderObject, UnionFlags(renderers.Select(r => r.gameObject)));

                renderObject.AddComponent<MeshFilter>().sharedMesh = mesh;
                var meshRenderer = renderObject.AddComponent<MeshRenderer>();
                meshRenderer.sharedMaterials = materials;
                CopyRendererSettings(first, meshRenderer);
                groupIndex++;
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
            foreach (SourceUnit unit in cell.units)
            {
                record.sourceObjectCount++;
                record.sourceRendererCount += unit.renderers.Count;

                if (unit.removable)
                {
                    Transform transform = unit.root.transform;
                    record.removed.Add(new RemovedObject
                    {
                        gameObject = unit.root,
                        parent = transform.parent,
                        siblingIndex = transform.GetSiblingIndex()
                    });
                }
                else
                {
                    foreach (MeshRenderer renderer in unit.renderers)
                    {
                        renderer.enabled = false;
                        record.disabledRenderers.Add(renderer);
                    }
                }
            }

            foreach (RemovedObject removed in record.removed)
            {
                removed.gameObject.transform.SetParent(grid.sourcesHolder, true);
            }

            grid.bakedSections.Add(record);
        }

        private static int UnbakeSection(MeshSectionGrid grid, BakedSection section)
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

            foreach (MeshRenderer renderer in section.disabledRenderers)
            {
                if (renderer != null) renderer.enabled = true;
                else missing++;
            }

            if (section.output != null)
            {
                Object.DestroyImmediate(section.output);
            }

            foreach (string path in section.meshAssetPaths)
            {
                AssetDatabase.DeleteAsset(path);
            }

            grid.bakedSections.Remove(section);
            return missing;
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

            if (grid.sourcesHolder == null)
            {
                // Kept out of the grid's own hierarchy on purpose: deleting the grid object must
                // never delete the removed source objects along with it.
                var holder = new GameObject("[MeshSections Sources · EditorOnly]") { tag = "EditorOnly" };
                holder.SetActive(false);
                EditorSceneManager.MoveGameObjectToScene(holder, scene);
                grid.sourcesHolder = holder.transform;
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

        private static string EnsureOutputFolder(MeshSectionGrid grid)
        {
            if (string.IsNullOrEmpty(grid.gridId))
            {
                grid.gridId = Guid.NewGuid().ToString("N").Substring(0, 8);
            }

            string folder = OutputFolderPath(grid);
            EnsureFolder(folder);
            return folder;
        }

        private static string OutputFolderPath(MeshSectionGrid grid)
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

        private static void Finish(MeshSectionGrid grid)
        {
            // The records must never be rolled back by an unrelated Ctrl+Z on the grid (that would
            // orphan the removed objects), so drop the grid's undo history after bake/unbake.
            Undo.ClearUndo(grid);
            EditorUtility.SetDirty(grid);
            EditorSceneManager.MarkSceneDirty(grid.gameObject.scene);
        }
    }
}
