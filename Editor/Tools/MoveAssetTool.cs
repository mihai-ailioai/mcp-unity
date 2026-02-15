using System;
using System.IO;
using McpUnity.Unity;
using McpUnity.Utils;
using UnityEditor;
using Newtonsoft.Json.Linq;

namespace McpUnity.Tools
{
    /// <summary>
    /// Tool for moving an asset to a new path via AssetDatabase, preserving GUID and .meta files
    /// </summary>
    public class MoveAssetTool : McpToolBase
    {
        public MoveAssetTool()
        {
            Name = "move_asset";
            Description = "Moves an asset to a new path, preserving its GUID and handling .meta files automatically";
        }

        public override JObject Execute(JObject parameters)
        {
            string assetPath = parameters["assetPath"]?.ToObject<string>()?.Trim();
            string guid = parameters["guid"]?.ToObject<string>()?.Trim();
            string destinationPath = parameters["destinationPath"]?.ToObject<string>()?.Trim()?.Replace("\\", "/");

            // Resolve source asset
            string resolvedPath = ResolveAssetPath(assetPath, guid, out _, out JObject error);
            if (error != null) return error;

            // Validate destination
            if (string.IsNullOrEmpty(destinationPath))
            {
                return McpUnitySocketHandler.CreateErrorResponse(
                    "Required parameter 'destinationPath' not provided",
                    "validation_error"
                );
            }

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
                    CreateFolderRecursive(destDir);
                }

                string result = AssetDatabase.MoveAsset(resolvedPath, destinationPath);
                if (!string.IsNullOrEmpty(result))
                {
                    return McpUnitySocketHandler.CreateErrorResponse(
                        $"Failed to move asset: {result}",
                        "move_error"
                    );
                }

                AssetDatabase.Refresh();
                string newGuid = AssetDatabase.AssetPathToGUID(destinationPath);

                McpLogger.LogInfo($"[MCP Unity] Moved asset from '{resolvedPath}' to '{destinationPath}'");

                return new JObject
                {
                    ["success"] = true,
                    ["type"] = "text",
                    ["message"] = $"Successfully moved asset from '{resolvedPath}' to '{destinationPath}'",
                    ["data"] = new JObject
                    {
                        ["previousPath"] = resolvedPath,
                        ["assetPath"] = destinationPath,
                        ["guid"] = newGuid
                    }
                };
            }
            catch (Exception ex)
            {
                return McpUnitySocketHandler.CreateErrorResponse(
                    $"Error moving asset: {ex.Message}",
                    "move_error"
                );
            }
        }

        /// <summary>
        /// Resolves an asset path from assetPath and/or guid parameters.
        /// At least one must be provided. If both are provided, they must agree.
        /// </summary>
        internal static string ResolveAssetPath(string assetPath, string guid, out string resolvedGuid, out JObject error)
        {
            error = null;
            resolvedGuid = null;

            if (string.IsNullOrEmpty(assetPath) && string.IsNullOrEmpty(guid))
            {
                error = McpUnitySocketHandler.CreateErrorResponse(
                    "At least one of 'assetPath' or 'guid' must be provided",
                    "validation_error"
                );
                return null;
            }

            string resolvedPath = assetPath;

            if (!string.IsNullOrEmpty(guid))
            {
                string pathFromGuid = AssetDatabase.GUIDToAssetPath(guid);
                if (string.IsNullOrEmpty(pathFromGuid))
                {
                    error = McpUnitySocketHandler.CreateErrorResponse(
                        $"No asset found with GUID '{guid}'",
                        "not_found_error"
                    );
                    return null;
                }

                if (!string.IsNullOrEmpty(assetPath) &&
                    !string.Equals(assetPath, pathFromGuid, StringComparison.OrdinalIgnoreCase))
                {
                    error = McpUnitySocketHandler.CreateErrorResponse(
                        $"Provided assetPath '{assetPath}' does not match GUID '{guid}' which resolves to '{pathFromGuid}'",
                        "validation_error"
                    );
                    return null;
                }

                resolvedPath = pathFromGuid;
            }

            // Verify the asset actually exists
            resolvedGuid = AssetDatabase.AssetPathToGUID(resolvedPath, AssetPathToGUIDOptions.OnlyExistingAssets);
            if (string.IsNullOrEmpty(resolvedGuid))
            {
                error = McpUnitySocketHandler.CreateErrorResponse(
                    $"No asset found at path '{resolvedPath}'",
                    "not_found_error"
                );
                return null;
            }

            return resolvedPath;
        }

        /// <summary>
        /// Creates a folder hierarchy recursively using AssetDatabase.CreateFolder
        /// </summary>
        internal static void CreateFolderRecursive(string folderPath)
        {
            if (AssetDatabase.IsValidFolder(folderPath)) return;

            string parent = Path.GetDirectoryName(folderPath)?.Replace("\\", "/");
            if (!string.IsNullOrEmpty(parent) && !AssetDatabase.IsValidFolder(parent))
            {
                CreateFolderRecursive(parent);
            }

            string folderName = Path.GetFileName(folderPath);
            string result = AssetDatabase.CreateFolder(parent, folderName);
            if (string.IsNullOrEmpty(result))
            {
                throw new InvalidOperationException($"Failed to create folder '{folderPath}' (parent: '{parent}')");
            }
        }
    }
}
