import * as z from 'zod';
import * as fs from 'fs';
import * as path from 'path';
import { McpUnityError, ErrorType } from '../utils/errors.js';
const toolName = 'index_project';
const toolDescription = 'Collects project assets from Unity and indexes them into the local project context engine.';
const paramsSchema = z.object({});
/**
 * Helper to send MCP progress notifications.
 * If the client provided a progressToken, sends notifications/progress to keep the
 * connection alive and report status. Silently no-ops when no token is available.
 */
async function sendProgress(extra, progress, total, message, logger) {
    const progressToken = extra?._meta?.progressToken;
    if (!progressToken || !extra.sendNotification)
        return;
    try {
        await extra.sendNotification({
            method: 'notifications/progress',
            params: {
                progressToken,
                progress,
                total,
                message,
            },
        });
    }
    catch (err) {
        // Progress notifications are best-effort; don't fail the tool
        logger.error(`Failed to send progress notification: ${err.message}`);
    }
}
export function registerIndexProjectTool(server, mcpUnity, contextEngine, logger) {
    logger.info(`Registering tool: ${toolName}`);
    server.tool(toolName, toolDescription, paramsSchema.shape, async (params, extra) => {
        try {
            logger.info(`Executing tool: ${toolName}`, params);
            const result = await toolHandler(mcpUnity, contextEngine, params, extra, logger);
            logger.info(`Tool execution successful: ${toolName}`);
            return result;
        }
        catch (error) {
            logger.error(`Tool execution failed: ${toolName}`, error);
            throw error;
        }
    });
}
async function toolHandler(mcpUnity, contextEngine, rawParams, extra, logger) {
    const parsed = paramsSchema.safeParse(rawParams);
    if (!parsed.success) {
        throw new McpUnityError(ErrorType.VALIDATION, `Invalid parameters: ${parsed.error.message}`);
    }
    if (!contextEngine.isInitialized) {
        throw new McpUnityError(ErrorType.INTERNAL, 'Context engine is not initialized');
    }
    // Use 4 phases for progress: collect, read scripts, index, done
    const TOTAL_PHASES = 4;
    // Phase 1: Collect asset paths from Unity
    await sendProgress(extra, 1, TOTAL_PHASES, 'Collecting project assets from Unity...', logger);
    // Folders and includeScenes are configured in the Unity editor Context Engine tab
    const response = (await mcpUnity.sendRequest({ method: 'collect_project_assets', params: {} }, { timeout: 300000 }));
    if (!response.success) {
        throw new McpUnityError(ErrorType.TOOL_EXECUTION, response.message || 'Failed to collect project assets');
    }
    // CWD is the Unity project root (set by the launcher wrapper script)
    const unityProjectRoot = process.cwd();
    // Phase 2: Read script contents from disk
    const scriptPaths = response.scriptPaths ?? [];
    const scriptDocuments = [];
    await sendProgress(extra, 2, TOTAL_PHASES, `Reading ${scriptPaths.length} scripts from disk...`, logger);
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
        }
        catch (err) {
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
    // Phase 3: Index documents into context engine
    await sendProgress(extra, 3, TOTAL_PHASES, `Indexing ${allDocuments.length} documents (${scriptDocuments.length} scripts, ${unityDocuments.length} prefabs/scenes)...`, logger);
    await contextEngine.indexDocuments(allDocuments);
    // Phase 4: Done
    const indexedPaths = contextEngine.getIndexedPaths();
    const summary = `Indexed ${allDocuments.length} documents (${scriptDocuments.length} scripts read from disk, ${unityDocuments.length} prefabs/scenes from Unity). Context engine now tracks ${indexedPaths.length} paths.`;
    await sendProgress(extra, 4, TOTAL_PHASES, summary, logger);
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
