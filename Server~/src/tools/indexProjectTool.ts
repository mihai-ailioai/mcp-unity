import { McpServer } from '@modelcontextprotocol/sdk/server/mcp.js';
import { CallToolResult } from '@modelcontextprotocol/sdk/types.js';
import * as z from 'zod';
import * as fs from 'fs';
import * as path from 'path';
import { ContextEngineService, BATCH_SIZE } from '../services/contextEngine.js';
import { McpUnity } from '../unity/mcpUnity.js';
import { McpUnityError, ErrorType } from '../utils/errors.js';
import { Logger } from '../utils/logger.js';

const toolName = 'index_project';
const toolDescription = 'Indexes project assets into the context engine for semantic search. Supports automatic resume if a previous run was interrupted.';

const paramsSchema = z.object({});

type CollectProjectAssetsResponse = {
  success: boolean;
  scriptPaths?: string[];
  documents?: Array<{ path: string; contents: string }>;
  message?: string;
};

// ── Checkpoint persistence ──────────────────────────────────────────────

const CHECKPOINT_PATH = path.resolve(process.cwd(), 'ProjectSettings/.context-engine-index-checkpoint.json');

interface IndexCheckpoint {
  /** All documents to index (scripts already read from disk + prefabs/scenes from Unity). */
  documents: Array<{ path: string; contents: string }>;
  /** Number of documents already indexed in previous batches. */
  indexedCount: number;
  /** Whether the index was cleared at the start of this run. */
  cleared: boolean;
}

function loadCheckpoint(logger: Logger): IndexCheckpoint | null {
  try {
    if (!fs.existsSync(CHECKPOINT_PATH)) return null;
    const raw = fs.readFileSync(CHECKPOINT_PATH, 'utf-8');
    const data = JSON.parse(raw) as IndexCheckpoint;
    if (!Array.isArray(data.documents) || typeof data.indexedCount !== 'number') return null;
    logger.info(`Loaded checkpoint: ${data.indexedCount}/${data.documents.length} documents already indexed`);
    return data;
  } catch {
    return null;
  }
}

function saveCheckpoint(checkpoint: IndexCheckpoint, logger: Logger): void {
  try {
    fs.writeFileSync(CHECKPOINT_PATH, JSON.stringify(checkpoint), 'utf-8');
  } catch (err: any) {
    logger.error(`Failed to save checkpoint: ${err.message}`);
  }
}

function deleteCheckpoint(logger: Logger): void {
  try {
    if (fs.existsSync(CHECKPOINT_PATH)) {
      fs.unlinkSync(CHECKPOINT_PATH);
      logger.info('Deleted indexing checkpoint');
    }
  } catch (err: any) {
    logger.error(`Failed to delete checkpoint: ${err.message}`);
  }
}

// ── Progress notification helper ────────────────────────────────────────

async function sendProgress(
  extra: { sendNotification?: (notification: any) => Promise<void>; _meta?: { progressToken?: string | number } },
  progress: number,
  total: number,
  message: string,
  logger: Logger
): Promise<void> {
  const progressToken = extra?._meta?.progressToken;
  if (!progressToken || !extra.sendNotification) return;

  try {
    await extra.sendNotification({
      method: 'notifications/progress' as const,
      params: { progressToken, progress, total, message },
    });
  } catch (err: any) {
    logger.error(`Failed to send progress notification: ${err.message}`);
  }
}

// ── Tool registration ───────────────────────────────────────────────────

export function registerIndexProjectTool(
  server: McpServer,
  mcpUnity: McpUnity,
  contextEngine: ContextEngineService,
  logger: Logger
): void {
  logger.info(`Registering tool: ${toolName}`);

  server.tool(toolName, toolDescription, paramsSchema.shape as any, async (params: any, extra: any) => {
    try {
      logger.info(`Executing tool: ${toolName}`, params);
      const result = await toolHandler(mcpUnity, contextEngine, params, extra, logger);
      logger.info(`Tool execution successful: ${toolName}`);
      return result;
    } catch (error) {
      logger.error(`Tool execution failed: ${toolName}`, error);
      throw error;
    }
  });
}

// ── Core handler ────────────────────────────────────────────────────────

async function toolHandler(
  mcpUnity: McpUnity,
  contextEngine: ContextEngineService,
  rawParams: Record<string, unknown>,
  extra: any,
  logger: Logger
): Promise<CallToolResult> {
  const parsed = paramsSchema.safeParse(rawParams);
  if (!parsed.success) {
    throw new McpUnityError(ErrorType.VALIDATION, `Invalid parameters: ${parsed.error.message}`);
  }

  if (!contextEngine.isInitialized) {
    throw new McpUnityError(ErrorType.INTERNAL, 'Context engine is not initialized');
  }

  // ── Try to resume from checkpoint ─────────────────────────────────
  const checkpoint = loadCheckpoint(logger);

  let allDocuments: Array<{ path: string; contents: string }>;
  let startOffset: number;
  let isResume: boolean;

  if (checkpoint && checkpoint.documents.length > 0 && checkpoint.indexedCount < checkpoint.documents.length) {
    // Resume from checkpoint
    allDocuments = checkpoint.documents;
    startOffset = checkpoint.indexedCount;
    isResume = true;

    logger.info(`Resuming indexing from checkpoint: ${startOffset}/${allDocuments.length} already indexed`);
    await sendProgress(extra, startOffset, allDocuments.length,
      `Resuming indexing from checkpoint (${startOffset}/${allDocuments.length} already indexed)...`, logger);
  } else {
    // Fresh run — collect and prepare all documents
    isResume = false;
    startOffset = 0;

    await sendProgress(extra, 0, 1, 'Collecting project assets from Unity...', logger);

    const response = (await mcpUnity.sendRequest(
      { method: 'collect_project_assets', params: {} },
      { timeout: 300000 }
    )) as CollectProjectAssetsResponse;

    if (!response.success) {
      throw new McpUnityError(ErrorType.TOOL_EXECUTION, response.message || 'Failed to collect project assets');
    }

    // Read script contents from disk (Unity only sends paths)
    const unityProjectRoot = process.cwd();
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
        logger.error(`Failed to read script ${scriptPath}: ${err.message}`);
      }
    }

    logger.info(`Successfully read ${scriptDocuments.length}/${scriptPaths.length} scripts from disk`);

    const unityDocuments = response.documents ?? [];
    allDocuments = [...scriptDocuments, ...unityDocuments];

    if (allDocuments.length === 0) {
      deleteCheckpoint(logger);
      return {
        content: [{ type: 'text', text: 'No assets found to index.' }],
      };
    }

    // Clear index once at the start of a fresh run
    await contextEngine.clearIndex();

    // Save initial checkpoint so we can resume if interrupted during indexing
    saveCheckpoint({ documents: allDocuments, indexedCount: 0, cleared: true }, logger);
  }

  // ── Batch indexing with checkpoint updates ────────────────────────
  const totalDocs = allDocuments.length;
  const totalBatches = Math.ceil((totalDocs - startOffset) / BATCH_SIZE);
  let batchesDone = 0;

  for (let offset = startOffset; offset < totalDocs; offset += BATCH_SIZE) {
    const batch = allDocuments.slice(offset, offset + BATCH_SIZE);
    const isLastBatch = offset + BATCH_SIZE >= totalDocs;
    batchesDone++;

    const progressMsg = `Indexing batch ${batchesDone}/${totalBatches} (${offset + batch.length}/${totalDocs} documents)...`;
    logger.info(progressMsg);
    await sendProgress(extra, offset + batch.length, totalDocs, progressMsg, logger);

    await contextEngine.indexBatch(batch, isLastBatch);

    // Update checkpoint after each batch so we can resume from here
    saveCheckpoint({ documents: allDocuments, indexedCount: offset + batch.length, cleared: true }, logger);
  }

  // ── Done — clean up checkpoint ────────────────────────────────────
  deleteCheckpoint(logger);

  const indexedPaths = contextEngine.getIndexedPaths();
  const resumeNote = isResume ? ' (resumed from checkpoint)' : '';
  const summary = `Indexed ${totalDocs} documents${resumeNote}. Context engine now tracks ${indexedPaths.length} paths.`;

  await sendProgress(extra, totalDocs, totalDocs, summary, logger);

  logger.info('Completed project indexing run', {
    totalIndexed: totalDocs,
    indexedPathCount: indexedPaths.length,
    resumed: isResume,
  });

  return {
    content: [{ type: 'text', text: summary }],
  };
}
