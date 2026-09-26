using UnityEditor;
using UnityEditor.ShortcutManagement;
using UnityEngine;

namespace MeshSectionBaker
{
    /// <summary>
    /// "Hold Z and drag to move a whole zone". Plain Z is Unity's global "Tools/Toggle Pivot
    /// Position", which the shortcut system consumes before OnSceneGUI ever sees the key - so the
    /// hold is a ClutchShortcut in a custom context that is active only while zones are being
    /// edited. Outside zone editing, Z keeps toggling the pivot as usual. Rebindable in
    /// Edit ▸ Shortcuts ▸ Mesh Section Baker.
    /// </summary>
    [InitializeOnLoad]
    public static class ZoneShortcuts
    {
        public const string MoveZoneId = "Mesh Section Baker/Move Zone (hold)";

        private sealed class ZoneEditContext : IShortcutContext
        {
            public bool active => MeshSectionGridEditor.IsZoneEditing;
        }

        private static readonly ZoneEditContext s_context = new ZoneEditContext();

        public static bool MoveHeld { get; private set; }

        static ZoneShortcuts()
        {
            EditorApplication.delayCall += () => ShortcutManager.RegisterContext(s_context);
        }

        [ClutchShortcut(MoveZoneId, typeof(ZoneEditContext), KeyCode.Z)]
        private static void MoveZone(ShortcutArguments args)
        {
            MoveHeld = args.stage == ShortcutStage.Begin;
            SceneView.RepaintAll();
        }

        public static void Release() => MoveHeld = false;

        /// <summary>The key currently bound to the move shortcut (the user may have rebound it).</summary>
        public static string MoveKeyLabel
        {
            get
            {
                try
                {
                    string label = ShortcutManager.instance.GetShortcutBinding(MoveZoneId).ToString();
                    return string.IsNullOrEmpty(label) ? "Z" : label;
                }
                catch
                {
                    return "Z";
                }
            }
        }
    }
}
