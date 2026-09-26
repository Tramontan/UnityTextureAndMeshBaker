using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace MeshSectionBaker
{
    /// <summary>
    /// "Убрать со сцены": removed source objects are deleted from the scene, so neither the editor
    /// nor a build loads them. Before that they are saved - as they are, prefab instances with all
    /// their overrides - into one prefab asset per section (Generated/&lt;scene&gt;/Sources/), which
    /// nothing in a build references. Unbaking instantiates that asset, unpacks it one level (its
    /// children become the original prefab instances again) and puts every object back into its
    /// old parent and sibling position.
    ///
    /// Scene saving and asset writes are not atomic, so an asset is deleted only once neither the
    /// in-memory records nor the saved scene file refer to it (checked after unbake and on save).
    /// </summary>
    public static class SourceStore
    {
        private const string FolderName = "Sources";

        /// <summary>
        /// Sections baked by older versions keep their removed objects in the EditorOnly holder,
        /// i.e. still in the scene. Moves them into stores (as if baked now) and deletes the holder.
        /// Returns the number of objects moved.
        /// </summary>
        public static int MigrateLegacy(MeshSectionGrid grid)
        {
            string folder = SectionBakeRunner.EnsureOutputFolder(grid);
            int moved = 0;
            try
            {
                List<BakedSection> legacy = grid.bakedSections.Where(IsLegacy).ToList();
                for (int i = 0; i < legacy.Count; i++)
                {
                    BakedSection section = legacy[i];
                    EditorUtility.DisplayProgressBar("Mesh Section Baker", $"Перенос исходников {section.name} ({i + 1}/{legacy.Count})", (float)i / legacy.Count);

                    var roots = new List<GameObject>();
                    foreach (RemovedObject removed in section.removed)
                    {
                        removed.storedIndex = removed.gameObject != null ? roots.Count : -1;
                        if (removed.gameObject != null) roots.Add(removed.gameObject);
                    }

                    if (roots.Count == 0 || !Store(grid, section, roots, folder, grid.sourcesHolder)) continue;

                    foreach (RemovedObject removed in section.removed) removed.gameObject = null;
                    moved += roots.Count;
                }
            }
            finally
            {
                EditorUtility.ClearProgressBar();
                AssetDatabase.SaveAssets();
                if (grid.sourcesHolder != null && grid.sourcesHolder.childCount == 0)
                {
                    Object.DestroyImmediate(grid.sourcesHolder.gameObject);
                    grid.sourcesHolder = null;
                }

                SectionBakeRunner.Finish(grid);
            }

            return moved;
        }

        public static bool IsLegacy(BakedSection section)
        {
            return string.IsNullOrEmpty(section.sourcesPrefabPath) && section.removed.Any(r => r.gameObject != null);
        }

        /// <summary>
        /// Moves <paramref name="roots"/> into a new prefab asset and deletes them from the scene.
        /// On failure they go back to <paramref name="fallbackParent"/>, or to their recorded places if null.
        /// </summary>
        public static bool Store(MeshSectionGrid grid, BakedSection record, List<GameObject> roots, string folder, Transform fallbackParent = null)
        {
            RecordRelinks(grid, record, roots);

            string directory = $"{folder}/{FolderName}";
            EnsureFolder(directory);
            string path = AssetDatabase.GenerateUniqueAssetPath($"{directory}/{Sanitize(record.name)}.prefab");

            var container = new GameObject($"{record.name} · Sources");
            SceneManager.MoveGameObjectToScene(container, grid.gameObject.scene);
            foreach (GameObject root in roots) root.transform.SetParent(container.transform, true);

            GameObject asset = PrefabUtility.SaveAsPrefabAsset(container, path, out bool success);
            if (!success || asset == null)
            {
                // Put everything back; the caller decides what happens to the objects instead.
                foreach (RemovedObject removed in record.removed.Where(r => r.storedIndex >= 0).OrderBy(r => r.siblingIndex))
                {
                    Transform transform = roots[removed.storedIndex].transform;
                    transform.SetParent(fallbackParent != null ? fallbackParent : removed.parent, true);
                    if (fallbackParent == null) transform.SetSiblingIndex(removed.siblingIndex);
                }

                Object.DestroyImmediate(container);
                record.relinks.Clear();
                Debug.LogError($"[MeshSectionBaker] Не удалось сохранить исходные объекты секции {record.name} в {path}.");
                return false;
            }

            record.sourcesPrefab = asset;
            record.sourcesPrefabPath = path;
            Object.DestroyImmediate(container);
            return true;
        }

        /// <summary>Spawns the stored objects back into their places. Returns the number that could not be restored.</summary>
        public static int Restore(MeshSectionGrid grid, BakedSection section)
        {
            GameObject asset = section.sourcesPrefab != null ? section.sourcesPrefab : AssetDatabase.LoadAssetAtPath<GameObject>(section.sourcesPrefabPath);
            if (asset == null) return section.removed.Count;

            var instance = (GameObject)PrefabUtility.InstantiatePrefab(asset, grid.gameObject.scene);
            PrefabUtility.UnpackPrefabInstance(instance, PrefabUnpackMode.OutermostRoot, InteractionMode.AutomatedAction);
            List<Transform> stored = instance.transform.Cast<Transform>().ToList();

            int missing = 0;
            var restored = new Transform[stored.Count];
            foreach (RemovedObject removed in section.removed.OrderBy(r => r.siblingIndex))
            {
                if (removed.storedIndex < 0 || removed.storedIndex >= stored.Count)
                {
                    missing++;
                    continue;
                }

                Transform transform = stored[removed.storedIndex];
                transform.SetParent(removed.parent, true);
                transform.SetSiblingIndex(removed.siblingIndex);
                restored[removed.storedIndex] = transform;
            }

            Object.DestroyImmediate(instance);
            ApplyRelinks(grid, section, restored);
            return missing;
        }

        /// <summary>
        /// Deletes stored-sources assets of this grid that no baked section refers to - neither in
        /// memory nor in the saved scene file (the scene may still be reverted to that state).
        /// </summary>
        public static void CleanupOrphans(MeshSectionGrid grid)
        {
            string directory = $"{SectionBakeRunner.OutputFolderPath(grid)}/{FolderName}";
            if (!AssetDatabase.IsValidFolder(directory)) return;

            var inUse = new HashSet<string>(grid.bakedSections.Select(s => s.sourcesPrefabPath).Where(p => !string.IsNullOrEmpty(p)));
            string scenePath = grid.gameObject.scene.path;
            string savedScene = !string.IsNullOrEmpty(scenePath) && File.Exists(scenePath) ? File.ReadAllText(scenePath) : null;

            foreach (string guid in AssetDatabase.FindAssets("t:GameObject", new[] { directory }))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (inUse.Contains(path)) continue;
                if (savedScene != null && savedScene.Contains(guid)) continue;
                AssetDatabase.DeleteAsset(path);
            }

            DeleteIfEmpty(directory);
            DeleteIfEmpty(SectionBakeRunner.OutputFolderPath(grid));
        }

        // ------------------------------------------------------------------ references

        /// <summary>
        /// Deleting an object loses every reference to it (and a respawned copy is a new object), so
        /// objects referenced from the rest of the scene - or referencing scene objects themselves,
        /// which a prefab asset can't store - are kept in place instead (renderers switched off).
        /// Selection-mode lists are the exception: they are re-pointed on unbake.
        /// </summary>
        public static void KeepReferencedUnits(MeshSectionGrid grid, IEnumerable<SectionCell> cells)
        {
            List<SourceUnit> units = cells.SelectMany(c => c.units).Where(u => u.removable).ToList();
            if (units.Count == 0) return;

            var owner = new Dictionary<Object, SourceUnit>();
            foreach (SourceUnit unit in units)
            {
                foreach (Transform transform in unit.root.GetComponentsInChildren<Transform>(true))
                {
                    owner[transform.gameObject] = unit;
                    foreach (Component component in transform.GetComponents<Component>())
                    {
                        if (component != null) owner[component] = unit;
                    }
                }
            }

            var keep = new Dictionary<SourceUnit, string>();
            foreach (GameObject sceneRoot in grid.gameObject.scene.GetRootGameObjects())
            {
                foreach (Component component in sceneRoot.GetComponentsInChildren<Component>(true))
                {
                    // Transforms (hierarchy links), meshes and colliders can't point at other scene objects.
                    if (component == null || component is Transform || component is MeshFilter || component is Collider || component is MeshSectionGrid) continue;

                    owner.TryGetValue(component, out SourceUnit self);
                    if (self != null && keep.ContainsKey(self)) continue;

                    SerializedProperty property = new SerializedObject(component).GetIterator();
                    while (property.Next(true))
                    {
                        if (property.propertyType != SerializedPropertyType.ObjectReference) continue;

                        Object value = property.objectReferenceValue;
                        if (value == null || !(value is GameObject || value is Component) || EditorUtility.IsPersistent(value)) continue;

                        owner.TryGetValue(value, out SourceUnit target);
                        if (self != null)
                        {
                            if (target == self) continue;
                            keep[self] = "ссылается на объекты сцены";
                            break;
                        }

                        if (target != null && !keep.ContainsKey(target)) keep[target] = "на объект ссылаются другие объекты";
                    }
                }
            }

            foreach (KeyValuePair<SourceUnit, string> pair in keep)
            {
                pair.Key.removable = false;
                pair.Key.keepReason = pair.Value;
            }

            if (keep.Count > 0)
            {
                Debug.Log($"[MeshSectionBaker] Оставлены на сцене (только выключены рендеры), т.к. на них есть ссылки: {keep.Count} — " +
                          string.Join(", ", keep.Keys.Take(10).Select(u => u.root.name)) + (keep.Count > 10 ? " …" : string.Empty));
            }
        }

        // ------------------------------------------------------------------ selection-mode links

        private static void RecordRelinks(MeshSectionGrid grid, BakedSection record, List<GameObject> roots)
        {
            record.relinks.Clear();
            foreach (ManualSection section in grid.manualSections)
            {
                for (int i = 0; i < section.objects.Count; i++)
                {
                    GameObject obj = section.objects[i];
                    if (obj == null) continue;

                    for (int r = 0; r < roots.Count; r++)
                    {
                        if (!obj.transform.IsChildOf(roots[r].transform)) continue;
                        record.relinks.Add(new RelinkEntry { manualSectionId = section.id, objectIndex = i, storedIndex = r, path = RelativePath(roots[r].transform, obj.transform) });
                        break;
                    }
                }
            }
        }

        private static void ApplyRelinks(MeshSectionGrid grid, BakedSection section, Transform[] restored)
        {
            foreach (RelinkEntry entry in section.relinks)
            {
                ManualSection manual = grid.manualSections.FirstOrDefault(m => m.id == entry.manualSectionId);
                if (manual == null || entry.storedIndex < 0 || entry.storedIndex >= restored.Length || restored[entry.storedIndex] == null) continue;

                Transform target = Resolve(restored[entry.storedIndex], entry.path);
                if (target == null || manual.objects.Contains(target.gameObject)) continue;

                // The list may have been cleaned of the dead entries meanwhile - then just append.
                if (entry.objectIndex < manual.objects.Count && manual.objects[entry.objectIndex] == null) manual.objects[entry.objectIndex] = target.gameObject;
                else manual.objects.Add(target.gameObject);
            }

            section.relinks.Clear();
        }

        private static string RelativePath(Transform root, Transform target)
        {
            var indices = new List<int>();
            for (Transform t = target; t != root; t = t.parent) indices.Add(t.GetSiblingIndex());
            indices.Reverse();
            return string.Join("/", indices);
        }

        private static Transform Resolve(Transform root, string path)
        {
            Transform current = root;
            if (string.IsNullOrEmpty(path)) return current;

            foreach (string part in path.Split('/'))
            {
                if (!int.TryParse(part, out int index) || index >= current.childCount) return null;
                current = current.GetChild(index);
            }

            return current;
        }

        // ------------------------------------------------------------------ files

        private static void EnsureFolder(string path)
        {
            if (AssetDatabase.IsValidFolder(path)) return;

            string parent = Path.GetDirectoryName(path)?.Replace('\\', '/');
            EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, Path.GetFileName(path));
        }

        private static void DeleteIfEmpty(string folder)
        {
            if (AssetDatabase.IsValidFolder(folder) && AssetDatabase.FindAssets(string.Empty, new[] { folder }).Length == 0)
            {
                AssetDatabase.DeleteAsset(folder);
            }
        }

        private static string Sanitize(string name)
        {
            foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
            return name;
        }

        /// <summary>After a scene is saved its file no longer refers to stores of unbaked sections: delete them.</summary>
        [InitializeOnLoadMethod]
        private static void HookSceneSaved()
        {
            EditorSceneManager.sceneSaved += scene =>
            {
                EditorApplication.delayCall += () =>
                {
                    if (!scene.IsValid() || !scene.isLoaded) return;
                    foreach (GameObject root in scene.GetRootGameObjects())
                    {
                        foreach (MeshSectionGrid grid in root.GetComponentsInChildren<MeshSectionGrid>(true)) CleanupOrphans(grid);
                    }
                };
            };
        }
    }
}
