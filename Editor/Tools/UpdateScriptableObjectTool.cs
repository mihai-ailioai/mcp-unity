using System;
using McpUnity.Unity;
using McpUnity.Utils;
using UnityEngine;
using UnityEditor;
using Newtonsoft.Json.Linq;

namespace McpUnity.Tools
{
    /// <summary>
    /// Tool for updating field values on existing ScriptableObject assets
    /// </summary>
    public class UpdateScriptableObjectTool : McpToolBase
    {
        public UpdateScriptableObjectTool()
        {
            Name = "update_scriptable_object";
            Description = "Updates field values on an existing ScriptableObject asset";
        }

        /// <summary>
        /// Execute the UpdateScriptableObject tool with the provided parameters
        /// </summary>
        public override JObject Execute(JObject parameters)
        {
            string assetPath = parameters["assetPath"]?.ToObject<string>();
            string guid = parameters["guid"]?.ToObject<string>();
            JObject fieldData = parameters["fieldData"] as JObject;

            // Trim inputs
            if (!string.IsNullOrEmpty(assetPath)) assetPath = assetPath.Trim();
            if (!string.IsNullOrEmpty(guid)) guid = guid.Trim();

            // Validate: at least one identifier required
            if (string.IsNullOrEmpty(assetPath) && string.IsNullOrEmpty(guid))
            {
                return McpUnitySocketHandler.CreateErrorResponse(
                    "Either 'assetPath' or 'guid' must be provided",
                    "validation_error"
                );
            }

            // Validate fieldData
            if (fieldData == null || fieldData.Count == 0)
            {
                return McpUnitySocketHandler.CreateErrorResponse(
                    "Required parameter 'fieldData' must be provided and non-empty",
                    "validation_error"
                );
            }

            // Resolve path from guid if provided
            string guidResolvedPath = null;
            if (!string.IsNullOrEmpty(guid))
            {
                guidResolvedPath = AssetDatabase.GUIDToAssetPath(guid);
                if (string.IsNullOrEmpty(guidResolvedPath))
                {
                    return McpUnitySocketHandler.CreateErrorResponse(
                        $"No asset found for GUID '{guid}'",
                        "not_found_error"
                    );
                }
            }

            // If both provided, validate they agree
            if (!string.IsNullOrEmpty(assetPath) && !string.IsNullOrEmpty(guidResolvedPath))
            {
                if (!string.Equals(assetPath, guidResolvedPath, StringComparison.OrdinalIgnoreCase))
                {
                    return McpUnitySocketHandler.CreateErrorResponse(
                        $"Asset path mismatch: assetPath='{assetPath}' but GUID '{guid}' resolves to '{guidResolvedPath}'",
                        "validation_error"
                    );
                }
            }

            string resolvedPath = !string.IsNullOrEmpty(assetPath) ? assetPath : guidResolvedPath;

            // Load the ScriptableObject
            ScriptableObject so = AssetDatabase.LoadAssetAtPath<ScriptableObject>(resolvedPath);
            if (so == null)
            {
                // Check if an asset exists at all at this path
                var existingAsset = AssetDatabase.LoadMainAssetAtPath(resolvedPath);
                if (existingAsset != null)
                {
                    return McpUnitySocketHandler.CreateErrorResponse(
                        $"Asset at '{resolvedPath}' is a {existingAsset.GetType().Name}, not a ScriptableObject",
                        "type_error"
                    );
                }

                return McpUnitySocketHandler.CreateErrorResponse(
                    $"No asset found at path '{resolvedPath}'",
                    "not_found_error"
                );
            }

            // Note: Field updates are applied incrementally. If some fields fail,
            // earlier fields may already be set. Undo can revert all changes.
            bool success = SerializedFieldUtils.UpdateFieldsFromJson(so, fieldData, out string errorMessage);
            if (!success)
            {
                return McpUnitySocketHandler.CreateErrorResponse(
                    $"Failed to update fields: {errorMessage}",
                    "update_error"
                );
            }

            // Save changes
            try
            {
                EditorUtility.SetDirty(so);
                AssetDatabase.SaveAssets();
            }
            catch (Exception ex)
            {
                return McpUnitySocketHandler.CreateErrorResponse(
                    $"Failed to save ScriptableObject changes: {ex.Message}",
                    "save_error"
                );
            }

            string resolvedGuid = AssetDatabase.AssetPathToGUID(resolvedPath);

            McpLogger.LogInfo($"[MCP Unity] Updated ScriptableObject '{so.GetType().Name}' at '{resolvedPath}'");

            return new JObject
            {
                ["success"] = true,
                ["type"] = "text",
                ["message"] = $"Successfully updated ScriptableObject '{so.GetType().Name}' at '{resolvedPath}'",
                ["data"] = new JObject
                {
                    ["assetPath"] = resolvedPath,
                    ["typeName"] = so.GetType().FullName,
                    ["guid"] = resolvedGuid
                }
            };
        }
    }
}
