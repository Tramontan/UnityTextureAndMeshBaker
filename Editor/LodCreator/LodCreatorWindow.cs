using System.Collections.Generic;
using System.Linq;
using TexturesBaker;
using UnityEditor;
using UnityEngine;

namespace LodCreator
{
    /// <summary>
    /// Tools/Создание LOD. Follows the scene selection: shows triangles/vertices and existing LOD
    /// levels of the selected object(s), recommended LOD settings (editable), and creates/removes
    /// LODs. Works with plain objects, prefab instances with atlas textures and baked mesh sections.
    /// </summary>
    public class LodCreatorWindow : EditorWindow
    {
        [SerializeField] private bool autoSettings = true;
        [SerializeField] private LodSettings settings = new LodSettings();

        private List<LodObjectInfo> _infos = new List<LodObjectInfo>();
        private HashSet<string> _atlasMaterials = new HashSet<string>();
        private HashSet<string> _bakedPrefabs = new HashSet<string>();
        private Vector2 _scroll;

        [MenuItem("Tools/Создание LOD")]
        public static void Open()
        {
            var window = GetWindow<LodCreatorWindow>("Создание LOD");
            window.minSize = new Vector2(360, 420);
            window.Show();
        }

        private void OnEnable()
        {
            RefreshBakeData();
            Refresh();
        }

        private void OnFocus()
        {
            RefreshBakeData();
            Refresh();
        }

        private void OnSelectionChange()
        {
            Refresh();
            Repaint();
        }

        private void OnHierarchyChange()
        {
            Refresh();
            Repaint();
        }

        private void RefreshBakeData()
        {
            List<BakeConfig> configs = BakeRegistry.LoadAll();
            _atlasMaterials = new HashSet<string>(configs.SelectMany(c => c.atlases).Select(a => a.materialPath));
            _bakedPrefabs = BakeRegistry.AllBakedPrefabGuids(configs);
        }

        private void Refresh()
        {
            _infos = Selection.gameObjects
                .Where(go => go != null && !EditorUtility.IsPersistent(go))
                .Select(go => LodObjectInfo.Build(go, _atlasMaterials, _bakedPrefabs))
                .ToList();

            if (autoSettings && _infos.Count > 0)
            {
                settings = LodBuilder.Recommend(_infos.OrderByDescending(i => i.triangles).First());
            }
        }

        private void OnGUI()
        {
            if (_infos.Count == 0)
            {
                EditorGUILayout.HelpBox("Выберите объект (или несколько) в сцене.", MessageType.Info);
                return;
            }

            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            if (_infos.Count == 1) DrawInfo(_infos[0]);
            else DrawSummary();

            EditorGUILayout.Space(8);
            DrawSettings();
            EditorGUILayout.EndScrollView();

            DrawButtons();
        }

        // ------------------------------------------------------------------ info

        private void DrawInfo(LodObjectInfo info)
        {
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField(info.root.name, EditorStyles.boldLabel);
            if (GUILayout.Button("Показать", EditorStyles.miniButton, GUILayout.Width(70))) EditorGUIUtility.PingObject(info.root);
            EditorGUILayout.EndHorizontal();

            var tags = new List<string>();
            if (info.kind == LodObjectKind.Section) tags.Add("секция «Запекание мешей» — LOD только для Render, коллизия не трогается");
            if (info.usesAtlas) tags.Add("текстуры в атласе — UV в LOD сохраняются точно");
            if (info.isBakedPrefab) tags.Add("запечённый префаб");
            foreach (string tag in tags) EditorGUILayout.LabelField("• " + tag, EditorStyles.wordWrappedMiniLabel);

            EditorGUILayout.HelpBox(
                $"Треугольников: {info.triangles:N0}\nВершин: {info.vertices:N0}\nРендереров: {info.renderers.Count} · размер {info.worldSize:0.#} м",
                MessageType.None);

            if (info.levels.Count > 0)
            {
                EditorGUILayout.LabelField($"LOD: {info.levels.Count} уровней ({info.lodGroupOrigin})", EditorStyles.boldLabel);
                int baseTris = Mathf.Max(1, info.levels[0].triangles);
                for (int i = 0; i < info.levels.Count; i++)
                {
                    LodLevelInfo level = info.levels[i];
                    string switchText = i < info.levels.Count - 1 ? $"до {level.height:P1} экрана" : level.height > 0f ? $"отсечение < {level.height:P1}" : "без отсечения";
                    EditorGUILayout.LabelField(
                        $"LOD{i}: {level.triangles:N0} тр. ({(float)level.triangles / baseTris:P0}) · {level.vertices:N0} верш. · {switchText}",
                        EditorStyles.miniLabel);
                }
            }
            else
            {
                EditorGUILayout.LabelField("LOD: нет", EditorStyles.miniLabel);
            }

            if (info.blockReason != null)
            {
                EditorGUILayout.HelpBox(info.blockReason, MessageType.Warning);
            }
        }

        private void DrawSummary()
        {
            EditorGUILayout.LabelField($"Выбрано объектов: {_infos.Count}", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                $"Треугольников всего: {_infos.Sum(i => i.triangles):N0}\n" +
                $"Можно создать LOD: {_infos.Count(i => i.CanCreate)} · можно удалить: {_infos.Count(i => i.CanRemove)} · недоступно: {_infos.Count(i => !i.CanCreate && !i.CanRemove)}",
                MessageType.None);

            foreach (LodObjectInfo info in _infos.Take(30))
            {
                string state = info.record != null ? $"LOD ×{info.levels.Count}" : info.lodGroup != null ? "свой LODGroup" : info.blockReason != null ? "недоступно" : "без LOD";
                EditorGUILayout.LabelField($"{info.root.name} · {info.triangles:N0} тр. · {state}", EditorStyles.miniLabel);
            }

            if (_infos.Count > 30) EditorGUILayout.LabelField($"… и ещё {_infos.Count - 30}", EditorStyles.miniLabel);
        }

        // ------------------------------------------------------------------ settings

        private void DrawSettings()
        {
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Настройки LOD", EditorStyles.boldLabel);
            if (GUILayout.Button(new GUIContent("Рекомендуемые", "Уровни по числу треугольников, отсечение по размеру объекта."), EditorStyles.miniButton, GUILayout.Width(110)))
            {
                autoSettings = true;
                Refresh();
            }

            EditorGUILayout.EndHorizontal();

            EditorGUILayout.LabelField(autoSettings ? "Сейчас: рекомендуемые для выбранного объекта" : "Сейчас: свои настройки", EditorStyles.miniLabel);

            EditorGUI.BeginChangeCheck();
            for (int i = 0; i < settings.ratios.Count; i++)
            {
                EditorGUILayout.BeginVertical(EditorStyles.helpBox);
                EditorGUILayout.LabelField($"LOD{i + 1}", EditorStyles.miniBoldLabel);
                settings.ratios[i] = EditorGUILayout.IntSlider(new GUIContent("Треугольников, %", "Доля треугольников исходного меша."),
                    Mathf.RoundToInt(settings.ratios[i] * 100f), 1, 95) / 100f;
                settings.transitions[i] = EditorGUILayout.Slider(new GUIContent($"Включать при высоте < %", $"LOD{i} переключается на LOD{i + 1}, когда объект занимает меньше этой доли высоты экрана."),
                    settings.transitions[i] * 100f, 0.1f, 100f) / 100f;
                EditorGUILayout.EndVertical();
            }

            EditorGUILayout.BeginHorizontal();
            using (new EditorGUI.DisabledScope(settings.ratios.Count >= LodBuilder.MaxLevels))
            {
                if (GUILayout.Button("+ уровень", EditorStyles.miniButtonLeft))
                {
                    float lastRatio = settings.ratios.Count > 0 ? settings.ratios[settings.ratios.Count - 1] : 1f;
                    float lastTransition = settings.transitions.Count > 0 ? settings.transitions[settings.transitions.Count - 1] : 1f;
                    settings.ratios.Add(Mathf.Max(0.01f, lastRatio * 0.5f));
                    settings.transitions.Add(Mathf.Max(0.001f, lastTransition * 0.5f));
                }
            }

            using (new EditorGUI.DisabledScope(settings.ratios.Count <= 1))
            {
                if (GUILayout.Button("− уровень", EditorStyles.miniButtonRight))
                {
                    settings.ratios.RemoveAt(settings.ratios.Count - 1);
                    settings.transitions.RemoveAt(settings.transitions.Count - 1);
                }
            }

            EditorGUILayout.EndHorizontal();

            settings.maxPixelError = EditorGUILayout.Slider(new GUIContent("Погрешность, пикс.",
                    "Насколько LOD может отличаться от оригинала (в пикселях экрана 1080p в момент переключения). " +
                    "Если заданный % треугольников даёт ошибку больше - упрощение останавливается раньше, чтобы тонкие детали не ломались. 0 - без ограничения."),
                settings.maxPixelError, 0f, 20f);

            settings.cull = EditorGUILayout.Toggle(new GUIContent("Отсекать вдали", "Последний LOD пропадает, когда объект занимает меньше заданной доли экрана."), settings.cull);
            if (settings.cull)
            {
                settings.cullHeight = EditorGUILayout.Slider("Отсечение при высоте < %", settings.cullHeight * 100f, 0.01f, 20f) / 100f;
            }

            if (EditorGUI.EndChangeCheck()) autoSettings = false;

            float[] heights = settings.BuildHeights();
            bool adjusted = false;
            for (int i = 0; i < settings.transitions.Count; i++)
            {
                if (Mathf.Abs(heights[i] - settings.transitions[i]) > 0.0001f) adjusted = true;
            }

            if (adjusted)
            {
                EditorGUILayout.HelpBox("Пороги должны убывать от LOD к LOD — при создании они будут скорректированы: " +
                                        string.Join(" → ", heights.Select(h => h.ToString("P1"))), MessageType.Info);
            }
        }

        // ------------------------------------------------------------------ buttons

        private void DrawButtons()
        {
            EditorGUILayout.Space(4);
            int creatable = _infos.Count(i => i.CanCreate);
            int removable = _infos.Count(i => i.CanRemove);

            EditorGUILayout.BeginHorizontal();
            using (new EditorGUI.DisabledScope(creatable == 0 || EditorApplication.isPlaying))
            {
                GUI.backgroundColor = new Color(0.55f, 1f, 0.6f);
                if (GUILayout.Button(_infos.Count > 1 ? $"Создать LOD ({creatable})" : "Создать LOD", GUILayout.Height(30)))
                {
                    int created = LodBuilder.CreateMany(_infos, settings);
                    ShowNotification(new GUIContent($"LOD созданы: {created}"));
                    Refresh();
                    GUIUtility.ExitGUI();
                }
            }

            using (new EditorGUI.DisabledScope(removable == 0 || EditorApplication.isPlaying))
            {
                GUI.backgroundColor = new Color(1f, 0.65f, 0.6f);
                if (GUILayout.Button(_infos.Count > 1 ? $"Удалить LOD ({removable})" : "Удалить LOD", GUILayout.Height(30)))
                {
                    int removed = LodBuilder.RemoveMany(_infos);
                    ShowNotification(new GUIContent($"LOD удалены: {removed}"));
                    Refresh();
                    GUIUtility.ExitGUI();
                }
            }

            GUI.backgroundColor = Color.white;
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.Space(4);
        }
    }
}
