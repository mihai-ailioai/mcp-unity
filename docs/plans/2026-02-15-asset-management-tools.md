# Asset Management Tools Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Add 4 asset management tools (move, rename, copy, delete) that operate through Unity's AssetDatabase API, ensuring .meta files and the import pipeline are handled correctly.

**Architecture:** Each tool is a standalone C# class inheriting `McpToolBase` + a matching TypeScript file. All source asset identification uses the `assetPath`/`guid` dual-ID pattern (at least one required, agreement validation if both). Destination paths are explicit strings. All tools use `AssetDatabase` APIs so Unity handles .meta files automatically.

**Tech Stack:** C# (Unity Editor), TypeScript (Node MCP server), Newtonsoft.Json, zod

---

### Task 1: Create `MoveAssetTool.cs`

**Files:**
- Create: `Editor/Tools/MoveAssetTool.cs`

**Step 1: Write the C# tool**

```csharp
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
            string destinationPath = parameters["destinationPath"]?.ToObject<string>()?.Trim();

            // Resolve source asset
            string resolvedPath = ResolveAssetPath(assetPath, guid, out string resolvedGuid, out JObject error);
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

            // Ensure destination directory exists
            string destDir = Path.GetDirectoryName(destinationPath)?.Replace("\\", "/");
            if (!string.IsNullOrEmpty(destDir) && !AssetDatabase.IsValidFolder(destDir))
            {
                CreateFolderRecursive(destDir);
            }

            try
            {
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
            AssetDatabase.CreateFolder(parent, folderName);
        }
    }
}
```

**Step 2: Commit**

```bash
git add Editor/Tools/MoveAssetTool.cs
git commit -m "feat: add move_asset C# tool"
```

---

### Task 2: Create `RenameAssetTool.cs`

**Files:**
- Create: `Editor/Tools/RenameAssetTool.cs`

**Step 1: Write the C# tool**

```csharp
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
            string resolvedPath = MoveAssetTool.ResolveAssetPath(assetPath, guid, out string resolvedGuid, out JObject error);
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
```

**Step 2: Commit**

```bash
git add Editor/Tools/RenameAssetTool.cs
git commit -m "feat: add rename_asset C# tool"
```

---

### Task 3: Create `CopyAssetTool.cs`

**Files:**
- Create: `Editor/Tools/CopyAssetTool.cs`

**Step 1: Write the C# tool**

```csharp
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
            string destinationPath = parameters["destinationPath"]?.ToObject<string>()?.Trim();

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

            // Ensure destination directory exists
            string destDir = Path.GetDirectoryName(destinationPath)?.Replace("\\", "/");
            if (!string.IsNullOrEmpty(destDir) && !AssetDatabase.IsValidFolder(destDir))
            {
                MoveAssetTool.CreateFolderRecursive(destDir);
            }

            try
            {
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
```

**Step 2: Commit**

```bash
git add Editor/Tools/CopyAssetTool.cs
git commit -m "feat: add copy_asset C# tool"
```

---

### Task 4: Create `DeleteAssetTool.cs`

**Files:**
- Create: `Editor/Tools/DeleteAssetTool.cs`

**Step 1: Write the C# tool**

```csharp
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
```

**Step 2: Commit**

```bash
git add Editor/Tools/DeleteAssetTool.cs
git commit -m "feat: add delete_asset C# tool"
```

---

### Task 5: Create TypeScript tools + registration

**Files:**
- Create: `Server~/src/tools/moveAssetTool.ts`
- Create: `Server~/src/tools/renameAssetTool.ts`
- Create: `Server~/src/tools/copyAssetTool.ts`
- Create: `Server~/src/tools/deleteAssetTool.ts`
- Modify: `Server~/src/index.ts` (add imports + register calls)
- Modify: `Editor/UnityBridge/McpUnityServer.cs` (add to `RegisterTools()`)

**Step 1: Write `moveAssetTool.ts`**

```typescript
import * as z from 'zod';
import { Logger } from '../utils/logger.js';
import { McpUnity } from '../unity/mcpUnity.js';
import { McpServer } from '@modelcontextprotocol/sdk/server/mcp.js';
import { McpUnityError, ErrorType } from '../utils/errors.js';
import { CallToolResult } from '@modelcontextprotocol/sdk/types.js';

const toolName = 'move_asset';
const toolDescription = `Moves an asset to a new location, preserving its GUID and handling .meta files automatically via Unity's AssetDatabase.

Identify the source asset by assetPath and/or guid (at least one required).
The destinationPath must be a full asset path including filename (e.g. "Assets/Prefabs/Player.prefab").
Destination folders are created automatically if they don't exist.`;

const paramsSchema = z.object({
  assetPath: z.string().optional().describe('Current asset path (e.g. "Assets/Models/Player.fbx")'),
  guid: z.string().optional().describe('Asset GUID'),
  destinationPath: z.string().describe('Full destination path including filename (e.g. "Assets/NewFolder/Player.fbx")')
});

export function registerMoveAssetTool(server: McpServer, mcpUnity: McpUnity, logger: Logger) {
  logger.info(`Registering tool: ${toolName}`);
  server.tool(toolName, toolDescription, paramsSchema.shape, async (params: any) => {
    try {
      logger.info(`Executing tool: ${toolName}`, params);
      const result = await toolHandler(mcpUnity, params);
      logger.info(`Tool execution successful: ${toolName}`);
      return result;
    } catch (error) {
      logger.error(`Tool execution failed: ${toolName}`, error);
      throw error;
    }
  });
}

async function toolHandler(mcpUnity: McpUnity, params: any): Promise<CallToolResult> {
  if ((!params.assetPath || params.assetPath.trim() === '') && (!params.guid || params.guid.trim() === '')) {
    throw new McpUnityError(ErrorType.VALIDATION, "At least one of 'assetPath' or 'guid' must be provided");
  }
  if (!params.destinationPath || params.destinationPath.trim() === '') {
    throw new McpUnityError(ErrorType.VALIDATION, "Required parameter 'destinationPath' must be provided");
  }

  const response = await mcpUnity.sendRequest({
    method: toolName,
    params: { assetPath: params.assetPath, guid: params.guid, destinationPath: params.destinationPath }
  });

  if (!response.success) {
    throw new McpUnityError(ErrorType.TOOL_EXECUTION, response.message || 'Failed to move asset');
  }

  let text = response.message || 'Asset moved successfully';
  if (response.data) {
    text += `\n\nPrevious path: ${response.data.previousPath}`;
    text += `\nNew path: ${response.data.assetPath}`;
    text += `\nGUID: ${response.data.guid}`;
  }

  return { content: [{ type: response.type, text }] };
}
```

**Step 2: Write `renameAssetTool.ts`**

```typescript
import * as z from 'zod';
import { Logger } from '../utils/logger.js';
import { McpUnity } from '../unity/mcpUnity.js';
import { McpServer } from '@modelcontextprotocol/sdk/server/mcp.js';
import { McpUnityError, ErrorType } from '../utils/errors.js';
import { CallToolResult } from '@modelcontextprotocol/sdk/types.js';

const toolName = 'rename_asset';
const toolDescription = `Renames an asset in place (changes filename only, not directory), handling .meta files automatically.

Identify the asset by assetPath and/or guid (at least one required).
The newName parameter should be just the filename without path or extension (e.g. "PlayerModel" not "Assets/PlayerModel.fbx").
If you need to move an asset to a different folder, use move_asset instead.`;

const paramsSchema = z.object({
  assetPath: z.string().optional().describe('Current asset path (e.g. "Assets/Models/OldName.fbx")'),
  guid: z.string().optional().describe('Asset GUID'),
  newName: z.string().describe('New filename without path or extension (e.g. "NewName")')
});

export function registerRenameAssetTool(server: McpServer, mcpUnity: McpUnity, logger: Logger) {
  logger.info(`Registering tool: ${toolName}`);
  server.tool(toolName, toolDescription, paramsSchema.shape, async (params: any) => {
    try {
      logger.info(`Executing tool: ${toolName}`, params);
      const result = await toolHandler(mcpUnity, params);
      logger.info(`Tool execution successful: ${toolName}`);
      return result;
    } catch (error) {
      logger.error(`Tool execution failed: ${toolName}`, error);
      throw error;
    }
  });
}

async function toolHandler(mcpUnity: McpUnity, params: any): Promise<CallToolResult> {
  if ((!params.assetPath || params.assetPath.trim() === '') && (!params.guid || params.guid.trim() === '')) {
    throw new McpUnityError(ErrorType.VALIDATION, "At least one of 'assetPath' or 'guid' must be provided");
  }
  if (!params.newName || params.newName.trim() === '') {
    throw new McpUnityError(ErrorType.VALIDATION, "Required parameter 'newName' must be provided");
  }

  const response = await mcpUnity.sendRequest({
    method: toolName,
    params: { assetPath: params.assetPath, guid: params.guid, newName: params.newName }
  });

  if (!response.success) {
    throw new McpUnityError(ErrorType.TOOL_EXECUTION, response.message || 'Failed to rename asset');
  }

  let text = response.message || 'Asset renamed successfully';
  if (response.data) {
    text += `\n\nPrevious path: ${response.data.previousPath}`;
    text += `\nNew path: ${response.data.assetPath}`;
    text += `\nGUID: ${response.data.guid}`;
  }

  return { content: [{ type: response.type, text }] };
}
```

**Step 3: Write `copyAssetTool.ts`**

```typescript
import * as z from 'zod';
import { Logger } from '../utils/logger.js';
import { McpUnity } from '../unity/mcpUnity.js';
import { McpServer } from '@modelcontextprotocol/sdk/server/mcp.js';
import { McpUnityError, ErrorType } from '../utils/errors.js';
import { CallToolResult } from '@modelcontextprotocol/sdk/types.js';

const toolName = 'copy_asset';
const toolDescription = `Copies an asset to a new location, creating a new asset with a new GUID.

Identify the source asset by assetPath and/or guid (at least one required).
If destinationPath is omitted, a copy is created in the same folder with an auto-generated unique name (e.g. "MyAsset 1.mat").
Destination folders are created automatically if they don't exist.`;

const paramsSchema = z.object({
  assetPath: z.string().optional().describe('Source asset path (e.g. "Assets/Materials/Base.mat")'),
  guid: z.string().optional().describe('Source asset GUID'),
  destinationPath: z.string().optional().describe('Full destination path including filename. If omitted, duplicates in the same folder with a unique name.')
});

export function registerCopyAssetTool(server: McpServer, mcpUnity: McpUnity, logger: Logger) {
  logger.info(`Registering tool: ${toolName}`);
  server.tool(toolName, toolDescription, paramsSchema.shape, async (params: any) => {
    try {
      logger.info(`Executing tool: ${toolName}`, params);
      const result = await toolHandler(mcpUnity, params);
      logger.info(`Tool execution successful: ${toolName}`);
      return result;
    } catch (error) {
      logger.error(`Tool execution failed: ${toolName}`, error);
      throw error;
    }
  });
}

async function toolHandler(mcpUnity: McpUnity, params: any): Promise<CallToolResult> {
  if ((!params.assetPath || params.assetPath.trim() === '') && (!params.guid || params.guid.trim() === '')) {
    throw new McpUnityError(ErrorType.VALIDATION, "At least one of 'assetPath' or 'guid' must be provided");
  }

  const response = await mcpUnity.sendRequest({
    method: toolName,
    params: { assetPath: params.assetPath, guid: params.guid, destinationPath: params.destinationPath }
  });

  if (!response.success) {
    throw new McpUnityError(ErrorType.TOOL_EXECUTION, response.message || 'Failed to copy asset');
  }

  let text = response.message || 'Asset copied successfully';
  if (response.data) {
    text += `\n\nSource: ${response.data.sourcePath} (${response.data.sourceGuid})`;
    text += `\nCopy: ${response.data.assetPath} (${response.data.guid})`;
  }

  return { content: [{ type: response.type, text }] };
}
```

**Step 4: Write `deleteAssetTool.ts`**

```typescript
import * as z from 'zod';
import { Logger } from '../utils/logger.js';
import { McpUnity } from '../unity/mcpUnity.js';
import { McpServer } from '@modelcontextprotocol/sdk/server/mcp.js';
import { McpUnityError, ErrorType } from '../utils/errors.js';
import { CallToolResult } from '@modelcontextprotocol/sdk/types.js';

const toolName = 'delete_asset';
const toolDescription = `Deletes an asset from the project. By default, moves the asset to the OS trash (recoverable). Set permanent=true for irreversible deletion.

Identify the asset by assetPath and/or guid (at least one required).
The .meta file is handled automatically.`;

const paramsSchema = z.object({
  assetPath: z.string().optional().describe('Asset path (e.g. "Assets/Materials/OldMaterial.mat")'),
  guid: z.string().optional().describe('Asset GUID'),
  permanent: z.boolean().optional().default(false).describe('When true, permanently deletes the asset. When false (default), moves to OS trash.')
});

export function registerDeleteAssetTool(server: McpServer, mcpUnity: McpUnity, logger: Logger) {
  logger.info(`Registering tool: ${toolName}`);
  server.tool(toolName, toolDescription, paramsSchema.shape, async (params: any) => {
    try {
      logger.info(`Executing tool: ${toolName}`, params);
      const result = await toolHandler(mcpUnity, params);
      logger.info(`Tool execution successful: ${toolName}`);
      return result;
    } catch (error) {
      logger.error(`Tool execution failed: ${toolName}`, error);
      throw error;
    }
  });
}

async function toolHandler(mcpUnity: McpUnity, params: any): Promise<CallToolResult> {
  if ((!params.assetPath || params.assetPath.trim() === '') && (!params.guid || params.guid.trim() === '')) {
    throw new McpUnityError(ErrorType.VALIDATION, "At least one of 'assetPath' or 'guid' must be provided");
  }

  const response = await mcpUnity.sendRequest({
    method: toolName,
    params: { assetPath: params.assetPath, guid: params.guid, permanent: params.permanent }
  });

  if (!response.success) {
    throw new McpUnityError(ErrorType.TOOL_EXECUTION, response.message || 'Failed to delete asset');
  }

  let text = response.message || 'Asset deleted successfully';
  if (response.data) {
    text += `\n\nPath: ${response.data.assetPath}`;
    text += `\nGUID: ${response.data.guid}`;
    text += `\nPermanent: ${response.data.permanent}`;
  }

  return { content: [{ type: response.type, text }] };
}
```

**Step 5: Register all 4 tools in `McpUnityServer.cs`**

Add before the BatchExecuteTool registration:

```csharp
            // Register Asset Management Tools
            MoveAssetTool moveAssetTool = new MoveAssetTool();
            _tools.Add(moveAssetTool.Name, moveAssetTool);

            RenameAssetTool renameAssetTool = new RenameAssetTool();
            _tools.Add(renameAssetTool.Name, renameAssetTool);

            CopyAssetTool copyAssetTool = new CopyAssetTool();
            _tools.Add(copyAssetTool.Name, copyAssetTool);

            DeleteAssetTool deleteAssetTool = new DeleteAssetTool();
            _tools.Add(deleteAssetTool.Name, deleteAssetTool);
```

**Step 6: Register all 4 tools in `Server~/src/index.ts`**

Add imports after the ScriptableObject tool imports:

```typescript
import { registerMoveAssetTool } from './tools/moveAssetTool.js';
import { registerRenameAssetTool } from './tools/renameAssetTool.js';
import { registerCopyAssetTool } from './tools/copyAssetTool.js';
import { registerDeleteAssetTool } from './tools/deleteAssetTool.js';
```

Add registration calls after the ScriptableObject registrations:

```typescript
  registerMoveAssetTool(server, mcpUnity, toolLogger);
  registerRenameAssetTool(server, mcpUnity, toolLogger);
  registerCopyAssetTool(server, mcpUnity, toolLogger);
  registerDeleteAssetTool(server, mcpUnity, toolLogger);
```

**Step 7: Build**

```bash
cd Server~ && npm run build
```

**Step 8: Commit**

```bash
git add -f Server~/src/tools/moveAssetTool.ts Server~/src/tools/renameAssetTool.ts Server~/src/tools/copyAssetTool.ts Server~/src/tools/deleteAssetTool.ts Server~/src/index.ts Editor/UnityBridge/McpUnityServer.cs
git commit -m "feat: add TypeScript tools and registration for asset management"
```

---

### Task 6: Meta files + AGENTS.md update

**Files:**
- Create: `Editor/Tools/MoveAssetTool.cs.meta`
- Create: `Editor/Tools/RenameAssetTool.cs.meta`
- Create: `Editor/Tools/CopyAssetTool.cs.meta`
- Create: `Editor/Tools/DeleteAssetTool.cs.meta`
- Modify: `AGENTS.md`

**Step 1: Generate GUIDs and create .meta files**

Generate 4 GUIDs:
```bash
python3 -c "import uuid; [print(uuid.uuid4().hex[:32]) for _ in range(4)]"
```

Create each .meta file with format:
```
fileFormatVersion: 2
guid: <generated-guid>
MonoImporter:
  externalObjects: {}
  serializedVersion: 2
  defaultReferences: []
  executionOrder: 0
  icon: {instanceID: 0}
  userData: 
  assetBundleName: 
  assetBundleVariant: 
```

**Step 2: Update AGENTS.md**

Add 4 new tools to the "Available tools (current)" section:
- `move_asset` — Move assets to new paths, preserving GUIDs and handling .meta files
- `rename_asset` — Rename assets in place (filename only)
- `copy_asset` — Copy assets to new locations with new GUIDs
- `delete_asset` — Delete assets (trash by default, permanent optional)

**Step 3: Commit**

```bash
git add Editor/Tools/MoveAssetTool.cs.meta Editor/Tools/RenameAssetTool.cs.meta Editor/Tools/CopyAssetTool.cs.meta Editor/Tools/DeleteAssetTool.cs.meta AGENTS.md
git commit -m "chore: add meta files and update AGENTS.md for asset management tools"
```

---

### Task 7: Review

**Step 1: Review all C# tools for correctness**

Check:
- ResolveAssetPath shared method: null/empty handling, GUID resolution, agreement validation
- CreateFolderRecursive: handles edge cases (root "Assets" folder, nested creation)
- MoveAsset/RenameAsset error strings returned by AssetDatabase (non-empty = error)
- CopyAsset returns bool (not error string)
- DeleteAsset/MoveAssetToTrash returns bool
- RenameAsset extension stripping logic
- All tools: try/catch, proper error types, structured response data

**Step 2: Review TypeScript tools for correctness**

Check:
- TS validation matches C# expectations
- Response data fields match C# output
- All tools follow consistent pattern

**Step 3: Fix any issues found, commit**
