using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace TexturesBaker
{
    /// <summary>
    /// Text (JSON) record of one bake: which source prefabs/materials were merged into which
    /// atlases/baked prefabs. Lives next to the generated assets as "bake_config.json" and is
    /// everything the "Baked" tab and the unbake step need.
    /// </summary>
    [Serializable]
    public class BakeConfig
    {
        public string bakeName;
        public string createdAt;
        public string outputFolder;
        public int requestedMaterialCount;
        public int maxAtlasSize;
        public int padding;
        public List<AtlasRecord> atlases = new List<AtlasRecord>();
        public List<PrefabRecord> prefabs = new List<PrefabRecord>();
        public List<ExcludedMaterialRecord> excludedMaterials = new List<ExcludedMaterialRecord>();
        public List<SceneRecord> scenes = new List<SceneRecord>();

        [NonSerialized] public string configPath;

        public void AddSceneReplacements(string scenePath, int count)
        {
            SceneRecord record = scenes.Find(s => s.scenePath == scenePath);
            if (record == null)
            {
                record = new SceneRecord { scenePath = scenePath };
                scenes.Add(record);
            }

            record.instancesReplaced += count;
        }
    }

    [Serializable]
    public class AtlasRecord
    {
        public string materialGuid;
        public string materialPath;
        public string textureGuid;
        public string texturePath;
        public string shader;
        public int width;
        public int height;
        public float scale;
        public List<AtlasItemRecord> items = new List<AtlasItemRecord>();
    }

    [Serializable]
    public class AtlasItemRecord
    {
        public string sourceMaterialGuid;
        public string sourceMaterialPath;
        public string sourceTexturePath;
        public int x;
        public int y;
        public int width;
        public int height;
    }

    [Serializable]
    public class PrefabRecord
    {
        public string sourcePrefabGuid;
        public string sourcePrefabPath;
        public string bakedPrefabGuid;
        public string bakedPrefabPath;
        public int renderersChanged;
    }

    [Serializable]
    public class ExcludedMaterialRecord
    {
        public string materialPath;
        public string reason;
    }

    [Serializable]
    public class SceneRecord
    {
        public string scenePath;
        public int instancesReplaced;
    }

    public static class BakeRegistry
    {
        public const string ConfigFileName = "bake_config.json";

        public static List<BakeConfig> LoadAll()
        {
            var result = new List<BakeConfig>();
            foreach (string guid in AssetDatabase.FindAssets(Path.GetFileNameWithoutExtension(ConfigFileName)))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (Path.GetFileName(path) != ConfigFileName)
                {
                    continue;
                }

                BakeConfig config = Load(path);
                if (config != null)
                {
                    result.Add(config);
                }
            }

            result.Sort((a, b) => string.CompareOrdinal(b.createdAt, a.createdAt));
            return result;
        }

        public static BakeConfig Load(string path)
        {
            try
            {
                var config = JsonUtility.FromJson<BakeConfig>(File.ReadAllText(path));
                config.configPath = path;
                return config;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[TexturesBaker] Failed to read '{path}': {ex.Message}");
                return null;
            }
        }

        public static void Save(BakeConfig config)
        {
            File.WriteAllText(config.configPath, JsonUtility.ToJson(config, true));
            AssetDatabase.ImportAsset(config.configPath);
        }

        public static HashSet<string> AllBakedPrefabGuids(IEnumerable<BakeConfig> configs)
        {
            var guids = new HashSet<string>();
            foreach (BakeConfig config in configs)
            {
                foreach (PrefabRecord record in config.prefabs)
                {
                    guids.Add(record.bakedPrefabGuid);
                }
            }

            return guids;
        }

        public static T LoadAsset<T>(string guid, string path) where T : UnityEngine.Object
        {
            if (!string.IsNullOrEmpty(guid))
            {
                string guidPath = AssetDatabase.GUIDToAssetPath(guid);
                if (!string.IsNullOrEmpty(guidPath))
                {
                    var asset = AssetDatabase.LoadAssetAtPath<T>(guidPath);
                    if (asset != null)
                    {
                        return asset;
                    }
                }
            }

            return string.IsNullOrEmpty(path) ? null : AssetDatabase.LoadAssetAtPath<T>(path);
        }

        public static string Guid(UnityEngine.Object asset)
        {
            return asset == null ? string.Empty : AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(asset));
        }
    }
}
