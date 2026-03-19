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
        /// 
        /// Supports bracket-index syntax for disambiguating same-name siblings:
        /// "Parent/Child[0]" returns the first child named "Child",
        /// "Parent/Child[2]" returns the third, etc.
        /// Without an index, returns the first match (same as Transform.Find).
        /// </summary>
        /// <param name="root">The root GameObject to search within</param>
        /// <param name="path">The path to search for</param>
        /// <returns>The found GameObject, or null if not found</returns>
        private static GameObject FindInHierarchy(GameObject root, string path)
        {
            // Strip leading slash if present
            string cleanPath = path.TrimStart('/');

            // Check if path contains bracket indices — if so, use segment-by-segment resolution
            bool hasIndexSyntax = cleanPath.Contains("[");

            // If the path is just the root name (with optional index), return the root
            string rootName = ParsePathSegment(cleanPath, out _);
            if (rootName == root.name && !cleanPath.Contains("/"))
                return root;

            // Determine the relative path (strip root name prefix if present)
            string relativePath;
            string rootPrefix = root.name + "/";
            if (cleanPath.StartsWith(rootPrefix))
            {
                relativePath = cleanPath.Substring(rootPrefix.Length);
            }
            else
            {
                // Check if path starts with root name + index + slash (e.g., "Root[0]/Child")
                string rootWithIndex = rootName;
                if (cleanPath.StartsWith(rootWithIndex + "/") && rootWithIndex != root.name)
                {
                    // Root name didn't match, this isn't rooted here
                    relativePath = cleanPath;
                }
                else if (cleanPath.StartsWith(root.name + "["))
                {
                    // Root with index, e.g., "Root[0]/Child/..."
                    int slashIdx = cleanPath.IndexOf('/');
                    relativePath = slashIdx >= 0 ? cleanPath.Substring(slashIdx + 1) : "";
                }
                else
                {
                    relativePath = cleanPath;
                }
            }

            if (string.IsNullOrEmpty(relativePath))
                return root;

            // If no bracket-index syntax, use Unity's fast Transform.Find first
            if (!hasIndexSyntax)
            {
                Transform directFind = root.transform.Find(relativePath);
                if (directFind != null)
                    return directFind.gameObject;

                // Fall back to recursive name search
                return FindByNameRecursive(root.transform, relativePath);
            }

            // Walk path segment by segment, resolving bracket indices
            return FindBySegmentPath(root.transform, relativePath);
        }

        /// <summary>
        /// Parses a path segment, extracting the name and optional bracket index.
        /// "Bubble_Tutorial[2]" returns "Bubble_Tutorial" with index=2.
        /// "Bubble_Tutorial" returns "Bubble_Tutorial" with index=-1 (no index).
        /// </summary>
        public static string ParsePathSegment(string segment, out int index)
        {
            index = -1;
            if (string.IsNullOrEmpty(segment))
                return segment;

            int bracketStart = segment.IndexOf('[');
            if (bracketStart < 0)
                return segment;

            int bracketEnd = segment.IndexOf(']', bracketStart);
            if (bracketEnd < 0)
                return segment;

            string indexStr = segment.Substring(bracketStart + 1, bracketEnd - bracketStart - 1);
            if (int.TryParse(indexStr, out int parsedIndex))
                index = parsedIndex;

            return segment.Substring(0, bracketStart);
        }

        /// <summary>
        /// Walks a path segment by segment, resolving bracket indices at each level.
        /// E.g., "Content/Bubble_Tutorial[2]/Arrow" finds the 3rd "Bubble_Tutorial"
        /// child of "Content", then finds "Arrow" under it.
        /// </summary>
        private static GameObject FindBySegmentPath(Transform current, string relativePath)
        {
            string[] segments = relativePath.Split('/');

            foreach (string segment in segments)
            {
                string childName = ParsePathSegment(segment, out int index);
                Transform found = FindNthChild(current, childName, index >= 0 ? index : 0);
                if (found == null)
                    return null;
                current = found;
            }

            return current.gameObject;
        }

        /// <summary>
        /// Finds the Nth direct child with the given name (zero-based).
        /// </summary>
        public static Transform FindNthChild(Transform parent, string name, int n)
        {
            int count = 0;
            for (int i = 0; i < parent.childCount; i++)
            {
                Transform child = parent.GetChild(i);
                if (child.name == name)
                {
                    if (count == n)
                        return child;
                    count++;
                }
            }
            return null;
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
