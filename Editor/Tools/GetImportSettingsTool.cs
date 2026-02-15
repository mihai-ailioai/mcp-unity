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
    /// Tool for reading import settings from an asset's AssetImporter
    /// </summary>
    public class GetImportSettingsTool : McpToolBase
    {
        // Properties to exclude from output — internal Unity bookkeeping, not user-facing import settings
        private static readonly HashSet<string> ExcludedProperties = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "name", "hideFlags", "assetPath", "assetBundleName", "assetBundleVariant",
            "userData", "importSettingsMissing"
        };

        // Known platform names for reading overrides
        private static readonly string[] PlatformNames = new string[]
        {
            "Standalone", "Android", "iPhone", "WebGL", "Windows Store Apps",
            "PS4", "PS5", "XboxOne", "GameCoreScarlett", "Nintendo Switch", "tvOS", "LinuxHeadlessSimulation"
        };

        public GetImportSettingsTool()
        {
            Name = "get_import_settings";
            Description = "Reads import settings from an asset's AssetImporter";
        }

        public override JObject Execute(JObject parameters)
        {
            string assetPath = parameters["assetPath"]?.ToObject<string>()?.Trim();
            string guid = parameters["guid"]?.ToObject<string>()?.Trim();

            // Resolve asset path using shared utility
            string resolvedPath = MoveAssetTool.ResolveAssetPath(assetPath, guid, out string resolvedGuid, out JObject error);
            if (error != null) return error;

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

            // Read all public writable properties via reflection
            JObject settings = ReadImporterProperties(importer, importerType);

            // Build response
            JObject data = new JObject
            {
                ["assetPath"] = resolvedPath,
                ["guid"] = resolvedGuid,
                ["importerType"] = importerType.Name
            };

            data["settings"] = settings;

            // Read platform overrides for supported importer types
            JObject platformOverrides = ReadPlatformOverrides(importer);
            if (platformOverrides != null)
            {
                data["platformOverrides"] = platformOverrides;
            }

            McpLogger.LogInfo($"[MCP Unity] Read import settings for '{resolvedPath}' (importer: {importerType.Name})");

            return new JObject
            {
                ["success"] = true,
                ["type"] = "text",
                ["message"] = $"Import settings for '{resolvedPath}' (importer: {importerType.Name})",
                ["data"] = data
            };
        }

        /// <summary>
        /// Read all public writable instance properties from an importer, excluding internal ones.
        /// </summary>
        private JObject ReadImporterProperties(AssetImporter importer, Type importerType)
        {
            JObject settings = new JObject();

            PropertyInfo[] properties = importerType.GetProperties(BindingFlags.Public | BindingFlags.Instance);

            foreach (PropertyInfo prop in properties)
            {
                // Skip excluded properties
                if (ExcludedProperties.Contains(prop.Name))
                {
                    continue;
                }

                // Skip read-only properties (no setter)
                if (!prop.CanWrite)
                {
                    continue;
                }

                // Skip indexers
                if (prop.GetIndexParameters().Length > 0)
                {
                    continue;
                }

                // Skip obsolete properties
                if (prop.GetCustomAttribute<ObsoleteAttribute>() != null)
                {
                    continue;
                }

                try
                {
                    object value = prop.GetValue(importer);
                    settings[prop.Name] = ConvertPropertyValueToJToken(value, prop.PropertyType);
                }
                catch (Exception ex)
                {
                    // Some properties throw when accessed in certain states — skip silently
                    McpLogger.LogError($"[MCP Unity] Error reading property '{prop.Name}' on {importerType.Name}: {ex.Message}");
                }
            }

            return settings;
        }

        /// <summary>
        /// Convert a property value to JToken. Similar to SerializedFieldUtils.ConvertValueToJToken
        /// but also handles struct types with field-by-field serialization.
        /// </summary>
        internal static JToken ConvertPropertyValueToJToken(object value, Type valueType)
        {
            if (value == null)
            {
                return JValue.CreateNull();
            }

            // Handle UnityEngine.Object references
            if (value is UnityEngine.Object unityObj)
            {
                if (unityObj == null) return JValue.CreateNull();

                string objAssetPath = AssetDatabase.GetAssetPath(unityObj);
                if (!string.IsNullOrEmpty(objAssetPath))
                {
                    return new JObject
                    {
                        ["$ref"] = "asset",
                        ["assetPath"] = objAssetPath,
                        ["guid"] = AssetDatabase.AssetPathToGUID(objAssetPath),
                        ["typeName"] = unityObj.GetType().Name
                    };
                }
                return JValue.CreateNull();
            }

            // Enums — serialize as string
            if (valueType.IsEnum)
            {
                return value.ToString();
            }

            // Unity value types
            if (value is Vector2 v2) return new JObject { ["x"] = v2.x, ["y"] = v2.y };
            if (value is Vector3 v3) return new JObject { ["x"] = v3.x, ["y"] = v3.y, ["z"] = v3.z };
            if (value is Vector4 v4) return new JObject { ["x"] = v4.x, ["y"] = v4.y, ["z"] = v4.z, ["w"] = v4.w };
            if (value is Color c) return new JObject { ["r"] = c.r, ["g"] = c.g, ["b"] = c.b, ["a"] = c.a };

            // Primitives and strings
            if (valueType.IsPrimitive || valueType == typeof(string))
            {
                try { return JToken.FromObject(value); }
                catch { return $"[{valueType.Name}]"; }
            }

            // Structs (e.g. TextureImporterSettings, AudioImporterSampleSettings) — serialize field-by-field
            if (valueType.IsValueType && !valueType.IsPrimitive && !valueType.IsEnum)
            {
                return SerializeStructToJObject(value, valueType);
            }

            // Arrays/lists
            if (value is System.Collections.IList list)
            {
                Type elementType = valueType.IsArray
                    ? valueType.GetElementType()
                    : (valueType.IsGenericType ? valueType.GetGenericArguments()[0] : typeof(object));
                var arr = new JArray();
                foreach (object item in list)
                {
                    arr.Add(ConvertPropertyValueToJToken(item, elementType));
                }
                return arr;
            }

            // Fallback
            try { return JToken.FromObject(value); }
            catch { return $"[{valueType.Name}]"; }
        }

        /// <summary>
        /// Serialize a struct to a JObject by reading its public fields and properties.
        /// </summary>
        private static JObject SerializeStructToJObject(object structValue, Type structType)
        {
            JObject obj = new JObject();

            // Read public fields
            foreach (FieldInfo field in structType.GetFields(BindingFlags.Public | BindingFlags.Instance))
            {
                if (field.GetCustomAttribute<ObsoleteAttribute>() != null) continue;
                try
                {
                    object val = field.GetValue(structValue);
                    obj[field.Name] = ConvertPropertyValueToJToken(val, field.FieldType);
                }
                catch { }
            }

            // Read public writable properties (some structs expose settings as properties)
            foreach (PropertyInfo prop in structType.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (!prop.CanRead || prop.GetIndexParameters().Length > 0) continue;
                if (prop.GetCustomAttribute<ObsoleteAttribute>() != null) continue;
                if (obj.ContainsKey(prop.Name)) continue;
                try
                {
                    object val = prop.GetValue(structValue);
                    obj[prop.Name] = ConvertPropertyValueToJToken(val, prop.PropertyType);
                }
                catch { }
            }

            return obj;
        }

        /// <summary>
        /// Read platform-specific overrides for supported importer types.
        /// Returns null for unsupported importer types.
        /// </summary>
        private JObject ReadPlatformOverrides(AssetImporter importer)
        {
            if (importer is TextureImporter textureImporter)
            {
                return ReadTexturePlatformOverrides(textureImporter);
            }

            if (importer is AudioImporter audioImporter)
            {
                return ReadAudioPlatformOverrides(audioImporter);
            }

            return null;
        }

        private JObject ReadTexturePlatformOverrides(TextureImporter importer)
        {
            JObject overrides = new JObject();

            foreach (string platform in PlatformNames)
            {
                TextureImporterPlatformSettings platformSettings = importer.GetPlatformTextureSettings(platform);
                if (platformSettings.overridden)
                {
                    overrides[platform] = SerializeStructToJObject(platformSettings, typeof(TextureImporterPlatformSettings));
                }
            }

            return overrides.Count > 0 ? overrides : null;
        }

        private JObject ReadAudioPlatformOverrides(AudioImporter importer)
        {
            JObject overrides = new JObject();

            foreach (string platform in PlatformNames)
            {
                if (importer.ContainsSampleSettingsOverride(platform))
                {
                    AudioImporterSampleSettings sampleSettings = importer.GetOverrideSampleSettings(platform);
                    overrides[platform] = SerializeStructToJObject(sampleSettings, typeof(AudioImporterSampleSettings));
                }
            }

            return overrides.Count > 0 ? overrides : null;
        }
    }
}
