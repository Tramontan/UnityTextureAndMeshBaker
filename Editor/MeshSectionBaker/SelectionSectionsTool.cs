using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.Overlays;
using UnityEditor.SceneManagement;
using UnityEditorInternal;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace MeshSectionBaker
{
    /// <summary>
    /// "Выделение объектов" mode: sections assembled by hand from the scene selection.
    ///
    /// Works without the MeshSectionGrid being selected (selecting scene objects would otherwise
    /// switch the inspector away): the Scene view panel is an Overlay and the yellow highlight is
    /// drawn from SceneView.duringSceneGui. The same GUI is embedded in the grid's inspector.
    /// </summary>
    [InitializeOnLoad]
    public static class SelectionSectionsTool
    {
        private const string EditingKey = "MeshSectionBaker.EditingSectionId";
        private static readonly Color s_highlight = new Color(1f, 0.88f, 0.1f);

        private static MeshSectionGrid s_grid;
        private static bool s_gridDirty = true;
        private static GUIStyle s_label;
        private static GUIStyle s_editingLabel;

        /// <summary>Raised whenever sections change, so open inspectors can refresh their analysis.</summary>
        public static event Action Changed;

        static SelectionSectionsTool()
        {
            SceneView.duringSceneGui += OnSceneGui;
            Selection.selectionChanged += RepaintAll;
            EditorApplication.hierarchyChanged += () => s_gridDirty = true;
            EditorSceneManager.activeSceneChangedInEditMode += (_, __) => s_gridDirty = true;
        }

        public static string EditingSectionId
        {
            get => SessionState.GetString(EditingKey, string.Empty);
            set
            {
                SessionState.SetString(EditingKey, value ?? string.Empty);
                RepaintAll();
            }
        }

        /// <summary>The MeshSectionGrid of the loaded scenes (cached; roots are checked first, a deep search only if needed).</summary>
        public static MeshSectionGrid GetGrid()
        {
            if (!s_gridDirty && s_grid != null) return s_grid;

            s_grid = null;
            for (int i = 0; i < SceneManager.sceneCount && s_grid == null; i++)
            {
                Scene scene = SceneManager.GetSceneAt(i);
                if (!scene.isLoaded) continue;

                GameObject[] roots = scene.GetRootGameObjects();
                foreach (GameObject root in roots)
                {
                    if (root.TryGetComponent(out MeshSectionGrid grid)) { s_grid = grid; break; }
                }

                if (s_grid != null) break;
                foreach (GameObject root in roots)
                {
                    MeshSectionGrid grid = root.GetComponentInChildren<MeshSectionGrid>(true);
                    if (grid != null) { s_grid = grid; break; }
                }
            }

            s_gridDirty = false;
            return s_grid;
        }

        public static bool IsActive(MeshSectionGrid grid) => grid != null && grid.partitionMode == PartitionMode.Selection;

        public static bool IsBaked(MeshSectionGrid grid, ManualSection section)
        {
            return grid.bakedSections.Any(b => b.sourceSectionId == section.id);
        }

        // ------------------------------------------------------------------ shared GUI (overlay + inspector)

        public static void DrawSectionsGUI(MeshSectionGrid grid, bool detailed)
        {
            string editingId = EditingSectionId;

            GUI.backgroundColor = new Color(0.55f, 1f, 0.6f);
            if (GUILayout.Button(new GUIContent("+ Добавить секцию", "Создать пустую секцию и сразу начать её редактирование."), GUILayout.Height(22)))
            {
                AddSection(grid);
                GUIUtility.ExitGUI();
            }

            GUI.backgroundColor = Color.white;

            if (grid.manualSections.Count == 0)
            {
                GUILayout.Label("Секций пока нет.", EditorStyles.miniLabel);
                return;
            }

            ManualSection remove = null;
            foreach (ManualSection section in grid.manualSections)
            {
                bool editing = section.id == editingId;
                bool baked = IsBaked(grid, section);
                int count = section.objects.Count(o => o != null);

                GUILayout.BeginHorizontal();
                if (detailed && !baked)
                {
                    EditorGUI.BeginChangeCheck();
                    string name = EditorGUILayout.TextField(section.name, GUILayout.MinWidth(70));
                    if (EditorGUI.EndChangeCheck())
                    {
                        Undo.RecordObject(grid, "Rename section");
                        section.name = name;
                        MarkChanged(grid);
                    }
                }
                else
                {
                    GUILayout.Label(section.name, editing ? EditorStyles.boldLabel : EditorStyles.label, GUILayout.MinWidth(70));
                }

                GUILayout.Label($"{count} об.", EditorStyles.miniLabel, GUILayout.Width(44));

                if (baked)
                {
                    GUILayout.Label("✔ запечена", EditorStyles.miniBoldLabel, GUILayout.Width(96));
                }
                else if (editing)
                {
                    GUI.backgroundColor = s_highlight;
                    if (GUILayout.Button("Готово", EditorStyles.miniButton, GUILayout.Width(96))) EditingSectionId = string.Empty;
                    GUI.backgroundColor = Color.white;
                }
                else if (GUILayout.Button("Редактировать", EditorStyles.miniButton, GUILayout.Width(96)))
                {
                    EditingSectionId = section.id;
                }

                using (new EditorGUI.DisabledScope(baked))
                {
                    if (GUILayout.Button(new GUIContent("×", baked ? "Сначала распеките секцию" : "Удалить секцию"), EditorStyles.miniButton, GUILayout.Width(22)))
                    {
                        remove = section;
                    }
                }

                GUILayout.EndHorizontal();

                if (editing && !baked)
                {
                    DrawEditingControls(grid, section, detailed);
                }
            }

            if (remove != null)
            {
                int objects = remove.objects.Count(o => o != null);
                if (objects == 0 || EditorUtility.DisplayDialog("Удалить секцию", $"Удалить секцию «{remove.name}» ({objects} об.)? Сами объекты не трогаются.", "Удалить", "Отмена"))
                {
                    Undo.RecordObject(grid, "Remove section");
                    grid.manualSections.Remove(remove);
                    if (remove.id == editingId) EditingSectionId = string.Empty;
                    MarkChanged(grid);
                }

                GUIUtility.ExitGUI();
            }
        }

        private static void DrawEditingControls(MeshSectionGrid grid, ManualSection section, bool detailed)
        {
            List<GameObject> selected = SelectableObjects(grid);
            int selectedInSection = selected.Count(section.objects.Contains);

            GUILayout.BeginVertical(EditorStyles.helpBox);
            GUILayout.Label($"Редактирование «{section.name}» — объекты секции подсвечены жёлтым", EditorStyles.wordWrappedMiniLabel);

            GUILayout.BeginHorizontal();
            using (new EditorGUI.DisabledScope(selected.Count == 0))
            {
                if (GUILayout.Button($"Добавить выделенные ({selected.Count})", EditorStyles.miniButtonLeft))
                {
                    AddObjects(grid, section, selected);
                }
            }

            using (new EditorGUI.DisabledScope(selectedInSection == 0))
            {
                if (GUILayout.Button($"Убрать выделенные ({selectedInSection})", EditorStyles.miniButtonRight))
                {
                    RemoveObjects(grid, section, selected);
                }
            }

            GUILayout.EndHorizontal();

            if (detailed)
            {
                GameObject removeObject = null;
                foreach (GameObject obj in section.objects)
                {
                    GUILayout.BeginHorizontal();
                    EditorGUILayout.ObjectField(obj, typeof(GameObject), true);
                    if (GUILayout.Button("×", EditorStyles.miniButton, GUILayout.Width(22))) removeObject = obj;
                    GUILayout.EndHorizontal();
                }

                if (removeObject != null || section.objects.Contains(null))
                {
                    Undo.RecordObject(grid, "Remove object from section");
                    section.objects.Remove(removeObject);
                    section.objects.RemoveAll(o => o == null);
                    MarkChanged(grid);
                }
            }

            GUILayout.EndVertical();
        }

        // ------------------------------------------------------------------ operations

        public static ManualSection AddSection(MeshSectionGrid grid)
        {
            var section = new ManualSection { id = Guid.NewGuid().ToString("N"), name = UniqueName(grid) };
            Undo.RecordObject(grid, "Add section");
            grid.manualSections.Add(section);
            EditingSectionId = section.id;
            MarkChanged(grid);
            return section;
        }

        /// <summary>Adds objects to a section; an object lives in one section only, so it leaves any other.</summary>
        public static void AddObjects(MeshSectionGrid grid, ManualSection section, IEnumerable<GameObject> objects)
        {
            List<GameObject> list = objects.Where(o => o != null).Distinct().ToList();
            Undo.RecordObject(grid, "Add objects to section");
            foreach (ManualSection other in grid.manualSections)
            {
                if (other != section && !IsBaked(grid, other)) other.objects.RemoveAll(list.Contains);
            }

            foreach (GameObject obj in list)
            {
                if (!section.objects.Contains(obj)) section.objects.Add(obj);
            }

            MarkChanged(grid);
        }

        public static void RemoveObjects(MeshSectionGrid grid, ManualSection section, IEnumerable<GameObject> objects)
        {
            var set = new HashSet<GameObject>(objects);
            Undo.RecordObject(grid, "Remove objects from section");
            section.objects.RemoveAll(o => o == null || set.Contains(o));
            MarkChanged(grid);
        }

        /// <summary>Selected scene objects that may go into a section (not assets, not the tool's own objects).</summary>
        public static List<GameObject> SelectableObjects(MeshSectionGrid grid)
        {
            Scene scene = grid.gameObject.scene;
            HashSet<GameObject> toolRoots = SectionAnalyzer.CollectToolRoots(scene);
            return Selection.gameObjects
                .Where(go => go != null && !EditorUtility.IsPersistent(go) && go.scene == scene && !toolRoots.Contains(go.transform.root.gameObject))
                .ToList();
        }

        private static string UniqueName(MeshSectionGrid grid)
        {
            for (int i = grid.manualSections.Count + 1; ; i++)
            {
                string name = $"Section_{i}";
                if (grid.manualSections.All(s => s.name != name) && grid.bakedSections.All(b => b.name != name)) return name;
            }
        }

        private static void MarkChanged(MeshSectionGrid grid)
        {
            EditorUtility.SetDirty(grid);
            EditorSceneManager.MarkSceneDirty(grid.gameObject.scene);
            Changed?.Invoke();
            RepaintAll();
        }

        private static void RepaintAll()
        {
            InternalEditorUtility.RepaintAllViews();
        }

        // ------------------------------------------------------------------ Scene view

        private static void OnSceneGui(SceneView view)
        {
            MeshSectionGrid grid = GetGrid();
            bool active = IsActive(grid);

            if (view.TryGetOverlay(SelectionSectionsOverlay.Id, out Overlay overlay) && overlay.displayed != active)
            {
                overlay.displayed = active;
            }

            if (!active) return;

            if (s_label == null)
            {
                s_label = new GUIStyle(EditorStyles.whiteMiniLabel) { alignment = TextAnchor.MiddleCenter };
                s_editingLabel = new GUIStyle(EditorStyles.whiteBoldLabel) { alignment = TextAnchor.MiddleCenter, normal = { textColor = s_highlight } };
            }

            string editingId = EditingSectionId;
            foreach (ManualSection section in grid.manualSections)
            {
                GameObject[] objects = section.objects.Where(o => o != null && o.activeInHierarchy).ToArray();
                if (objects.Length == 0) continue;

                bool editing = section.id == editingId;
                if (editing && Event.current.type == EventType.Repaint)
                {
                    Handles.DrawOutline(objects, s_highlight, s_highlight, 0.35f);
                }

                if (TryGetBounds(objects, out Bounds bounds))
                {
                    Handles.Label(new Vector3(bounds.center.x, bounds.max.y + 1f, bounds.center.z),
                        editing ? $"✎ {section.name}" : section.name,
                        editing ? s_editingLabel : s_label);
                }
            }
        }

        private static bool TryGetBounds(GameObject[] objects, out Bounds bounds)
        {
            bounds = default;
            bool found = false;
            foreach (GameObject obj in objects)
            {
                foreach (Renderer renderer in obj.GetComponentsInChildren<Renderer>(false))
                {
                    if (!found) { bounds = renderer.bounds; found = true; }
                    else bounds.Encapsulate(renderer.bounds);
                }
            }

            return found;
        }
    }

    /// <summary>Scene view panel of the "Выделение объектов" mode (dockable/collapsible like any Unity overlay).</summary>
    [Overlay(typeof(SceneView), Id, "Mesh Sections", true)]
    public class SelectionSectionsOverlay : IMGUIOverlay
    {
        public const string Id = "mesh-section-baker-sections";

        public override void OnGUI()
        {
            MeshSectionGrid grid = SelectionSectionsTool.GetGrid();
            if (!SelectionSectionsTool.IsActive(grid))
            {
                GUILayout.Label("Режим «Выделение объектов» выключен", EditorStyles.miniLabel);
                return;
            }

            GUILayout.BeginVertical(GUILayout.Width(290));
            GUILayout.Label("Секции из выделения", EditorStyles.boldLabel);
            SelectionSectionsTool.DrawSectionsGUI(grid, false);
            GUILayout.EndVertical();
        }
    }
}
