using System;
using System.IO;
using UnityEngine;
using UnityEditor;
using McpUnity.Unity;
using McpUnity.Utils;
using Newtonsoft.Json.Linq;

namespace McpUnity.Tools
{
    /// <summary>
    /// Tool for saving an existing scene GameObject as a prefab asset.
    /// </summary>
    public class SaveAsPrefabTool : McpToolBase
    {
        public SaveAsPrefabTool()
        {
            Name = "save_as_prefab";
            Description = "Saves an existing scene GameObject as a prefab asset, with optional overwrite and prefab variant support";
        }

        public override JObject Execute(JObject parameters)
        {
            int? instanceId = parameters["instanceId"]?.ToObject<int?>();
            string objectPath = parameters["objectPath"]?.ToObject<string>();
            string prefabPath = parameters["prefabPath"]?.ToObject<string>();
            bool overwrite = parameters["overwrite"]?.ToObject<bool?>() ?? false;
            bool variant = parameters["variant"]?.ToObject<bool?>() ?? false;

            if (!instanceId.HasValue && string.IsNullOrEmpty(objectPath))
            {
                return McpUnitySocketHandler.CreateErrorResponse(
                    "Either 'instanceId' or 'objectPath' must be provided",
                    "validation_error"
                );
            }

            GameObject gameObject = null;
            if (instanceId.HasValue)
            {
                gameObject = EditorUtility.InstanceIDToObject(instanceId.Value) as GameObject;
            }
            else if (!string.IsNullOrEmpty(objectPath))
            {
                gameObject = GameObject.Find(objectPath);
            }

            if (gameObject == null)
            {
                string identifierInfo = instanceId.HasValue ? $"instanceId '{instanceId.Value}'" : $"objectPath '{objectPath}'";
                return McpUnitySocketHandler.CreateErrorResponse(
                    $"GameObject not found using {identifierInfo}",
                    "validation_error"
                );
            }

            bool isSourcePrefabInstance = PrefabUtility.IsPartOfPrefabInstance(gameObject);

            if (variant && !isSourcePrefabInstance)
            {
                return McpUnitySocketHandler.CreateErrorResponse(
                    "Parameter 'variant' can only be true when the source GameObject is a prefab instance",
                    "validation_error"
                );
            }

            // When variant is requested, ensure we target the outermost prefab instance root
            // to get predictable variant behavior
            if (variant)
            {
                GameObject outermostRoot = PrefabUtility.GetOutermostPrefabInstanceRoot(gameObject);
                if (outermostRoot != null && outermostRoot != gameObject)
                {
                    McpLogger.LogWarning($"[MCP Unity] Targeting outermost prefab root '{outermostRoot.name}' instead of child '{gameObject.name}' for variant creation");
                    gameObject = outermostRoot;
                }
            }

            try
            {
                bool hasCustomPrefabPath = !string.IsNullOrEmpty(prefabPath);
                string targetPrefabPath = hasCustomPrefabPath
                    ? NormalizePrefabPath(prefabPath)
                    : BuildDefaultPrefabPath(gameObject.name);

                EnsurePrefabDirectoryExists(targetPrefabPath);

                if (hasCustomPrefabPath)
                {
                    if (File.Exists(targetPrefabPath) && !overwrite)
                    {
                        return McpUnitySocketHandler.CreateErrorResponse(
                            $"Prefab already exists at path '{targetPrefabPath}'. Set 'overwrite' to true to replace it",
                            "validation_error"
                        );
                    }
                }
                else if (!overwrite)
                {
                    targetPrefabPath = GetUniquePrefabPath(targetPrefabPath);
                }

                string basePrefabPath = null;
                if (variant)
                {
                    UnityEngine.Object sourcePrefab = PrefabUtility.GetCorrespondingObjectFromSource(gameObject);
                    basePrefabPath = AssetDatabase.GetAssetPath(sourcePrefab);

                    if (string.IsNullOrEmpty(basePrefabPath))
                    {
                        return McpUnitySocketHandler.CreateErrorResponse(
                            "Unable to resolve base prefab path for variant creation",
                            "validation_error"
                        );
                    }
                }

                Undo.RegisterFullObjectHierarchyUndo(gameObject, "Save As Prefab");

                // When source is a prefab instance and variant=false, unpack to create
                // a standalone prefab (SaveAsPrefabAssetAndConnect would otherwise
                // automatically create a variant)
                if (isSourcePrefabInstance && !variant)
                {
                    PrefabUtility.UnpackPrefabInstance(gameObject, PrefabUnpackMode.OutermostRoot, InteractionMode.UserAction);
                }

                PrefabUtility.SaveAsPrefabAssetAndConnect(gameObject, targetPrefabPath, InteractionMode.UserAction, out bool success);

                if (!success)
                {
                    return McpUnitySocketHandler.CreateErrorResponse(
                        $"Failed to save GameObject '{gameObject.name}' as prefab at '{targetPrefabPath}'",
                        "save_error"
                    );
                }

                AssetDatabase.Refresh();

                // Verify actual variant status from the saved asset
                bool actuallyIsVariant = PrefabUtility.IsPartOfVariantPrefab(gameObject);

                JObject data = new JObject
                {
                    ["prefabPath"] = targetPrefabPath,
                    ["instanceId"] = gameObject.GetInstanceID(),
                    ["name"] = gameObject.name,
                    ["isVariant"] = actuallyIsVariant
                };

                if (!string.IsNullOrEmpty(basePrefabPath))
                {
                    data["basePrefabPath"] = basePrefabPath;
                }

                McpLogger.LogInfo($"Saved GameObject '{gameObject.name}' as prefab at '{targetPrefabPath}' (variant: {actuallyIsVariant})");

                return new JObject
                {
                    ["success"] = true,
                    ["type"] = "text",
                    ["message"] = $"Successfully saved GameObject '{gameObject.name}' as prefab at '{targetPrefabPath}'",
                    ["data"] = data
                };
            }
            catch (Exception ex)
            {
                return McpUnitySocketHandler.CreateErrorResponse(
                    $"Error saving GameObject as prefab: {ex.Message}",
                    "save_error"
                );
            }
        }

        private string BuildDefaultPrefabPath(string gameObjectName)
        {
            return $"Assets/{gameObjectName}.prefab";
        }

        private string NormalizePrefabPath(string prefabPath)
        {
            string normalizedPath = prefabPath.Replace('\\', '/').Trim().TrimStart('/');

            // Reject path traversal segments
            if (normalizedPath.Contains(".."))
            {
                throw new ArgumentException($"Path traversal ('..') is not allowed in prefab path: '{prefabPath}'");
            }

            if (!normalizedPath.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase))
            {
                normalizedPath += ".prefab";
            }

            // Reject paths that would result in just ".prefab" (empty filename)
            string fileName = Path.GetFileNameWithoutExtension(normalizedPath);
            if (string.IsNullOrWhiteSpace(fileName))
            {
                throw new ArgumentException($"Prefab path must contain a valid filename: '{prefabPath}'");
            }

            if (!normalizedPath.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase))
            {
                normalizedPath = "Assets/" + normalizedPath;
            }

            return normalizedPath;
        }

        private string GetUniquePrefabPath(string prefabPath)
        {
            if (!File.Exists(prefabPath))
            {
                return prefabPath;
            }

            string directory = Path.GetDirectoryName(prefabPath)?.Replace('\\', '/') ?? "Assets";
            string fileName = Path.GetFileNameWithoutExtension(prefabPath);
            int counter = 1;

            string uniquePath;
            do
            {
                uniquePath = $"{directory}/{fileName}_{counter}.prefab";
                counter++;
            }
            while (File.Exists(uniquePath));

            return uniquePath;
        }

        private void EnsurePrefabDirectoryExists(string prefabPath)
        {
            string directory = Path.GetDirectoryName(prefabPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }
        }
    }
}
