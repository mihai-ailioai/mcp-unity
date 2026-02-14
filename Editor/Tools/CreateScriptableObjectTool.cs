using System;
using System.IO;
using McpUnity.Unity;
using McpUnity.Utils;
using UnityEngine;
using UnityEditor;
using Newtonsoft.Json.Linq;

namespace McpUnity.Tools
{
    /// <summary>
    /// Tool for creating ScriptableObject assets in the Unity Editor
    /// </summary>
    public class CreateScriptableObjectTool : McpToolBase
    {
        public CreateScriptableObjectTool()
        {
            Name = "create_scriptable_object";
            Description = "Creates a ScriptableObject asset of the specified type with optional initial field values";
        }

        public override JObject Execute(JObject parameters)
        {
            string scriptableObjectType = parameters["scriptableObjectType"]?.ToObject<string>();
            string savePath = parameters["savePath"]?.ToObject<string>();
            JObject fieldData = parameters["fieldData"] as JObject;
            bool overwrite = parameters["overwrite"]?.ToObject<bool>() ?? false;

            if (!string.IsNullOrEmpty(savePath))
            {
                savePath = savePath.Trim();
            }

            // Validate required parameter
            if (string.IsNullOrEmpty(scriptableObjectType) || string.IsNullOrWhiteSpace(scriptableObjectType))
            {
                return McpUnitySocketHandler.CreateErrorResponse(
                    "Required parameter 'scriptableObjectType' not provided",
                    "validation_error"
                );
            }

            scriptableObjectType = scriptableObjectType.Trim();

            // Resolve the ScriptableObject type
            Type soType = SerializedFieldUtils.FindType(scriptableObjectType, typeof(ScriptableObject));
            if (soType == null)
            {
                return McpUnitySocketHandler.CreateErrorResponse(
                    $"ScriptableObject type '{scriptableObjectType}' not found. Ensure the class exists and inherits from ScriptableObject.",
                    "type_error"
                );
            }

            // Resolve and validate save path
            if (string.IsNullOrEmpty(savePath))
            {
                savePath = $"Assets/{soType.Name}.asset";
            }

            // Validate path
            if (!savePath.StartsWith("Assets/"))
            {
                return McpUnitySocketHandler.CreateErrorResponse(
                    "Save path must start with 'Assets/'",
                    "validation_error"
                );
            }

            if (savePath.Contains(".."))
            {
                return McpUnitySocketHandler.CreateErrorResponse(
                    "Save path must not contain '..' path traversal",
                    "validation_error"
                );
            }

            if (!savePath.EndsWith(".asset"))
            {
                savePath += ".asset";
            }

            string fileName = Path.GetFileNameWithoutExtension(savePath);
            if (string.IsNullOrWhiteSpace(fileName))
            {
                return McpUnitySocketHandler.CreateErrorResponse(
                    "Save path must contain a valid filename",
                    "validation_error"
                );
            }

            // Handle collision
            if (!overwrite && !string.IsNullOrEmpty(AssetDatabase.AssetPathToGUID(savePath, AssetPathToGUIDOptions.OnlyExistingAssets)))
            {
                string basePath = savePath.Substring(0, savePath.Length - ".asset".Length);
                int counter = 1;
                const int maxAttempts = 1000;
                do
                {
                    savePath = $"{basePath}_{counter}.asset";
                    counter++;
                    if (counter > maxAttempts)
                    {
                        return McpUnitySocketHandler.CreateErrorResponse(
                            $"Could not find available filename after {maxAttempts} attempts",
                            "validation_error"
                        );
                    }
                } while (!string.IsNullOrEmpty(AssetDatabase.AssetPathToGUID(savePath, AssetPathToGUIDOptions.OnlyExistingAssets)));

                fileName = Path.GetFileNameWithoutExtension(savePath);
            }

            // Validate the type is instantiable
            if (soType.IsAbstract)
            {
                return McpUnitySocketHandler.CreateErrorResponse(
                    $"Cannot create instance of abstract ScriptableObject type '{scriptableObjectType}'",
                    "type_error"
                );
            }

            // Create the ScriptableObject instance
            ScriptableObject instance = ScriptableObject.CreateInstance(soType);
            if (instance == null)
            {
                return McpUnitySocketHandler.CreateErrorResponse(
                    $"Failed to create instance of ScriptableObject type '{scriptableObjectType}'",
                    "creation_error"
                );
            }

            instance.name = fileName;

            // Set field data if provided
            if (fieldData != null && fieldData.Count > 0)
            {
                bool fieldSuccess = SerializedFieldUtils.UpdateFieldsFromJson(instance, fieldData, out string fieldError);
                if (!fieldSuccess)
                {
                    // Clean up the instance since we haven't saved it yet
                    UnityEngine.Object.DestroyImmediate(instance);
                    return McpUnitySocketHandler.CreateErrorResponse(
                        $"Failed to set field data: {fieldError}",
                        "field_error"
                    );
                }
            }

            string directory = Path.GetDirectoryName(savePath);
            try
            {
                if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                // Delete existing asset if overwrite is true
                if (overwrite && !string.IsNullOrEmpty(AssetDatabase.AssetPathToGUID(savePath, AssetPathToGUIDOptions.OnlyExistingAssets)))
                {
                    AssetDatabase.DeleteAsset(savePath);
                }

                AssetDatabase.CreateAsset(instance, savePath);
                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();
            }
            catch (Exception ex)
            {
                UnityEngine.Object.DestroyImmediate(instance);
                return McpUnitySocketHandler.CreateErrorResponse(
                    $"Failed to create ScriptableObject asset: {ex.Message}",
                    "creation_error"
                );
            }

            string guid = AssetDatabase.AssetPathToGUID(savePath);

            McpLogger.LogInfo($"[MCP Unity] Created ScriptableObject '{soType.Name}' at '{savePath}' (GUID: {guid})");

            return new JObject
            {
                ["success"] = true,
                ["type"] = "text",
                ["message"] = $"Successfully created ScriptableObject '{soType.Name}' at '{savePath}'",
                ["data"] = new JObject
                {
                    ["assetPath"] = savePath,
                    ["typeName"] = soType.FullName,
                    ["guid"] = guid
                }
            };
        }
    }
}
