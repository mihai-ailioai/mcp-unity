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
