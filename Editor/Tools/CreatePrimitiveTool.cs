using System;
using McpUnity.Unity;
using McpUnity.Utils;
using UnityEngine;
using UnityEditor;
using Newtonsoft.Json.Linq;

namespace McpUnity.Tools
{
    public class CreatePrimitiveTool : McpToolBase
    {
        public CreatePrimitiveTool()
        {
            Name = "create_primitive";
            Description = "Creates a primitive GameObject (Cube, Sphere, Capsule, Cylinder, Plane, Quad) in the current scene";
        }

        public override JObject Execute(JObject parameters)
        {
            string primitiveTypeStr = parameters["primitiveType"]?.ToObject<string>();
            string name = parameters["name"]?.ToObject<string>();
            string parentPath = parameters["parentPath"]?.ToObject<string>();
            int? parentId = parameters["parentId"]?.ToObject<int?>();
            JObject position = parameters["position"] as JObject;
            JObject rotation = parameters["rotation"] as JObject;
            JObject scale = parameters["scale"] as JObject;

            if (string.IsNullOrEmpty(primitiveTypeStr))
            {
                return McpUnitySocketHandler.CreateErrorResponse(
                    "Required parameter 'primitiveType' not provided",
                    "validation_error"
                );
            }

            if (!Enum.TryParse<PrimitiveType>(primitiveTypeStr, true, out PrimitiveType primitiveType))
            {
                return McpUnitySocketHandler.CreateErrorResponse(
                    $"Invalid primitive type '{primitiveTypeStr}'. Valid types: Cube, Sphere, Capsule, Cylinder, Plane, Quad",
                    "validation_error"
                );
            }

            GameObject primitive = GameObject.CreatePrimitive(primitiveType);
            Undo.RegisterCreatedObjectUndo(primitive, $"Create {primitiveType}");

            if (!string.IsNullOrEmpty(name))
            {
                primitive.name = name;
            }

            if (!string.IsNullOrEmpty(parentPath) || parentId.HasValue)
            {
                GameObject parent = null;
                if (parentId.HasValue)
                {
                    parent = EditorUtility.InstanceIDToObject(parentId.Value) as GameObject;
                }
                else
                {
                    parent = GameObject.Find(parentPath);
                }

                if (parent != null)
                {
                    Undo.SetTransformParent(primitive.transform, parent.transform, $"Parent {primitive.name}");
                }
                else
                {
                    McpLogger.LogWarning($"[MCP Unity] Parent not found, primitive created at root");
                }
            }

            if (position != null)
            {
                primitive.transform.localPosition = new Vector3(
                    position["x"]?.ToObject<float>() ?? 0f,
                    position["y"]?.ToObject<float>() ?? 0f,
                    position["z"]?.ToObject<float>() ?? 0f
                );
            }

            if (rotation != null)
            {
                primitive.transform.localEulerAngles = new Vector3(
                    rotation["x"]?.ToObject<float>() ?? 0f,
                    rotation["y"]?.ToObject<float>() ?? 0f,
                    rotation["z"]?.ToObject<float>() ?? 0f
                );
            }

            if (scale != null)
            {
                primitive.transform.localScale = new Vector3(
                    scale["x"]?.ToObject<float>() ?? 1f,
                    scale["y"]?.ToObject<float>() ?? 1f,
                    scale["z"]?.ToObject<float>() ?? 1f
                );
            }

            EditorUtility.SetDirty(primitive);

            return new JObject
            {
                ["success"] = true,
                ["type"] = "text",
                ["message"] = $"Created {primitiveType} '{primitive.name}' (instanceId: {primitive.GetInstanceID()})",
                ["data"] = new JObject
                {
                    ["instanceId"] = primitive.GetInstanceID(),
                    ["name"] = primitive.name,
                    ["primitiveType"] = primitiveType.ToString()
                }
            };
        }
    }
}
