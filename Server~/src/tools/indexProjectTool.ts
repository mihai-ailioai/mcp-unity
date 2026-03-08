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
  totalDocuments?: number;
  offset?: number;
  nextOffset?: number;
  message?: string;
};

// ── Checkpoint persistence ──────────────────────────────────────────────

const CHECKPOINT_PATH = path.resolve(process.cwd(), 'ProjectSettings/.context-engine-index-checkpoint.json');

interface IndexCheckpoint {
  /** Script documents already read from disk (only collected on the first page). */
  scriptDocuments: Array<{ path: string; contents: string }>;
  /** Total number of prefab/scene documents reported by Unity. */
  totalUnityDocuments: number;
  /** How many prefab/scene documents have been collected and indexed so far. */
  collectedUnityDocuments: number;
  /** Whether the context engine index was cleared at the start of this run. */
  cleared: boolean;
  /** Whether all script documents have been indexed. */
  scriptsIndexed: boolean;
}

function loadCheckpoint(logger: Logger): IndexCheckpoint | null {
  try {
    if (!fs.existsSync(CHECKPOINT_PATH)) return null;
    const raw = fs.readFileSync(CHECKPOINT_PATH, 'utf-8');
    const data = JSON.parse(raw) as IndexCheckpoint;
    if (typeof data.totalUnityDocuments !== 'number' || typeof data.collectedUnityDocuments !== 'number') return null;
    logger.info(`Loaded checkpoint: ${data.collectedUnityDocuments}/${data.totalUnityDocuments} Unity docs collected, scripts indexed: ${data.scriptsIndexed}`);
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
  let checkpoint = loadCheckpoint(logger);
  const isResume = checkpoint !== null && checkpoint.collectedUnityDocuments < checkpoint.totalUnityDocuments;

  if (!isResume) {
    // Fresh run — clear the index and start from scratch
    checkpoint = null;
  }

  // ── Phase 1: First page — collect scripts + discover total count ──
  let scriptDocuments: Array<{ path: string; contents: string }>;
  let totalUnityDocuments: number;
  let unityOffset: number;
  let scriptsIndexed: boolean;
  let totalSkipped = 0;
  let totalUnityDocsIndexed = 0;

  if (checkpoint) {
    // Resume: use cached script documents and pick up where we left off
    scriptDocuments = checkpoint.scriptDocuments;
    totalUnityDocuments = checkpoint.totalUnityDocuments;
    unityOffset = checkpoint.collectedUnityDocuments;
    scriptsIndexed = checkpoint.scriptsIndexed;

    logger.info(`Resuming: ${unityOffset}/${totalUnityDocuments} Unity docs already collected`);
    await sendProgress(extra, unityOffset, totalUnityDocuments,
      `Resuming from checkpoint (${unityOffset}/${totalUnityDocuments} docs)...`, logger);
  } else {
    // Fresh run
    await sendProgress(extra, 0, 1, 'Collecting project assets from Unity (page 1)...', logger);

    const firstPage = (await mcpUnity.sendRequest(
      { method: 'collect_project_assets', params: { offset: 0 } },
      { timeout: 300000 }
    )) as CollectProjectAssetsResponse;

    if (!firstPage.success) {
      throw new McpUnityError(ErrorType.TOOL_EXECUTION, firstPage.message || 'Failed to collect project assets');
    }

    totalUnityDocuments = firstPage.totalDocuments ?? (firstPage.documents?.length ?? 0);

    // Read script contents from disk (Unity only sends paths, which are always on the first page)
    const unityProjectRoot = process.cwd();
    const scriptPaths = firstPage.scriptPaths ?? [];
    scriptDocuments = [];

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

    // Clear index once at the start of a fresh run
    await contextEngine.clearIndex();

    // Index the first page of Unity documents immediately
    const firstPageDocs = firstPage.documents ?? [];
    totalUnityDocsIndexed += firstPageDocs.length;
    const firstBatchDocs = [...scriptDocuments, ...firstPageDocs];

    if (firstBatchDocs.length > 0) {
      const isOnlyPage = (firstPage.nextOffset ?? firstPageDocs.length) >= totalUnityDocuments;
      totalSkipped += await contextEngine.indexBatch(firstBatchDocs, isOnlyPage);
    }

    unityOffset = firstPage.nextOffset ?? firstPageDocs.length;
    scriptsIndexed = true;

    // Save checkpoint
    saveCheckpoint({
      scriptDocuments,
      totalUnityDocuments,
      collectedUnityDocuments: unityOffset,
      cleared: true,
      scriptsIndexed: true,
    }, logger);

    if (unityOffset >= totalUnityDocuments) {
      // All done in a single page
      deleteCheckpoint(logger);
      const indexedPaths = contextEngine.getIndexedPaths();
      const skippedNote = totalSkipped > 0 ? ` (${totalSkipped} skipped as oversized)` : '';
      const summary = `Indexed ${scriptDocuments.length} scripts + ${totalUnityDocsIndexed} prefabs/scenes${skippedNote}. Context engine now tracks ${indexedPaths.length} paths.`;
      await sendProgress(extra, totalUnityDocuments, totalUnityDocuments, summary, logger);
      return { content: [{ type: 'text', text: summary }] };
    }
  }

  // ── Phase 2: Paginate remaining Unity documents ───────────────────
  while (unityOffset < totalUnityDocuments) {
    const pageMsg = `Collecting Unity assets (${unityOffset}/${totalUnityDocuments})...`;
    logger.info(pageMsg);
    await sendProgress(extra, unityOffset, totalUnityDocuments, pageMsg, logger);

    const page = (await mcpUnity.sendRequest(
      { method: 'collect_project_assets', params: { offset: unityOffset } },
      { timeout: 300000 }
    )) as CollectProjectAssetsResponse;

    if (!page.success) {
      throw new McpUnityError(ErrorType.TOOL_EXECUTION, page.message || `Failed to collect assets at offset ${unityOffset}`);
    }

    const pageDocs = page.documents ?? [];
    if (pageDocs.length === 0) {
      // No more documents — totalDocuments may have been an overcount (e.g., null prefabs)
      logger.info(`Empty page at offset ${unityOffset}, stopping pagination`);
      break;
    }

    totalUnityDocsIndexed += pageDocs.length;

    // If resuming and scripts haven't been indexed yet, prepend them to first batch
    let docsToIndex = pageDocs;
    if (!scriptsIndexed && scriptDocuments.length > 0) {
      docsToIndex = [...scriptDocuments, ...pageDocs];
      scriptsIndexed = true;
    }

    const newOffset = page.nextOffset ?? (unityOffset + pageDocs.length);
    const isLastPage = newOffset >= totalUnityDocuments;
    totalSkipped += await contextEngine.indexBatch(docsToIndex, isLastPage);

    unityOffset = newOffset;

    // Update checkpoint
    saveCheckpoint({
      scriptDocuments,
      totalUnityDocuments,
      collectedUnityDocuments: unityOffset,
      cleared: true,
      scriptsIndexed,
    }, logger);
  }

  // ── Done ──────────────────────────────────────────────────────────
  deleteCheckpoint(logger);

  const indexedPaths = contextEngine.getIndexedPaths();
  const resumeNote = isResume ? ' (resumed from checkpoint)' : '';
  const skippedNote = totalSkipped > 0 ? ` (${totalSkipped} skipped as oversized)` : '';
  const summary = `Indexed ${scriptDocuments.length} scripts + ${totalUnityDocsIndexed} prefabs/scenes${skippedNote}${resumeNote}. Context engine now tracks ${indexedPaths.length} paths.`;

  await sendProgress(extra, totalUnityDocuments, totalUnityDocuments, summary, logger);

  logger.info('Completed project indexing run', {
    scriptCount: scriptDocuments.length,
    unityDocumentCount: totalUnityDocsIndexed,
    indexedPathCount: indexedPaths.length,
    skippedCount: totalSkipped,
    resumed: isResume,
  });

  return { content: [{ type: 'text', text: summary }] };
}
