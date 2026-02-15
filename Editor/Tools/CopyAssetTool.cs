using System;
using System.IO;
using McpUnity.Unity;
using McpUnity.Utils;
using UnityEditor;
using Newtonsoft.Json.Linq;

namespace McpUnity.Tools
{
    /// <summary>
    /// Tool for copying an asset to a new path via AssetDatabase, creating a new asset with a new GUID
    /// </summary>
    public class CopyAssetTool : McpToolBase
    {
        public CopyAssetTool()
        {
            Name = "copy_asset";
            Description = "Copies an asset to a new path, creating a new asset with a new GUID. If no destination is specified, creates a copy in the same folder with an auto-generated unique name.";
        }

        public override JObject Execute(JObject parameters)
        {
            string assetPath = parameters["assetPath"]?.ToObject<string>()?.Trim();
            string guid = parameters["guid"]?.ToObject<string>()?.Trim();
            string destinationPath = parameters["destinationPath"]?.ToObject<string>()?.Trim()?.Replace("\\", "/");

            // Resolve source asset
            string resolvedPath = MoveAssetTool.ResolveAssetPath(assetPath, guid, out string resolvedGuid, out JObject error);
            if (error != null) return error;

            // If no destination, duplicate in same folder with unique name
            if (string.IsNullOrEmpty(destinationPath))
            {
                destinationPath = AssetDatabase.GenerateUniqueAssetPath(resolvedPath);
            }

            // Validate destination
            if (!destinationPath.StartsWith("Assets/"))
            {
                return McpUnitySocketHandler.CreateErrorResponse(
                    "Destination path must start with 'Assets/'",
                    "validation_error"
                );
            }

            if (destinationPath.Contains(".."))
            {
                return McpUnitySocketHandler.CreateErrorResponse(
                    "Destination path must not contain '..' path traversal",
                    "validation_error"
                );
            }

            string destFileName = Path.GetFileName(destinationPath);
            if (string.IsNullOrWhiteSpace(destFileName))
            {
                return McpUnitySocketHandler.CreateErrorResponse(
                    "Destination path must include a filename",
                    "validation_error"
                );
            }

            try
            {
                // Ensure destination directory exists
                string destDir = Path.GetDirectoryName(destinationPath)?.Replace("\\", "/");
                if (!string.IsNullOrEmpty(destDir) && !AssetDatabase.IsValidFolder(destDir))
                {
                    MoveAssetTool.CreateFolderRecursive(destDir);
                }

                bool success = AssetDatabase.CopyAsset(resolvedPath, destinationPath);
                if (!success)
                {
                    return McpUnitySocketHandler.CreateErrorResponse(
                        $"Failed to copy asset from '{resolvedPath}' to '{destinationPath}'",
                        "copy_error"
                    );
                }

                AssetDatabase.Refresh();
                string newGuid = AssetDatabase.AssetPathToGUID(destinationPath);

                McpLogger.LogInfo($"[MCP Unity] Copied asset from '{resolvedPath}' to '{destinationPath}'");

                return new JObject
                {
                    ["success"] = true,
                    ["type"] = "text",
                    ["message"] = $"Successfully copied asset from '{resolvedPath}' to '{destinationPath}'",
                    ["data"] = new JObject
                    {
                        ["sourcePath"] = resolvedPath,
                        ["sourceGuid"] = resolvedGuid,
                        ["assetPath"] = destinationPath,
                        ["guid"] = newGuid
                    }
                };
            }
            catch (Exception ex)
            {
                return McpUnitySocketHandler.CreateErrorResponse(
                    $"Error copying asset: {ex.Message}",
                    "copy_error"
                );
            }
        }
    }
}
