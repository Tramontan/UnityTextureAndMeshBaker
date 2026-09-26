using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace TexturesBaker
{
    public sealed class BakeRequest
    {
        public string bakeName;
        public string outputRoot;
        public IReadOnlyList<GameObject> prefabs;
        public BakePlan plan;
        public List<ExcludedMaterialRecord> excluded;
        public int requestedMaterialCount;
        public int maxAtlasSize;
        public int padding;
        public bool replaceInOpenScenes;
    }

    /// <summary>
    /// Executes a <see cref="BakePlan"/>:
    ///  1. renders each atlas (source textures scaled into their cells, material tint baked in,
    ///     edges extruded into the padding) and creates one material per atlas;
    ///  2. for every source prefab that uses an atlased material, saves a Prefab Variant whose
    ///     meshes have UV0 remapped into the atlas and submeshes merged per atlas material;
    ///  3. writes bake_config.json and swaps scene instances of the sources to the variants.
    /// Source assets are never modified. On failure the partial output folder is deleted.
    /// </summary>
    public static class TextureBakeRunner
    {
        private const string ProgressTitle = "TexturesBaker";

        private readonly struct MaterialRemap
        {
            public readonly Material atlasMaterial;
            public readonly RectInt content;
            public readonly int atlasWidth;
            public readonly int atlasHeight;

            public MaterialRemap(Material atlasMaterial, RectInt content, int atlasWidth, int atlasHeight)
            {
                this.atlasMaterial = atlasMaterial;
                this.content = content;
                this.atlasWidth = atlasWidth;
                this.atlasHeight = atlasHeight;
            }
        }

        private sealed class BakedMesh
        {
            public Mesh mesh;
            public Material[] materials;
        }

        public static BakeConfig Run(BakeRequest request)
        {
            string folder = CreateOutputFolders(request.outputRoot, request.bakeName);
            var config = new BakeConfig
            {
                bakeName = Path.GetFileName(folder),
                createdAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                outputFolder = folder,
                requestedMaterialCount = request.requestedMaterialCount,
                maxAtlasSize = request.maxAtlasSize,
                padding = request.padding,
                configPath = $"{folder}/{BakeRegistry.ConfigFileName}"
            };

            Scene previewScene = default;
            try
            {
                Dictionary<Material, MaterialRemap> remap = BuildAtlases(request, folder, config);
                previewScene = EditorSceneManager.NewPreviewScene();
                BuildPrefabs(request, folder, remap, previewScene, config);
                config.excludedMaterials.AddRange(request.excluded);
                BakeRegistry.Save(config);
                AssetDatabase.SaveAssets();
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
                EditorUtility.ClearProgressBar();
                AssetDatabase.DeleteAsset(folder);
                EditorUtility.DisplayDialog("TexturesBaker",
                    $"Запекание прервано ошибкой, созданные файлы удалены.\n\n{ex.Message}", "OK");
                return null;
            }
            finally
            {
                if (previewScene.IsValid())
                {
                    EditorSceneManager.ClosePreviewScene(previewScene);
                }

                EditorUtility.ClearProgressBar();
            }

            if (request.replaceInOpenScenes && config.prefabs.Count > 0)
            {
                PrefabInstanceSwapper.SwapLoadedScenes(BuildSourceToBakedMap(config), registerUndo: true,
                    (scene, count) => config.AddSceneReplacements(ScenePathOrName(scene), count));
                BakeRegistry.Save(config);
            }

            return config;
        }

        public static Dictionary<GameObject, GameObject> BuildSourceToBakedMap(BakeConfig config)
        {
            var map = new Dictionary<GameObject, GameObject>();
            foreach (PrefabRecord record in config.prefabs)
            {
                var source = BakeRegistry.LoadAsset<GameObject>(record.sourcePrefabGuid, record.sourcePrefabPath);
                var baked = BakeRegistry.LoadAsset<GameObject>(record.bakedPrefabGuid, record.bakedPrefabPath);
                if (source != null && baked != null)
                {
                    map[source] = baked;
                }
            }

            return map;
        }

        public static string ScenePathOrName(Scene scene)
        {
            return string.IsNullOrEmpty(scene.path) ? scene.name : scene.path;
        }

        public static void EnsureFolder(string path)
        {
            path = path.Replace('\\', '/').TrimEnd('/');
            if (AssetDatabase.IsValidFolder(path)) return;

            string parent = Path.GetDirectoryName(path)?.Replace('\\', '/');
            if (string.IsNullOrEmpty(parent)) throw new ArgumentException($"Invalid folder '{path}'.");

            EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, Path.GetFileName(path));
        }

        private static string CreateOutputFolders(string root, string bakeName)
        {
            EnsureFolder(root);
            string folder = AssetDatabase.GenerateUniqueAssetPath($"{root.TrimEnd('/')}/{Sanitize(bakeName)}");
            AssetDatabase.CreateFolder(root.TrimEnd('/'), Path.GetFileName(folder));
            foreach (string sub in new[] { "Atlases", "Materials", "Meshes", "Prefabs" })
            {
                AssetDatabase.CreateFolder(folder, sub);
            }

            return folder;
        }

        private static Dictionary<Material, MaterialRemap> BuildAtlases(BakeRequest request, string folder, BakeConfig config)
        {
            var remap = new Dictionary<Material, MaterialRemap>();
            int count = request.plan.atlases.Count;

            for (int i = 0; i < count; i++)
            {
                AtlasPlan atlas = request.plan.atlases[i];
                EditorUtility.DisplayProgressBar(ProgressTitle, $"Атлас {i + 1}/{count} ({atlas.width}×{atlas.height})", 0.3f * i / count);

                string materialPath = $"{folder}/Materials/Atlas_{i + 1}.mat";
                Material material = CreateAtlasMaterial(atlas, materialPath);
                bool keepAlpha = material.renderQueue >= (int)RenderQueue.AlphaTest;

                string texturePath = $"{folder}/Atlases/Atlas_{i + 1}.png";
                WriteAtlasPng(atlas, request.padding, texturePath);
                ConfigureAtlasImporter(texturePath, atlas, keepAlpha);
                var texture = AssetDatabase.LoadAssetAtPath<Texture2D>(texturePath);

                string mainProp = MaterialProps.MainTexture(material);
                material.SetTexture(mainProp, texture);
                material.SetTextureScale(mainProp, Vector2.one);
                material.SetTextureOffset(mainProp, Vector2.zero);
                EditorUtility.SetDirty(material);

                var record = new AtlasRecord
                {
                    materialGuid = AssetDatabase.AssetPathToGUID(materialPath),
                    materialPath = materialPath,
                    textureGuid = AssetDatabase.AssetPathToGUID(texturePath),
                    texturePath = texturePath,
                    shader = atlas.shader.name,
                    width = atlas.width,
                    height = atlas.height,
                    scale = atlas.scale
                };

                foreach (PlacedItem placed in atlas.placed)
                {
                    foreach (Material source in placed.item.materials)
                    {
                        remap[source] = new MaterialRemap(material, placed.contentRect, atlas.width, atlas.height);
                        record.items.Add(new AtlasItemRecord
                        {
                            sourceMaterialGuid = BakeRegistry.Guid(source),
                            sourceMaterialPath = AssetDatabase.GetAssetPath(source),
                            sourceTexturePath = placed.item.texture != null ? AssetDatabase.GetAssetPath(placed.item.texture) : string.Empty,
                            x = placed.contentRect.x,
                            y = placed.contentRect.y,
                            width = placed.contentRect.width,
                            height = placed.contentRect.height
                        });
                    }
                }

                config.atlases.Add(record);
            }

            return remap;
        }

        private static Material CreateAtlasMaterial(AtlasPlan atlas, string path)
        {
            var material = new Material(atlas.templateMaterial) { name = Path.GetFileNameWithoutExtension(path) };
            if (material.shader != atlas.shader)
            {
                material.shader = atlas.shader;
            }

            // Any secondary map (normal, emission, ...) belonged to one source only - drop them all.
            foreach (string prop in material.GetTexturePropertyNames())
            {
                material.SetTexture(prop, null);
            }

            // Per-material tints are baked into the atlas pixels.
            string colorProp = MaterialProps.Color(material);
            if (colorProp != null)
            {
                material.SetColor(colorProp, Color.white);
            }

            AssetDatabase.CreateAsset(material, path);
            return material;
        }

        private static void WriteAtlasPng(AtlasPlan atlas, int padding, string path)
        {
            var pixels = new Color32[atlas.width * atlas.height];
            foreach (PlacedItem placed in atlas.placed)
            {
                RectInt rect = placed.contentRect;
                Color32[] content = ReadScaled(placed.item.texture, rect.width, rect.height, placed.item.tint);
                BlitWithExtrusion(pixels, atlas.width, atlas.height, content, rect, padding);
            }

            var texture = new Texture2D(atlas.width, atlas.height, TextureFormat.RGBA32, false);
            try
            {
                texture.SetPixels32(pixels);
                texture.Apply(false);
                File.WriteAllBytes(path, texture.EncodeToPNG());
            }
            finally
            {
                Object.DestroyImmediate(texture);
            }

            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
        }

        /// <summary>
        /// Reads any texture (compressed, non-readable - no import settings are touched) at the
        /// requested size via a GPU blit; the blit picks the right mip for downscaling.
        /// </summary>
        private static Color32[] ReadScaled(Texture source, int width, int height, Color tint)
        {
            var result = new Color32[width * height];
            if (source == null)
            {
                Color32 solid = tint;
                for (int i = 0; i < result.Length; i++) result[i] = solid;
                return result;
            }

            RenderTexture rt = RenderTexture.GetTemporary(width, height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            RenderTexture previous = RenderTexture.active;
            var readback = new Texture2D(width, height, TextureFormat.RGBA32, false, false);
            try
            {
                Graphics.Blit(source, rt);
                RenderTexture.active = rt;
                readback.ReadPixels(new Rect(0, 0, width, height), 0, 0, false);
                readback.Apply(false);
                result = readback.GetPixels32();
            }
            finally
            {
                RenderTexture.active = previous;
                RenderTexture.ReleaseTemporary(rt);
                Object.DestroyImmediate(readback);
            }

            if (tint != Color.white)
            {
                for (int i = 0; i < result.Length; i++)
                {
                    result[i] = (Color)result[i] * tint;
                }
            }

            return result;
        }

        /// <summary>Copies a cell and repeats its edge pixels into the padding so filtering/mips don't bleed neighbours in.</summary>
        private static void BlitWithExtrusion(Color32[] atlas, int atlasWidth, int atlasHeight, Color32[] content, RectInt rect, int padding)
        {
            for (int y = -padding; y < rect.height + padding; y++)
            {
                int atlasY = rect.y + y;
                if (atlasY < 0 || atlasY >= atlasHeight) continue;

                int atlasRow = atlasY * atlasWidth;
                int contentRow = Mathf.Clamp(y, 0, rect.height - 1) * rect.width;
                for (int x = -padding; x < rect.width + padding; x++)
                {
                    int atlasX = rect.x + x;
                    if (atlasX < 0 || atlasX >= atlasWidth) continue;

                    atlas[atlasRow + atlasX] = content[contentRow + Mathf.Clamp(x, 0, rect.width - 1)];
                }
            }
        }

        private static void ConfigureAtlasImporter(string path, AtlasPlan atlas, bool keepAlpha)
        {
            var importer = (TextureImporter)AssetImporter.GetAtPath(path);
            importer.textureType = TextureImporterType.Default;
            importer.sRGBTexture = true;
            importer.alphaSource = keepAlpha ? TextureImporterAlphaSource.FromInput : TextureImporterAlphaSource.None;
            importer.alphaIsTransparency = false;
            importer.mipmapEnabled = true;
            importer.wrapMode = TextureWrapMode.Clamp;
            importer.filterMode = FilterMode.Bilinear;
            importer.maxTextureSize = Mathf.Max(atlas.width, atlas.height);
            importer.textureCompression = TextureImporterCompression.Compressed;
            importer.isReadable = false;
            importer.SaveAndReimport();
        }

        private static void BuildPrefabs(BakeRequest request, string folder, Dictionary<Material, MaterialRemap> remap, Scene previewScene, BakeConfig config)
        {
            var meshCache = new Dictionary<string, BakedMesh>();
            var uvCache = new UvCache();
            int count = request.prefabs.Count;

            for (int i = 0; i < count; i++)
            {
                GameObject source = request.prefabs[i];
                if (source == null) continue;

                EditorUtility.DisplayProgressBar(ProgressTitle, $"Префаб {i + 1}/{count}: {source.name}", 0.3f + 0.7f * i / count);

                // An instance of the source saved to a new path becomes a Prefab Variant of it -
                // works the same for .prefab files and FBX model prefabs, and keeps the link.
                var instance = (GameObject)PrefabUtility.InstantiatePrefab(source, previewScene);
                try
                {
                    int changed = 0;
                    foreach (MeshRenderer renderer in instance.GetComponentsInChildren<MeshRenderer>(true))
                    {
                        if (!BakeAnalyzer.TryGetBakeableRenderer(renderer, out Mesh mesh, out Material[] materials, out _)) continue;
                        if (!materials.Any(m => m != null && remap.ContainsKey(m))) continue;

                        string key = MeshKey(mesh, materials);
                        if (!meshCache.TryGetValue(key, out BakedMesh baked))
                        {
                            baked = BuildBakedMesh(mesh, materials, remap, uvCache);
                            string meshPath = AssetDatabase.GenerateUniqueAssetPath($"{folder}/Meshes/{Sanitize(baked.mesh.name)}.asset");
                            AssetDatabase.CreateAsset(baked.mesh, meshPath);
                            meshCache[key] = baked;
                        }

                        var filter = renderer.GetComponent<MeshFilter>();
                        filter.sharedMesh = baked.mesh;
                        renderer.sharedMaterials = baked.materials;
                        PrefabUtility.RecordPrefabInstancePropertyModifications(filter);
                        PrefabUtility.RecordPrefabInstancePropertyModifications(renderer);
                        changed++;
                    }

                    if (changed == 0) continue;

                    string prefabPath = AssetDatabase.GenerateUniqueAssetPath($"{folder}/Prefabs/{Sanitize(source.name)}_baked.prefab");
                    GameObject bakedPrefab = PrefabUtility.SaveAsPrefabAsset(instance, prefabPath, out bool success);
                    if (!success || bakedPrefab == null)
                    {
                        throw new InvalidOperationException($"Не удалось сохранить префаб '{prefabPath}'.");
                    }

                    config.prefabs.Add(new PrefabRecord
                    {
                        sourcePrefabGuid = BakeRegistry.Guid(source),
                        sourcePrefabPath = AssetDatabase.GetAssetPath(source),
                        bakedPrefabGuid = AssetDatabase.AssetPathToGUID(prefabPath),
                        bakedPrefabPath = prefabPath,
                        renderersChanged = changed
                    });
                }
                finally
                {
                    Object.DestroyImmediate(instance);
                }
            }
        }

        private static string MeshKey(Mesh mesh, Material[] materials)
        {
            return mesh.GetHashCode() + "|" + string.Join("|", materials.Select(m => m != null ? m.GetHashCode() : 0));
        }

        /// <summary>
        /// Copies the mesh with UV0 of atlased submeshes moved into their atlas cell. Submeshes that
        /// end up on the same atlas material are merged into one (one draw call instead of several).
        /// Vertices are duplicated per remapped submesh so a vertex shared by two submeshes can carry
        /// two different atlas UVs; every other vertex attribute (incl. lightmap UV1) is kept as is.
        /// </summary>
        private static BakedMesh BuildBakedMesh(Mesh source, Material[] materials, Dictionary<Material, MaterialRemap> remap, UvCache uvCache)
        {
            int vertexCount = source.vertexCount;
            var positions = new List<Vector3>(vertexCount);
            var normals = new List<Vector3>(vertexCount);
            var tangents = new List<Vector4>(vertexCount);
            var colors = new List<Color32>(vertexCount);
            source.GetVertices(positions);
            source.GetNormals(normals);
            source.GetTangents(tangents);
            source.GetColors(colors);
            bool hasNormals = normals.Count == vertexCount;
            bool hasTangents = tangents.Count == vertexCount;
            bool hasColors = colors.Count == vertexCount;

            var uvIn = new List<Vector4>[8];
            var uvOut = new List<Vector4>[8];
            var uvDimensions = new int[8];
            for (int channel = 0; channel < 8; channel++)
            {
                var attribute = (VertexAttribute)((int)VertexAttribute.TexCoord0 + channel);
                if (!source.HasVertexAttribute(attribute)) continue;

                uvDimensions[channel] = source.GetVertexAttributeDimension(attribute);
                uvIn[channel] = new List<Vector4>(vertexCount);
                source.GetUVs(channel, uvIn[channel]);
                uvOut[channel] = new List<Vector4>(vertexCount);
            }

            var outPositions = new List<Vector3>(vertexCount);
            var outNormals = new List<Vector3>(hasNormals ? vertexCount : 0);
            var outTangents = new List<Vector4>(hasTangents ? vertexCount : 0);
            var outColors = new List<Color32>(hasColors ? vertexCount : 0);
            var groupMaterials = new List<Material>();
            var groupIndices = new List<List<int>>();
            var vertexMap = new Dictionary<long, int>();
            List<Vector2> uv0 = uvCache.Get(source);

            for (int submesh = 0; submesh < source.subMeshCount; submesh++)
            {
                Material material = materials[submesh];
                MaterialRemap target = default;
                bool remapped = material != null && remap.TryGetValue(material, out target);

                Vector2 stScale = Vector2.one;
                Vector2 stOffset = Vector2.zero;
                Vector2 shift = Vector2.zero;
                if (remapped)
                {
                    BakeAnalyzer.GetMainTextureTransform(material, out stScale, out stOffset);
                    remapped = BakeAnalyzer.TryComputeUvShift(source, submesh, uv0, stScale, stOffset, out shift, out _);
                }

                Material outMaterial = remapped ? target.atlasMaterial : material;
                int group = groupMaterials.IndexOf(outMaterial);
                if (group < 0)
                {
                    group = groupMaterials.Count;
                    groupMaterials.Add(outMaterial);
                    groupIndices.Add(new List<int>());
                }

                List<int> indicesOut = groupIndices[group];
                long transformId = remapped ? submesh + 1 : 0;

                foreach (int oldIndex in source.GetIndices(submesh))
                {
                    long key = (transformId << 32) | (uint)oldIndex;
                    if (!vertexMap.TryGetValue(key, out int newIndex))
                    {
                        newIndex = outPositions.Count;
                        outPositions.Add(positions[oldIndex]);
                        if (hasNormals) outNormals.Add(normals[oldIndex]);
                        if (hasTangents) outTangents.Add(tangents[oldIndex]);
                        if (hasColors) outColors.Add(colors[oldIndex]);

                        for (int channel = 0; channel < 8; channel++)
                        {
                            if (uvOut[channel] == null) continue;

                            Vector4 uv = uvIn[channel][oldIndex];
                            if (channel == 0 && remapped)
                            {
                                float u = uv.x * stScale.x + stOffset.x - shift.x;
                                float v = uv.y * stScale.y + stOffset.y - shift.y;
                                uv.x = (target.content.x + u * target.content.width) / target.atlasWidth;
                                uv.y = (target.content.y + v * target.content.height) / target.atlasHeight;
                            }

                            uvOut[channel].Add(uv);
                        }

                        vertexMap[key] = newIndex;
                    }

                    indicesOut.Add(newIndex);
                }
            }

            var mesh = new Mesh { name = source.name + "_baked" };
            mesh.indexFormat = outPositions.Count > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16;
            mesh.SetVertices(outPositions);
            if (hasNormals) mesh.SetNormals(outNormals);
            if (hasTangents) mesh.SetTangents(outTangents);
            if (hasColors) mesh.SetColors(outColors);

            for (int channel = 0; channel < 8; channel++)
            {
                if (uvOut[channel] != null)
                {
                    SetUvChannel(mesh, channel, uvOut[channel], uvDimensions[channel]);
                }
            }

            mesh.subMeshCount = groupIndices.Count;
            for (int g = 0; g < groupIndices.Count; g++)
            {
                mesh.SetTriangles(groupIndices[g], g, false);
            }

            mesh.bounds = source.bounds;
            return new BakedMesh { mesh = mesh, materials = groupMaterials.ToArray() };
        }

        private static void SetUvChannel(Mesh mesh, int channel, List<Vector4> uvs, int dimension)
        {
            switch (dimension)
            {
                case 2:
                    mesh.SetUVs(channel, uvs.ConvertAll(v => new Vector2(v.x, v.y)));
                    break;
                case 3:
                    mesh.SetUVs(channel, uvs.ConvertAll(v => new Vector3(v.x, v.y, v.z)));
                    break;
                default:
                    mesh.SetUVs(channel, uvs);
                    break;
            }
        }

        private static string Sanitize(string name)
        {
            foreach (char c in Path.GetInvalidFileNameChars())
            {
                name = name.Replace(c, '_');
            }

            return string.IsNullOrWhiteSpace(name) ? "Bake" : name.Trim();
        }
    }
}
