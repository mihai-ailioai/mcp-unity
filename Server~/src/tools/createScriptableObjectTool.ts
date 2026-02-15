import * as z from 'zod';
import { Logger } from '../utils/logger.js';
import { McpUnity } from '../unity/mcpUnity.js';
import { McpServer } from '@modelcontextprotocol/sdk/server/mcp.js';
import { McpUnityError, ErrorType } from '../utils/errors.js';
import { CallToolResult } from '@modelcontextprotocol/sdk/types.js';

const toolName = 'create_scriptable_object';
const toolDescription = `Creates a ScriptableObject asset of the specified type with optional initial field values.

The scriptableObjectType parameter should be the C# class name (e.g. "GameConfig", "WeaponData").

For setting references to other assets in fieldData, use $ref descriptors:
  { "$ref": "asset", "assetPath": "Assets/Path/To/Asset.ext" }
  { "$ref": "asset", "guid": "a1b2c3d4e5f6..." }
  { "$ref": "scene", "objectPath": "Path/To/SceneObject" }

If both assetPath and guid are provided in a $ref, they must point to the same asset.`;

const paramsSchema = z.object({
  scriptableObjectType: z.string().describe('The C# class name of the ScriptableObject type to create'),
  savePath: z.string().optional().describe('Asset save path (e.g. "Assets/Config/GameConfig.asset"). Defaults to "Assets/{TypeName}.asset"'),
  fieldData: z.record(z.any()).optional().describe('Optional key/value pairs to set on the ScriptableObject fields'),
  overwrite: z.boolean().optional().default(false).describe('When true, overwrite an existing asset at the target path')
});

export function registerCreateScriptableObjectTool(server: McpServer, mcpUnity: McpUnity, logger: Logger) {
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
  if (!params.scriptableObjectType || params.scriptableObjectType.trim() === '') {
    throw new McpUnityError(
      ErrorType.VALIDATION,
      "Required parameter 'scriptableObjectType' must be provided"
    );
  }

  const response = await mcpUnity.sendRequest({
    method: toolName,
    params: {
      scriptableObjectType: params.scriptableObjectType,
      savePath: params.savePath,
      fieldData: params.fieldData,
      overwrite: params.overwrite
    }
  });

  if (!response.success) {
    throw new McpUnityError(
      ErrorType.TOOL_EXECUTION,
      response.message || 'Failed to create ScriptableObject'
    );
  }

  let text = response.message || 'ScriptableObject created successfully';
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
