using System;
using System.Reflection;
using McpUnity.Unity;
using McpUnity.Utils;
using UnityEngine;
using UnityEditor;
using Newtonsoft.Json.Linq;

namespace McpUnity.Tools
{
    /// <summary>
    /// Tool for removing components from GameObjects in the Unity Editor
    /// </summary>
    public class RemoveComponentTool : McpToolBase
    {
        public RemoveComponentTool()
        {
            Name = "remove_component";
            Description = "Removes a component from a GameObject in the current scene";
        }

        public override JObject Execute(JObject parameters)
        {
            int? instanceId = parameters["instanceId"]?.ToObject<int?>();
            string objectPath = parameters["objectPath"]?.ToObject<string>();
            string componentName = parameters["componentName"]?.ToObject<string>();

            if (!instanceId.HasValue && string.IsNullOrEmpty(objectPath))
            {
                return McpUnitySocketHandler.CreateErrorResponse(
                    "Either 'instanceId' or 'objectPath' must be provided",
                    "validation_error"
                );
            }

            if (string.IsNullOrEmpty(componentName))
            {
                return McpUnitySocketHandler.CreateErrorResponse(
                    "Required parameter 'componentName' not provided",
                    "validation_error"
                );
            }

            GameObject gameObject = null;
            string identifier;

            if (instanceId.HasValue)
            {
                gameObject = EditorUtility.InstanceIDToObject(instanceId.Value) as GameObject;
                identifier = $"ID {instanceId.Value}";
            }
            else
            {
                gameObject = GameObject.Find(objectPath);
                identifier = $"path '{objectPath}'";
            }

            if (gameObject == null)
            {
                return McpUnitySocketHandler.CreateErrorResponse(
                    $"GameObject not found: {identifier}",
                    "not_found_error"
                );
            }

            Component component = gameObject.GetComponent(componentName);
            if (component == null)
            {
                Type componentType = FindComponentType(componentName);
                if (componentType != null)
                {
                    component = gameObject.GetComponent(componentType);
                }
            }

            if (component == null)
            {
                return McpUnitySocketHandler.CreateErrorResponse(
                    $"Component '{componentName}' not found on GameObject '{gameObject.name}'",
                    "not_found_error"
                );
            }

            if (component is Transform)
            {
                return McpUnitySocketHandler.CreateErrorResponse(
                    "Cannot remove the Transform component from a GameObject",
                    "validation_error"
                );
            }

            string removedComponentName = component.GetType().Name;
            Undo.DestroyObjectImmediate(component);
            EditorUtility.SetDirty(gameObject);

            McpLogger.LogInfo($"[MCP Unity] Removed component '{removedComponentName}' from GameObject '{gameObject.name}'");

            return new JObject
            {
                ["success"] = true,
                ["type"] = "text",
                ["message"] = $"Successfully removed component '{removedComponentName}' from GameObject '{gameObject.name}'"
            };
        }

        private Type FindComponentType(string componentName)
        {
            string[] commonNamespaces = new string[]
            {
                "UnityEngine",
                "UnityEngine.UI",
                "UnityEngine.EventSystems",
                "UnityEngine.Animations",
                "UnityEngine.Rendering",
                "TMPro"
            };

            foreach (string ns in commonNamespaces)
            {
                Type type = Type.GetType($"{ns}.{componentName}, UnityEngine");
                if (type != null && typeof(Component).IsAssignableFrom(type))
                {
                    return type;
                }
            }

            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    foreach (Type t in assembly.GetTypes())
                    {
                        if (t.Name == componentName && typeof(Component).IsAssignableFrom(t))
                        {
                            return t;
                        }
                    }
                }
                catch (Exception)
                {
                    continue;
                }
            }

            return null;
        }
    }
}
