using McpUnity.Unity;
using McpUnity.Utils;
using UnityEditor;
using UnityEngine;
using Newtonsoft.Json.Linq;

namespace McpUnity.Tools
{
    /// <summary>
    /// Tool for creating tags in the Unity Editor
    /// </summary>
    public class CreateTagTool : McpToolBase
    {
        public CreateTagTool()
        {
            Name = "create_tag";
            Description = "Creates a new tag in the Unity Tag Manager if it does not already exist";
        }

        public override JObject Execute(JObject parameters)
        {
            string tagName = parameters["tagName"]?.ToObject<string>();

            if (string.IsNullOrEmpty(tagName))
            {
                return McpUnitySocketHandler.CreateErrorResponse(
                    "Required parameter 'tagName' not provided",
                    "validation_error"
                );
            }

            foreach (string existingTag in UnityEditorInternal.InternalEditorUtility.tags)
            {
                if (existingTag == tagName)
                {
                    return new JObject
                    {
                        ["success"] = true,
                        ["type"] = "text",
                        ["message"] = $"Tag '{tagName}' already exists"
                    };
                }
            }

            UnityEditorInternal.InternalEditorUtility.AddTag(tagName);
            McpLogger.LogInfo($"[MCP Unity] Created tag '{tagName}'");

            return new JObject
            {
                ["success"] = true,
                ["type"] = "text",
                ["message"] = $"Successfully created tag '{tagName}'"
            };
        }
    }
}
