using System;
using McpUnity.Resources;
using McpUnity.Unity;
using UnityEngine;
using UnityEditor;
using Newtonsoft.Json.Linq;

namespace McpUnity.Tools
{
    /// <summary>
    /// Tool for retrieving detailed information about a prefab asset without entering
    /// Prefab Mode or instantiating it in a scene. Uses AssetDatabase.LoadAssetAtPath
    /// for read-only access to the prefab hierarchy and components.
    /// Supports optional filtering by rootPath, namePattern, and componentType to
    /// handle large prefabs efficiently.
    /// </summary>
    public class GetPrefabInfoTool : McpToolBase
    {
        public GetPrefabInfoTool()
        {
            Name = "get_prefab_info";
            Description = "Get detailed information about a prefab asset by asset path, without entering Prefab Mode or instantiating in scene. " +
                          "Returns hierarchy, components, and prefab metadata (variant status, base prefab path). " +
                          "Use 'summary' for a lightweight listing with name, instanceId, and component type names per child (no deduplication). " +
                          "For large prefabs, use 'rootPath' to inspect a specific subtree, or 'namePattern'/'componentType' to " +
                          "search for matching GameObjects (returns a flat list of matches instead of the full hierarchy). " +
                          "Note: instanceIds from this tool are NOT valid inside modify_prefab (different object graph). Use objectPath instead.";
        }

        public override JObject Execute(JObject parameters)
        {
            // Validate assetPath
            string assetPath = parameters?["assetPath"]?.ToObject<string>();
            if (string.IsNullOrEmpty(assetPath))
            {
                return McpUnitySocketHandler.CreateErrorResponse(
                    "Missing required parameter: assetPath",
                    "validation_error"
                );
            }

            if (!assetPath.StartsWith("Assets/"))
            {
                return McpUnitySocketHandler.CreateErrorResponse(
                    "assetPath must start with 'Assets/'",
                    "validation_error"
                );
            }

            if (!assetPath.EndsWith(".prefab"))
            {
                return McpUnitySocketHandler.CreateErrorResponse(
                    "assetPath must end with '.prefab'",
                    "validation_error"
                );
            }

            bool summary = parameters["summary"]?.ToObject<bool>() ?? false;
            string rootPath = parameters["rootPath"]?.ToObject<string>();
            string namePattern = parameters["namePattern"]?.ToObject<string>();
            string componentType = parameters["componentType"]?.ToObject<string>();

            bool hasFilter = !string.IsNullOrEmpty(namePattern) || !string.IsNullOrEmpty(componentType);

            // Load the prefab asset (read-only, no editing context)
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(assetPath);
            if (prefab == null)
            {
                return McpUnitySocketHandler.CreateErrorResponse(
                    $"Prefab not found at path: {assetPath}",
                    "not_found_error"
                );
            }

            // Resolve rootPath to narrow the starting point
            GameObject startNode = prefab;
            if (!string.IsNullOrEmpty(rootPath))
            {
                startNode = FindChildByPath(prefab, rootPath);
                if (startNode == null)
                {
                    return McpUnitySocketHandler.CreateErrorResponse(
                        $"rootPath '{rootPath}' not found within prefab '{prefab.name}'",
                        "not_found_error"
                    );
                }
            }

            // Prefab-specific metadata
            string assetGuid = AssetDatabase.AssetPathToGUID(assetPath);
            bool isVariant = PrefabUtility.IsPartOfVariantPrefab(prefab);

            // Resolve component type filter
            Type resolvedComponentType = null;
            if (!string.IsNullOrEmpty(componentType))
            {
                resolvedComponentType = ResolveComponentType(componentType);
                if (resolvedComponentType == null)
                {
                    return McpUnitySocketHandler.CreateErrorResponse(
                        $"Component type '{componentType}' could not be resolved. Provide the class name (e.g. 'MeshRenderer').",
                        "validation_error"
                    );
                }
            }

            if (hasFilter)
            {
                // Filtered mode: collect matching GameObjects as a flat list
                JArray matches = new JArray();
                CollectFilteredMatches(startNode, prefab.transform, matches, namePattern, resolvedComponentType, summary);

                var filterResponse = new JObject
                {
                    ["success"] = true,
                    ["message"] = $"Found {matches.Count} matching GameObject(s) in prefab '{prefab.name}' at {assetPath}",
                    ["matchCount"] = matches.Count,
                    ["matches"] = matches,
                    ["assetPath"] = assetPath,
                    ["assetGuid"] = assetGuid,
                    ["isVariant"] = isVariant
                };

                if (!string.IsNullOrEmpty(rootPath))
                    filterResponse["rootPath"] = rootPath;
                if (!string.IsNullOrEmpty(namePattern))
                    filterResponse["namePattern"] = namePattern;
                if (!string.IsNullOrEmpty(componentType))
                    filterResponse["componentType"] = componentType;

                if (isVariant)
                {
                    var sourceObject = PrefabUtility.GetCorrespondingObjectFromSource(prefab);
                    if (sourceObject != null)
                        filterResponse["basePrefabPath"] = AssetDatabase.GetAssetPath(sourceObject);
                }

                return filterResponse;
            }

            // Unfiltered mode: full hierarchy serialization
            // summary: listing mode (name, instanceId, component types — no dedup, no properties)
            // detailed: full component property serialization
            JObject prefabData = summary
                ? GetGameObjectResource.GameObjectToListingJObject(startNode)
                : GetGameObjectResource.GameObjectToJObject(startNode, true);

            prefabData["assetPath"] = assetPath;
            prefabData["assetGuid"] = assetGuid;
            prefabData["isVariant"] = isVariant;

            if (!string.IsNullOrEmpty(rootPath))
                prefabData["rootPath"] = rootPath;

            if (isVariant)
            {
                var sourceObject = PrefabUtility.GetCorrespondingObjectFromSource(prefab);
                if (sourceObject != null)
                    prefabData["basePrefabPath"] = AssetDatabase.GetAssetPath(sourceObject);
            }

            string modeDesc = summary ? "summary" : "detailed info";
            string scopeDesc = !string.IsNullOrEmpty(rootPath) ? $" (subtree: {rootPath})" : "";

            return new JObject
            {
                ["success"] = true,
                ["message"] = $"Retrieved {modeDesc} for prefab '{prefab.name}' at {assetPath}{scopeDesc}",
                ["prefab"] = prefabData,
                ["assetPath"] = assetPath,
                ["assetGuid"] = assetGuid
            };
        }

        /// <summary>
        /// Recursively collect GameObjects matching the name pattern and/or component type.
        /// Each match is serialized with its path relative to the prefab root and full component data.
        /// </summary>
        private void CollectFilteredMatches(
            GameObject node,
            Transform prefabRoot,
            JArray matches,
            string namePattern,
            Type componentType,
            bool summary)
        {
            if (node == null) return;

            bool nameMatches = string.IsNullOrEmpty(namePattern) || GameObjectToolUtils.MatchesNamePattern(node.name, namePattern);
            bool componentMatches = componentType == null || node.GetComponent(componentType) != null;

            if (nameMatches && componentMatches)
            {
                JObject matchObj = summary
                    ? GetGameObjectResource.GameObjectToListingJObject(node)
                    : GetGameObjectResource.GameObjectToJObject(node, true);

                // Add the path relative to the prefab root for easy identification
                matchObj["path"] = GetRelativePath(node.transform, prefabRoot);

                // Remove children from the match to keep output flat
                // (children are visited separately by the recursion)
                matchObj.Remove("children");

                matches.Add(matchObj);
            }

            // Recurse into children
            for (int i = 0; i < node.transform.childCount; i++)
            {
                CollectFilteredMatches(
                    node.transform.GetChild(i).gameObject,
                    prefabRoot,
                    matches,
                    namePattern,
                    componentType,
                    summary);
            }
        }

        /// <summary>
        /// Get the path of a transform relative to the prefab root.
        /// </summary>
        private static string GetRelativePath(Transform target, Transform root)
        {
            if (target == root) return target.name;

            string path = target.name;
            Transform current = target.parent;
            while (current != null)
            {
                path = current.name + "/" + path;
                if (current == root) break;
                current = current.parent;
            }
            return path;
        }

        /// <summary>
        /// Find a child GameObject by path within a prefab hierarchy.
        /// Supports paths like "Root/Child/SubChild" or "Child/SubChild" (without root name).
        /// </summary>
        private static GameObject FindChildByPath(GameObject root, string path)
        {
            if (string.IsNullOrEmpty(path)) return root;

            string cleanPath = path.TrimStart('/');

            // If path equals root name, return root
            if (cleanPath == root.name) return root;

            // If path starts with root name, strip it
            string rootPrefix = root.name + "/";
            if (cleanPath.StartsWith(rootPrefix))
            {
                string relativePath = cleanPath.Substring(rootPrefix.Length);
                Transform found = root.transform.Find(relativePath);
                return found != null ? found.gameObject : null;
            }

            // Try as relative path from root
            Transform direct = root.transform.Find(cleanPath);
            return direct != null ? direct.gameObject : null;
        }

        /// <summary>
        /// Resolve a component type name to a System.Type.
        /// Searches Unity assemblies for the type.
        /// </summary>
        private static Type ResolveComponentType(string typeName)
        {
            if (string.IsNullOrEmpty(typeName)) return null;

            // Try UnityEngine types first
            Type type = typeof(Component).Assembly.GetType("UnityEngine." + typeName);
            if (type != null && typeof(Component).IsAssignableFrom(type)) return type;

            // Try UnityEngine.UI
            var uiAssembly = typeof(UnityEngine.UI.Graphic).Assembly;
            type = uiAssembly.GetType("UnityEngine.UI." + typeName);
            if (type != null && typeof(Component).IsAssignableFrom(type)) return type;

            // Search all loaded assemblies
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                type = assembly.GetType(typeName);
                if (type != null && typeof(Component).IsAssignableFrom(type)) return type;

                // Also try without namespace
                foreach (var t in assembly.GetTypes())
                {
                    if (t.Name == typeName && typeof(Component).IsAssignableFrom(t))
                        return t;
                }
            }

            return null;
        }
    }
}
