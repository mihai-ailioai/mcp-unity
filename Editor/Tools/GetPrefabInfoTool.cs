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
    /// </summary>
    public class GetPrefabInfoTool : McpToolBase
    {
        public GetPrefabInfoTool()
        {
            Name = "get_prefab_info";
            Description = "Get detailed information about a prefab asset by asset path, without entering Prefab Mode or instantiating in scene. Returns hierarchy, components, and prefab metadata (variant status, base prefab path). Use 'summary' for a lightweight overview (names, instanceIds, component type names only).";
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

            // Load the prefab asset (read-only, no editing context)
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(assetPath);
            if (prefab == null)
            {
                return McpUnitySocketHandler.CreateErrorResponse(
                    $"Prefab not found at path: {assetPath}",
                    "not_found_error"
                );
            }

            // Serialize using existing methods
            JObject prefabData = summary
                ? GetGameObjectResource.GameObjectToSummaryJObject(prefab)
                : GetGameObjectResource.GameObjectToJObject(prefab, true);

            // Add prefab-specific metadata
            string assetGuid = AssetDatabase.AssetPathToGUID(assetPath);
            bool isVariant = PrefabUtility.IsPartOfVariantPrefab(prefab);

            prefabData["assetPath"] = assetPath;
            prefabData["assetGuid"] = assetGuid;
            prefabData["isVariant"] = isVariant;

            if (isVariant)
            {
                var sourceObject = PrefabUtility.GetCorrespondingObjectFromSource(prefab);
                if (sourceObject != null)
                {
                    prefabData["basePrefabPath"] = AssetDatabase.GetAssetPath(sourceObject);
                }
            }

            return new JObject
            {
                ["success"] = true,
                ["message"] = summary
                    ? $"Retrieved summary for prefab '{prefab.name}' at {assetPath}"
                    : $"Retrieved detailed info for prefab '{prefab.name}' at {assetPath}",
                ["prefab"] = prefabData,
                ["assetPath"] = assetPath,
                ["assetGuid"] = assetGuid
            };
        }
    }
}
