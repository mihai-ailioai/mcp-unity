import * as z from 'zod';
import { Logger } from '../utils/logger.js';
import { McpUnity } from '../unity/mcpUnity.js';
import { McpServer } from '@modelcontextprotocol/sdk/server/mcp.js';
import { McpUnityError, ErrorType } from '../utils/errors.js';
import { CallToolResult } from '@modelcontextprotocol/sdk/types.js';

const toolName = 'get_import_settings';
const toolDescription = `Reads import settings from any asset's AssetImporter.

Provide either assetPath, guid, or both (must agree if both are provided). Returns importer type, generic importer settings, and platform overrides when available.`;

const paramsSchema = z.object({
  assetPath: z.string().optional().describe('Path to the asset (e.g. "Assets/Textures/hero.png")'),
  guid: z.string().optional().describe('GUID of the asset (found in .meta files)')
});

export function registerGetImportSettingsTool(server: McpServer, mcpUnity: McpUnity, logger: Logger) {
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
  const assetPath = params.assetPath?.trim();
  const guid = params.guid?.trim();

  if (!assetPath && !guid) {
    throw new McpUnityError(
      ErrorType.VALIDATION,
      "Either 'assetPath' or 'guid' must be provided"
    );
  }

  const response = await mcpUnity.sendRequest({
    method: toolName,
    params: {
      assetPath,
      guid
    }
  });

  if (!response.success) {
    throw new McpUnityError(
      ErrorType.TOOL_EXECUTION,
      response.message || 'Failed to read import settings'
    );
  }

  const data = response.data || {};
  let text = response.message || 'Import settings read successfully';

  if (data.assetPath) {
    text += `\n\nAsset path: ${data.assetPath}`;
  }
  if (data.guid) {
    text += `\nGUID: ${data.guid}`;
  }
  if (data.importerType) {
    text += `\nImporter type: ${data.importerType}`;
  }
  if (data.settings) {
    text += `\n\nSettings:\n${JSON.stringify(data.settings, null, 2)}`;
  }
  if (data.platformOverrides) {
    text += `\n\nPlatform overrides:\n${JSON.stringify(data.platformOverrides, null, 2)}`;
  }

  return {
    content: [{
      type: response.type,
      text
    }],
    data: {
      assetPath: data.assetPath,
      guid: data.guid,
      importerType: data.importerType,
      settings: data.settings,
      platformOverrides: data.platformOverrides
    }
  };
}
