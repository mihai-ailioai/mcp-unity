using System;
using McpUnity.Unity;
using McpUnity.Utils;
using UnityEditor;
using Newtonsoft.Json.Linq;

namespace McpUnity.Tools
{
    /// <summary>
    /// Tool for deleting an asset via AssetDatabase.
    /// Defaults to moving to OS trash (recoverable); supports permanent deletion.
    /// </summary>
    public class DeleteAssetTool : McpToolBase
    {
        public DeleteAssetTool()
        {
            Name = "delete_asset";
            Description = "Deletes an asset. By default moves it to the OS trash (recoverable). Set permanent=true for irreversible deletion.";
        }

        public override JObject Execute(JObject parameters)
        {
            string assetPath = parameters["assetPath"]?.ToObject<string>()?.Trim();
            string guid = parameters["guid"]?.ToObject<string>()?.Trim();
            bool permanent = parameters["permanent"]?.ToObject<bool>() ?? false;

            // Resolve source asset
            string resolvedPath = MoveAssetTool.ResolveAssetPath(assetPath, guid, out string resolvedGuid, out JObject error);
            if (error != null) return error;

            try
            {
                bool success;
                string method;

                if (permanent)
                {
                    success = AssetDatabase.DeleteAsset(resolvedPath);
                    method = "permanently deleted";
                }
                else
                {
                    success = AssetDatabase.MoveAssetToTrash(resolvedPath);
                    method = "moved to trash";
                }

                if (!success)
                {
                    return McpUnitySocketHandler.CreateErrorResponse(
                        $"Failed to delete asset at '{resolvedPath}'",
                        "delete_error"
                    );
                }

                AssetDatabase.Refresh();

                McpLogger.LogInfo($"[MCP Unity] Asset '{resolvedPath}' {method}");

                return new JObject
                {
                    ["success"] = true,
                    ["type"] = "text",
                    ["message"] = $"Successfully {method} asset '{resolvedPath}'",
                    ["data"] = new JObject
                    {
                        ["assetPath"] = resolvedPath,
                        ["guid"] = resolvedGuid,
                        ["permanent"] = permanent
                    }
                };
            }
            catch (Exception ex)
            {
                return McpUnitySocketHandler.CreateErrorResponse(
                    $"Error deleting asset: {ex.Message}",
                    "delete_error"
                );
            }
        }
    }
}
