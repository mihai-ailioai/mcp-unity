import { McpUnity } from '../unity/mcpUnity.js';
import { Logger } from '../utils/logger.js';
import { McpServer } from '@modelcontextprotocol/sdk/server/mcp.js';
import { McpUnityError, ErrorType } from '../utils/errors.js';
import { ReadResourceResult } from '@modelcontextprotocol/sdk/types.js';

const resourceName = 'get_selection';
const resourceUri = 'unity://selection';
const resourceMimeType = 'application/json';

/**
 * Registers the Selection resource with the MCP server.
 * Returns the current Unity Editor selection: active GameObject,
 * all selected GameObjects, and selected assets in the Project window.
 */
export function registerGetSelectionResource(server: McpServer, mcpUnity: McpUnity, logger: Logger) {
  logger.info(`Registering resource: ${resourceName}`);

  server.resource(
    resourceName,
    resourceUri,
    {
      description: 'Retrieve the current Unity Editor selection (active object, selected GameObjects, selected assets)',
      mimeType: resourceMimeType
    },
    async () => {
      try {
        return await resourceHandler(mcpUnity);
      } catch (error) {
        logger.error(`Error handling resource ${resourceName}: ${error}`);
        throw error;
      }
    }
  );
}

async function resourceHandler(mcpUnity: McpUnity): Promise<ReadResourceResult> {
  const response = await mcpUnity.sendRequest({
    method: resourceName,
    params: {}
  });

  if (!response.success) {
    throw new McpUnityError(
      ErrorType.RESOURCE_FETCH,
      response.message || 'Failed to fetch selection from Unity Editor'
    );
  }

  const selectionData = {
    activeGameObject: response.activeGameObject ?? null,
    selectedGameObjects: response.selectedGameObjects ?? [],
    selectedAssets: response.selectedAssets ?? []
  };

  return {
    contents: [
      {
        uri: resourceUri,
        mimeType: resourceMimeType,
        text: JSON.stringify(selectionData, null, 2)
      }
    ]
  };
}
