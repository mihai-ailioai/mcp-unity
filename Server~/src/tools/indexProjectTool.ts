import { McpServer } from '@modelcontextprotocol/sdk/server/mcp.js';
import { CallToolResult } from '@modelcontextprotocol/sdk/types.js';
import * as z from 'zod';
import * as fs from 'fs';
import * as path from 'path';
import { ContextEngineService } from '../services/contextEngine.js';
import { McpUnity } from '../unity/mcpUnity.js';
import { McpUnityError, ErrorType } from '../utils/errors.js';
import { Logger } from '../utils/logger.js';

const toolName = 'index_project';
const toolDescription = 'Collects project assets from Unity and indexes them into the local project context engine.';

const paramsSchema = z.object({});

type CollectProjectAssetsResponse = {
  success: boolean;
  scriptPaths?: string[];
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

  // Folders and includeScenes are configured in the Unity editor Context Engine tab
  const response = (await mcpUnity.sendRequest(
    { method: 'collect_project_assets', params: {} },
    { timeout: 300000 }
  )) as CollectProjectAssetsResponse;

  if (!response.success) {
    throw new McpUnityError(ErrorType.TOOL_EXECUTION, response.message || 'Failed to collect project assets');
  }

  // CWD is the Unity project root (set by the launcher wrapper script)
  const unityProjectRoot = process.cwd();

  // Read script contents from disk (Unity only sends paths to avoid large WebSocket payloads)
  const scriptPaths = response.scriptPaths ?? [];
  const scriptDocuments: Array<{ path: string; contents: string }> = [];

  logger.info(`Reading ${scriptPaths.length} scripts from disk (project root: ${unityProjectRoot})`);

  for (const scriptPath of scriptPaths) {
    try {
      const fullPath = path.resolve(unityProjectRoot, scriptPath);
      const fileContents = fs.readFileSync(fullPath, 'utf-8');
      if (fileContents.trim().length > 0) {
        scriptDocuments.push({
          path: scriptPath,
          contents: `// File: ${scriptPath}\n${fileContents}`,
        });
      }
    } catch (err: any) {
      logger.error(`Failed to read script ${scriptPath} (resolved: ${path.resolve(unityProjectRoot, scriptPath)}): ${err.message}`);
    }
  }

  logger.info(`Successfully read ${scriptDocuments.length}/${scriptPaths.length} scripts from disk`);

  // Prefab/scene documents come with contents from Unity (they need runtime summarization)
  const unityDocuments = response.documents ?? [];

  const allDocuments = [...scriptDocuments, ...unityDocuments];

  if (allDocuments.length === 0) {
    logger.info('No documents found for indexing');
    return {
      content: [
        {
          type: 'text',
          text: 'No assets found',
        },
      ],
    };
  }

  await contextEngine.indexDocuments(allDocuments);

  const indexedPaths = contextEngine.getIndexedPaths();
  const summary = `Indexed ${allDocuments.length} documents (${scriptDocuments.length} scripts read from disk, ${unityDocuments.length} prefabs/scenes from Unity). Context engine now tracks ${indexedPaths.length} paths.`;

  logger.info('Completed project indexing run', {
    scriptCount: scriptDocuments.length,
    unityDocumentCount: unityDocuments.length,
    totalIndexed: allDocuments.length,
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
