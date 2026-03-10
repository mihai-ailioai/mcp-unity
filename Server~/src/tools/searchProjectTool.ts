import { McpServer } from '@modelcontextprotocol/sdk/server/mcp.js';
import { CallToolResult } from '@modelcontextprotocol/sdk/types.js';
import * as z from 'zod';
import { ContextEngineService } from '../services/contextEngine.js';
import { McpUnityError, ErrorType } from '../utils/errors.js';
import { Logger } from '../utils/logger.js';

const toolName = 'search_project';
const toolDescription =
  'Semantic search over indexed Unity project assets. Returns actual source code sections with line numbers from multiple relevant files, ranked by relevance. ' +
  'Use as a FIRST STEP when exploring unfamiliar systems or answering broad questions like "how does X work" or "where is Y handled". ' +
  'A single query can map out an entire system\'s key files and implementations. ' +
  'Effective query examples: "camera follow system", "save player data persistence", "multiplayer networking sync", "UI loading screen progress". ' +
  'Prefer this over file search tools when you need to understand HOW something works rather than find a specific symbol.';

const paramsSchema = z.object({
  query: z.string().min(1).describe('Natural language query used to search the indexed project context.'),
});

export function registerSearchProjectTool(server: McpServer, contextEngine: ContextEngineService, logger: Logger): void {
  logger.info(`Registering tool: ${toolName}`);

  server.tool(toolName, toolDescription, paramsSchema.shape as any, async (params: any) => {
    try {
      logger.info(`Executing tool: ${toolName}`, params);
      const result = await toolHandler(contextEngine, params, logger);
      logger.info(`Tool execution successful: ${toolName}`);
      return result;
    } catch (error) {
      logger.error(`Tool execution failed: ${toolName}`, error);
      throw error;
    }
  });
}

async function toolHandler(
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

  const { query } = parsed.data;
  const results = await contextEngine.search(query);
  const text = results.trim().length > 0 ? results : 'No search results found for the provided query.';

  logger.info(`Returning search results for query: ${query}`, {
    hasResults: results.trim().length > 0,
  });

  return {
    content: [
      {
        type: 'text',
        text,
      },
    ],
  };
}
