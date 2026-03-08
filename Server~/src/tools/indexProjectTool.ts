import { McpServer } from '@modelcontextprotocol/sdk/server/mcp.js';
import { CallToolResult } from '@modelcontextprotocol/sdk/types.js';
import * as z from 'zod';
import { ContextEngineService } from '../services/contextEngine.js';
import { McpUnity } from '../unity/mcpUnity.js';
import { McpUnityError, ErrorType } from '../utils/errors.js';
import { Logger } from '../utils/logger.js';

const toolName = 'index_project';
const toolDescription = 'Collects project assets from Unity and indexes them into the local project context engine.';

const paramsSchema = z.object({
  includeScenes: z.boolean().optional().default(false).describe('When true, include scene assets in the collected documents.'),
  folders: z.array(z.string()).optional().default([]).describe('Optional Unity asset folders to limit indexing scope.'),
});

type CollectProjectAssetsResponse = {
  success: boolean;
  documents?: Array<{ path: string; contents: string }>;
  message?: string;
};

export function registerIndexProjectTool(
  server: McpServer,
  mcpUnity: McpUnity,
  contextEngine: ContextEngineService,
  logger: Logger
): void {
  logger.info(`Registering tool: ${toolName}`);

  server.tool(toolName, toolDescription, paramsSchema.shape as any, async (params: any) => {
    try {
      logger.info(`Executing tool: ${toolName}`, params);
      const result = await toolHandler(mcpUnity, contextEngine, params, logger);
      logger.info(`Tool execution successful: ${toolName}`);
      return result;
    } catch (error) {
      logger.error(`Tool execution failed: ${toolName}`, error);
      throw error;
    }
  });
}

async function toolHandler(
  mcpUnity: McpUnity,
  contextEngine: ContextEngineService,
  rawParams: Record<string, unknown>,
  logger: Logger
): Promise<CallToolResult> {
  const parsed = paramsSchema.safeParse(rawParams);
  if (!parsed.success) {
    throw new McpUnityError(ErrorType.VALIDATION, `Invalid parameters: ${parsed.error.message}`);
  }

  if (!contextEngine.isInitialized) {
    throw new McpUnityError(ErrorType.INTERNAL, 'Context engine is not initialized');
  }

  const { includeScenes, folders } = parsed.data;
  const response = (await mcpUnity.sendRequest(
    { method: 'collect_project_assets', params: { includeScenes, folders } },
    { timeout: 300000 }
  )) as CollectProjectAssetsResponse;

  if (!response.success) {
    throw new McpUnityError(ErrorType.TOOL_EXECUTION, response.message || 'Failed to collect project assets');
  }

  const documents = response.documents ?? [];
  if (documents.length === 0) {
    logger.info('Unity returned no documents for indexing');
    return {
      content: [
        {
          type: 'text',
          text: 'No assets found',
        },
      ],
    };
  }

  await contextEngine.indexDocuments(documents);

  const indexedPaths = contextEngine.getIndexedPaths();
  const summary = `Indexed ${documents.length} documents. Context engine now tracks ${indexedPaths.length} paths.`;

  logger.info('Completed project indexing run', {
    indexedDocumentCount: documents.length,
    indexedPathCount: indexedPaths.length,
  });

  return {
    content: [
      {
        type: 'text',
        text: summary,
      },
    ],
  };
}
