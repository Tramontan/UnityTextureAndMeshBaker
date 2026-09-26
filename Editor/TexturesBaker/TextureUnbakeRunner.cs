using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace TexturesBaker
{
    /// <summary>
    /// Reverts a bake: puts the original prefabs back in every scene that references the baked
    /// ones (open scenes, and closed scenes found on disk), then deletes the bake folder
    /// (atlases, materials, meshes, variants and the config). Source assets were never touched,
    /// so nothing else needs restoring.
    /// </summary>
    public static class TextureUnbakeRunner
    {
        public static bool Run(BakeConfig config)
        {
            if (MeshSectionsGuard.BlockIfSectionsBaked("Распекание материалов"))
            {
                return false;
            }

            var bakedToSource = new Dictionary<GameObject, GameObject>();
            var bakedGuids = new HashSet<string>();
            var missingSources = new List<string>();

            foreach (PrefabRecord record in config.prefabs)
            {
                var source = BakeRegistry.LoadAsset<GameObject>(record.sourcePrefabGuid, record.sourcePrefabPath);
                var baked = BakeRegistry.LoadAsset<GameObject>(record.bakedPrefabGuid, record.bakedPrefabPath);
                if (source == null)
                {
                    missingSources.Add(record.sourcePrefabPath);
                    continue;
                }

                if (baked != null) bakedToSource[baked] = source;
                if (!string.IsNullOrEmpty(record.bakedPrefabGuid)) bakedGuids.Add(record.bakedPrefabGuid);
            }

            if (missingSources.Count > 0)
            {
                EditorUtility.DisplayDialog("Распечь материалы",
                    "Не найдены исходные префабы, вернуть их в сцены невозможно:\n\n" + string.Join("\n", missingSources.Take(10)), "OK");
                return false;
            }

            string bakeFolder = config.outputFolder.TrimEnd('/') + "/";
            var loadedScenes = new List<Scene>();
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                Scene scene = SceneManager.GetSceneAt(i);
                if (scene.isLoaded) loadedScenes.Add(scene);
            }

            var loadedPaths = new HashSet<string>(loadedScenes.Select(s => s.path));
            List<string> closedScenes = FindFilesReferencing("*.unity", bakedGuids)
                .Where(p => !loadedPaths.Contains(p) && !p.StartsWith(bakeFolder)).ToList();
            List<string> otherPrefabs = FindFilesReferencing("*.prefab", bakedGuids)
                .Where(p => !p.StartsWith(bakeFolder)).ToList();

            if (!EditorUtility.DisplayDialog("Распечь материалы", BuildConfirmMessage(config, closedScenes, otherPrefabs), "Распечь", "Отмена"))
            {
                return false;
            }

            try
            {
                var modified = new List<Scene>();
                foreach (Scene scene in loadedScenes)
                {
                    if (PrefabInstanceSwapper.Swap(scene, bakedToSource, registerUndo: false) > 0)
                    {
                        modified.Add(scene);
                    }
                }

                EditorUtility.ClearProgressBar();
                if (modified.Count > 0)
                {
                    EditorSceneManager.SaveModifiedScenesIfUserWantsTo(modified.ToArray());
                }

                foreach (string path in closedScenes)
                {
                    Scene scene = EditorSceneManager.OpenScene(path, OpenSceneMode.Additive);
                    PrefabInstanceSwapper.Swap(scene, bakedToSource, registerUndo: false);
                    EditorSceneManager.SaveScene(scene);
                    EditorSceneManager.CloseScene(scene, true);
                }
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }

            AssetDatabase.DeleteAsset(config.outputFolder);
            AssetDatabase.Refresh();
            return true;
        }

        private static string BuildConfirmMessage(BakeConfig config, List<string> closedScenes, List<string> otherPrefabs)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"Исходные префабы ({config.prefabs.Count}) будут возвращены в открытые сцены.");
            if (closedScenes.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine($"Закрытые сцены с запечёнными префабами будут открыты, исправлены и сохранены ({closedScenes.Count}):");
                foreach (string path in closedScenes.Take(8)) sb.AppendLine("  " + path);
                if (closedScenes.Count > 8) sb.AppendLine("  …");
            }

            if (otherPrefabs.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine($"ВНИМАНИЕ: другие префабы ссылаются на запечённые ({otherPrefabs.Count}) — в них ссылки станут Missing:");
                foreach (string path in otherPrefabs.Take(8)) sb.AppendLine("  " + path);
                if (otherPrefabs.Count > 8) sb.AppendLine("  …");
            }

            sb.AppendLine();
            sb.AppendLine($"Папка будет удалена: {config.outputFolder}");
            return sb.ToString();
        }

        /// <summary>
        /// Text-scans YAML assets for "guid: xxxx" references. Tokenizing instead of calling
        /// Contains() per GUID keeps it linear even for large scenes and hundreds of GUIDs.
        /// </summary>
        private static List<string> FindFilesReferencing(string pattern, HashSet<string> guids)
        {
            var result = new List<string>();
            if (guids.Count == 0) return result;

            const string marker = "guid: ";
            foreach (string file in Directory.GetFiles("Assets", pattern, SearchOption.AllDirectories))
            {
                string text;
                try
                {
                    text = File.ReadAllText(file);
                }
                catch (IOException)
                {
                    continue;
                }

                int index = 0;
                while ((index = text.IndexOf(marker, index, System.StringComparison.Ordinal)) >= 0)
                {
                    index += marker.Length;
                    if (index + 32 <= text.Length && guids.Contains(text.Substring(index, 32)))
                    {
                        result.Add(file.Replace('\\', '/'));
                        break;
                    }
                }
            }

            return result;
        }
    }
}
