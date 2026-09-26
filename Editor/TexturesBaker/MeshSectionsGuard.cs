using System.Collections.Generic;
using System.Linq;
using LodCreator;
using MeshSectionBaker;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace TexturesBaker
{
    /// <summary>
    /// Swapping prefab instances while Mesh Section Baker has sections baked would leave the
    /// removed source objects (kept in its stores, or in the holder of older versions) on the
    /// old prefabs - unbaking would bring back pre-swap or deleted prefabs. Block that case.
    /// Same for LODs made by Tools/Создание LOD on prefab instances: the swap would drop them.
    /// </summary>
    public static class MeshSectionsGuard
    {
        public static bool BlockIfSectionsBaked(string action)
        {
            var scenes = new List<string>();
            var lodObjects = new List<string>();
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                Scene scene = SceneManager.GetSceneAt(i);
                if (!scene.isLoaded) continue;

                foreach (GameObject root in scene.GetRootGameObjects())
                {
                    foreach (MeshSectionGrid grid in root.GetComponentsInChildren<MeshSectionGrid>(true))
                    {
                        if (grid.bakedSections.Count > 0)
                        {
                            scenes.Add($"{scene.name}: {grid.bakedSections.Count} секций");
                        }
                    }

                    // LODs made by Tools/Создание LOD on prefab instances would be lost/orphaned by the swap.
                    foreach (LodCreatorRecord record in root.GetComponentsInChildren<LodCreatorRecord>(true))
                    {
                        if (PrefabUtility.IsPartOfPrefabInstance(record.gameObject)) lodObjects.Add($"{scene.name}: {record.name}");
                    }
                }
            }

            if (scenes.Count > 0)
            {
                EditorUtility.DisplayDialog("TexturesBaker",
                    $"{action} невозможно, пока в сцене есть сшитые секции (Mesh Section Baker):\n\n{string.Join("\n", scenes)}\n\n" +
                    "Сначала распеките секции, затем повторите.", "OK");
                return true;
            }

            if (lodObjects.Count > 0)
            {
                string list = string.Join("\n", lodObjects.Take(15)) + (lodObjects.Count > 15 ? $"\n… и ещё {lodObjects.Count - 15}" : string.Empty);
                EditorUtility.DisplayDialog("TexturesBaker",
                    $"{action} невозможно, пока на экземплярах префабов есть LOD (Tools/Создание LOD):\n\n{list}\n\n" +
                    "Сначала удалите LOD, затем повторите (после замены LOD можно создать заново).", "OK");
                return true;
            }

            return false;
        }
    }
}
