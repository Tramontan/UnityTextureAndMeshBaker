using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEditorInternal;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace TexturesBaker
{
    /// <summary>
    /// Replaces scene instances of one prefab with instances of another (source ⇄ baked), keeping
    /// what the instance carried in the scene: transform, name, sibling order, active state,
    /// layer/tag/static flags and renderer settings for every matching child, plus any objects or
    /// components that were added to the instance in the scene.
    /// </summary>
    public static class PrefabInstanceSwapper
    {
        public const string UndoName = "TexturesBaker: swap prefab instances";

        public static void SwapLoadedScenes(IReadOnlyDictionary<GameObject, GameObject> map, bool registerUndo, Action<Scene, int> onSceneSwapped)
        {
            int undoGroup = -1;
            if (registerUndo)
            {
                Undo.IncrementCurrentGroup();
                undoGroup = Undo.GetCurrentGroup();
                Undo.SetCurrentGroupName(UndoName);
            }

            try
            {
                for (int i = 0; i < SceneManager.sceneCount; i++)
                {
                    Scene scene = SceneManager.GetSceneAt(i);
                    if (!scene.isLoaded) continue;

                    int swapped = Swap(scene, map, registerUndo);
                    if (swapped > 0)
                    {
                        onSceneSwapped?.Invoke(scene, swapped);
                    }
                }
            }
            finally
            {
                EditorUtility.ClearProgressBar();
                if (registerUndo)
                {
                    Undo.CollapseUndoOperations(undoGroup);
                }
            }
        }

        public static int Swap(Scene scene, IReadOnlyDictionary<GameObject, GameObject> map, bool registerUndo)
        {
            var targets = new List<KeyValuePair<GameObject, GameObject>>();
            foreach (GameObject root in scene.GetRootGameObjects())
            {
                foreach (Transform transform in root.GetComponentsInChildren<Transform>(true))
                {
                    GameObject go = transform.gameObject;
                    if (!PrefabUtility.IsOutermostPrefabInstanceRoot(go)) continue;

                    GameObject asset = PrefabUtility.GetCorrespondingObjectFromSource(go);
                    if (asset != null && map.TryGetValue(asset, out GameObject replacement))
                    {
                        targets.Add(new KeyValuePair<GameObject, GameObject>(go, replacement));
                    }
                }
            }

            int swapped = 0;
            for (int i = 0; i < targets.Count; i++)
            {
                if (targets[i].Key == null) continue;

                if (i % 25 == 0)
                {
                    EditorUtility.DisplayProgressBar("TexturesBaker", $"Замена экземпляров в сцене '{scene.name}' ({i}/{targets.Count})", (float)i / targets.Count);
                }

                Replace(targets[i].Key, targets[i].Value, registerUndo);
                swapped++;
            }

            if (swapped > 0)
            {
                EditorSceneManager.MarkSceneDirty(scene);
            }

            return swapped;
        }

        private static void Replace(GameObject oldRoot, GameObject prefab, bool registerUndo)
        {
            Transform oldTransform = oldRoot.transform;
            var newRoot = (GameObject)PrefabUtility.InstantiatePrefab(prefab, oldRoot.scene);
            if (registerUndo)
            {
                Undo.RegisterCreatedObjectUndo(newRoot, UndoName);
            }

            Transform newTransform = newRoot.transform;
            newTransform.SetParent(oldTransform.parent, false);
            newTransform.SetSiblingIndex(oldTransform.GetSiblingIndex());
            newRoot.name = oldRoot.name;

            var oldPaths = new Dictionary<Transform, string>();
            var newByPath = new Dictionary<string, Transform>();
            CollectPaths(oldTransform, string.Empty, null, oldPaths, skipAddedObjects: true);
            CollectPaths(newTransform, string.Empty, newByPath, null, skipAddedObjects: false);

            foreach (KeyValuePair<Transform, string> pair in oldPaths)
            {
                if (newByPath.TryGetValue(pair.Value, out Transform target))
                {
                    CopyState(pair.Key, target);
                }
            }

            CopyAddedComponents(oldRoot, oldPaths, newByPath);
            MoveAddedObjects(oldTransform, newTransform, oldPaths, newByPath, registerUndo);

            if (registerUndo)
            {
                Undo.DestroyObjectImmediate(oldRoot);
            }
            else
            {
                Object.DestroyImmediate(oldRoot);
            }
        }

        /// <summary>Hierarchy path keyed by name + index among same-named siblings, so duplicate child names still match.</summary>
        private static void CollectPaths(Transform transform, string path, Dictionary<string, Transform> byPath,
            Dictionary<Transform, string> byTransform, bool skipAddedObjects)
        {
            if (byPath != null) byPath[path] = transform;
            if (byTransform != null) byTransform[transform] = path;

            var nameCounts = new Dictionary<string, int>();
            for (int i = 0; i < transform.childCount; i++)
            {
                Transform child = transform.GetChild(i);
                if (skipAddedObjects && PrefabUtility.IsAddedGameObjectOverride(child.gameObject)) continue;

                nameCounts.TryGetValue(child.name, out int index);
                nameCounts[child.name] = index + 1;
                CollectPaths(child, $"{path}/{child.name}#{index}", byPath, byTransform, skipAddedObjects);
            }
        }

        private static void CopyState(Transform from, Transform to)
        {
            to.localPosition = from.localPosition;
            to.localRotation = from.localRotation;
            to.localScale = from.localScale;

            GameObject source = from.gameObject;
            GameObject target = to.gameObject;
            target.layer = source.layer;
            if (!target.CompareTag(source.tag))
            {
                target.tag = source.tag;
            }

            GameObjectUtility.SetStaticEditorFlags(target, GameObjectUtility.GetStaticEditorFlags(source));
            if (target.activeSelf != source.activeSelf)
            {
                target.SetActive(source.activeSelf);
            }

            if (source.TryGetComponent(out MeshRenderer sourceRenderer) && target.TryGetComponent(out MeshRenderer targetRenderer))
            {
                targetRenderer.enabled = sourceRenderer.enabled;
                targetRenderer.shadowCastingMode = sourceRenderer.shadowCastingMode;
                targetRenderer.receiveShadows = sourceRenderer.receiveShadows;
                targetRenderer.lightProbeUsage = sourceRenderer.lightProbeUsage;
                targetRenderer.reflectionProbeUsage = sourceRenderer.reflectionProbeUsage;
                targetRenderer.motionVectorGenerationMode = sourceRenderer.motionVectorGenerationMode;
                targetRenderer.allowOcclusionWhenDynamic = sourceRenderer.allowOcclusionWhenDynamic;
                targetRenderer.renderingLayerMask = sourceRenderer.renderingLayerMask;
                targetRenderer.sortingLayerID = sourceRenderer.sortingLayerID;
                targetRenderer.sortingOrder = sourceRenderer.sortingOrder;
            }
        }

        private static void CopyAddedComponents(GameObject oldRoot, Dictionary<Transform, string> oldPaths, Dictionary<string, Transform> newByPath)
        {
            foreach (var added in PrefabUtility.GetAddedComponents(oldRoot))
            {
                Component component = added.instanceComponent;
                if (component == null) continue;

                // Components on added objects travel with the object itself (see MoveAddedObjects).
                if (!oldPaths.TryGetValue(component.transform, out string path)) continue;
                if (!newByPath.TryGetValue(path, out Transform target)) continue;

                if (ComponentUtility.CopyComponent(component))
                {
                    ComponentUtility.PasteComponentAsNew(target.gameObject);
                }
            }
        }

        private static void MoveAddedObjects(Transform oldRoot, Transform newRoot, Dictionary<Transform, string> oldPaths,
            Dictionary<string, Transform> newByPath, bool registerUndo)
        {
            var addedRoots = new List<Transform>();
            foreach (Transform transform in oldRoot.GetComponentsInChildren<Transform>(true))
            {
                if (transform == oldRoot) continue;
                if (!PrefabUtility.IsAddedGameObjectOverride(transform.gameObject)) continue;
                if (transform.parent != null && PrefabUtility.IsAddedGameObjectOverride(transform.parent.gameObject)) continue;

                addedRoots.Add(transform);
            }

            foreach (Transform added in addedRoots)
            {
                Transform newParent = newRoot;
                if (oldPaths.TryGetValue(added.parent, out string parentPath) && newByPath.TryGetValue(parentPath, out Transform match))
                {
                    newParent = match;
                }

                if (registerUndo)
                {
                    Undo.SetTransformParent(added, newParent, UndoName);
                }
                else
                {
                    added.SetParent(newParent, true);
                }
            }
        }
    }
}
