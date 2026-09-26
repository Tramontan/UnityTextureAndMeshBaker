using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace MeshSectionBaker
{
    [CustomEditor(typeof(MeshSectionGrid))]
    public class MeshSectionGridEditor : Editor
    {
        private const float ButtonOffset = 16f;
        private const float PickDistance = 12f;
        private static readonly Color s_addColor = new Color(0.55f, 1f, 0.6f);
        private static readonly Color s_removeColor = new Color(1f, 0.55f, 0.5f);
        private static readonly Color s_zoneColor = new Color(0.3f, 0.85f, 1f, 1f);
        private static readonly Color s_zoneMoveColor = new Color(1f, 0.6f, 0.2f, 1f);
        private static readonly int s_zoneControlHash = "MeshSectionZoneEdit".GetHashCode();

        private static bool s_editMode;
        private static GUIStyle s_hintStyle;
        private static MeshSectionGrid s_currentGrid;

        private GridAnalysis _analysis;
        private bool _analysisDirty = true;
        private Vector2 _cellScroll;
        private Vector2 _bakedScroll;
        private Vector2 _zoneScroll;
        private GUIStyle _cellLabel;
        private GUIStyle _bakedLabel;
        private int _dragZone = -1;
        private Vector3 _dragLast;

        private MeshSectionGrid Grid => (MeshSectionGrid)target;

        /// <summary>True while zone polygons are being edited - drives the "hold Z to move a zone" shortcut context.</summary>
        public static bool IsZoneEditing => s_editMode && s_currentGrid != null && s_currentGrid.partitionMode == PartitionMode.Zones;

        [MenuItem("Tools/Запекание мешей")]
        public static void OpenOrCreate()
        {
            Scene scene = SceneManager.GetActiveScene();
            MeshSectionGrid grid = FindGrid(scene);
            if (grid == null)
            {
                var go = new GameObject("MeshSectionGrid") { tag = "EditorOnly" };
                EditorSceneManager.MoveGameObjectToScene(go, scene);
                grid = go.AddComponent<MeshSectionGrid>();
                grid.gridId = Guid.NewGuid().ToString("N").Substring(0, 8);
                FitArea(grid, SectionAnalyzer.Analyze(grid));
                Undo.RegisterCreatedObjectUndo(go, "Create Mesh Section Grid");
                EditorSceneManager.MarkSceneDirty(scene);
            }

            Selection.activeGameObject = grid.gameObject;
            s_editMode = true;
            TopView(grid);
        }

        private static MeshSectionGrid FindGrid(Scene scene)
        {
            foreach (GameObject root in scene.GetRootGameObjects())
            {
                var grid = root.GetComponentInChildren<MeshSectionGrid>(true);
                if (grid != null) return grid;
            }

            return null;
        }

        private void OnEnable()
        {
            if (string.IsNullOrEmpty(Grid.gridId))
            {
                Grid.gridId = Guid.NewGuid().ToString("N").Substring(0, 8);
                EditorUtility.SetDirty(Grid);
            }

            s_currentGrid = Grid;
            EditorApplication.hierarchyChanged += MarkDirty;
            Undo.undoRedoPerformed += MarkDirty;
            SelectionSectionsTool.Changed += MarkDirty;
            _analysisDirty = true;
        }

        private void OnDisable()
        {
            if (s_currentGrid == target) s_currentGrid = null;
            ZoneShortcuts.Release();
            EditorApplication.hierarchyChanged -= MarkDirty;
            Undo.undoRedoPerformed -= MarkDirty;
            SelectionSectionsTool.Changed -= MarkDirty;
        }

        private void MarkDirty()
        {
            _analysisDirty = true;
            Repaint();
            SceneView.RepaintAll();
        }

        private GridAnalysis Analysis
        {
            get
            {
                if (_analysisDirty || _analysis == null)
                {
                    _analysis = SectionAnalyzer.Analyze(Grid);
                    _analysisDirty = false;
                }

                return _analysis;
            }
        }

        // ------------------------------------------------------------------ Inspector

        public override void OnInspectorGUI()
        {
            MeshSectionGrid grid = Grid;
            GridAnalysis analysis = Analysis;

            EditorGUI.BeginChangeCheck();
            var partition = (PartitionMode)EditorGUILayout.EnumPopup(
                new GUIContent("Разбиение", "Сетка полос — равномерные прямоугольные ячейки.\nЗоны вручную — свои многоугольники; всё, что вне зон, не сшивается."),
                grid.partitionMode);
            if (EditorGUI.EndChangeCheck())
            {
                Undo.RecordObject(grid, "Section partition mode");
                grid.partitionMode = partition;
                MarkDirty();
            }

            EditorGUILayout.Space(4);
            switch (grid.partitionMode)
            {
                case PartitionMode.Grid:
                    DrawGridSection(grid, analysis);
                    break;
                case PartitionMode.Zones:
                    DrawZonesSection(grid, analysis);
                    break;
                default:
                    DrawSelectionSection(grid, analysis);
                    break;
            }

            EditorGUILayout.Space(8);
            DrawSettingsSection(grid);
            EditorGUILayout.Space(8);
            DrawCellsSection(grid, analysis);
            EditorGUILayout.Space(8);
            DrawBakedSection(grid);
        }

        private void DrawEditToggle(MeshSectionGrid grid, string onLabel, string offLabel)
        {
            EditorGUILayout.BeginHorizontal();
            bool editMode = GUILayout.Toggle(s_editMode, s_editMode ? onLabel : offLabel, "Button", GUILayout.Height(26));
            if (editMode != s_editMode)
            {
                s_editMode = editMode;
                if (!editMode) ZoneShortcuts.Release();
                SceneView.RepaintAll();
            }

            if (GUILayout.Button("Вид сверху", GUILayout.Height(26), GUILayout.Width(110)))
            {
                TopView(grid);
            }

            EditorGUILayout.EndHorizontal();
        }

        private void DrawSnapField(MeshSectionGrid grid)
        {
            EditorGUI.BeginChangeCheck();
            float snap = EditorGUILayout.FloatField(new GUIContent("Привязка, м", "Шаг округления положения полос и точек. 0 — без привязки."), grid.snapStep);
            if (EditorGUI.EndChangeCheck())
            {
                Undo.RecordObject(grid, "Section grid snap");
                grid.snapStep = Mathf.Max(0f, snap);
            }
        }

        private void DrawGridSection(MeshSectionGrid grid, GridAnalysis analysis)
        {
            EditorGUILayout.LabelField("Сетка секций", EditorStyles.boldLabel);
            DrawEditToggle(grid, "● Рисование сетки: вкл", "Рисовать сетку");

            if (s_editMode)
            {
                EditorGUILayout.HelpBox(
                    "В Scene view:\n" +
                    "«+» над сеткой — добавить вертикальную полосу посередине колонки\n" +
                    "«+» слева от сетки — добавить горизонтальную полосу посередине ряда\n" +
                    "«×» у полосы — удалить её\n" +
                    "Тащите точку на полосе — двигать её",
                    MessageType.Info);
            }

            DrawSnapField(grid);

            EditorGUILayout.BeginHorizontal();
            EditorGUI.BeginChangeCheck();
            float step = EditorGUILayout.FloatField(new GUIContent("Равномерный шаг, м"), grid.uniformStep);
            if (EditorGUI.EndChangeCheck())
            {
                Undo.RecordObject(grid, "Section grid step");
                grid.uniformStep = Mathf.Max(1f, step);
            }

            if (GUILayout.Button("Разбить", GUILayout.Width(80)))
            {
                Undo.RecordObject(grid, "Uniform section grid");
                FillUniform(grid);
                MarkDirty();
            }

            EditorGUILayout.EndHorizontal();

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button(new GUIContent("Область по объектам", "Подогнать область сетки под все подходящие объекты сцены.")))
            {
                Undo.RecordObject(grid, "Fit section grid area");
                FitArea(grid, analysis);
                MarkDirty();
            }

            if (GUILayout.Button("Очистить линии"))
            {
                Undo.RecordObject(grid, "Clear section grid");
                grid.xLines.Clear();
                grid.zLines.Clear();
                MarkDirty();
            }

            EditorGUILayout.EndHorizontal();

            int cellsTotal = (grid.XBoundaries().Length - 1) * (grid.ZBoundaries().Length - 1);
            EditorGUILayout.LabelField(
                $"Область {grid.area.size.x:0}×{grid.area.size.z:0} м · линий X: {grid.xLines.Count}, Z: {grid.zLines.Count} · ячеек: {cellsTotal} (с объектами: {analysis.cells.Count})",
                EditorStyles.miniLabel);
        }

        private void DrawZonesSection(MeshSectionGrid grid, GridAnalysis analysis)
        {
            EditorGUILayout.LabelField($"Зоны ({grid.zones.Count})", EditorStyles.boldLabel);
            DrawEditToggle(grid, "● Редактирование зон: вкл", "Редактировать зоны");

            if (s_editMode)
            {
                string moveKey = ZoneShortcuts.MoveKeyLabel;
                EditorGUILayout.HelpBox(
                    "В Scene view:\n" +
                    "Тащите точку — менять форму зоны\n" +
                    "Ctrl + клик по краю зоны — добавить точку\n" +
                    "Shift + клик по точке — удалить точку (минимум 3)\n" +
                    $"Держите {moveKey} и тащите — двигать всю зону\n" +
                    "(клавишу можно сменить: Edit ▸ Shortcuts ▸ Mesh Section Baker)",
                    MessageType.Info);
            }

            DrawSnapField(grid);

            if (GUILayout.Button(new GUIContent("Добавить зону", "Прямоугольная зона в центре Scene view — дальше меняйте её форму точками."), GUILayout.Height(24)))
            {
                AddZone(grid);
            }

            if (grid.zones.Count == 0)
            {
                EditorGUILayout.HelpBox("Зон пока нет. Добавьте зону и растяните её по нужному участку.", MessageType.None);
                return;
            }

            var countByZone = new Dictionary<int, int>();
            foreach (SectionCell cell in analysis.cells) countByZone[cell.ix] = cell.units.Count;

            int remove = -1;
            _zoneScroll = EditorGUILayout.BeginScrollView(_zoneScroll, GUILayout.Height(Mathf.Min(200, grid.zones.Count * 22 + 6)));
            for (int i = 0; i < grid.zones.Count; i++)
            {
                SectionZone zone = grid.zones[i];
                EditorGUILayout.BeginHorizontal();

                EditorGUI.BeginChangeCheck();
                string name = EditorGUILayout.TextField(zone.name, GUILayout.MinWidth(80));
                if (EditorGUI.EndChangeCheck())
                {
                    Undo.RecordObject(grid, "Rename zone");
                    zone.name = name;
                    MarkDirty();
                }

                countByZone.TryGetValue(i, out int objects);
                GUILayout.Label($"точек {zone.points.Count} · объектов {objects}", EditorStyles.miniLabel, GUILayout.Width(140));

                if (GUILayout.Button("Показать", EditorStyles.miniButtonLeft, GUILayout.Width(66)))
                {
                    FrameZone(grid, zone);
                }

                if (GUILayout.Button("×", EditorStyles.miniButtonRight, GUILayout.Width(22)))
                {
                    remove = i;
                }

                EditorGUILayout.EndHorizontal();
            }

            EditorGUILayout.EndScrollView();

            if (remove >= 0)
            {
                Undo.RecordObject(grid, "Remove zone");
                grid.zones.RemoveAt(remove);
                MarkDirty();
            }

            if (analysis.outsideZonesCount > 0)
            {
                EditorGUILayout.HelpBox($"Вне всех зон: {analysis.outsideZonesCount} объектов — они не будут сшиты.", MessageType.None);
            }
        }

        private void DrawSelectionSection(MeshSectionGrid grid, GridAnalysis analysis)
        {
            EditorGUILayout.LabelField($"Секции из выделения ({grid.manualSections.Count})", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "«+ Добавить секцию» → выделите объекты в сцене → «Добавить выделенные».\n" +
                "«Редактировать» у секции — объекты этой секции подсвечиваются жёлтым, их можно добавлять и убирать.\n" +
                "Та же панель есть в Scene view (оверлей «Mesh Sections»). Инспектор можно закрепить замком.",
                MessageType.Info);

            SelectionSectionsTool.DrawSectionsGUI(grid, true);

            if (analysis.duplicateObjectCount > 0)
            {
                EditorGUILayout.HelpBox($"Рендеров, попавших в несколько секций: {analysis.duplicateObjectCount} — учтены только в первой из них.", MessageType.Warning);
            }
        }

        private void DrawSettingsSection(MeshSectionGrid grid)
        {
            EditorGUILayout.LabelField("Параметры запекания", EditorStyles.boldLabel);

            serializedObject.Update();
            EditorGUI.BeginChangeCheck();
            if (grid.partitionMode != PartitionMode.Selection)
            {
                EditorGUILayout.PropertyField(serializedObject.FindProperty(nameof(MeshSectionGrid.scope)),
                    new GUIContent("Что сшивать", "Вся сцена — все подходящие объекты сцены.\nТолько внутри объектов — только то, что лежит внутри объектов из списка ниже."));
                if (grid.scope == BakeScope.InsideObjects)
                {
                    EditorGUILayout.PropertyField(serializedObject.FindProperty(nameof(MeshSectionGrid.scopeRoots)),
                        new GUIContent("Объекты-корни", "Перетащите сюда объекты сцены. Сшиваются только их дочерние объекты (и они сами)."), true);
                }
            }

            if (EditorGUI.EndChangeCheck())
            {
                serializedObject.ApplyModifiedProperties();

                // With nothing drawn or baked yet, follow the new content automatically.
                if (grid.xLines.Count == 0 && grid.zLines.Count == 0 && grid.zones.Count == 0 && grid.bakedSections.Count == 0)
                {
                    FitArea(grid, SectionAnalyzer.Analyze(grid));
                }

                MarkDirty();
            }

            EditorGUI.BeginChangeCheck();

            bool onlyStatic = EditorGUILayout.Toggle(new GUIContent("Только Static объекты", "Сшиваются только объекты с флагом Batching Static — подвижные объекты сшивать нельзя."), grid.onlyStatic);
            var mode = (SourceObjectMode)EditorGUILayout.EnumPopup(
                new GUIContent("Исходные объекты", "Убрать со сцены — объекты удаляются из сцены (не грузятся ни в редакторе, ни в билде); их копия хранится в префабе Generated/…/Sources, при распекании они создаются заново на тех же местах. Объекты, на которые ссылаются другие объекты сцены, остаются (выключаются рендеры).\nОставить, выключить рендеры — объекты остаются, выключаются только их MeshRenderer."),
                grid.sourceMode);
            bool transfer = grid.transferColliders;
            if (mode == SourceObjectMode.RemoveObjects)
            {
                transfer = EditorGUILayout.Toggle(
                    new GUIContent("Переносить MeshCollider", "Коллизия удаляемых объектов сшивается в один MeshCollider на секцию. Если выключено — объекты с коллайдерами не удаляются, а только прячут рендеры."),
                    grid.transferColliders);
            }

            if (EditorGUI.EndChangeCheck())
            {
                Undo.RecordObject(grid, "Section bake settings");
                grid.onlyStatic = onlyStatic;
                grid.sourceMode = mode;
                grid.transferColliders = transfer;
                MarkDirty();
            }
        }

        private void DrawCellsSection(MeshSectionGrid grid, GridAnalysis analysis)
        {
            EditorGUILayout.LabelField("Секции к запеканию", EditorStyles.boldLabel);

            foreach (string warning in analysis.scopeWarnings)
            {
                EditorGUILayout.HelpBox(warning, MessageType.Warning);
            }

            foreach (KeyValuePair<string, int> skipped in analysis.skippedRenderers)
            {
                EditorGUILayout.HelpBox($"Не сшиваются рендеров: {skipped.Value} — {skipped.Key}", MessageType.None);
            }

            if (grid.sourceMode == SourceObjectMode.RemoveObjects && analysis.keepReasons.Count > 0)
            {
                string reasons = string.Join("\n", analysis.keepReasons.Select(p => $"  {p.Value} — {p.Key}"));
                EditorGUILayout.HelpBox("Останутся на сцене (выключатся только рендеры), т.к. содержат что-то кроме геометрии:\n" + reasons, MessageType.Info);
            }

            if (analysis.cells.Count == 0)
            {
                EditorGUILayout.HelpBox("Нет объектов для сшивания.", MessageType.None);
                return;
            }

            int objects = analysis.cells.Sum(c => c.units.Count);
            int renderGroupsEstimate = analysis.cells.Sum(c => c.materials.Count);
            EditorGUILayout.HelpBox(
                $"Сейчас: {objects} объектов, {analysis.rendererCount} рендеров, {analysis.submeshCount} сабмешей.\n" +
                $"После: {analysis.cells.Count} секций, ≈{renderGroupsEstimate} сабмешей (по материалу на секцию).",
                MessageType.Info);

            using (new EditorGUI.DisabledScope(EditorApplication.isPlaying))
            {
                if (GUILayout.Button($"Запечь все секции ({analysis.cells.Count})", GUILayout.Height(30)))
                {
                    BakeCells(grid, analysis.cells.ToList());
                }
            }

            _cellScroll = EditorGUILayout.BeginScrollView(_cellScroll, GUILayout.Height(Mathf.Min(240, analysis.cells.Count * 24 + 6)));
            foreach (SectionCell cell in analysis.cells)
            {
                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.LabelField(
                    $"{cell.Name} · объектов {cell.units.Count} (удалится {cell.RemovableCount}) · мат. {cell.materials.Count} · тр. {cell.triangleCount / 1000f:0.#}k",
                    EditorStyles.miniLabel);
                if (GUILayout.Button("Показать", EditorStyles.miniButtonLeft, GUILayout.Width(66)))
                {
                    FrameRect(grid, cell.min, cell.max);
                }

                using (new EditorGUI.DisabledScope(EditorApplication.isPlaying))
                {
                    if (GUILayout.Button("Запечь", EditorStyles.miniButtonRight, GUILayout.Width(60)))
                    {
                        BakeCells(grid, new List<SectionCell> { cell });
                    }
                }

                EditorGUILayout.EndHorizontal();
            }

            EditorGUILayout.EndScrollView();
        }

        private void DrawBakedSection(MeshSectionGrid grid)
        {
            EditorGUILayout.LabelField($"Запечённые секции ({grid.bakedSections.Count})", EditorStyles.boldLabel);
            if (grid.bakedSections.Count == 0) return;

            if (grid.sourcesHolder == null && grid.bakedSections.Any(s => s.removed.Count > 0 && string.IsNullOrEmpty(s.sourcesPrefabPath)))
            {
                EditorGUILayout.HelpBox("Контейнер с исходными объектами удалён — удалённые объекты вернуть не получится.", MessageType.Error);
            }

            List<string> lostStores = grid.bakedSections
                .Where(s => !string.IsNullOrEmpty(s.sourcesPrefabPath) && s.sourcesPrefab == null)
                .Select(s => s.name).ToList();
            if (lostStores.Count > 0)
            {
                EditorGUILayout.HelpBox($"Не найден файл с исходными объектами ({string.Join(", ", lostStores)}) — удалённые объекты этих секций вернуть не получится.", MessageType.Error);
            }

            List<BakedSection> legacy = grid.bakedSections.Where(SourceStore.IsLegacy).ToList();
            if (legacy.Count > 0 && grid.sourcesHolder != null)
            {
                EditorGUILayout.HelpBox($"Секций, запечённых прежней версией: {legacy.Count}. Их исходные объекты ({legacy.Sum(s => s.removed.Count(r => r.gameObject != null))}) " +
                                        "лежат в скрытом контейнере в сцене. Перенесите их в хранилище — они удалятся со сцены, распекание вернёт их как обычно.", MessageType.Warning);
                using (new EditorGUI.DisabledScope(EditorApplication.isPlaying))
                {
                    if (GUILayout.Button("Удалить исходники со сцены (перенести в хранилище)"))
                    {
                        int moved = SourceStore.MigrateLegacy(grid);
                        MarkDirty();
                        SceneView.lastActiveSceneView?.ShowNotification(new GUIContent($"Перенесено объектов: {moved}"));
                        GUIUtility.ExitGUI();
                    }
                }
            }

            using (new EditorGUI.DisabledScope(EditorApplication.isPlaying))
            {
                GUI.backgroundColor = new Color(1f, 0.65f, 0.6f);
                if (GUILayout.Button($"Распечь все ({grid.bakedSections.Count})", GUILayout.Height(28)))
                {
                    UnbakeSections(grid, grid.bakedSections.ToList());
                }

                GUI.backgroundColor = Color.white;
            }

            _bakedScroll = EditorGUILayout.BeginScrollView(_bakedScroll, GUILayout.Height(Mathf.Min(240, grid.bakedSections.Count * 24 + 6)));
            foreach (BakedSection section in grid.bakedSections.ToList())
            {
                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.LabelField(
                    $"✔ {section.name} · объектов {section.sourceObjectCount} (удалено {section.removed.Count}, скрыто рендеров {section.disabledRenderers.Count})",
                    EditorStyles.miniLabel);
                if (GUILayout.Button("Выделить", EditorStyles.miniButtonLeft, GUILayout.Width(66)) && section.output != null)
                {
                    Selection.activeGameObject = section.output;
                    EditorGUIUtility.PingObject(section.output);
                }

                using (new EditorGUI.DisabledScope(EditorApplication.isPlaying))
                {
                    if (GUILayout.Button("Распечь", EditorStyles.miniButtonRight, GUILayout.Width(60)))
                    {
                        UnbakeSections(grid, new List<BakedSection> { section });
                    }
                }

                EditorGUILayout.EndHorizontal();
            }

            EditorGUILayout.EndScrollView();
        }

        private void BakeCells(MeshSectionGrid grid, List<SectionCell> cells)
        {
            int baked = SectionBakeRunner.Bake(grid, cells);
            MarkDirty();
            SceneView.lastActiveSceneView?.ShowNotification(new GUIContent($"Запечено секций: {baked}"));
            GUIUtility.ExitGUI();
        }

        private void UnbakeSections(MeshSectionGrid grid, List<BakedSection> sections)
        {
            int count = SectionBakeRunner.Unbake(grid, sections);
            MarkDirty();
            SceneView.lastActiveSceneView?.ShowNotification(new GUIContent($"Распечено секций: {count}"));
            GUIUtility.ExitGUI();
        }

        // ------------------------------------------------------------------ Scene view

        private void OnSceneGUI()
        {
            MeshSectionGrid grid = Grid;
            InitStyles();
            float y = grid.DrawHeight;

            // Selection mode draws its own section labels/highlight (SelectionSectionsTool), even when the grid isn't selected.
            bool selectionMode = grid.partitionMode == PartitionMode.Selection;
            foreach (SectionCell cell in selectionMode ? Enumerable.Empty<SectionCell>() : Analysis.cells)
            {
                Vector2 center = cell.polygon.Count >= 3 ? Centroid(cell.polygon) : (cell.min + cell.max) * 0.5f;
                Handles.Label(new Vector3(center.x, y, center.y), $"{cell.Name}\n{cell.units.Count} об. · {cell.materials.Count} мат.", _cellLabel);
            }

            foreach (BakedSection section in grid.bakedSections)
            {
                Vector2 center = section.polygon != null && section.polygon.Count >= 3 ? Centroid(section.polygon) : (section.min + section.max) * 0.5f;
                Handles.Label(new Vector3(center.x, y, center.y), "✔ " + section.name, _bakedLabel);
            }

            if (!s_editMode || selectionMode) return;

            if (grid.partitionMode == PartitionMode.Grid)
            {
                DrawHint("Сетка: «+» — добавить полосу · «×» — удалить · точки — двигать", false, grid);
                DrawLineHandles(grid, y);
                DrawLineButtons(grid, y);
            }
            else
            {
                DrawHint($"Зоны: Ctrl+клик по краю — точка · Shift+клик — удалить точку · держать {ZoneShortcuts.MoveKeyLabel} — двигать зону", true, grid);
                DrawZoneEditing(grid, y);
            }
        }

        // ---------------- zones

        private void DrawZoneEditing(MeshSectionGrid grid, float y)
        {
            int controlId = GUIUtility.GetControlID(s_zoneControlHash, FocusType.Passive);
            Event e = Event.current;
            bool moveHeld = ZoneShortcuts.MoveHeld;

            // Take plain clicks only while a modifier / the move key is held, so ordinary clicks
            // still select scene objects.
            if (e.type == EventType.Layout && (e.control || e.command || e.shift || moveHeld))
            {
                HandleUtility.AddDefaultControl(controlId);
            }

            // Modifier clicks are handled BEFORE the point handles so they win over a handle grab.
            if (e.type == EventType.MouseDown && e.button == 0 && !e.alt)
            {
                if (moveHeld) BeginZoneDrag(grid, y, controlId, e);
                else if (e.shift) RemovePointUnderMouse(grid, y, e);
                else if (e.control || e.command) InsertPointOnEdge(grid, y, e);
            }

            if (GUIUtility.hotControl == controlId && _dragZone >= 0)
            {
                if (e.type == EventType.MouseDrag)
                {
                    if (TryPlanePoint(e.mousePosition, y, out Vector3 point) && _dragZone < grid.zones.Count)
                    {
                        Vector3 delta = point - _dragLast;
                        List<Vector2> points = grid.zones[_dragZone].points;
                        for (int i = 0; i < points.Count; i++) points[i] += new Vector2(delta.x, delta.z);
                        _dragLast = point;
                        MarkDirty();
                    }

                    e.Use();
                }
                else if (e.type == EventType.MouseUp)
                {
                    SnapZone(grid, _dragZone);
                    GUIUtility.hotControl = 0;
                    _dragZone = -1;
                    MarkDirty();
                    e.Use();
                }
            }

            for (int z = 0; z < grid.zones.Count; z++)
            {
                List<Vector2> points = grid.zones[z].points;
                if (points.Count < 2) continue;

                bool dragging = z == _dragZone;
                Handles.color = dragging || moveHeld ? s_zoneMoveColor : s_zoneColor;
                var outline = new Vector3[points.Count + 1];
                for (int i = 0; i < points.Count; i++) outline[i] = new Vector3(points[i].x, y, points[i].y);
                outline[points.Count] = outline[0];
                Handles.DrawAAPolyLine(dragging ? 5f : 3f, outline);

                Handles.color = s_zoneColor;
                for (int i = 0; i < points.Count; i++)
                {
                    var position = new Vector3(points[i].x, y, points[i].y);
                    float size = HandleUtility.GetHandleSize(position) * 0.07f;
                    EditorGUI.BeginChangeCheck();
                    Vector3 moved = Handles.FreeMoveHandle(position, size, Vector3.zero, Handles.DotHandleCap);
                    if (EditorGUI.EndChangeCheck())
                    {
                        Undo.RecordObject(grid, "Move zone point");
                        points[i] = new Vector2(Snap(grid, moved.x), Snap(grid, moved.z));
                        MarkDirty();
                    }
                }
            }
        }

        private void BeginZoneDrag(MeshSectionGrid grid, float y, int controlId, Event e)
        {
            if (!TryPlanePoint(e.mousePosition, y, out Vector3 point)) return;

            int zone = SectionAnalyzer.FindZone(grid, new Vector2(point.x, point.z));
            if (zone < 0 && TryPickEdge(grid, y, e.mousePosition, point, out int edgeZone, out _, out _)) zone = edgeZone;
            if (zone < 0) return;

            Undo.RecordObject(grid, "Move zone");
            _dragZone = zone;
            _dragLast = point;
            GUIUtility.hotControl = controlId;
            e.Use();
        }

        private void RemovePointUnderMouse(MeshSectionGrid grid, float y, Event e)
        {
            if (!TryPickPoint(grid, y, e.mousePosition, out int zone, out int index)) return;

            List<Vector2> points = grid.zones[zone].points;
            if (points.Count <= 3)
            {
                SceneView.lastActiveSceneView?.ShowNotification(new GUIContent("У зоны должно остаться минимум 3 точки"));
            }
            else
            {
                Undo.RecordObject(grid, "Remove zone point");
                points.RemoveAt(index);
                MarkDirty();
            }

            e.Use();
        }

        private void InsertPointOnEdge(MeshSectionGrid grid, float y, Event e)
        {
            if (!TryPlanePoint(e.mousePosition, y, out Vector3 point)) return;
            if (TryPickPoint(grid, y, e.mousePosition, out _, out _)) return; // clicked on a point, not an edge
            if (!TryPickEdge(grid, y, e.mousePosition, point, out int zone, out int edge, out Vector2 onEdge)) return;

            Undo.RecordObject(grid, "Add zone point");
            grid.zones[zone].points.Insert(edge + 1, onEdge);
            MarkDirty();
            e.Use();
        }

        private static bool TryPickPoint(MeshSectionGrid grid, float y, Vector2 mouse, out int zone, out int index)
        {
            zone = -1;
            index = -1;
            float best = PickDistance;
            for (int z = 0; z < grid.zones.Count; z++)
            {
                List<Vector2> points = grid.zones[z].points;
                for (int i = 0; i < points.Count; i++)
                {
                    float distance = Vector2.Distance(HandleUtility.WorldToGUIPoint(new Vector3(points[i].x, y, points[i].y)), mouse);
                    if (distance < best)
                    {
                        best = distance;
                        zone = z;
                        index = i;
                    }
                }
            }

            return zone >= 0;
        }

        /// <summary>Nearest zone edge within a few screen pixels of the mouse; <paramref name="onEdge"/> is the click projected onto it.</summary>
        private static bool TryPickEdge(MeshSectionGrid grid, float y, Vector2 mouse, Vector3 planePoint, out int zone, out int edge, out Vector2 onEdge)
        {
            zone = -1;
            edge = -1;
            onEdge = default;
            float best = PickDistance;

            for (int z = 0; z < grid.zones.Count; z++)
            {
                List<Vector2> points = grid.zones[z].points;
                for (int i = 0; i < points.Count; i++)
                {
                    Vector2 a = points[i];
                    Vector2 b = points[(i + 1) % points.Count];
                    float distance = HandleUtility.DistanceToLine(new Vector3(a.x, y, a.y), new Vector3(b.x, y, b.y));
                    if (distance >= best) continue;

                    best = distance;
                    zone = z;
                    edge = i;
                    Vector2 ab = b - a;
                    float t = ab.sqrMagnitude > 1e-6f ? Mathf.Clamp01(Vector2.Dot(new Vector2(planePoint.x, planePoint.z) - a, ab) / ab.sqrMagnitude) : 0f;
                    onEdge = a + ab * t;
                }
            }

            return zone >= 0;
        }

        private void AddZone(MeshSectionGrid grid)
        {
            SceneView view = SceneView.lastActiveSceneView;
            Vector3 center = view != null ? view.pivot : grid.area.center;
            float half = view != null ? Mathf.Clamp(view.size * 0.3f, 5f, 200f) : 20f;

            var zone = new SectionZone { name = UniqueZoneName(grid) };
            zone.points.Add(new Vector2(Snap(grid, center.x - half), Snap(grid, center.z - half)));
            zone.points.Add(new Vector2(Snap(grid, center.x + half), Snap(grid, center.z - half)));
            zone.points.Add(new Vector2(Snap(grid, center.x + half), Snap(grid, center.z + half)));
            zone.points.Add(new Vector2(Snap(grid, center.x - half), Snap(grid, center.z + half)));

            Undo.RecordObject(grid, "Add zone");
            grid.zones.Add(zone);
            s_editMode = true;
            EditorUtility.SetDirty(grid);
            MarkDirty();
        }

        private static string UniqueZoneName(MeshSectionGrid grid)
        {
            for (int i = grid.zones.Count + 1; ; i++)
            {
                string name = $"Zone_{i}";
                if (grid.zones.All(z => z.name != name) && grid.bakedSections.All(s => s.name != name)) return name;
            }
        }

        private static void SnapZone(MeshSectionGrid grid, int zone)
        {
            if (zone < 0 || zone >= grid.zones.Count || grid.snapStep <= 0f) return;

            // Snap the zone as a whole (by its first point) so its shape is kept exactly.
            List<Vector2> points = grid.zones[zone].points;
            Vector2 offset = new Vector2(Snap(grid, points[0].x), Snap(grid, points[0].y)) - points[0];
            for (int i = 0; i < points.Count; i++) points[i] += offset;
        }

        // ---------------- grid strips

        private void DrawLineHandles(MeshSectionGrid grid, float y)
        {
            Handles.color = new Color(1f, 0.85f, 0.2f, 1f);
            for (int i = 0; i < grid.xLines.Count; i++)
            {
                var position = new Vector3(grid.xLines[i], y, grid.area.center.z);
                float size = HandleUtility.GetHandleSize(position) * 0.08f;
                EditorGUI.BeginChangeCheck();
                Vector3 moved = Handles.FreeMoveHandle(position, size, Vector3.zero, Handles.DotHandleCap);
                if (EditorGUI.EndChangeCheck())
                {
                    Undo.RecordObject(grid, "Move section line");
                    grid.xLines[i] = Snap(grid, moved.x);
                    MarkDirty();
                }
            }

            for (int i = 0; i < grid.zLines.Count; i++)
            {
                var position = new Vector3(grid.area.center.x, y, grid.zLines[i]);
                float size = HandleUtility.GetHandleSize(position) * 0.08f;
                EditorGUI.BeginChangeCheck();
                Vector3 moved = Handles.FreeMoveHandle(position, size, Vector3.zero, Handles.DotHandleCap);
                if (EditorGUI.EndChangeCheck())
                {
                    Undo.RecordObject(grid, "Move section line");
                    grid.zLines[i] = Snap(grid, moved.z);
                    MarkDirty();
                }
            }
        }

        /// <summary>
        /// "+" buttons outside the grid edges (above it for vertical strips, left of it for
        /// horizontal ones) - one per column/row, inserting a strip in its middle - and a "×" at
        /// each existing strip.
        /// </summary>
        private void DrawLineButtons(MeshSectionGrid grid, float y)
        {
            float[] xs = grid.XBoundaries();
            float[] zs = grid.ZBoundaries();
            Bounds area = grid.area;
            var addVertical = new GUIContent("+", "Добавить вертикальную полосу посередине колонки");
            var addHorizontal = new GUIContent("+", "Добавить горизонтальную полосу посередине ряда");
            var remove = new GUIContent("×", "Удалить полосу");

            float? newX = null;
            float? newZ = null;
            int removeX = -1;
            int removeZ = -1;

            Handles.BeginGUI();

            for (int i = 0; i < xs.Length - 1; i++)
            {
                float mid = (xs[i] + xs[i + 1]) * 0.5f;
                if (SceneButton(new Vector3(mid, y, area.max.z), new Vector2(0f, -ButtonOffset), addVertical, s_addColor)) newX = mid;
            }

            for (int i = 0; i < zs.Length - 1; i++)
            {
                float mid = (zs[i] + zs[i + 1]) * 0.5f;
                if (SceneButton(new Vector3(area.min.x, y, mid), new Vector2(-ButtonOffset, 0f), addHorizontal, s_addColor)) newZ = mid;
            }

            for (int i = 0; i < grid.xLines.Count; i++)
            {
                if (SceneButton(new Vector3(grid.xLines[i], y, area.max.z), new Vector2(0f, -ButtonOffset), remove, s_removeColor)) removeX = i;
            }

            for (int i = 0; i < grid.zLines.Count; i++)
            {
                if (SceneButton(new Vector3(area.min.x, y, grid.zLines[i]), new Vector2(-ButtonOffset, 0f), remove, s_removeColor)) removeZ = i;
            }

            Handles.EndGUI();

            if (newX == null && newZ == null && removeX < 0 && removeZ < 0) return;

            Undo.RecordObject(grid, "Edit section grid");
            if (newX.HasValue) grid.xLines.Add(SnapInside(grid, newX.Value, xs));
            if (newZ.HasValue) grid.zLines.Add(SnapInside(grid, newZ.Value, zs));
            if (removeX >= 0) grid.xLines.RemoveAt(removeX);
            if (removeZ >= 0) grid.zLines.RemoveAt(removeZ);
            EditorUtility.SetDirty(grid);
            MarkDirty();
        }

        private static bool SceneButton(Vector3 worldPosition, Vector2 screenOffset, GUIContent content, Color color)
        {
            Vector2 point = HandleUtility.WorldToGUIPoint(worldPosition) + screenOffset;
            var rect = new Rect(point.x - 10f, point.y - 10f, 20f, 20f);

            Color previous = GUI.backgroundColor;
            GUI.backgroundColor = color;
            bool clicked = GUI.Button(rect, content, EditorStyles.miniButton);
            GUI.backgroundColor = previous;
            return clicked;
        }

        /// <summary>Snapped position, unless snapping would land it on an existing strip/edge.</summary>
        private static float SnapInside(MeshSectionGrid grid, float value, float[] boundaries)
        {
            float snapped = Snap(grid, value);
            return boundaries.Any(b => Mathf.Abs(b - snapped) < 0.01f) ? value : snapped;
        }

        /// <summary>One-line hint at the top center (the corners belong to Unity's own Scene view overlays), plus "+ Зона" in zone mode.</summary>
        private void DrawHint(string text, bool withAddZone, MeshSectionGrid grid)
        {
            SceneView view = SceneView.currentDrawingSceneView;
            float viewWidth = view != null ? view.position.width : 640f;

            if (s_hintStyle == null)
            {
                s_hintStyle = new GUIStyle(EditorStyles.helpBox) { wordWrap = false, alignment = TextAnchor.MiddleCenter, fontSize = 11 };
            }

            var hint = new GUIContent(text);
            Vector2 size = s_hintStyle.CalcSize(hint);
            float buttonWidth = withAddZone ? 64f : 0f;
            float x = (viewWidth - size.x - buttonWidth) * 0.5f;

            Handles.BeginGUI();
            GUI.Label(new Rect(x, 4f, size.x, size.y), hint, s_hintStyle);
            if (withAddZone)
            {
                Color previous = GUI.backgroundColor;
                GUI.backgroundColor = s_addColor;
                if (GUI.Button(new Rect(x + size.x + 4f, 4f, buttonWidth - 4f, size.y), "+ Зона"))
                {
                    AddZone(grid);
                }

                GUI.backgroundColor = previous;
            }

            Handles.EndGUI();
        }

        private void InitStyles()
        {
            if (_cellLabel != null) return;

            _cellLabel = new GUIStyle(EditorStyles.whiteMiniLabel) { alignment = TextAnchor.MiddleCenter, normal = { textColor = new Color(1f, 0.9f, 0.4f) } };
            _bakedLabel = new GUIStyle(EditorStyles.whiteBoldLabel) { alignment = TextAnchor.MiddleCenter, normal = { textColor = new Color(0.4f, 1f, 0.55f) } };
        }

        // ------------------------------------------------------------------ helpers

        private static bool TryPlanePoint(Vector2 mouse, float y, out Vector3 point)
        {
            Ray ray = HandleUtility.GUIPointToWorldRay(mouse);
            if (new Plane(Vector3.up, new Vector3(0f, y, 0f)).Raycast(ray, out float distance))
            {
                point = ray.GetPoint(distance);
                return true;
            }

            point = default;
            return false;
        }

        private static Vector2 Centroid(IList<Vector2> points)
        {
            Vector2 sum = Vector2.zero;
            foreach (Vector2 point in points) sum += point;
            return sum / points.Count;
        }

        private static float Snap(MeshSectionGrid grid, float value)
        {
            return grid.snapStep > 0f ? Mathf.Round(value / grid.snapStep) * grid.snapStep : value;
        }

        private static void FillUniform(MeshSectionGrid grid)
        {
            grid.xLines.Clear();
            grid.zLines.Clear();
            float step = Mathf.Max(1f, grid.uniformStep);
            for (float x = grid.area.min.x + step; x < grid.area.max.x - 0.01f; x += step) grid.xLines.Add(x);
            for (float z = grid.area.min.z + step; z < grid.area.max.z - 0.01f; z += step) grid.zLines.Add(z);
        }

        private static void FitArea(MeshSectionGrid grid, GridAnalysis analysis)
        {
            if (!analysis.contentBounds.HasValue) return;

            Bounds bounds = analysis.contentBounds.Value;
            bounds.Expand(new Vector3(2f, 0f, 2f));
            grid.area = bounds;
            EditorUtility.SetDirty(grid);
        }

        private static void TopView(MeshSectionGrid grid)
        {
            SceneView view = SceneView.lastActiveSceneView;
            if (view == null) return;

            float size = Mathf.Max(grid.area.size.x, grid.area.size.z) * 0.55f;
            view.LookAt(grid.area.center, Quaternion.Euler(90f, 0f, 0f), Mathf.Max(size, 10f), true);
            view.Repaint();
        }

        private static void FrameRect(MeshSectionGrid grid, Vector2 min, Vector2 max)
        {
            SceneView view = SceneView.lastActiveSceneView;
            if (view == null) return;

            Vector2 center = (min + max) * 0.5f;
            float size = Mathf.Max(max.x - min.x, max.y - min.y) * 0.6f;
            view.LookAt(new Vector3(center.x, grid.area.center.y, center.y), Quaternion.Euler(90f, 0f, 0f), Mathf.Max(size, 5f), true);
            view.Repaint();
        }

        private static void FrameZone(MeshSectionGrid grid, SectionZone zone)
        {
            if (zone.points.Count == 0) return;

            Vector2 min = zone.points[0];
            Vector2 max = zone.points[0];
            foreach (Vector2 point in zone.points)
            {
                min = Vector2.Min(min, point);
                max = Vector2.Max(max, point);
            }

            FrameRect(grid, min, max);
        }
    }
}
