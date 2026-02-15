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
