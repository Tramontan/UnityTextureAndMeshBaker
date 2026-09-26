using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace MeshSectionBaker
{
    /// <summary>
    /// Merges meshes into the local space of a target transform. Render meshes keep one submesh
    /// per distinct material (so N renderers × M submeshes become as many draw calls as there are
    /// materials); collision meshes are merged into a single submesh. Mirrored transforms
    /// (negative determinant) get their triangle winding flipped so faces don't turn inside out.
    /// </summary>
    public static class MeshCombiner
    {
        private const int UvChannels = 8;

        public static Mesh CombineRender(IList<MeshRenderer> renderers, Matrix4x4 worldToLocal, out Material[] materials)
        {
            bool anyNormals = false;
            bool anyTangents = false;
            bool anyColors = false;
            var uvDimensions = new int[UvChannels];

            foreach (MeshRenderer renderer in renderers)
            {
                Mesh mesh = renderer.GetComponent<MeshFilter>().sharedMesh;
                anyNormals |= mesh.HasVertexAttribute(VertexAttribute.Normal);
                anyTangents |= mesh.HasVertexAttribute(VertexAttribute.Tangent);
                anyColors |= mesh.HasVertexAttribute(VertexAttribute.Color);
                for (int channel = 0; channel < UvChannels; channel++)
                {
                    var attribute = (VertexAttribute)((int)VertexAttribute.TexCoord0 + channel);
                    if (mesh.HasVertexAttribute(attribute))
                    {
                        uvDimensions[channel] = Mathf.Max(uvDimensions[channel], mesh.GetVertexAttributeDimension(attribute));
                    }
                }
            }

            var positions = new List<Vector3>();
            var normals = new List<Vector3>();
            var tangents = new List<Vector4>();
            var colors = new List<Color32>();
            var uvs = new List<Vector4>[UvChannels];
            for (int channel = 0; channel < UvChannels; channel++)
            {
                if (uvDimensions[channel] > 0) uvs[channel] = new List<Vector4>();
            }

            var materialOrder = new List<Material>();
            var indicesPerMaterial = new List<List<int>>();
            var tempV3 = new List<Vector3>();
            var tempV4 = new List<Vector4>();
            var tempColors = new List<Color32>();

            foreach (MeshRenderer renderer in renderers)
            {
                Mesh mesh = renderer.GetComponent<MeshFilter>().sharedMesh;
                Matrix4x4 matrix = worldToLocal * renderer.transform.localToWorldMatrix;
                Matrix4x4 normalMatrix = matrix.inverse.transpose;
                bool flip = matrix.determinant < 0f;
                int baseIndex = positions.Count;
                int vertexCount = mesh.vertexCount;

                mesh.GetVertices(tempV3);
                foreach (Vector3 vertex in tempV3) positions.Add(matrix.MultiplyPoint3x4(vertex));

                if (anyNormals)
                {
                    if (mesh.HasVertexAttribute(VertexAttribute.Normal))
                    {
                        mesh.GetNormals(tempV3);
                        foreach (Vector3 normal in tempV3) normals.Add(normalMatrix.MultiplyVector(normal).normalized);
                    }
                    else
                    {
                        Fill(normals, Vector3.up, vertexCount);
                    }
                }

                if (anyTangents)
                {
                    if (mesh.HasVertexAttribute(VertexAttribute.Tangent))
                    {
                        mesh.GetTangents(tempV4);
                        foreach (Vector4 tangent in tempV4)
                        {
                            Vector3 direction = matrix.MultiplyVector(new Vector3(tangent.x, tangent.y, tangent.z)).normalized;
                            tangents.Add(new Vector4(direction.x, direction.y, direction.z, flip ? -tangent.w : tangent.w));
                        }
                    }
                    else
                    {
                        Fill(tangents, new Vector4(1f, 0f, 0f, 1f), vertexCount);
                    }
                }

                if (anyColors)
                {
                    if (mesh.HasVertexAttribute(VertexAttribute.Color))
                    {
                        mesh.GetColors(tempColors);
                        colors.AddRange(tempColors);
                    }
                    else
                    {
                        Fill(colors, new Color32(255, 255, 255, 255), vertexCount);
                    }
                }

                for (int channel = 0; channel < UvChannels; channel++)
                {
                    if (uvs[channel] == null) continue;

                    var attribute = (VertexAttribute)((int)VertexAttribute.TexCoord0 + channel);
                    if (mesh.HasVertexAttribute(attribute))
                    {
                        mesh.GetUVs(channel, tempV4);
                        uvs[channel].AddRange(tempV4);
                    }
                    else
                    {
                        Fill(uvs[channel], Vector4.zero, vertexCount);
                    }
                }

                Material[] rendererMaterials = renderer.sharedMaterials;
                for (int submesh = 0; submesh < mesh.subMeshCount; submesh++)
                {
                    Material material = rendererMaterials[submesh];
                    int group = materialOrder.IndexOf(material);
                    if (group < 0)
                    {
                        group = materialOrder.Count;
                        materialOrder.Add(material);
                        indicesPerMaterial.Add(new List<int>());
                    }

                    AppendTriangles(indicesPerMaterial[group], mesh.GetIndices(submesh), baseIndex, flip);
                }
            }

            var result = new Mesh
            {
                indexFormat = positions.Count > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16
            };
            result.SetVertices(positions);
            if (anyNormals) result.SetNormals(normals);
            if (anyTangents) result.SetTangents(tangents);
            if (anyColors) result.SetColors(colors);

            for (int channel = 0; channel < UvChannels; channel++)
            {
                if (uvs[channel] != null) SetUvChannel(result, channel, uvs[channel], uvDimensions[channel]);
            }

            result.subMeshCount = indicesPerMaterial.Count;
            for (int i = 0; i < indicesPerMaterial.Count; i++)
            {
                result.SetTriangles(indicesPerMaterial[i], i, false);
            }

            result.RecalculateBounds();
            materials = materialOrder.ToArray();
            return result;
        }

        public static Mesh CombineCollision(IList<MeshCollider> colliders, Matrix4x4 worldToLocal)
        {
            var positions = new List<Vector3>();
            var indices = new List<int>();
            var temp = new List<Vector3>();

            foreach (MeshCollider collider in colliders)
            {
                Mesh mesh = collider.sharedMesh;
                Matrix4x4 matrix = worldToLocal * collider.transform.localToWorldMatrix;
                bool flip = matrix.determinant < 0f;
                int baseIndex = positions.Count;

                mesh.GetVertices(temp);
                foreach (Vector3 vertex in temp) positions.Add(matrix.MultiplyPoint3x4(vertex));

                for (int submesh = 0; submesh < mesh.subMeshCount; submesh++)
                {
                    if (mesh.GetTopology(submesh) != MeshTopology.Triangles) continue;
                    AppendTriangles(indices, mesh.GetIndices(submesh), baseIndex, flip);
                }
            }

            var result = new Mesh
            {
                indexFormat = positions.Count > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16
            };
            result.SetVertices(positions);
            result.SetTriangles(indices, 0, false);
            result.RecalculateBounds();
            return result;
        }

        private static void AppendTriangles(List<int> target, int[] source, int baseIndex, bool flip)
        {
            for (int i = 0; i + 2 < source.Length; i += 3)
            {
                target.Add(source[i] + baseIndex);
                if (flip)
                {
                    target.Add(source[i + 2] + baseIndex);
                    target.Add(source[i + 1] + baseIndex);
                }
                else
                {
                    target.Add(source[i + 1] + baseIndex);
                    target.Add(source[i + 2] + baseIndex);
                }
            }
        }

        private static void Fill<T>(List<T> list, T value, int count)
        {
            for (int i = 0; i < count; i++) list.Add(value);
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
    }
}
