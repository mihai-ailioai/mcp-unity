using UnityEngine;
using UnityEditor;
using UnityEditor.SceneManagement;

namespace McpUnity.Utils
{
    /// <summary>
    /// Utility methods for Prefab Stage (Prefab Mode) awareness.
    /// When the user has a prefab open in Prefab Mode, all lookups and hierarchy
    /// queries should target the prefab stage contents instead of the main scenes.
    /// </summary>
    internal static class PrefabStageUtils
    {
        /// <summary>
        /// Returns the current PrefabStage if one is active, or null otherwise.
        /// </summary>
        public static PrefabStage GetCurrentPrefabStage()
        {
            return PrefabStageUtility.GetCurrentPrefabStage();
        }

        /// <summary>
        /// Returns true if the editor is currently in Prefab Mode (a prefab stage is open).
        /// </summary>
        public static bool IsInPrefabStage()
        {
            return GetCurrentPrefabStage() != null;
        }

        /// <summary>
        /// Prefab-stage-aware replacement for GameObject.Find().
        /// When in Prefab Mode, searches the prefab stage hierarchy by path.
        /// When not in Prefab Mode, falls back to standard GameObject.Find().
        /// 
        /// Supports both absolute paths ("/Root/Child") and relative paths ("Root/Child").
        /// </summary>
        /// <param name="path">The GameObject path to search for (e.g., "Root/Child/SubChild")</param>
        /// <returns>The found GameObject, or null if not found</returns>
        public static GameObject FindGameObject(string path)
        {
            if (string.IsNullOrEmpty(path))
                return null;

            var prefabStage = GetCurrentPrefabStage();
            if (prefabStage != null)
            {
                return FindInPrefabStage(prefabStage, path);
            }

            return GameObject.Find(path);
        }

        /// <summary>
        /// Searches for a GameObject by path within the prefab stage hierarchy.
        /// </summary>
        private static GameObject FindInPrefabStage(PrefabStage prefabStage, string path)
        {
            GameObject root = prefabStage.prefabContentsRoot;
            if (root == null)
                return null;

            // Strip leading slash if present
            string cleanPath = path.TrimStart('/');

            // If the path is just the root name, return the root
            if (cleanPath == root.name)
                return root;

            // If the path starts with the root name, search from root
            string rootPrefix = root.name + "/";
            if (cleanPath.StartsWith(rootPrefix))
            {
                string relativePath = cleanPath.Substring(rootPrefix.Length);
                Transform found = root.transform.Find(relativePath);
                return found != null ? found.gameObject : null;
            }

            // Try searching as a relative path from root (path doesn't include root name)
            Transform directFind = root.transform.Find(cleanPath);
            if (directFind != null)
                return directFind.gameObject;

            // Try searching by name through the entire prefab hierarchy
            return FindByNameRecursive(root.transform, cleanPath);
        }

        /// <summary>
        /// Recursively searches for a GameObject by name in the hierarchy.
        /// Used as a last-resort fallback when path-based lookup fails.
        /// </summary>
        private static GameObject FindByNameRecursive(Transform parent, string name)
        {
            // Check direct children first
            for (int i = 0; i < parent.childCount; i++)
            {
                Transform child = parent.GetChild(i);
                if (child.name == name)
                    return child.gameObject;
            }

            // Then recurse into children
            for (int i = 0; i < parent.childCount; i++)
            {
                GameObject found = FindByNameRecursive(parent.GetChild(i), name);
                if (found != null)
                    return found;
            }

            return null;
        }
    }
}
