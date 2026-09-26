using System.IO;
using UnityEditor;

namespace TextureMeshBaker
{
    /// <summary>
    /// Creates asset folders reliably. <see cref="AssetDatabase.CreateFolder"/> does not fail when a directory of
    /// that name already exists on disk without being part of the AssetDatabase (made outside Unity, or left
    /// behind while Auto Refresh is off): it quietly creates "&lt;name&gt; 1" next to it and returns that. Code
    /// that then writes into the path it asked for gets "Parent directory must exist before creating asset".
    /// <see cref="Ensure"/> imports such a directory instead, and fails loudly if the folder still does not end
    /// up where it was asked for.
    /// </summary>
    internal static class AssetFolders
    {
        /// <summary>Makes <paramref name="path"/> (under Assets/) a folder the AssetDatabase knows, creating any missing parents.</summary>
        public static void Ensure(string path)
        {
            path = Normalize(path);
            if (AssetDatabase.IsValidFolder(path)) return;

            string parent = Path.GetDirectoryName(path)?.Replace('\\', '/');
            if (string.IsNullOrEmpty(parent)) throw new IOException($"Некорректный путь папки '{path}'.");
            Ensure(parent);

            if (Directory.Exists(path))
            {
                AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
                if (AssetDatabase.IsValidFolder(path)) return;
            }

            string created = AssetDatabase.GUIDToAssetPath(AssetDatabase.CreateFolder(parent, Path.GetFileName(path)));
            if (created == path) return;

            // Never leave a stray "<name> 1" behind.
            if (!string.IsNullOrEmpty(created)) AssetDatabase.DeleteAsset(created);
            throw new IOException($"Не удалось создать папку '{path}'" +
                                  (string.IsNullOrEmpty(created) ? "." : $": Unity создал '{created}' вместо неё."));
        }

        /// <summary>
        /// A path for a new folder <paramref name="name"/> inside <paramref name="parent"/> that is taken neither
        /// in the AssetDatabase nor on disk (<see cref="AssetDatabase.GenerateUniqueAssetPath"/> only checks the
        /// former, so it can hand out the name of a directory Unity has not imported).
        /// </summary>
        public static string UniqueNew(string parent, string name)
        {
            parent = Normalize(parent);
            string candidate = $"{parent}/{name}";
            for (int i = 1; AssetDatabase.IsValidFolder(candidate) || Directory.Exists(candidate) || File.Exists(candidate); i++)
            {
                candidate = $"{parent}/{name} {i}";
            }

            return candidate;
        }

        private static string Normalize(string path) => path.Replace('\\', '/').TrimEnd('/');
    }
}
