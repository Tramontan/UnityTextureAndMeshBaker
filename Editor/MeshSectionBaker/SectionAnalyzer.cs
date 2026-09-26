using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace MeshSectionBaker
{
    /// <summary>
    /// One "scene object" as the user sees it: the outermost prefab instance (or a lone GameObject)
    /// owning one or more renderers. It is the unit that is assigned to a cell and removed/restored.
    /// </summary>
    public sealed class SourceUnit
    {
        public GameObject root;
        public readonly List<MeshRenderer> renderers = new List<MeshRenderer>();
        public readonly List<MeshCollider> colliders = new List<MeshCollider>();
        public Vector3 center;
        public bool removable;
        public string keepReason;
    }

    public sealed class SectionCell
    {
        public int ix;
        public int iz;
        public Vector2 min;
        public Vector2 max;

        /// <summary>Zone/selection mode: section name; zone mode also fills the outline. Grid cells leave these empty.</summary>
        public string zoneName;

        /// <summary>Selection mode: id of the <see cref="ManualSection"/> this cell comes from.</summary>
        public string sectionId;
        public readonly List<Vector2> polygon = new List<Vector2>();
        public readonly List<SourceUnit> units = new List<SourceUnit>();
        public readonly HashSet<Material> materials = new HashSet<Material>();
        public int rendererCount;
        public int submeshCount;
        public long triangleCount;

        public string Name => !string.IsNullOrEmpty(zoneName) ? zoneName : $"Section_{ix}_{iz}";
        public int RemovableCount => units.Count(u => u.removable);
    }

    public sealed class GridAnalysis
    {
        public readonly List<SectionCell> cells = new List<SectionCell>();
        public readonly Dictionary<string, int> skippedRenderers = new Dictionary<string, int>();
        public readonly Dictionary<string, int> keepReasons = new Dictionary<string, int>();
        public readonly List<string> scopeWarnings = new List<string>();
        public int rendererCount;
        public int submeshCount;
        public int unitCount;
        public int outsideZonesCount;
        public int duplicateObjectCount;
        public Bounds? contentBounds;
    }

    /// <summary>
    /// Finds the renderers that can be merged in the grid's scene and sorts them into cells.
    /// Also decides per object whether it may be removed: only if it contains nothing but
    /// meshes, renderers, transferable MeshColliders and controller-less Animators - anything
    /// else (scripts, lights, triggers, ...) means the object has a job beyond being drawn, so
    /// it stays and only its renderers are switched off.
    /// </summary>
    public static class SectionAnalyzer
    {
        public static GridAnalysis Analyze(MeshSectionGrid grid)
        {
            var result = new GridAnalysis();
            Scene scene = grid.gameObject.scene;
            HashSet<GameObject> toolRoots = CollectToolRoots(scene);

            if (grid.partitionMode == PartitionMode.Selection)
            {
                AnalyzeSelection(grid, scene, toolRoots, result);
                return result;
            }

            var units = new Dictionary<GameObject, SourceUnit>();
            var seen = new HashSet<MeshRenderer>();

            void Collect(MeshRenderer renderer, GameObject scopeRoot)
            {
                if (!seen.Add(renderer)) return;

                if (!IsEligible(renderer, grid.onlyStatic, out string reason))
                {
                    if (reason != null) Count(result.skippedRenderers, reason);
                    return;
                }

                GameObject unitRoot = FindUnitRoot(renderer.gameObject, scopeRoot);
                if (!units.TryGetValue(unitRoot, out SourceUnit unit))
                {
                    unit = new SourceUnit { root = unitRoot };
                    units[unitRoot] = unit;
                }

                unit.renderers.Add(renderer);
            }

            if (grid.scope == BakeScope.InsideObjects)
            {
                // Outer roots first, so a renderer covered by nested roots is always grouped the same way.
                foreach (GameObject scopeRoot in grid.scopeRoots.Where(r => r != null).Distinct().OrderBy(Depth))
                {
                    if (scopeRoot.scene != scene) { result.scopeWarnings.Add($"«{scopeRoot.name}» из другой сцены — пропущен."); continue; }
                    if (toolRoots.Contains(scopeRoot.transform.root.gameObject)) { result.scopeWarnings.Add($"«{scopeRoot.name}» — объект самого инструмента, пропущен."); continue; }
                    if (!scopeRoot.activeInHierarchy) { result.scopeWarnings.Add($"«{scopeRoot.name}» выключен — пропущен."); continue; }

                    foreach (MeshRenderer renderer in scopeRoot.GetComponentsInChildren<MeshRenderer>(false))
                    {
                        Collect(renderer, scopeRoot);
                    }
                }

                if (!grid.scopeRoots.Any(r => r != null))
                {
                    result.scopeWarnings.Add("Добавьте объекты в список — сшиваться будет только то, что внутри них.");
                }
            }
            else
            {
                foreach (GameObject root in scene.GetRootGameObjects())
                {
                    if (toolRoots.Contains(root)) continue;

                    foreach (MeshRenderer renderer in root.GetComponentsInChildren<MeshRenderer>(false))
                    {
                        Collect(renderer, null);
                    }
                }
            }

            float[] xs = grid.XBoundaries();
            float[] zs = grid.ZBoundaries();
            var cells = new Dictionary<(int, int), SectionCell>();

            foreach (SourceUnit unit in units.Values)
            {
                Bounds bounds = unit.renderers[0].bounds;
                foreach (MeshRenderer renderer in unit.renderers) bounds.Encapsulate(renderer.bounds);
                unit.center = bounds.center;

                if (result.contentBounds.HasValue)
                {
                    Bounds content = result.contentBounds.Value;
                    content.Encapsulate(bounds);
                    result.contentBounds = content;
                }
                else
                {
                    result.contentBounds = bounds;
                }

                SectionCell cell;
                if (grid.partitionMode == PartitionMode.Zones)
                {
                    int zoneIndex = FindZone(grid, new Vector2(unit.center.x, unit.center.z));
                    if (zoneIndex < 0)
                    {
                        result.outsideZonesCount++;
                        continue;
                    }

                    if (!cells.TryGetValue((zoneIndex, -1), out cell))
                    {
                        cell = CreateZoneCell(grid.zones[zoneIndex], zoneIndex);
                        cells[(zoneIndex, -1)] = cell;
                    }
                }
                else
                {
                    int ix = MeshSectionGrid.CellIndex(xs, unit.center.x);
                    int iz = MeshSectionGrid.CellIndex(zs, unit.center.z);
                    if (!cells.TryGetValue((ix, iz), out cell))
                    {
                        cell = new SectionCell
                        {
                            ix = ix,
                            iz = iz,
                            min = new Vector2(xs[ix], zs[iz]),
                            max = new Vector2(xs[ix + 1], zs[iz + 1])
                        };
                        cells[(ix, iz)] = cell;
                    }
                }

                EvaluateRemovable(unit, grid);
                if (!unit.removable && unit.keepReason != null) Count(result.keepReasons, unit.keepReason);
                AddToCell(cell, unit);
            }

            result.unitCount = units.Count;
            result.cells.AddRange(cells.Values.OrderBy(c => c.iz).ThenBy(c => c.ix));
            result.rendererCount = result.cells.Sum(c => c.rendererCount);
            result.submeshCount = result.cells.Sum(c => c.submeshCount);
            return result;
        }

        /// <summary>Roots created by the tool itself (grids, their "[MeshSections]" roots and source holders).</summary>
        public static HashSet<GameObject> CollectToolRoots(Scene scene)
        {
            var roots = new HashSet<GameObject>();
            foreach (GameObject root in scene.GetRootGameObjects())
            {
                foreach (MeshSectionGrid grid in root.GetComponentsInChildren<MeshSectionGrid>(true))
                {
                    roots.Add(grid.transform.root.gameObject);
                    if (grid.sectionsRoot != null) roots.Add(grid.sectionsRoot.root.gameObject);
                    if (grid.sourcesHolder != null) roots.Add(grid.sourcesHolder.root.gameObject);
                }
            }

            return roots;
        }

        /// <summary>
        /// Whole scene: the outermost prefab instance owning the renderer. Inside a chosen root:
        /// the highest prefab instance root BELOW that root - never the root itself or anything
        /// above it, so removing a unit can't take along objects outside the chosen root.
        /// Without a prefab in between, the renderer's own GameObject is the unit.
        /// </summary>
        private static GameObject FindUnitRoot(GameObject go, GameObject scopeRoot)
        {
            if (scopeRoot == null)
            {
                GameObject outermost = PrefabUtility.GetOutermostPrefabInstanceRoot(go);
                return outermost != null ? outermost : go;
            }

            GameObject highest = null;
            for (Transform t = go.transform; t != null && t != scopeRoot.transform; t = t.parent)
            {
                if (PrefabUtility.IsAnyPrefabInstanceRoot(t.gameObject)) highest = t.gameObject;
            }

            return highest != null ? highest : go;
        }

        /// <summary>
        /// Selection mode: every manual section becomes one cell made of the objects listed in it
        /// (and everything under them). A renderer claimed by an earlier section is skipped in
        /// later ones. Objects of already baked sections sit in the tool's holder and are skipped.
        /// </summary>
        private static void AnalyzeSelection(MeshSectionGrid grid, Scene scene, HashSet<GameObject> toolRoots, GridAnalysis result)
        {
            var claimed = new HashSet<MeshRenderer>();

            for (int si = 0; si < grid.manualSections.Count; si++)
            {
                ManualSection section = grid.manualSections[si];
                var units = new Dictionary<GameObject, SourceUnit>();

                foreach (GameObject obj in section.objects.Where(o => o != null).Distinct().OrderBy(Depth))
                {
                    if (obj.scene != scene || toolRoots.Contains(obj.transform.root.gameObject)) continue;

                    foreach (MeshRenderer renderer in obj.GetComponentsInChildren<MeshRenderer>(false))
                    {
                        if (!claimed.Add(renderer))
                        {
                            result.duplicateObjectCount++;
                            continue;
                        }

                        if (!IsEligible(renderer, grid.onlyStatic, out string reason))
                        {
                            if (reason != null) Count(result.skippedRenderers, reason);
                            continue;
                        }

                        GameObject unitRoot = FindUnitRootInclusive(renderer.gameObject, obj);
                        if (!units.TryGetValue(unitRoot, out SourceUnit unit))
                        {
                            unit = new SourceUnit { root = unitRoot };
                            units[unitRoot] = unit;
                        }

                        unit.renderers.Add(renderer);
                    }
                }

                if (units.Count == 0) continue;

                var cell = new SectionCell
                {
                    ix = si,
                    iz = -2,
                    zoneName = string.IsNullOrEmpty(section.name) ? $"Section_{si + 1}" : section.name,
                    sectionId = section.id
                };

                Bounds? cellBounds = null;
                foreach (SourceUnit unit in units.Values)
                {
                    Bounds bounds = unit.renderers[0].bounds;
                    foreach (MeshRenderer renderer in unit.renderers) bounds.Encapsulate(renderer.bounds);
                    unit.center = bounds.center;

                    if (cellBounds.HasValue)
                    {
                        Bounds b = cellBounds.Value;
                        b.Encapsulate(bounds);
                        cellBounds = b;
                    }
                    else
                    {
                        cellBounds = bounds;
                    }

                    EvaluateRemovable(unit, grid);
                    if (!unit.removable && unit.keepReason != null) Count(result.keepReasons, unit.keepReason);
                    AddToCell(cell, unit);
                }

                Bounds final = cellBounds.Value;
                cell.min = new Vector2(final.min.x, final.min.z);
                cell.max = new Vector2(final.max.x, final.max.z);
                result.unitCount += units.Count;
                result.cells.Add(cell);

                if (result.contentBounds.HasValue)
                {
                    Bounds content = result.contentBounds.Value;
                    content.Encapsulate(final);
                    result.contentBounds = content;
                }
                else
                {
                    result.contentBounds = final;
                }
            }

            result.rendererCount = result.cells.Sum(c => c.rendererCount);
            result.submeshCount = result.cells.Sum(c => c.submeshCount);
        }

        /// <summary>
        /// Selection mode: the highest prefab instance root between the renderer and the selected
        /// object, the selected object itself included. Selecting a prefab gives that prefab;
        /// selecting a plain parent gives the prefabs inside it.
        /// </summary>
        private static GameObject FindUnitRootInclusive(GameObject go, GameObject selected)
        {
            GameObject highest = null;
            for (Transform t = go.transform; t != null; t = t.parent)
            {
                if (PrefabUtility.IsAnyPrefabInstanceRoot(t.gameObject)) highest = t.gameObject;
                if (t == selected.transform) break;
            }

            return highest != null ? highest : go;
        }

        private static void AddToCell(SectionCell cell, SourceUnit unit)
        {
            cell.units.Add(unit);
            foreach (MeshRenderer renderer in unit.renderers)
            {
                Mesh mesh = renderer.GetComponent<MeshFilter>().sharedMesh;
                cell.rendererCount++;
                cell.submeshCount += mesh.subMeshCount;
                for (int s = 0; s < mesh.subMeshCount; s++) cell.triangleCount += mesh.GetIndexCount(s) / 3;
                foreach (Material material in renderer.sharedMaterials) cell.materials.Add(material);
            }
        }

        /// <summary>First zone (in list order) containing the point, or -1. Overlaps go to the earlier zone.</summary>
        public static int FindZone(MeshSectionGrid grid, Vector2 point)
        {
            for (int i = 0; i < grid.zones.Count; i++)
            {
                List<Vector2> points = grid.zones[i].points;
                if (points.Count >= 3 && MeshSectionGrid.PointInPolygon(point, points)) return i;
            }

            return -1;
        }

        private static SectionCell CreateZoneCell(SectionZone zone, int zoneIndex)
        {
            var cell = new SectionCell
            {
                ix = zoneIndex,
                iz = -1,
                zoneName = string.IsNullOrEmpty(zone.name) ? $"Zone_{zoneIndex + 1}" : zone.name,
                min = new Vector2(float.MaxValue, float.MaxValue),
                max = new Vector2(float.MinValue, float.MinValue)
            };

            foreach (Vector2 point in zone.points)
            {
                cell.polygon.Add(point);
                cell.min = Vector2.Min(cell.min, point);
                cell.max = Vector2.Max(cell.max, point);
            }

            return cell;
        }

        private static int Depth(GameObject go)
        {
            int depth = 0;
            for (Transform t = go.transform.parent; t != null; t = t.parent) depth++;
            return depth;
        }

        private static bool IsEligible(MeshRenderer renderer, bool onlyStatic, out string reason)
        {
            reason = null;

            // Explicit check: GetComponentsInChildren(includeInactive: false) called on an inactive
            // ROOT still returns its components, so disabled root objects would slip through.
            if (!renderer.enabled || !renderer.gameObject.activeInHierarchy) return false;

            var filter = renderer.GetComponent<MeshFilter>();
            if (filter == null || filter.sharedMesh == null) return false;

            Mesh mesh = filter.sharedMesh;
            if (onlyStatic && (GameObjectUtility.GetStaticEditorFlags(renderer.gameObject) & StaticEditorFlags.BatchingStatic) == 0)
            {
                reason = "не отмечен Static";
                return false;
            }

            if (!mesh.isReadable) { reason = "меш без Read/Write"; return false; }
            if (renderer.sharedMaterials.Length != mesh.subMeshCount) { reason = "число материалов не совпадает с числом сабмешей"; return false; }
            if (renderer.GetComponentInParent<LODGroup>() != null) { reason = "внутри LODGroup"; return false; }

            for (int s = 0; s < mesh.subMeshCount; s++)
            {
                if (mesh.GetTopology(s) != MeshTopology.Triangles)
                {
                    reason = "сабмеш не из треугольников";
                    return false;
                }
            }

            return true;
        }

        private static void EvaluateRemovable(SourceUnit unit, MeshSectionGrid grid)
        {
            unit.removable = false;
            unit.keepReason = null;
            unit.colliders.Clear();

            if (grid.sourceMode != SourceObjectMode.RemoveObjects) return;

            GameObject root = unit.root;
            bool isPrefab = PrefabUtility.IsAnyPrefabInstanceRoot(root);
            if (!isPrefab && root.transform.childCount > 0) { unit.keepReason = "объект с дочерними (не префаб)"; return; }

            Transform parent = root.transform.parent;
            if (parent != null && PrefabUtility.IsPartOfPrefabInstance(parent.gameObject)) { unit.keepReason = "вложен в другой префаб"; return; }

            var eligible = new HashSet<MeshRenderer>(unit.renderers);
            foreach (Transform transform in root.GetComponentsInChildren<Transform>(true))
            {
                if (!transform.gameObject.activeSelf) { unit.keepReason = "есть выключенные дочерние объекты"; unit.colliders.Clear(); return; }

                foreach (Component component in transform.GetComponents<Component>())
                {
                    switch (component)
                    {
                        case null:
                            unit.keepReason = "missing script";
                            unit.colliders.Clear();
                            return;
                        case Transform _:
                        case MeshFilter _:
                            continue;
                        case MeshRenderer renderer when eligible.Contains(renderer):
                            continue;
                        case MeshRenderer _:
                            unit.keepReason = "есть неподходящий рендерер";
                            unit.colliders.Clear();
                            return;
                        case MeshCollider collider:
                            if (!grid.transferColliders) { unit.keepReason = "коллайдер (перенос выключен)"; unit.colliders.Clear(); return; }
                            if (!collider.enabled || collider.isTrigger || collider.convex || collider.sharedMesh == null || !collider.sharedMesh.isReadable)
                            {
                                unit.keepReason = "MeshCollider нельзя перенести (trigger/convex/не читается)";
                                unit.colliders.Clear();
                                return;
                            }

                            unit.colliders.Add(collider);
                            continue;
                        case Animator animator when animator.runtimeAnimatorController == null:
                            continue;
                        default:
                            unit.keepReason = component.GetType().Name;
                            unit.colliders.Clear();
                            return;
                    }
                }
            }

            unit.removable = true;
        }

        private static void Count(Dictionary<string, int> counter, string key)
        {
            counter.TryGetValue(key, out int count);
            counter[key] = count + 1;
        }
    }
}
