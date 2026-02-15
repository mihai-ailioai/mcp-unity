using System;
using System.Collections.Generic;
using System.Reflection;
using McpUnity.Unity;
using McpUnity.Utils;
using UnityEngine;
using UnityEditor;
using Newtonsoft.Json.Linq;

namespace McpUnity.Tools
{
    /// <summary>
    /// Tool for updating import settings on an asset's AssetImporter
    /// </summary>
    public class UpdateImportSettingsTool : McpToolBase
    {
        // Properties that should not be set - internal Unity bookkeeping
        private static readonly HashSet<string> BlockedProperties = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "name", "hideFlags", "assetPath", "assetBundleName", "assetBundleVariant",
            "userData", "importSettingsMissing"
        };

        public UpdateImportSettingsTool()
        {
            Name = "update_import_settings";
            Description = "Updates import settings on an asset's AssetImporter";
        }

        public override JObject Execute(JObject parameters)
        {
            string assetPath = parameters["assetPath"]?.ToObject<string>()?.Trim();
            string guid = parameters["guid"]?.ToObject<string>()?.Trim();
            JObject settings = parameters["settings"] as JObject;
            JObject platformOverrides = parameters["platformOverrides"] as JObject;

            // Resolve asset path
            string resolvedPath = MoveAssetTool.ResolveAssetPath(assetPath, guid, out string resolvedGuid, out JObject error);
            if (error != null) return error;

            // Validate: at least one of settings or platformOverrides
            if ((settings == null || settings.Count == 0) &&
                (platformOverrides == null || platformOverrides.Count == 0))
            {
                return McpUnitySocketHandler.CreateErrorResponse(
                    "At least one of 'settings' or 'platformOverrides' must be provided and non-empty",
                    "validation_error"
                );
            }

            // Get the importer
            AssetImporter importer = AssetImporter.GetAtPath(resolvedPath);
            if (importer == null)
            {
                return McpUnitySocketHandler.CreateErrorResponse(
                    $"No importer found for asset at '{resolvedPath}'. Native assets (scenes, prefabs, scriptable objects) may not have configurable import settings.",
                    "not_found_error"
                );
            }

            Type importerType = importer.GetType();
            List<string> errors = new List<string>();
            List<string> updated = new List<string>();

            // Apply generic property settings
            if (settings != null && settings.Count > 0)
            {
                ApplyPropertySettings(importer, importerType, settings, updated, errors);
            }

            // Apply platform overrides
            if (platformOverrides != null && platformOverrides.Count > 0)
            {
                ApplyPlatformOverrides(importer, platformOverrides, updated, errors);
            }

            // Save and reimport
            try
            {
                importer.SaveAndReimport();
            }
            catch (Exception ex)
            {
                return McpUnitySocketHandler.CreateErrorResponse(
                    $"Failed to save and reimport: {ex.Message}",
                    "reimport_error"
                );
            }

            if (errors.Count > 0 && updated.Count == 0)
            {
                return McpUnitySocketHandler.CreateErrorResponse(
                    $"All settings failed to apply:\n{string.Join("\n", errors)}",
                    "update_error"
                );
            }

            string message = $"Updated import settings for '{resolvedPath}' ({importerType.Name}): {updated.Count} applied";
            if (errors.Count > 0)
            {
                message += $", {errors.Count} failed";
            }

            McpLogger.LogInfo($"[MCP Unity] {message}");

            JObject data = new JObject
            {
                ["assetPath"] = resolvedPath,
                ["guid"] = resolvedGuid,
                ["importerType"] = importerType.Name,
                ["updatedProperties"] = new JArray(updated.ToArray()),
            };

            if (errors.Count > 0)
            {
                data["errors"] = new JArray(errors.ToArray());
            }

            return new JObject
            {
                ["success"] = true,
                ["type"] = "text",
                ["message"] = message,
                ["data"] = data
            };
        }

        /// <summary>
        /// Apply generic property settings via reflection.
        /// Handles struct properties with read-modify-write pattern.
        /// </summary>
        private void ApplyPropertySettings(AssetImporter importer, Type importerType, JObject settings,
            List<string> updated, List<string> errors)
        {
            foreach (var prop in settings.Properties())
            {
                string propName = prop.Name;
                JToken propValue = prop.Value;

                if (BlockedProperties.Contains(propName))
                {
                    errors.Add($"Property '{propName}' is internal and cannot be modified");
                    continue;
                }

                PropertyInfo propertyInfo = importerType.GetProperty(propName, BindingFlags.Public | BindingFlags.Instance);
                if (propertyInfo == null)
                {
                    errors.Add($"Property '{propName}' not found on {importerType.Name}");
                    continue;
                }

                if (!propertyInfo.CanWrite)
                {
                    errors.Add($"Property '{propName}' on {importerType.Name} is read-only");
                    continue;
                }

                if (propertyInfo.GetIndexParameters().Length > 0)
                {
                    errors.Add($"Property '{propName}' is an indexer and cannot be set directly");
                    continue;
                }

                try
                {
                    Type propType = propertyInfo.PropertyType;

                    // Struct properties: read-modify-write pattern for partial updates
                    if (propType.IsValueType && !propType.IsPrimitive && !propType.IsEnum
                        && propValue.Type == JTokenType.Object)
                    {
                        object currentStruct = propertyInfo.GetValue(importer);
                        object updatedStruct = ApplyJsonToStruct(currentStruct, propType, (JObject)propValue, errors, propName);
                        propertyInfo.SetValue(importer, updatedStruct);
                    }
                    else
                    {
                        object value = SerializedFieldUtils.ConvertJTokenToValue(propValue, propType);
                        propertyInfo.SetValue(importer, value);
                    }

                    updated.Add(propName);
                }
                catch (Exception ex)
                {
                    string innerMsg = ex.InnerException?.Message ?? ex.Message;
                    errors.Add($"Error setting '{propName}': {innerMsg}");
                }
            }
        }

        /// <summary>
        /// Apply JSON fields onto an existing struct value (read-modify-write).
        /// Only updates fields that are present in the JSON.
        /// </summary>
        private object ApplyJsonToStruct(object structValue, Type structType, JObject json,
            List<string> errors, string parentPropName)
        {
            // Box the struct so we can modify it
            object boxed = structValue;

            foreach (var prop in json.Properties())
            {
                // Try field first
                FieldInfo field = structType.GetField(prop.Name, BindingFlags.Public | BindingFlags.Instance);
                if (field != null)
                {
                    try
                    {
                        object value = SerializedFieldUtils.ConvertJTokenToValue(prop.Value, field.FieldType);
                        field.SetValue(boxed, value);
                    }
                    catch (Exception ex)
                    {
                        errors.Add($"Error setting '{parentPropName}.{prop.Name}': {ex.Message}");
                    }
                    continue;
                }

                // Try property
                PropertyInfo propInfo = structType.GetProperty(prop.Name, BindingFlags.Public | BindingFlags.Instance);
                if (propInfo != null && propInfo.CanWrite)
                {
                    try
                    {
                        object value = SerializedFieldUtils.ConvertJTokenToValue(prop.Value, propInfo.PropertyType);
                        propInfo.SetValue(boxed, value);
                    }
                    catch (Exception ex)
                    {
                        errors.Add($"Error setting '{parentPropName}.{prop.Name}': {ex.Message}");
                    }
                    continue;
                }

                errors.Add($"Field or property '{prop.Name}' not found on struct {structType.Name} (in '{parentPropName}')");
            }

            return boxed;
        }

        /// <summary>
        /// Apply platform-specific overrides. Routes to the correct API based on importer type.
        /// </summary>
        private void ApplyPlatformOverrides(AssetImporter importer, JObject platformOverrides,
            List<string> updated, List<string> errors)
        {
            if (importer is TextureImporter textureImporter)
            {
                ApplyTexturePlatformOverrides(textureImporter, platformOverrides, updated, errors);
            }
            else if (importer is AudioImporter audioImporter)
            {
                ApplyAudioPlatformOverrides(audioImporter, platformOverrides, updated, errors);
            }
            else
            {
                errors.Add($"Platform overrides are not supported for importer type '{importer.GetType().Name}'. Only TextureImporter and AudioImporter support platform overrides.");
            }
        }

        private void ApplyTexturePlatformOverrides(TextureImporter importer, JObject platformOverrides,
            List<string> updated, List<string> errors)
        {
            foreach (var platformProp in platformOverrides.Properties())
            {
                string platform = platformProp.Name;
                if (platformProp.Value.Type != JTokenType.Object)
                {
                    errors.Add($"Platform override for '{platform}' must be an object");
                    continue;
                }

                try
                {
                    // Read current settings for this platform
                    TextureImporterPlatformSettings currentSettings = importer.GetPlatformTextureSettings(platform);

                    // Apply JSON fields onto the struct
                    JObject overrideJson = (JObject)platformProp.Value;
                    object updatedSettings = ApplyJsonToStruct(currentSettings, typeof(TextureImporterPlatformSettings),
                        overrideJson, errors, $"platformOverrides.{platform}");

                    TextureImporterPlatformSettings typedSettings = (TextureImporterPlatformSettings)updatedSettings;

                    // Auto-set overridden=true unless explicitly set to false
                    if (overrideJson["overridden"] == null)
                    {
                        typedSettings.overridden = true;
                    }

                    importer.SetPlatformTextureSettings(typedSettings);
                    updated.Add($"platformOverrides.{platform}");
                }
                catch (Exception ex)
                {
                    errors.Add($"Error applying texture platform override for '{platform}': {ex.Message}");
                }
            }
        }

        private void ApplyAudioPlatformOverrides(AudioImporter importer, JObject platformOverrides,
            List<string> updated, List<string> errors)
        {
            foreach (var platformProp in platformOverrides.Properties())
            {
                string platform = platformProp.Name;
                if (platformProp.Value.Type != JTokenType.Object)
                {
                    errors.Add($"Platform override for '{platform}' must be an object");
                    continue;
                }

                try
                {
                    // Read current settings for this platform
                    AudioImporterSampleSettings currentSettings = importer.GetOverrideSampleSettings(platform);

                    // Apply JSON fields onto the struct
                    JObject overrideJson = (JObject)platformProp.Value;
                    object updatedSettings = ApplyJsonToStruct(currentSettings, typeof(AudioImporterSampleSettings),
                        overrideJson, errors, $"platformOverrides.{platform}");

                    AudioImporterSampleSettings typedSettings = (AudioImporterSampleSettings)updatedSettings;

                    bool result = importer.SetOverrideSampleSettings(platform, typedSettings);
                    if (!result)
                    {
                        errors.Add($"SetOverrideSampleSettings returned false for platform '{platform}'");
                    }
                    else
                    {
                        updated.Add($"platformOverrides.{platform}");
                    }
                }
                catch (Exception ex)
                {
                    errors.Add($"Error applying audio platform override for '{platform}': {ex.Message}");
                }
            }
        }
    }
}
