import * as z from 'zod';
import { Logger } from '../utils/logger.js';
import { McpUnity } from '../unity/mcpUnity.js';
import { McpServer } from '@modelcontextprotocol/sdk/server/mcp.js';
import { McpUnityError, ErrorType } from '../utils/errors.js';
import { CallToolResult } from '@modelcontextprotocol/sdk/types.js';

const toolName = 'update_scriptable_object';
const toolDescription = `Updates field values on an existing ScriptableObject asset. Identify the asset by assetPath, guid, or both (must agree).

For setting references to other assets or scene objects in fieldData, use $ref descriptors:
  { "$ref": "asset", "assetPath": "Assets/Path/To/Asset.ext" }
  { "$ref": "asset", "guid": "a1b2c3d4e5f6..." }
  { "$ref": "scene", "objectPath": "Path/To/SceneObject" }

If both assetPath and guid are provided in a $ref, they must point to the same asset.`;

const paramsSchema = z.object({
  assetPath: z.string().optional().describe('Path to the ScriptableObject asset (e.g. "Assets/Config/GameConfig.asset")'),
  guid: z.string().optional().describe('GUID of the ScriptableObject asset (found in .meta files)'),
  fieldData: z.record(z.any()).describe('Key/value pairs of fields to update on the ScriptableObject')
});

export function registerUpdateScriptableObjectTool(server: McpServer, mcpUnity: McpUnity, logger: Logger) {
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
  if ((!params.assetPath || params.assetPath.trim() === '') &&
      (!params.guid || params.guid.trim() === '')) {
    throw new McpUnityError(
      ErrorType.VALIDATION,
      "Either 'assetPath' or 'guid' must be provided"
    );
  }

  if (!params.fieldData || Object.keys(params.fieldData).length === 0) {
    throw new McpUnityError(
      ErrorType.VALIDATION,
      "Required parameter 'fieldData' must be provided and non-empty"
    );
  }

  const response = await mcpUnity.sendRequest({
    method: toolName,
    params: {
      assetPath: params.assetPath,
      guid: params.guid,
      fieldData: params.fieldData
    }
  });

  if (!response.success) {
    throw new McpUnityError(
      ErrorType.TOOL_EXECUTION,
      response.message || 'Failed to update ScriptableObject'
    );
  }

  let text = response.message || 'ScriptableObject updated successfully';
  if (response.data) {
    text += `\n\nAsset path: ${response.data.assetPath}`;
    text += `\nType: ${response.data.typeName}`;
    text += `\nGUID: ${response.data.guid}`;
  }

  return {
    content: [{
      type: response.type,
      text
    }]
  };
}
