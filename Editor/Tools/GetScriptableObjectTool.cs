using System;
using McpUnity.Unity;
using McpUnity.Utils;
using UnityEngine;
using UnityEditor;
using Newtonsoft.Json.Linq;

namespace McpUnity.Tools
{
    /// <summary>
    /// Tool for reading field values from existing ScriptableObject assets
    /// </summary>
    public class GetScriptableObjectTool : McpToolBase
    {
        public GetScriptableObjectTool()
        {
            Name = "get_scriptable_object";
            Description = "Reads all user-defined field values from a ScriptableObject asset";
        }

        /// <summary>
        /// Execute the GetScriptableObject tool with the provided parameters
        /// </summary>
        public override JObject Execute(JObject parameters)
        {
            string assetPath = parameters["assetPath"]?.ToObject<string>();
            string guid = parameters["guid"]?.ToObject<string>();

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

            // Read fields
            JObject fieldData;
            try
            {
                fieldData = SerializedFieldUtils.ReadFieldsToJson(so);
            }
            catch (Exception ex)
            {
                return McpUnitySocketHandler.CreateErrorResponse(
                    $"Failed to read fields: {ex.Message}",
                    "read_error"
                );
            }

            string resolvedGuid = AssetDatabase.AssetPathToGUID(resolvedPath);

            McpLogger.LogInfo($"[MCP Unity] Read ScriptableObject '{so.GetType().Name}' at '{resolvedPath}' ({fieldData.Count} fields)");

            return new JObject
            {
                ["success"] = true,
                ["type"] = "text",
                ["message"] = $"ScriptableObject '{so.GetType().Name}' at '{resolvedPath}' with {fieldData.Count} fields",
                ["data"] = new JObject
                {
                    ["assetPath"] = resolvedPath,
                    ["typeName"] = so.GetType().FullName,
                    ["guid"] = resolvedGuid,
                    ["name"] = so.name,
                    ["fieldData"] = fieldData
                }
            };
        }
    }
}
