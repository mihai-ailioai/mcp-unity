using System;
using McpUnity.Unity;
using McpUnity.Utils;
using UnityEditor;
using Newtonsoft.Json.Linq;

namespace McpUnity.Tools
{
    /// <summary>
    /// Tool for renaming an asset in place via AssetDatabase
    /// </summary>
    public class RenameAssetTool : McpToolBase
    {
        public RenameAssetTool()
        {
            Name = "rename_asset";
            Description = "Renames an asset in place (filename only, not path), handling .meta files automatically";
        }

        public override JObject Execute(JObject parameters)
        {
            string assetPath = parameters["assetPath"]?.ToObject<string>()?.Trim();
            string guid = parameters["guid"]?.ToObject<string>()?.Trim();
            string newName = parameters["newName"]?.ToObject<string>()?.Trim();

            // Resolve source asset
            string resolvedPath = MoveAssetTool.ResolveAssetPath(assetPath, guid, out _, out JObject error);
            if (error != null) return error;

            // Validate new name
            if (string.IsNullOrEmpty(newName))
            {
                return McpUnitySocketHandler.CreateErrorResponse(
                    "Required parameter 'newName' not provided",
                    "validation_error"
                );
            }

            // newName should be just a name, not a path
            if (newName.Contains("/") || newName.Contains("\\"))
            {
                return McpUnitySocketHandler.CreateErrorResponse(
                    "Parameter 'newName' must be a filename only (no path separators). Use move_asset to change directories.",
                    "validation_error"
                );
            }

            if (newName.Contains(".."))
            {
                return McpUnitySocketHandler.CreateErrorResponse(
                    "Parameter 'newName' must not contain '..'",
                    "validation_error"
                );
            }

            // Strip extension if provided — RenameAsset expects name without extension
            string currentExtension = System.IO.Path.GetExtension(resolvedPath);
            if (!string.IsNullOrEmpty(currentExtension) && newName.EndsWith(currentExtension, StringComparison.OrdinalIgnoreCase))
            {
                newName = newName.Substring(0, newName.Length - currentExtension.Length);
            }

            if (string.IsNullOrWhiteSpace(newName))
            {
                return McpUnitySocketHandler.CreateErrorResponse(
                    "New name cannot be empty after removing extension",
                    "validation_error"
                );
            }

            try
            {
                string result = AssetDatabase.RenameAsset(resolvedPath, newName);
                if (!string.IsNullOrEmpty(result))
                {
                    return McpUnitySocketHandler.CreateErrorResponse(
                        $"Failed to rename asset: {result}",
                        "rename_error"
                    );
                }

                // Build the new path
                string directory = System.IO.Path.GetDirectoryName(resolvedPath)?.Replace("\\", "/");
                string newPath = $"{directory}/{newName}{currentExtension}";
                string newGuid = AssetDatabase.AssetPathToGUID(newPath);

                McpLogger.LogInfo($"[MCP Unity] Renamed asset from '{resolvedPath}' to '{newPath}'");

                return new JObject
                {
                    ["success"] = true,
                    ["type"] = "text",
                    ["message"] = $"Successfully renamed asset from '{System.IO.Path.GetFileName(resolvedPath)}' to '{newName}{currentExtension}'",
                    ["data"] = new JObject
                    {
                        ["previousPath"] = resolvedPath,
                        ["assetPath"] = newPath,
                        ["guid"] = newGuid
                    }
                };
            }
            catch (Exception ex)
            {
                return McpUnitySocketHandler.CreateErrorResponse(
                    $"Error renaming asset: {ex.Message}",
                    "rename_error"
                );
            }
        }
    }
}
