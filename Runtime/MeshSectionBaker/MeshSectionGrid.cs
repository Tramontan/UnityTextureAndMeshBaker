using System;
using System.Collections.Generic;
using UnityEngine;

namespace MeshSectionBaker
{
    public enum SourceObjectMode
    {
        /// <summary>Source objects are moved into an inactive EditorOnly holder (gone from the scene and from builds).</summary>
        [InspectorName("Убрать со сцены")]
        RemoveObjects = 0,

        /// <summary>Source objects stay where they are, only their MeshRenderers are switched off.</summary>
        [InspectorName("Оставить, выключить рендеры")]
        DisableRenderers = 1
    }

    public enum PartitionMode
    {
        /// <summary>Axis-aligned grid built from X/Z divider strips.</summary>
        [InspectorName("Сетка полос")]
        Grid = 0,

        /// <summary>Hand-drawn polygon zones; objects outside every zone are left alone.</summary>
        [InspectorName("Зоны вручную")]
        Zones = 1,

        /// <summary>Sections assembled by hand from objects selected in the scene.</summary>
        [InspectorName("Выделение объектов")]
        Selection = 2
    }

    public enum BakeScope
    {
        [InspectorName("Вся сцена")]
        WholeScene = 0,

        /// <summary>Only renderers inside the GameObjects listed in <see cref="MeshSectionGrid.scopeRoots"/>.</summary>
        [InspectorName("Только внутри объектов")]
        InsideObjects = 1
    }

    /// <summary>
    /// Scene-side data of the Mesh Section Baker: the divider lines drawn in the Scene view, the
    /// bake settings, and a record of every baked section (what was created, what was removed and
    /// from where) - everything needed to put the scene back exactly as it was.
    ///
    /// The GameObject carrying this component is tagged EditorOnly, so it never reaches a build;
    /// the baked sections live under a separate "[MeshSections]" root that does.
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("Mesh Section Baker/Mesh Section Grid")]
    public class MeshSectionGrid : MonoBehaviour
    {
        public string gridId;
        public PartitionMode partitionMode = PartitionMode.Grid;
        public List<SectionZone> zones = new List<SectionZone>();
        public List<ManualSection> manualSections = new List<ManualSection>();
        public Bounds area = new Bounds(Vector3.zero, new Vector3(100f, 10f, 100f));
        public List<float> xLines = new List<float>();
        public List<float> zLines = new List<float>();
        public float uniformStep = 40f;
        public float snapStep = 1f;

        public BakeScope scope = BakeScope.WholeScene;
        public List<GameObject> scopeRoots = new List<GameObject>();
        public bool onlyStatic = true;
        public SourceObjectMode sourceMode = SourceObjectMode.RemoveObjects;
        public bool transferColliders = true;

        public Transform sectionsRoot;
        public Transform sourcesHolder;
        public List<BakedSection> bakedSections = new List<BakedSection>();

        public float DrawHeight => area.max.y + 0.5f;

        public float[] XBoundaries() => Boundaries(area.min.x, area.max.x, xLines);
        public float[] ZBoundaries() => Boundaries(area.min.z, area.max.z, zLines);

        /// <summary>Cell index of a coordinate; values outside the area fall into the edge cells.</summary>
        public static int CellIndex(float[] boundaries, float value)
        {
            for (int i = 1; i < boundaries.Length - 1; i++)
            {
                if (value < boundaries[i]) return i - 1;
            }

            return boundaries.Length - 2;
        }

        private static float[] Boundaries(float min, float max, List<float> lines)
        {
            var sorted = new List<float>(lines);
            sorted.Sort();

            var result = new List<float> { min };
            foreach (float line in sorted)
            {
                if (line > min + 0.01f && line < max - 0.01f && line - result[result.Count - 1] > 0.01f)
                {
                    result.Add(line);
                }
            }

            result.Add(max);
            return result.ToArray();
        }

        /// <summary>Even-odd point-in-polygon test on the XZ plane (works for concave zones too).</summary>
        public static bool PointInPolygon(Vector2 point, IList<Vector2> polygon)
        {
            bool inside = false;
            for (int i = 0, j = polygon.Count - 1; i < polygon.Count; j = i++)
            {
                Vector2 a = polygon[i];
                Vector2 b = polygon[j];
                if ((a.y > point.y) != (b.y > point.y) &&
                    point.x < (b.x - a.x) * (point.y - a.y) / (b.y - a.y) + a.x)
                {
                    inside = !inside;
                }
            }

            return inside;
        }

        private void OnDrawGizmos()
        {
            float y = DrawHeight;

            if (partitionMode == PartitionMode.Grid)
            {
                Gizmos.color = new Color(1f, 0.85f, 0.2f, 0.95f);
                foreach (float x in XBoundaries())
                {
                    Gizmos.DrawLine(new Vector3(x, y, area.min.z), new Vector3(x, y, area.max.z));
                }

                foreach (float z in ZBoundaries())
                {
                    Gizmos.DrawLine(new Vector3(area.min.x, y, z), new Vector3(area.max.x, y, z));
                }
            }
            else
            {
                Gizmos.color = new Color(0.3f, 0.85f, 1f, 0.95f);
                foreach (SectionZone zone in zones)
                {
                    DrawPolygon(zone.points, y);
                }
            }

            foreach (BakedSection section in bakedSections)
            {
                if (section.polygon != null && section.polygon.Count >= 3)
                {
                    Gizmos.color = new Color(0.2f, 1f, 0.45f, 0.9f);
                    DrawPolygon(section.polygon, y + 0.05f);
                }
                else
                {
                    Gizmos.color = new Color(0.2f, 1f, 0.45f, 0.2f);
                    Vector2 center = (section.min + section.max) * 0.5f;
                    Vector2 size = section.max - section.min;
                    Gizmos.DrawCube(new Vector3(center.x, y, center.y), new Vector3(size.x, 0.05f, size.y));
                }
            }
        }

        private static void DrawPolygon(IList<Vector2> points, float y)
        {
            for (int i = 0; i < points.Count; i++)
            {
                Vector2 a = points[i];
                Vector2 b = points[(i + 1) % points.Count];
                Gizmos.DrawLine(new Vector3(a.x, y, a.y), new Vector3(b.x, y, b.y));
            }
        }
    }

    /// <summary>
    /// Section assembled by hand: the listed scene objects (and everything under them) are merged
    /// together. An object can belong to one section only.
    /// </summary>
    [Serializable]
    public class ManualSection
    {
        public string id;
        public string name;
        public List<GameObject> objects = new List<GameObject>();
    }

    /// <summary>Hand-drawn section zone: a closed polygon on the XZ plane (x = world X, y = world Z).</summary>
    [Serializable]
    public class SectionZone
    {
        public string name;
        public List<Vector2> points = new List<Vector2>();

        public Vector2 Center
        {
            get
            {
                Vector2 sum = Vector2.zero;
                foreach (Vector2 point in points) sum += point;
                return points.Count > 0 ? sum / points.Count : Vector2.zero;
            }
        }
    }

    [Serializable]
    public class BakedSection
    {
        public string name;

        /// <summary>Cell rectangle on the XZ plane (x = world X, y = world Z).</summary>
        public Vector2 min;
        public Vector2 max;

        /// <summary>Zone outline for hand-drawn zones; empty for grid cells.</summary>
        public List<Vector2> polygon = new List<Vector2>();

        /// <summary>Id of the <see cref="ManualSection"/> this was baked from (selection mode only).</summary>
        public string sourceSectionId;

        public GameObject output;
        public List<RemovedObject> removed = new List<RemovedObject>();
        public List<MeshRenderer> disabledRenderers = new List<MeshRenderer>();
        public List<string> meshAssetPaths = new List<string>();
        public int sourceObjectCount;
        public int sourceRendererCount;
    }

    [Serializable]
    public class RemovedObject
    {
        public GameObject gameObject;
        public Transform parent;
        public int siblingIndex;
    }
}
