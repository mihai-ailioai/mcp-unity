import * as z from 'zod';
import { McpUnityError, ErrorType } from '../utils/errors.js';
const toolName = 'search_project';
const toolDescription = 'Searches the local project context index using a natural language query.';
const paramsSchema = z.object({
    query: z.string().min(1).describe('Natural language query used to search the indexed project context.'),
});
export function registerSearchProjectTool(server, contextEngine, logger) {
    logger.info(`Registering tool: ${toolName}`);
    server.tool(toolName, toolDescription, paramsSchema.shape, async (params) => {
        try {
            logger.info(`Executing tool: ${toolName}`, params);
            const result = await toolHandler(contextEngine, params, logger);
            logger.info(`Tool execution successful: ${toolName}`);
            return result;
        }
        catch (error) {
            logger.error(`Tool execution failed: ${toolName}`, error);
            throw error;
        }
    });
}
async function toolHandler(contextEngine, rawParams, logger) {
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
