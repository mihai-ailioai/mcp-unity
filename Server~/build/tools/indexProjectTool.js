import * as z from 'zod';
import * as fs from 'fs';
import * as path from 'path';
import { McpUnityError, ErrorType } from '../utils/errors.js';
const toolName = 'index_project';
const toolDescription = 'Collects project assets from Unity and indexes them into the local project context engine.';
const paramsSchema = z.object({
    includeScenes: z.boolean().optional().default(false).describe('When true, include scene assets in the collected documents.'),
    folders: z.array(z.string()).optional().default([]).describe('Optional Unity asset folders to limit indexing scope.'),
});
export function registerIndexProjectTool(server, mcpUnity, contextEngine, logger) {
    logger.info(`Registering tool: ${toolName}`);
    server.tool(toolName, toolDescription, paramsSchema.shape, async (params) => {
        try {
            logger.info(`Executing tool: ${toolName}`, params);
            const result = await toolHandler(mcpUnity, contextEngine, params, logger);
            logger.info(`Tool execution successful: ${toolName}`);
            return result;
        }
        catch (error) {
            logger.error(`Tool execution failed: ${toolName}`, error);
            throw error;
        }
    });
}
async function toolHandler(mcpUnity, contextEngine, rawParams, logger) {
    const parsed = paramsSchema.safeParse(rawParams);
    if (!parsed.success) {
        throw new McpUnityError(ErrorType.VALIDATION, `Invalid parameters: ${parsed.error.message}`);
    }
    if (!contextEngine.isInitialized) {
        throw new McpUnityError(ErrorType.INTERNAL, 'Context engine is not initialized');
    }
    const { includeScenes, folders } = parsed.data;
    const response = (await mcpUnity.sendRequest({ method: 'collect_project_assets', params: { includeScenes, folders } }, { timeout: 300000 }));
    if (!response.success) {
        throw new McpUnityError(ErrorType.TOOL_EXECUTION, response.message || 'Failed to collect project assets');
    }
    // Unity project root is one level up from Server~ CWD
    const unityProjectRoot = path.resolve(process.cwd(), '..');
    // Read script contents from disk (Unity only sends paths to avoid large WebSocket payloads)
    const scriptPaths = response.scriptPaths ?? [];
    const scriptDocuments = [];
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
        }
        catch (err) {
            logger.error(`Failed to read script ${scriptPath}: ${err.message}`);
        }
    }
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
