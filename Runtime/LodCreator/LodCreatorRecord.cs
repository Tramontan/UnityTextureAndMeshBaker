using System.Collections.Generic;
using UnityEngine;

namespace LodCreator
{
    /// <summary>
    /// Written by Tools/Создание LOD on the object it created LODs for: which child objects and
    /// mesh assets it added and whether it added the LODGroup - everything "Удалить LOD" needs to
    /// put the object back exactly as it was. The original renderers are never modified.
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("")]
    public class LodCreatorRecord : MonoBehaviour
    {
        public bool createdLodGroup;
        public List<GameObject> createdObjects = new List<GameObject>();
        public List<string> meshAssetPaths = new List<string>();

        /// <summary>Triangle count per LOD level (0 = original), for display.</summary>
        public List<int> levelTriangles = new List<int>();
    }
}
