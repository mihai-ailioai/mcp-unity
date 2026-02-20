using System;
using UnityEngine;
using UnityEditor;
using Newtonsoft.Json.Linq;
using McpUnity.Unity;
using McpUnity.Utils;

namespace McpUnity.Tools
{
    /// <summary>
    /// Tool for creating prefabs with optional MonoBehaviour scripts
    /// </summary>
    public class CreatePrefabTool : McpToolBase
    {
        public CreatePrefabTool()
        {
            Name = "create_prefab";
            Description = "Creates a prefab asset with an optional MonoBehaviour component and serialized field values. " +
                          "Use 'savePath' to specify the full asset path (e.g., 'Assets/Prefabs/MyPrefab.prefab'). " +
                          "If omitted, defaults to 'Assets/{prefabName}.prefab'. " +
                          "Component is resolved by class name (short or fully qualified).";
        }
        
        public override JObject Execute(JObject parameters)
        {
            string componentName = parameters["componentName"]?.ToObject<string>();
            string prefabName = parameters["prefabName"]?.ToObject<string>();
            string savePath = parameters["savePath"]?.ToObject<string>();
            JObject fieldValues = parameters["fieldValues"]?.ToObject<JObject>();
            
            if (string.IsNullOrEmpty(prefabName))
            {
                return McpUnitySocketHandler.CreateErrorResponse(
                    "Required parameter 'prefabName' not provided", 
                    "validation_error"
                );
            }
            
            // Build the prefab path
            string prefabPath;
            if (!string.IsNullOrEmpty(savePath))
            {
                prefabPath = savePath;
                if (!prefabPath.StartsWith("Assets/"))
                {
                    return McpUnitySocketHandler.CreateErrorResponse(
                        "savePath must start with 'Assets/'",
                        "validation_error"
                    );
                }
                if (!prefabPath.EndsWith(".prefab"))
                {
                    prefabPath += ".prefab";
                }
            }
            else
            {
                prefabPath = $"Assets/{prefabName}.prefab";
            }

            // Ensure the directory exists
            string directory = System.IO.Path.GetDirectoryName(prefabPath);
            if (!string.IsNullOrEmpty(directory) && !System.IO.Directory.Exists(directory))
            {
                System.IO.Directory.CreateDirectory(directory);
                AssetDatabase.Refresh();
            }

            // Generate unique path if prefab already exists
            if (!string.IsNullOrEmpty(AssetDatabase.AssetPathToGUID(prefabPath, AssetPathToGUIDOptions.OnlyExistingAssets)))
            {
                string basePath = prefabPath.Substring(0, prefabPath.Length - ".prefab".Length);
                int counter = 1;
                string candidatePath = $"{basePath}_{counter}.prefab";
                while (!string.IsNullOrEmpty(AssetDatabase.AssetPathToGUID(candidatePath, AssetPathToGUIDOptions.OnlyExistingAssets)))
                {
                    counter++;
                    candidatePath = $"{basePath}_{counter}.prefab";
                }
                prefabPath = candidatePath;
            }

            // Create a temporary GameObject
            GameObject tempObject = new GameObject(prefabName);

            // Add component if provided
            if (!string.IsNullOrEmpty(componentName))
            {
                Type scriptType = SerializedFieldUtils.FindType(componentName, typeof(Component));
                if (scriptType == null)
                {
                    // Log available assemblies for debugging
                    var assemblies = AppDomain.CurrentDomain.GetAssemblies();
                    var asmCSharp = System.Array.Find(assemblies, a => a.GetName().Name == "Assembly-CSharp");
                    string diagnostics = asmCSharp != null 
                        ? $"Assembly-CSharp loaded with {asmCSharp.GetTypes().Length} types" 
                        : "Assembly-CSharp NOT loaded (scripts may still be compiling)";
                    McpLogger.LogError($"Component type '{componentName}' not found. {diagnostics}");
                    
                    UnityEngine.Object.DestroyImmediate(tempObject);
                    return McpUnitySocketHandler.CreateErrorResponse(
                        $"Component type '{componentName}' not found. {diagnostics}. Make sure the script is compiled and the name is correct (short name or fully qualified). " +
                        "If this happens right after creating a script, try calling recompile_scripts first, or use create_prefab without a component and then modify_prefab to add it.", 
                        "component_error"
                    );
                }

                Component component;
                try
                {
                    component = tempObject.AddComponent(scriptType);
                }
                catch (Exception ex)
                {
                    UnityEngine.Object.DestroyImmediate(tempObject);
                    return McpUnitySocketHandler.CreateErrorResponse(
                        $"Failed to add component '{componentName}' ({scriptType.FullName}): {ex.Message}", 
                        "component_error"
                    );
                }

                if (component == null)
                {
                    UnityEngine.Object.DestroyImmediate(tempObject);
                    return McpUnitySocketHandler.CreateErrorResponse(
                        $"AddComponent returned null for '{componentName}' ({scriptType.FullName}). The component may have requirements that aren't met.", 
                        "component_error"
                    );
                }

                // Apply field values if provided
                if (fieldValues != null && fieldValues.Count > 0)
                {
                    try
                    {
                        ApplyFieldValues(fieldValues, component);
                    }
                    catch (Exception ex)
                    {
                        McpLogger.LogError($"Failed to apply field values to '{componentName}': {ex.Message}");
                        // Continue — prefab creation shouldn't fail just because field values couldn't be set
                    }
                }
            }
            
            // Create the prefab
            bool success = false;
            PrefabUtility.SaveAsPrefabAsset(tempObject, prefabPath, out success);
            
            // Clean up temporary object
            UnityEngine.Object.DestroyImmediate(tempObject);
            
            if (!success)
            {
                return McpUnitySocketHandler.CreateErrorResponse(
                    $"Failed to save prefab at '{prefabPath}'",
                    "prefab_save_error"
                );
            }

            // Refresh the asset database
            AssetDatabase.Refresh();
            
            McpLogger.LogInfo($"Created prefab '{prefabName}' at path '{prefabPath}'");

            return new JObject
            {
                ["success"] = true,
                ["type"] = "text",
                ["message"] = $"Successfully created prefab '{prefabName}' at {prefabPath}",
                ["prefabPath"] = prefabPath,
                ["assetGuid"] = AssetDatabase.AssetPathToGUID(prefabPath)
            };
        }

        private void ApplyFieldValues(JObject fieldValues, Component component)
        {
            if (fieldValues == null || fieldValues.Count == 0 || component == null)
            {
                return;
            }
            
            Undo.RecordObject(component, "Set field values");
                
            foreach (var property in fieldValues.Properties())
            {
                var fieldInfo = component.GetType().GetField(property.Name, 
                    System.Reflection.BindingFlags.Public | 
                    System.Reflection.BindingFlags.NonPublic | 
                    System.Reflection.BindingFlags.Instance);
                            
                if (fieldInfo != null)
                {
                    object value = property.Value.ToObject(fieldInfo.FieldType);
                    fieldInfo.SetValue(component, value);
                }
                else
                {
                    var propInfo = component.GetType().GetProperty(property.Name, 
                        System.Reflection.BindingFlags.Public | 
                        System.Reflection.BindingFlags.NonPublic | 
                        System.Reflection.BindingFlags.Instance);
                                
                    if (propInfo != null && propInfo.CanWrite)
                    {
                        object value = property.Value.ToObject(propInfo.PropertyType);
                        propInfo.SetValue(component, value);
                    }
                }
            }
        }
    }
}
