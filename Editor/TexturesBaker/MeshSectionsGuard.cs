using System.Collections.Generic;
using MeshSectionBaker;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace TexturesBaker
{
    /// <summary>
    /// Swapping prefab instances while Mesh Section Baker has sections baked would replace the
    /// source objects parked in its EditorOnly holder, leaving the section records pointing at
    /// destroyed objects (and unbaking sections unable to restore them). Block that case.
    /// </summary>
    public static class MeshSectionsGuard
    {
        public static bool BlockIfSectionsBaked(string action)
        {
            var scenes = new List<string>();
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
                }
            }

            if (scenes.Count == 0) return false;

            EditorUtility.DisplayDialog("TexturesBaker",
                $"{action} невозможно, пока в сцене есть сшитые секции (Mesh Section Baker):\n\n{string.Join("\n", scenes)}\n\n" +
                "Сначала распеките секции, затем повторите.", "OK");
            return true;
        }
    }
}
