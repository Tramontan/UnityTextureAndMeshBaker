using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace TexturesBaker
{
    /// <summary>
    /// Tools/Запекание текстур. "Запекание" tab: drop prefabs/models/folders, choose how many
    /// materials to end up with, preview the atlas layout, bake. "Запечённые" tab: every bake
    /// found in the project (via its bake_config.json), with its materials/prefabs and an unbake button.
    /// </summary>
    public class TexturesBakerWindow : EditorWindow
    {
        private static readonly int[] AtlasSizes = { 1024, 2048, 4096, 8192 };
        private static readonly string[] AtlasSizeLabels = { "1024", "2048", "4096", "8192" };
        private const string DefaultOutputRoot = "Assets/TexturesBaker/Bakes";

        [SerializeField] private List<GameObject> prefabs = new List<GameObject>();
        [SerializeField] private List<Material> userExcluded = new List<Material>();
        [SerializeField] private int materialCount = 3;
        [SerializeField] private int atlasSizeIndex = 2;
        [SerializeField] private int padding = 4;
        [SerializeField] private string bakeName = string.Empty;
        [SerializeField] private string outputRoot = DefaultOutputRoot;
        [SerializeField] private bool replaceInOpenScenes = true;
        [SerializeField] private bool forceSingleShader;
        [SerializeField] private Shader forcedShader;
        [SerializeField] private int tab;

        private PrefabAnalysis _analysis;
        private BakePlan _plan;
        private List<BakeConfig> _configs = new List<BakeConfig>();
        private bool _analysisDirty = true;
        private bool _planDirty = true;
        private bool _showPrefabs = true;
        private bool _showMaterials = true;
        private Vector2 _bakeScroll;
        private Vector2 _bakedScroll;
        private Vector2 _prefabScroll;
        private Vector2 _materialScroll;
        private readonly HashSet<string> _expandedConfigs = new HashSet<string>();
        private readonly HashSet<string> _expandedConfigPrefabs = new HashSet<string>();
        private GUIStyle _dropStyle;
        private GUIStyle _errorLabel;
        private GUIStyle _warningLabel;

        [MenuItem("Tools/Запекание текстур")]
        public static void Open()
        {
            var window = GetWindow<TexturesBakerWindow>("Запекание текстур");
            window.minSize = new Vector2(480, 560);
            window.Show();
        }

        private void OnEnable()
        {
            _analysisDirty = true;
            RefreshConfigs();
        }

        private void OnFocus() => RefreshConfigs();

        private void OnProjectChange()
        {
            _analysisDirty = true;
            RefreshConfigs();
        }

        private void RefreshConfigs()
        {
            _configs = BakeRegistry.LoadAll();
            Repaint();
        }

        private void OnGUI()
        {
            InitStyles();
            tab = GUILayout.Toolbar(tab, new[] { "Запекание", $"Запечённые ({_configs.Count})" }, GUILayout.Height(24));
            EditorGUILayout.Space(4);

            if (tab == 0) DrawBakeTab();
            else DrawBakedTab();
        }

        private void InitStyles()
        {
            if (_dropStyle != null) return;

            _dropStyle = new GUIStyle(EditorStyles.helpBox) { alignment = TextAnchor.MiddleCenter, fontSize = 12 };
            _errorLabel = new GUIStyle(EditorStyles.miniLabel) { wordWrap = true, normal = { textColor = new Color(1f, 0.45f, 0.4f) } };
            _warningLabel = new GUIStyle(EditorStyles.miniLabel) { wordWrap = true, normal = { textColor = new Color(1f, 0.8f, 0.35f) } };
        }

        // ------------------------------------------------------------------ Bake tab

        private void DrawBakeTab()
        {
            EnsureAnalysis();

            _bakeScroll = EditorGUILayout.BeginScrollView(_bakeScroll);
            DrawDropArea();
            DrawPrefabList();

            if (prefabs.Count > 0 && _analysis != null)
            {
                DrawMaterials();
                DrawSettings();
                EnsurePlan();
                DrawPreview();
            }

            EditorGUILayout.EndScrollView();
            DrawBakeButton();
        }

        private void EnsureAnalysis()
        {
            if (!_analysisDirty) return;

            prefabs.RemoveAll(p => p == null);
            _analysis = prefabs.Count > 0 ? BakeAnalyzer.Analyze(prefabs) : null;
            _analysisDirty = false;
            _planDirty = true;
        }

        private void EnsurePlan()
        {
            if (!_planDirty || _analysis == null) return;

            List<MaterialUsageInfo> included = IncludedMaterials();
            Shader shader = null;
            if (forceSingleShader && included.Count > 0)
            {
                shader = forcedShader != null
                    ? forcedShader
                    : included.GroupBy(i => i.material.shader).OrderByDescending(g => g.Count()).First().Key;
            }

            _plan = AtlasPlanner.Build(included, materialCount, AtlasSizes[atlasSizeIndex], padding, shader);
            if (shader != null && !MaterialProps.ShaderHasMainTexture(shader))
            {
                _plan.errors.Add($"У шейдера '{shader.name}' нет основной текстуры (_MainTex/_BaseMap).");
            }

            _planDirty = false;
        }

        private List<MaterialUsageInfo> IncludedMaterials()
        {
            return _analysis == null
                ? new List<MaterialUsageInfo>()
                : _analysis.materials.Where(i => i.atlasable && !userExcluded.Contains(i.material)).ToList();
        }

        private void DrawDropArea()
        {
            Rect area = GUILayoutUtility.GetRect(0, 56, GUILayout.ExpandWidth(true));
            GUI.Box(area, "Перетащите сюда префабы, модели (FBX), папки\nили объекты из сцены", _dropStyle);

            Event e = Event.current;
            if (area.Contains(e.mousePosition) && (e.type == EventType.DragUpdated || e.type == EventType.DragPerform))
            {
                DragAndDrop.visualMode = DragAndDropVisualMode.Copy;
                if (e.type == EventType.DragPerform)
                {
                    DragAndDrop.AcceptDrag();
                    AddPrefabs(DragAndDrop.objectReferences.SelectMany(ResolvePrefabs));
                }

                e.Use();
            }

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Добавить префабы из открытых сцен"))
            {
                AddPrefabsFromOpenScenes();
            }

            using (new EditorGUI.DisabledScope(prefabs.Count == 0))
            {
                if (GUILayout.Button("Очистить", GUILayout.Width(90)))
                {
                    prefabs.Clear();
                    userExcluded.Clear();
                    _analysisDirty = true;
                }
            }

            EditorGUILayout.EndHorizontal();
        }

        private void DrawPrefabList()
        {
            _showPrefabs = EditorGUILayout.Foldout(_showPrefabs, $"Префабы ({prefabs.Count})", true, EditorStyles.foldoutHeader);
            if (!_showPrefabs || prefabs.Count == 0) return;

            int remove = -1;
            _prefabScroll = EditorGUILayout.BeginScrollView(_prefabScroll, GUILayout.Height(Mathf.Min(180, prefabs.Count * 21 + 4)));
            for (int i = 0; i < prefabs.Count; i++)
            {
                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.ObjectField(prefabs[i], typeof(GameObject), false);
                if (GUILayout.Button("✕", GUILayout.Width(22))) remove = i;
                EditorGUILayout.EndHorizontal();
            }

            EditorGUILayout.EndScrollView();

            if (remove >= 0)
            {
                prefabs.RemoveAt(remove);
                _analysisDirty = true;
            }
        }

        private void DrawMaterials()
        {
            int includedCount = IncludedMaterials().Count;
            EditorGUILayout.Space(6);
            _showMaterials = EditorGUILayout.Foldout(_showMaterials,
                $"Материалы (в атлас: {includedCount} из {_analysis.materials.Count}) · рендереров: {_analysis.rendererCount}",
                true, EditorStyles.foldoutHeader);
            if (!_showMaterials) return;

            foreach (string warning in _analysis.warnings)
            {
                EditorGUILayout.HelpBox(warning, MessageType.Warning);
            }

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Включить все", EditorStyles.miniButtonLeft))
            {
                userExcluded.Clear();
                _planDirty = true;
            }

            if (GUILayout.Button("Выключить все", EditorStyles.miniButtonRight))
            {
                userExcluded = _analysis.materials.Select(i => i.material).ToList();
                _planDirty = true;
            }

            EditorGUILayout.EndHorizontal();

            _materialScroll = EditorGUILayout.BeginScrollView(_materialScroll, GUILayout.Height(Mathf.Min(260, _analysis.materials.Count * 42 + 4)));
            foreach (MaterialUsageInfo info in _analysis.materials)
            {
                DrawMaterialRow(info);
            }

            EditorGUILayout.EndScrollView();
        }

        private void DrawMaterialRow(MaterialUsageInfo info)
        {
            EditorGUILayout.BeginHorizontal(EditorStyles.helpBox);

            bool included = info.atlasable && !userExcluded.Contains(info.material);
            using (new EditorGUI.DisabledScope(!info.atlasable))
            {
                bool toggled = EditorGUILayout.Toggle(included, GUILayout.Width(16));
                if (toggled != included)
                {
                    if (toggled) userExcluded.Remove(info.material);
                    else userExcluded.Add(info.material);
                    _planDirty = true;
                }
            }

            Rect thumb = GUILayoutUtility.GetRect(32, 32, GUILayout.Width(32), GUILayout.Height(32));
            DrawTexture(thumb, info.texture, info.tint);

            EditorGUILayout.BeginVertical();
            EditorGUILayout.LabelField(info.material.name, EditorStyles.boldLabel);
            string size = info.texture != null ? $"{info.texture.width}×{info.texture.height}" : "без текстуры";
            EditorGUILayout.LabelField($"{info.material.shader.name} · {size} · сабмешей: {info.submeshUsages} · префабов: {info.prefabs.Count}", EditorStyles.miniLabel);
            if (!info.atlasable)
            {
                EditorGUILayout.LabelField("Не объединяется: " + info.reason, _errorLabel);
            }
            else if (!string.IsNullOrEmpty(info.droppedTextures))
            {
                EditorGUILayout.LabelField("Доп. текстуры не переносятся в атлас: " + info.droppedTextures, _warningLabel);
            }

            EditorGUILayout.EndVertical();

            if (GUILayout.Button(EditorGUIUtility.IconContent("d_Search Icon", "Показать материал"), GUILayout.Width(26), GUILayout.Height(22)))
            {
                EditorGUIUtility.PingObject(info.material);
            }

            EditorGUILayout.EndHorizontal();
        }

        private void DrawSettings()
        {
            EditorGUILayout.Space(6);
            EditorGUILayout.LabelField("Настройки", EditorStyles.boldLabel);

            int maxCount = Mathf.Max(1, IncludedMaterials().Count);
            EditorGUI.BeginChangeCheck();
            materialCount = EditorGUILayout.IntSlider(
                new GUIContent("Материалов на выходе", "Сколько материалов (и атласов) должно получиться из выбранных исходных."),
                Mathf.Clamp(materialCount, 1, maxCount), 1, maxCount);
            atlasSizeIndex = EditorGUILayout.Popup(
                new GUIContent("Макс. размер атласа", "Если текстуры не влезают — они равномерно уменьшаются внутри атласа."),
                atlasSizeIndex, AtlasSizeLabels);
            padding = EditorGUILayout.IntSlider(
                new GUIContent("Отступ, px", "Поля вокруг каждой текстуры (заполняются краевыми пикселями) — защита от просачивания соседей на мипах."),
                padding, 0, 16);
            forceSingleShader = EditorGUILayout.Toggle(
                new GUIContent("Приводить к одному шейдеру", "Разрешить объединять материалы с разными шейдерами в один. Уникальные свойства шейдеров (cutout, specular...) будут потеряны."),
                forceSingleShader);
            if (forceSingleShader)
            {
                EditorGUI.indentLevel++;
                forcedShader = (Shader)EditorGUILayout.ObjectField(new GUIContent("Шейдер", "Пусто — самый частый среди выбранных материалов."), forcedShader, typeof(Shader), false);
                EditorGUI.indentLevel--;
            }

            if (EditorGUI.EndChangeCheck())
            {
                _planDirty = true;
            }

            bakeName = EditorGUILayout.TextField(new GUIContent("Имя запекания", "Пусто — будет Bake_<дата_время>."), bakeName);

            EditorGUILayout.BeginHorizontal();
            outputRoot = EditorGUILayout.TextField("Папка результатов", outputRoot);
            if (GUILayout.Button("…", GUILayout.Width(26)))
            {
                string picked = EditorUtility.OpenFolderPanel("Папка результатов", Application.dataPath, string.Empty);
                string dataPath = Application.dataPath.Replace('\\', '/');
                if (!string.IsNullOrEmpty(picked) && picked.Replace('\\', '/').StartsWith(dataPath))
                {
                    outputRoot = "Assets" + picked.Replace('\\', '/').Substring(dataPath.Length);
                    GUI.FocusControl(null);
                }
            }

            EditorGUILayout.EndHorizontal();

            replaceInOpenScenes = EditorGUILayout.Toggle(
                new GUIContent("Заменить в открытых сценах", "Экземпляры исходных префабов в открытых сценах заменяются на запечённые (Ctrl+Z отменяет замену)."),
                replaceInOpenScenes);
        }

        private void DrawPreview()
        {
            EditorGUILayout.Space(6);
            EditorGUILayout.LabelField("Превью атласов", EditorStyles.boldLabel);

            foreach (string error in _plan.errors)
            {
                EditorGUILayout.HelpBox(error, MessageType.Error);
            }

            if (_plan.atlases.Count == 0) return;

            int sourceMaterials = _plan.atlases.Sum(a => a.placed.Sum(p => p.item.materials.Count));
            string countNote = _plan.atlases.Count < materialCount ? $" (текстур меньше, чем запрошено материалов: {materialCount})" : string.Empty;
            EditorGUILayout.HelpBox(
                $"{sourceMaterials} материалов → {_plan.atlases.Count}{countNote}.\n" +
                $"Пикселей текстур: {_plan.sourceTexels / 1e6:0.#}M → {_plan.atlasTexels / 1e6:0.#}M",
                MessageType.Info);

            for (int i = 0; i < _plan.atlases.Count; i++)
            {
                AtlasPlan atlas = _plan.atlases[i];
                EditorGUILayout.LabelField(
                    $"Материал {i + 1} · {atlas.shader.name} · {atlas.width}×{atlas.height} · масштаб {atlas.scale:P0} · текстур: {atlas.placed.Count}",
                    EditorStyles.miniBoldLabel);

                float width = Mathf.Min(position.width - 40f, 380f);
                float height = width * atlas.height / atlas.width;
                Rect rect = GUILayoutUtility.GetRect(width, height, GUILayout.Width(width), GUILayout.Height(height));
                DrawAtlasPreview(rect, atlas);
                EditorGUILayout.Space(8);
            }
        }

        private static void DrawAtlasPreview(Rect rect, AtlasPlan atlas)
        {
            EditorGUI.DrawRect(rect, new Color(0.1f, 0.1f, 0.1f));
            foreach (PlacedItem placed in atlas.placed)
            {
                RectInt content = placed.contentRect;
                var cell = new Rect(
                    rect.x + rect.width * content.x / atlas.width,
                    rect.y + rect.height * (atlas.height - content.yMax) / atlas.height,
                    rect.width * content.width / atlas.width,
                    rect.height * content.height / atlas.height);

                DrawTexture(cell, placed.item.texture, placed.item.tint);
                DrawOutline(cell, new Color(1f, 1f, 1f, 0.35f));

                string tooltip = $"{placed.item.DisplayName}\n{placed.item.width}×{placed.item.height} → {content.width}×{content.height}";
                GUI.Label(cell, new GUIContent(string.Empty, tooltip));
            }
        }

        private void DrawBakeButton()
        {
            EditorGUILayout.Space(4);
            bool canBake = prefabs.Count > 0 && _plan != null && _plan.errors.Count == 0 && _plan.atlases.Count > 0;
            using (new EditorGUI.DisabledScope(!canBake))
            {
                if (GUILayout.Button("Объединить", GUILayout.Height(34)))
                {
                    Bake();
                }
            }

            EditorGUILayout.Space(4);
        }

        private void Bake()
        {
            EnsurePlan();
            string root = outputRoot.Replace('\\', '/').TrimEnd('/');
            if (!root.StartsWith("Assets"))
            {
                EditorUtility.DisplayDialog("TexturesBaker", "Папка результатов должна находиться внутри Assets.", "OK");
                return;
            }

            if (replaceInOpenScenes && MeshSectionsGuard.BlockIfSectionsBaked("Замена префабов в сценах"))
            {
                return;
            }

            string name = string.IsNullOrWhiteSpace(bakeName) ? $"Bake_{DateTime.Now:yyyyMMdd_HHmmss}" : bakeName.Trim();
            string message =
                $"Материалов на выходе: {_plan.atlases.Count}\n" +
                $"Исходных префабов: {prefabs.Count}\n" +
                $"Папка: {root}/{name}\n\n" +
                (replaceInOpenScenes ? "Экземпляры в открытых сценах будут заменены на запечённые (Ctrl+Z отменит замену)." : "Сцены не изменятся.");
            if (!EditorUtility.DisplayDialog("Объединить материалы", message, "Объединить", "Отмена"))
            {
                return;
            }

            List<MaterialUsageInfo> included = IncludedMaterials();
            var request = new BakeRequest
            {
                bakeName = name,
                outputRoot = root,
                prefabs = prefabs.ToList(),
                plan = _plan,
                excluded = _analysis.materials
                    .Where(i => !included.Contains(i))
                    .Select(i => new ExcludedMaterialRecord
                    {
                        materialPath = AssetDatabase.GetAssetPath(i.material),
                        reason = i.atlasable ? "исключён вручную" : i.reason
                    })
                    .ToList(),
                requestedMaterialCount = materialCount,
                maxAtlasSize = AtlasSizes[atlasSizeIndex],
                padding = padding,
                replaceInOpenScenes = replaceInOpenScenes
            };

            BakeConfig config = TextureBakeRunner.Run(request);
            if (config != null)
            {
                RefreshConfigs();
                _expandedConfigs.Add(config.configPath);
                bakeName = string.Empty;
                tab = 1;
                int replaced = config.scenes.Sum(s => s.instancesReplaced);
                ShowNotification(new GUIContent($"Готово: {config.atlases.Count} материалов, {config.prefabs.Count} префабов, заменено в сценах: {replaced}"));
                EditorGUIUtility.PingObject(AssetDatabase.LoadAssetAtPath<Object>(config.configPath));
            }

            // Long synchronous work inside OnGUI - bail out of this layout pass cleanly.
            GUIUtility.ExitGUI();
        }

        private void AddPrefabs(IEnumerable<GameObject> candidates)
        {
            HashSet<string> bakedGuids = BakeRegistry.AllBakedPrefabGuids(_configs);
            int added = 0;
            int skippedBaked = 0;

            foreach (GameObject prefab in candidates)
            {
                if (prefab == null) continue;
                if (bakedGuids.Contains(BakeRegistry.Guid(prefab)))
                {
                    skippedBaked++;
                    continue;
                }

                if (!prefabs.Contains(prefab))
                {
                    prefabs.Add(prefab);
                    added++;
                }
            }

            if (added > 0) _analysisDirty = true;

            string note = $"Добавлено префабов: {added}";
            if (skippedBaked > 0) note += $"\nПропущено уже запечённых: {skippedBaked}";
            ShowNotification(new GUIContent(note));
        }

        private void AddPrefabsFromOpenScenes()
        {
            var found = new List<GameObject>();
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                Scene scene = SceneManager.GetSceneAt(i);
                if (!scene.isLoaded) continue;

                foreach (GameObject root in scene.GetRootGameObjects())
                {
                    foreach (Transform transform in root.GetComponentsInChildren<Transform>(true))
                    {
                        if (!PrefabUtility.IsOutermostPrefabInstanceRoot(transform.gameObject)) continue;

                        GameObject asset = PrefabUtility.GetCorrespondingObjectFromSource(transform.gameObject);
                        if (IsPrefabAsset(asset)) found.Add(asset);
                    }
                }
            }

            AddPrefabs(found.Distinct());
        }

        private static IEnumerable<GameObject> ResolvePrefabs(Object obj)
        {
            string path = AssetDatabase.GetAssetPath(obj);
            if (!string.IsNullOrEmpty(path) && AssetDatabase.IsValidFolder(path))
            {
                foreach (string guid in AssetDatabase.FindAssets("t:GameObject", new[] { path }))
                {
                    var asset = AssetDatabase.LoadAssetAtPath<GameObject>(AssetDatabase.GUIDToAssetPath(guid));
                    if (IsPrefabAsset(asset)) yield return asset;
                }

                yield break;
            }

            if (!(obj is GameObject go)) yield break;

            if (EditorUtility.IsPersistent(go))
            {
                var root = AssetDatabase.LoadMainAssetAtPath(path) as GameObject;
                if (IsPrefabAsset(root)) yield return root;
                yield break;
            }

            GameObject instanceRoot = PrefabUtility.GetOutermostPrefabInstanceRoot(go);
            GameObject source = instanceRoot != null ? PrefabUtility.GetCorrespondingObjectFromSource(instanceRoot) : null;
            if (IsPrefabAsset(source)) yield return source;
        }

        private static bool IsPrefabAsset(GameObject go)
        {
            if (go == null || !EditorUtility.IsPersistent(go)) return false;

            PrefabAssetType type = PrefabUtility.GetPrefabAssetType(go);
            return type != PrefabAssetType.NotAPrefab && type != PrefabAssetType.MissingAsset;
        }

        // ------------------------------------------------------------------ Baked tab

        private void DrawBakedTab()
        {
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Запечённые конфигурации в проекте", EditorStyles.boldLabel);
            if (GUILayout.Button("Обновить", GUILayout.Width(90))) RefreshConfigs();
            EditorGUILayout.EndHorizontal();

            if (_configs.Count == 0)
            {
                EditorGUILayout.HelpBox("Пока ничего не запечено.", MessageType.Info);
                return;
            }

            _bakedScroll = EditorGUILayout.BeginScrollView(_bakedScroll);
            foreach (BakeConfig config in _configs)
            {
                DrawConfig(config);
            }

            EditorGUILayout.EndScrollView();
        }

        private void DrawConfig(BakeConfig config)
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);

            bool expanded = _expandedConfigs.Contains(config.configPath);
            bool nowExpanded = EditorGUILayout.Foldout(expanded,
                $"{config.bakeName}   ·   {config.createdAt}   ·   материалов: {config.atlases.Count}, префабов: {config.prefabs.Count}",
                true, EditorStyles.foldoutHeader);
            if (nowExpanded != expanded)
            {
                if (nowExpanded) _expandedConfigs.Add(config.configPath);
                else _expandedConfigs.Remove(config.configPath);
            }

            if (nowExpanded)
            {
                DrawConfigDetails(config);
            }

            EditorGUILayout.EndVertical();
        }

        private void DrawConfigDetails(BakeConfig config)
        {
            EditorGUILayout.LabelField("Папка", config.outputFolder, EditorStyles.miniLabel);

            foreach (AtlasRecord atlas in config.atlases)
            {
                var material = BakeRegistry.LoadAsset<Material>(atlas.materialGuid, atlas.materialPath);
                var texture = BakeRegistry.LoadAsset<Texture2D>(atlas.textureGuid, atlas.texturePath);

                EditorGUILayout.BeginHorizontal();
                Rect thumb = GUILayoutUtility.GetRect(64, 64, GUILayout.Width(64), GUILayout.Height(64));
                DrawTexture(thumb, texture, Color.white);
                EditorGUILayout.BeginVertical();
                EditorGUILayout.ObjectField(material, typeof(Material), false);
                EditorGUILayout.LabelField($"{atlas.width}×{atlas.height} · исходных материалов: {atlas.items.Count} · масштаб {atlas.scale:P0}", EditorStyles.miniLabel);
                EditorGUILayout.LabelField(atlas.shader, EditorStyles.miniLabel);
                EditorGUILayout.EndVertical();
                EditorGUILayout.EndHorizontal();
            }

            bool showPrefabs = _expandedConfigPrefabs.Contains(config.configPath);
            bool nowShowPrefabs = EditorGUILayout.Foldout(showPrefabs, $"Префабы: исходный → запечённый ({config.prefabs.Count})", true);
            if (nowShowPrefabs != showPrefabs)
            {
                if (nowShowPrefabs) _expandedConfigPrefabs.Add(config.configPath);
                else _expandedConfigPrefabs.Remove(config.configPath);
            }

            if (nowShowPrefabs)
            {
                foreach (PrefabRecord record in config.prefabs)
                {
                    EditorGUILayout.BeginHorizontal();
                    EditorGUILayout.ObjectField(BakeRegistry.LoadAsset<GameObject>(record.sourcePrefabGuid, record.sourcePrefabPath), typeof(GameObject), false);
                    GUILayout.Label("→", GUILayout.Width(16));
                    EditorGUILayout.ObjectField(BakeRegistry.LoadAsset<GameObject>(record.bakedPrefabGuid, record.bakedPrefabPath), typeof(GameObject), false);
                    EditorGUILayout.EndHorizontal();
                }
            }

            if (config.excludedMaterials.Count > 0)
            {
                EditorGUILayout.LabelField($"Не объединено материалов: {config.excludedMaterials.Count}", EditorStyles.miniLabel);
            }

            foreach (SceneRecord scene in config.scenes)
            {
                EditorGUILayout.LabelField($"Сцена {scene.scenePath}: заменено экземпляров {scene.instancesReplaced}", EditorStyles.miniLabel);
            }

            EditorGUILayout.Space(4);
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Показать папку"))
            {
                EditorGUIUtility.PingObject(AssetDatabase.LoadAssetAtPath<Object>(config.outputFolder));
            }

            if (GUILayout.Button(new GUIContent("Применить к открытым сценам", "Заменить в открытых сценах исходные префабы на запечённые.")))
            {
                ApplyToOpenScenes(config);
            }

            GUI.backgroundColor = new Color(1f, 0.6f, 0.55f);
            if (GUILayout.Button(new GUIContent("Распечь материалы", "Вернуть исходные префабы в сцены и удалить всё, что создало это запекание.")))
            {
                if (TextureUnbakeRunner.Run(config))
                {
                    _expandedConfigs.Remove(config.configPath);
                    RefreshConfigs();
                    ShowNotification(new GUIContent($"«{config.bakeName}» распечено"));
                }

                GUI.backgroundColor = Color.white;
                GUIUtility.ExitGUI();
            }

            GUI.backgroundColor = Color.white;
            EditorGUILayout.EndHorizontal();
        }

        private void ApplyToOpenScenes(BakeConfig config)
        {
            if (MeshSectionsGuard.BlockIfSectionsBaked("Замена префабов в сценах"))
            {
                GUIUtility.ExitGUI();
            }

            int total = 0;
            PrefabInstanceSwapper.SwapLoadedScenes(TextureBakeRunner.BuildSourceToBakedMap(config), registerUndo: true,
                (scene, count) =>
                {
                    config.AddSceneReplacements(TextureBakeRunner.ScenePathOrName(scene), count);
                    total += count;
                });

            if (total > 0) BakeRegistry.Save(config);
            ShowNotification(new GUIContent(total > 0 ? $"Заменено экземпляров: {total}" : "В открытых сценах нет исходных префабов"));
            GUIUtility.ExitGUI();
        }

        // ------------------------------------------------------------------ drawing helpers

        private static void DrawTexture(Rect rect, Texture texture, Color tint)
        {
            if (texture == null)
            {
                EditorGUI.DrawRect(rect, tint);
                return;
            }

            Color previous = GUI.color;
            GUI.color = tint;
            GUI.DrawTexture(rect, texture, ScaleMode.StretchToFill);
            GUI.color = previous;
        }

        private static void DrawOutline(Rect rect, Color color)
        {
            EditorGUI.DrawRect(new Rect(rect.x, rect.y, rect.width, 1), color);
            EditorGUI.DrawRect(new Rect(rect.x, rect.yMax - 1, rect.width, 1), color);
            EditorGUI.DrawRect(new Rect(rect.x, rect.y, 1, rect.height), color);
            EditorGUI.DrawRect(new Rect(rect.xMax - 1, rect.y, 1, rect.height), color);
        }
    }
}
