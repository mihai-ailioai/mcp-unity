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
