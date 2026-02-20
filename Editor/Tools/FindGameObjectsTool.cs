using System;
using System.Collections.Generic;
using McpUnity.Unity;
using McpUnity.Utils;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace McpUnity.Tools
{
    public class FindGameObjectsTool : McpToolBase
    {
        public FindGameObjectsTool()
        {
            Name = "find_gameobjects";
            Description = "Searches scene hierarchy for GameObjects using component, name pattern, tag, and layer filters";
        }

        public override JObject Execute(JObject parameters)
        {
            string componentTypeName = parameters["componentType"]?.ToObject<string>();
            string namePattern = parameters["namePattern"]?.ToObject<string>();
            string tag = parameters["tag"]?.ToObject<string>();
            JToken layerToken = parameters["layer"];
            bool includeInactive = parameters["includeInactive"]?.ToObject<bool?>() ?? false;
            string rootPath = parameters["rootPath"]?.ToObject<string>();

            bool hasComponentFilter = !string.IsNullOrWhiteSpace(componentTypeName);
            bool hasNameFilter = !string.IsNullOrWhiteSpace(namePattern);
            bool hasTagFilter = !string.IsNullOrWhiteSpace(tag);
            bool hasLayerFilter = layerToken != null && layerToken.Type != JTokenType.Null && !string.IsNullOrWhiteSpace(layerToken.ToString());

            if (!hasComponentFilter && !hasNameFilter && !hasTagFilter && !hasLayerFilter)
            {
                return McpUnitySocketHandler.CreateErrorResponse(
                    "At least one filter must be provided: componentType, namePattern, tag, or layer",
                    "validation_error"
                );
            }

            Type componentType = null;
            if (hasComponentFilter)
            {
                componentType = SerializedFieldUtils.FindType(componentTypeName, typeof(Component));
                if (componentType == null)
                {
                    return McpUnitySocketHandler.CreateErrorResponse(
                        $"Component type '{componentTypeName}' not found",
                        "component_error"
                    );
                }
            }

            int? expectedLayer = null;
            if (hasLayerFilter)
            {
                if (!TryResolveLayer(layerToken, out int resolvedLayer))
                {
                    return McpUnitySocketHandler.CreateErrorResponse(
                        $"Layer '{layerToken}' not found or invalid",
                        "validation_error"
                    );
                }

                expectedLayer = resolvedLayer;
            }

            List<GameObject> searchRoots = GetSearchRoots(rootPath, out JObject rootError);
            if (rootError != null)
            {
                return rootError;
            }

            JArray matches = new JArray();
            foreach (GameObject root in searchRoots)
            {
                CollectMatchesRecursive(
                    root,
                    matches,
                    componentType,
                    hasNameFilter ? namePattern : null,
                    hasTagFilter ? tag : null,
                    expectedLayer,
                    includeInactive
                );
            }

            return new JObject
            {
                ["success"] = true,
                ["type"] = "text",
                ["message"] = $"Found {matches.Count} matching GameObject(s)",
                ["matches"] = matches,
                ["totalFound"] = matches.Count
            };
        }

        private static List<GameObject> GetSearchRoots(string rootPath, out JObject error)
        {
            error = null;
            List<GameObject> roots = new List<GameObject>();

            if (!string.IsNullOrWhiteSpace(rootPath))
            {
                GameObject root = PrefabStageUtils.FindGameObject(rootPath);
                if (root == null)
                {
                    root = SerializedFieldUtils.FindGameObjectByPath(rootPath);
                }

                if (root == null)
                {
                    error = McpUnitySocketHandler.CreateErrorResponse(
                        $"Root GameObject not found at path '{rootPath}'",
                        "not_found_error"
                    );
                    return roots;
                }

                roots.Add(root);
                return roots;
            }

            if (PrefabStageUtils.IsInPrefabStage())
            {
                GameObject prefabRoot = PrefabStageUtils.GetCurrentPrefabStage()?.prefabContentsRoot;
                if (prefabRoot != null)
                {
                    roots.Add(prefabRoot);
                    return roots;
                }
            }

            Scene activeScene = SceneManager.GetActiveScene();
            roots.AddRange(activeScene.GetRootGameObjects());
            return roots;
        }

        private static void CollectMatchesRecursive(
            GameObject gameObject,
            JArray matches,
            Type componentType,
            string namePattern,
            string tag,
            int? expectedLayer,
            bool includeInactive)
        {
            if (gameObject == null)
            {
                return;
            }

            if (!includeInactive && !gameObject.activeInHierarchy)
            {
                return;
            }

            if (IsMatch(gameObject, componentType, namePattern, tag, expectedLayer))
            {
                matches.Add(BuildMatchObject(gameObject));
            }

            Transform transform = gameObject.transform;
            for (int i = 0; i < transform.childCount; i++)
            {
                CollectMatchesRecursive(
                    transform.GetChild(i).gameObject,
                    matches,
                    componentType,
                    namePattern,
                    tag,
                    expectedLayer,
                    includeInactive
                );
            }
        }

        private static bool IsMatch(
            GameObject gameObject,
            Type componentType,
            string namePattern,
            string tag,
            int? expectedLayer)
        {
            if (componentType != null && gameObject.GetComponent(componentType) == null)
            {
                return false;
            }

            if (!string.IsNullOrEmpty(namePattern) && !MatchesNamePattern(gameObject.name, namePattern))
            {
                return false;
            }

            if (!string.IsNullOrEmpty(tag) && !gameObject.CompareTag(tag))
            {
                return false;
            }

            if (expectedLayer.HasValue && gameObject.layer != expectedLayer.Value)
            {
                return false;
            }

            return true;
        }

        private static bool MatchesNamePattern(string name, string pattern)
        {
            if (string.IsNullOrEmpty(pattern))
            {
                return true;
            }

            string source = name.ToLowerInvariant();
            string query = pattern.ToLowerInvariant();

            if (!query.Contains("*"))
            {
                return source.Contains(query);
            }

            if (query == "*")
            {
                return true;
            }

            bool startsWithWildcard = query.StartsWith("*");
            bool endsWithWildcard = query.EndsWith("*");
            string trimmed = query.Trim('*');

            if (startsWithWildcard && endsWithWildcard)
            {
                return source.Contains(trimmed);
            }

            if (startsWithWildcard)
            {
                return source.EndsWith(trimmed);
            }

            if (endsWithWildcard)
            {
                return source.StartsWith(trimmed);
            }

            string[] parts = query.Split(new[] { '*' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0)
            {
                return true;
            }

            int index = 0;
            foreach (string part in parts)
            {
                int foundIndex = source.IndexOf(part, index, StringComparison.Ordinal);
                if (foundIndex < 0)
                {
                    return false;
                }

                index = foundIndex + part.Length;
            }

            return true;
        }

        private static JObject BuildMatchObject(GameObject gameObject)
        {
            Component[] components = gameObject.GetComponents<Component>();
            JArray componentNames = new JArray();
            foreach (Component component in components)
            {
                componentNames.Add(component != null ? component.GetType().Name : "MissingComponent");
            }

            return new JObject
            {
                ["name"] = gameObject.name,
                ["instanceId"] = gameObject.GetInstanceID(),
                ["path"] = GameObjectToolUtils.GetGameObjectPath(gameObject),
                ["activeSelf"] = gameObject.activeSelf,
                ["components"] = componentNames
            };
        }

        private static bool TryResolveLayer(JToken layerToken, out int layer)
        {
            layer = -1;
            if (layerToken == null || layerToken.Type == JTokenType.Null)
            {
                return false;
            }

            if (layerToken.Type == JTokenType.Integer)
            {
                int candidate = layerToken.ToObject<int>();
                if (candidate < 0 || candidate > 31)
                {
                    return false;
                }

                layer = candidate;
                return true;
            }

            string layerString = layerToken.ToString();
            if (int.TryParse(layerString, out int parsedInt))
            {
                if (parsedInt < 0 || parsedInt > 31)
                {
                    return false;
                }

                layer = parsedInt;
                return true;
            }

            int namedLayer = LayerMask.NameToLayer(layerString);
            if (namedLayer < 0)
            {
                return false;
            }

            layer = namedLayer;
            return true;
        }
    }
}
