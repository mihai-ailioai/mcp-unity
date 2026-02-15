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
