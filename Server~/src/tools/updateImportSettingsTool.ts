import * as z from 'zod';
import { Logger } from '../utils/logger.js';
import { McpUnity } from '../unity/mcpUnity.js';
import { McpServer } from '@modelcontextprotocol/sdk/server/mcp.js';
import { McpUnityError, ErrorType } from '../utils/errors.js';
import { CallToolResult } from '@modelcontextprotocol/sdk/types.js';

const toolName = 'update_import_settings';
const toolDescription = `Updates import settings on any AssetImporter via reflection.

Supports generic importer properties such as isReadable, filterMode, globalScale, and other writable importer fields.
Supports platformOverrides for TextureImporter and AudioImporter.
Platform override format example: { "Android": { "maxTextureSize": 512, "format": "ASTC_6x6" } }.
For texture platform overrides, overridden=true is auto-applied unless explicitly provided.
Pass enum values as strings.`;

const paramsSchema = z.object({
  assetPath: z.string().optional().describe('Path to the asset (e.g. "Assets/Textures/hero.png")'),
  guid: z.string().optional().describe('GUID of the asset (found in .meta files)'),
  settings: z.record(z.any()).optional().describe('Generic import settings (property name -> value)'),
  platformOverrides: z.record(z.record(z.any())).optional().describe('Platform override settings (platform -> settings object)')
});

export function registerUpdateImportSettingsTool(server: McpServer, mcpUnity: McpUnity, logger: Logger) {
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
  const settings = params.settings;
  const platformOverrides = params.platformOverrides;

  if (!assetPath && !guid) {
    throw new McpUnityError(
      ErrorType.VALIDATION,
      "Either 'assetPath' or 'guid' must be provided"
    );
  }

  if ((!settings || Object.keys(settings).length === 0) &&
      (!platformOverrides || Object.keys(platformOverrides).length === 0)) {
    throw new McpUnityError(
      ErrorType.VALIDATION,
      "At least one of 'settings' or 'platformOverrides' must be provided and non-empty"
    );
  }

  const response = await mcpUnity.sendRequest({
    method: toolName,
    params: {
      assetPath,
      guid,
      settings,
      platformOverrides
    }
  });

  if (!response.success) {
    throw new McpUnityError(
      ErrorType.TOOL_EXECUTION,
      response.message || 'Failed to update import settings'
    );
  }

  const data = response.data || {};
  const updatedProperties = data.updatedProperties || [];
  const errors = data.errors || [];

  let text = response.message || 'Import settings updated successfully';
  if (data.assetPath) {
    text += `\n\nAsset path: ${data.assetPath}`;
  }
  if (data.guid) {
    text += `\nGUID: ${data.guid}`;
  }
  if (data.importerType) {
    text += `\nImporter type: ${data.importerType}`;
  }
  if (updatedProperties.length > 0) {
    text += `\n\nUpdated:\n- ${updatedProperties.join('\n- ')}`;
  }
  if (errors.length > 0) {
    text += `\n\nErrors:\n- ${errors.join('\n- ')}`;
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
      updatedProperties,
      errors
    }
  };
}
