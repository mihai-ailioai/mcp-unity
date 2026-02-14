import * as z from 'zod';
import { Logger } from '../utils/logger.js';
import { McpUnity } from '../unity/mcpUnity.js';
import { McpServer } from '@modelcontextprotocol/sdk/server/mcp.js';
import { McpUnityError, ErrorType } from '../utils/errors.js';
import { CallToolResult } from '@modelcontextprotocol/sdk/types.js';

const toolName = 'save_as_prefab';
const toolDescription = 'Saves an existing scene GameObject as a prefab asset, with optional overwrite behavior and prefab variant creation when the source is a prefab instance';
const paramsSchema = z.object({
  instanceId: z.number().optional().describe('Optional scene GameObject instance ID to save as a prefab'),
  objectPath: z.string().optional().describe('Optional scene GameObject hierarchy path to save as a prefab'),
  prefabPath: z.string().optional().describe('Optional target prefab asset path (for example: Assets/Prefabs/Player.prefab)'),
  overwrite: z.boolean().optional().default(false).describe('When true, overwrite an existing prefab at the target path'),
  variant: z.boolean().optional().default(false).describe('When true and source is a prefab instance, create a prefab variant')
});

export function registerSaveAsPrefabTool(server: McpServer, mcpUnity: McpUnity, logger: Logger) {
  logger.info(`Registering tool: ${toolName}`);

  server.tool(
    toolName,
    toolDescription,
    paramsSchema.shape,
    async (params: any) => {
      try {
        logger.info(`Executing tool: ${toolName}`, params);
        const result = await toolHandler(mcpUnity, params);
        logger.info(`Tool execution successful: ${toolName}`);
        return result;
      } catch (error) {
        logger.error(`Tool execution failed: ${toolName}`, error);
        throw error;
      }
    }
  );
}

async function toolHandler(mcpUnity: McpUnity, params: any): Promise<CallToolResult> {
  if ((params.instanceId === undefined || params.instanceId === null) &&
      (!params.objectPath || params.objectPath.trim() === '')) {
    throw new McpUnityError(
      ErrorType.VALIDATION,
      "Either 'instanceId' or 'objectPath' must be provided"
    );
  }

  const response = await mcpUnity.sendRequest({
    method: toolName,
    params: {
      instanceId: params.instanceId,
      objectPath: params.objectPath,
      prefabPath: params.prefabPath,
      overwrite: params.overwrite,
      variant: params.variant
    }
  });

  if (!response.success) {
    throw new McpUnityError(
      ErrorType.TOOL_EXECUTION,
      response.message || 'Failed to save GameObject as prefab'
    );
  }

  return {
    content: [{
      type: response.type,
      text: response.message
    }]
  };
}
