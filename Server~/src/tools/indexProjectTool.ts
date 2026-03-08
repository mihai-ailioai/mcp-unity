import { McpServer } from '@modelcontextprotocol/sdk/server/mcp.js';
import { CallToolResult } from '@modelcontextprotocol/sdk/types.js';
import * as z from 'zod';
import * as fs from 'fs';
import * as path from 'path';
import { ContextEngineService, BATCH_SIZE, type BatchIndexStats } from '../services/contextEngine.js';
import { McpUnity } from '../unity/mcpUnity.js';
import { McpUnityError, ErrorType } from '../utils/errors.js';
import { Logger } from '../utils/logger.js';

const toolName = 'index_project';
const toolDescription = 'Indexes project assets into the context engine for semantic search. Supports automatic resume — if the operation is too large to complete in one call, it will return a message asking you to call this tool again to continue.';

/**
 * Internal time budget for a single tool invocation. When approaching this
 * deadline, we save the checkpoint and return a "call again" message rather
 * than letting the MCP client timeout kill us mid-flight.
 *
 * Set conservatively at 45s — some MCP clients enforce ~60s tool timeouts
 * regardless of config. Multiple short invocations with checkpoint resume
 * is preferable to a single timeout error.
 */
const TOOL_TIME_BUDGET_MS = 45_000; // 45 seconds

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

  const startTime = Date.now();
  const deadline = startTime + TOOL_TIME_BUDGET_MS;

  /** Returns true when we should stop and ask the LLM to call again. */
  function isNearDeadline(): boolean {
    return Date.now() >= deadline;
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
  let totalUnityDocsIndexed = 0;
  // Aggregate indexing stats across all batches
  const stats = { skipped: 0, newlyUploaded: 0, alreadyUploaded: 0, bytesUploaded: 0 };

  function accumulateStats(batch: BatchIndexStats): void {
    stats.skipped += batch.skipped;
    stats.newlyUploaded += batch.newlyUploaded;
    stats.alreadyUploaded += batch.alreadyUploaded;
    stats.bytesUploaded += batch.bytesUploaded;
  }

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
      accumulateStats(await contextEngine.indexBatch(firstBatchDocs, isOnlyPage));
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
      const summary = formatSummary(scriptDocuments.length, totalUnityDocsIndexed, indexedPaths.length, stats, false);
      await sendProgress(extra, totalUnityDocuments, totalUnityDocuments, summary, logger);
      return { content: [{ type: 'text', text: summary }] };
    }

    // Check deadline after first page
    if (isNearDeadline()) {
      logger.info('Approaching time limit after first page, saving checkpoint for resume');
      const pct = Math.round((unityOffset / totalUnityDocuments) * 100);
      const elapsed = Math.round((Date.now() - startTime) / 1000);
      return {
        content: [{
          type: 'text',
          text: `Indexing in progress: ${unityOffset}/${totalUnityDocuments} prefabs/scenes collected (${pct}%), ${scriptDocuments.length} scripts indexed. Elapsed: ${elapsed}s. Please call index_project again to continue from the checkpoint.`,
        }],
      };
    }
  }

  // ── Phase 2: Paginate remaining Unity documents ───────────────────
  while (unityOffset < totalUnityDocuments) {
    // Check deadline before starting the next page
    if (isNearDeadline()) {
      logger.info('Approaching time limit, saving checkpoint for resume');
      const pct = Math.round((unityOffset / totalUnityDocuments) * 100);
      const elapsed = Math.round((Date.now() - startTime) / 1000);
      return {
        content: [{
          type: 'text',
          text: `Indexing in progress: ${unityOffset}/${totalUnityDocuments} prefabs/scenes collected (${pct}%), ${totalUnityDocsIndexed} indexed so far, ${scriptDocuments.length} scripts indexed. Elapsed: ${elapsed}s. Please call index_project again to continue from the checkpoint.`,
        }],
      };
    }

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
    accumulateStats(await contextEngine.indexBatch(docsToIndex, isLastPage));

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
  const summary = formatSummary(scriptDocuments.length, totalUnityDocsIndexed, indexedPaths.length, stats, isResume);

  await sendProgress(extra, totalUnityDocuments, totalUnityDocuments, summary, logger);

  logger.info('Completed project indexing run', {
    scriptCount: scriptDocuments.length,
    unityDocumentCount: totalUnityDocsIndexed,
    indexedPathCount: indexedPaths.length,
    ...stats,
    resumed: isResume,
  });

  return { content: [{ type: 'text', text: summary }] };
}

// ── Summary formatting ─────────────────────────────────────────────────

function formatBytes(bytes: number): string {
  if (bytes < 1024) return `${bytes}B`;
  if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)}KB`;
  return `${(bytes / (1024 * 1024)).toFixed(1)}MB`;
}

function formatSummary(
  scriptCount: number,
  unityDocCount: number,
  indexedPathCount: number,
  stats: { skipped: number; newlyUploaded: number; alreadyUploaded: number; bytesUploaded: number },
  isResume: boolean
): string {
  const parts: string[] = [];

  parts.push(`Indexed ${scriptCount} scripts + ${unityDocCount} prefabs/scenes`);

  // Upload details
  const details: string[] = [];
  if (stats.newlyUploaded > 0) {
    details.push(`${stats.newlyUploaded} new`);
  }
  if (stats.alreadyUploaded > 0) {
    details.push(`${stats.alreadyUploaded} unchanged`);
  }
  if (stats.skipped > 0) {
    details.push(`${stats.skipped} skipped as oversized`);
  }
  if (stats.bytesUploaded > 0) {
    details.push(`${formatBytes(stats.bytesUploaded)} uploaded`);
  }
  if (details.length > 0) {
    parts.push(` (${details.join(', ')})`);
  }

  parts.push(`. Context engine now tracks ${indexedPathCount} paths.`);

  if (isResume) {
    parts.push(' (resumed from checkpoint)');
  }

  return parts.join('');
}
