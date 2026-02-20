using UnityEngine;
using UnityEditor;
using UnityEditor.SceneManagement;

namespace McpUnity.Utils
{
    /// <summary>
    /// Utility methods for Prefab Stage (Prefab Mode) and headless prefab editing awareness.
    /// When the user has a prefab open in Prefab Mode, or when a prefab is loaded headlessly
    /// via LoadPrefabContents, all lookups and hierarchy queries should target the prefab
    /// contents instead of the main scenes.
    /// </summary>
    internal static class PrefabStageUtils
    {
        private static GameObject _headlessPrefabRoot;

        /// <summary>
        /// The root GameObject of a headlessly loaded prefab (via PrefabUtility.LoadPrefabContents).
        /// Set by ModifyPrefabTool before executing operations, cleared in finally block.
        /// When non-null, FindGameObject resolves against this root instead of scene or prefab stage.
        /// </summary>
        public static GameObject HeadlessPrefabRoot
        {
            get => _headlessPrefabRoot;
            set => _headlessPrefabRoot = value;
        }

        /// <summary>
        /// Returns the current PrefabStage if one is active, or null otherwise.
        /// </summary>
        public static PrefabStage GetCurrentPrefabStage()
        {
            return PrefabStageUtility.GetCurrentPrefabStage();
        }

        /// <summary>
        /// Returns true if the editor is currently in Prefab Mode (a prefab stage is open).
        /// Does NOT return true for headless prefab context.
        /// </summary>
        public static bool IsInPrefabStage()
        {
            return GetCurrentPrefabStage() != null;
        }

        /// <summary>
        /// Returns true if a headless prefab editing context is active
        /// (HeadlessPrefabRoot is set by ModifyPrefabTool).
        /// </summary>
        public static bool IsInHeadlessPrefabContext()
        {
            return _headlessPrefabRoot != null;
        }

        /// <summary>
        /// Returns true if either Prefab Mode is active or a headless prefab context is set.
        /// Useful for tools that need to guard against scene fallback in either context.
        /// </summary>
        public static bool IsInAnyPrefabContext()
        {
            return IsInPrefabStage() || IsInHeadlessPrefabContext();
        }

        /// <summary>
        /// Prefab-context-aware replacement for GameObject.Find().
        /// Priority: headless prefab root > prefab stage > scene.
        /// 
        /// Supports both absolute paths ("/Root/Child") and relative paths ("Root/Child").
        /// </summary>
        /// <param name="path">The GameObject path to search for (e.g., "Root/Child/SubChild")</param>
        /// <returns>The found GameObject, or null if not found</returns>
        public static GameObject FindGameObject(string path)
        {
            if (string.IsNullOrEmpty(path))
                return null;

            // Headless prefab context takes highest priority
            if (_headlessPrefabRoot != null)
            {
                return FindInHierarchy(_headlessPrefabRoot, path);
            }

            // Then check prefab stage
            var prefabStage = GetCurrentPrefabStage();
            if (prefabStage != null)
            {
                return FindInPrefabStage(prefabStage, path);
            }

            // Fall back to scene
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

            return FindInHierarchy(root, path);
        }

        /// <summary>
        /// Searches for a GameObject by path within a given root hierarchy.
        /// Shared logic for both prefab stage and headless prefab lookups.
        /// </summary>
        /// <param name="root">The root GameObject to search within</param>
        /// <param name="path">The path to search for</param>
        /// <returns>The found GameObject, or null if not found</returns>
        private static GameObject FindInHierarchy(GameObject root, string path)
        {
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

            // Try searching by name through the entire hierarchy
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
