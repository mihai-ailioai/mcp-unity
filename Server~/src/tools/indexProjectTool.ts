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
  /** Maps AssetDatabase package paths to disk-relative paths, e.g. "Packages/com.evlppy.core" -> "Packages/Core-Module" */
  packagePathMap?: Record<string, string>;
};

// ── Checkpoint persistence ──────────────────────────────────────────────

const CHECKPOINT_PATH = path.resolve(process.cwd(), 'Library/.context-engine-index-checkpoint.json');

interface IndexCheckpoint {
  /** Script asset paths (contents are re-read from disk on resume). */
  scriptPaths: string[];
  /** How many script documents have been indexed so far. */
  scriptsIndexedCount: number;
  /** Total number of prefab/scene documents reported by Unity. */
  totalUnityDocuments: number;
  /** How many prefab/scene documents have been collected and indexed so far. */
  collectedUnityDocuments: number;
  /** Whether the context engine index was cleared at the start of this run. */
  cleared: boolean;
  /** Maps AssetDatabase package paths to disk-relative paths for script reading. */
  packagePathMap?: Record<string, string>;
}

function loadCheckpoint(logger: Logger): IndexCheckpoint | null {
  try {
    if (!fs.existsSync(CHECKPOINT_PATH)) return null;
    const raw = fs.readFileSync(CHECKPOINT_PATH, 'utf-8');
    const data = JSON.parse(raw) as any;
    if (typeof data.totalUnityDocuments !== 'number' || typeof data.collectedUnityDocuments !== 'number') return null;

    // Migrate old checkpoints that stored full scriptDocuments instead of paths
    if (!Array.isArray(data.scriptPaths) && Array.isArray(data.scriptDocuments)) {
      data.scriptPaths = data.scriptDocuments.map((d: any) => d.path);
      delete data.scriptDocuments;
    }
    if (typeof data.scriptsIndexedCount !== 'number') {
      data.scriptsIndexedCount = (data as any).scriptsIndexed ? data.scriptPaths.length : 0;
    }

    const checkpoint = data as IndexCheckpoint;
    logger.info(`Loaded checkpoint: ${checkpoint.scriptsIndexedCount}/${checkpoint.scriptPaths.length} scripts indexed, ${checkpoint.collectedUnityDocuments}/${checkpoint.totalUnityDocuments} Unity docs collected`);
    return checkpoint;
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

// ── Script reading helper ──────────────────────────────────────────────

/**
 * Resolves a Unity AssetDatabase path to an actual disk path using the package path map.
 * E.g. "Packages/com.evlppy.core/Runtime/Foo.cs" -> "Packages/Core-Module/Runtime/Foo.cs"
 */
function resolveDiskPath(assetPath: string, packagePathMap: Record<string, string>): string {
  for (const [assetDbPrefix, diskPrefix] of Object.entries(packagePathMap)) {
    if (assetPath.startsWith(assetDbPrefix + '/')) {
      return diskPrefix + assetPath.substring(assetDbPrefix.length);
    }
  }
  return assetPath;
}

interface ScriptBatchResult {
  docs: Array<{ path: string; contents: string }>;
  readErrors: number;
  firstErrors: string[];
}

/**
 * Reads a slice of script paths from disk and returns documents.
 * Skips empty files and tracks errors for unreadable files.
 */
function readScriptBatch(
  scriptPaths: string[],
  startIdx: number,
  endIdx: number,
  projectRoot: string,
  logger: Logger,
  packagePathMap: Record<string, string> = {}
): ScriptBatchResult {
  const docs: Array<{ path: string; contents: string }> = [];
  let readErrors = 0;
  const firstErrors: string[] = [];
  for (let i = startIdx; i < endIdx; i++) {
    const scriptPath = scriptPaths[i];
    try {
      const diskRelative = resolveDiskPath(scriptPath, packagePathMap);
      const fullPath = path.resolve(projectRoot, diskRelative);
      const fileContents = fs.readFileSync(fullPath, 'utf-8');
      if (fileContents.trim().length > 0) {
        docs.push({
          path: scriptPath,
          contents: `// File: ${scriptPath}\n${fileContents}`,
        });
      }
    } catch (err: any) {
      readErrors++;
      if (firstErrors.length < 5) {
        firstErrors.push(`${scriptPath}: ${err.message}`);
      }
      logger.error(`Failed to read script ${scriptPath}: ${err.message}`);
    }
  }
  return { docs, readErrors, firstErrors };
}

// ── Early-return helper ────────────────────────────────────────────────

function earlyReturn(
  totalScripts: number,
  scriptsIndexedCount: number,
  totalUnityDocuments: number,
  unityOffset: number,
  totalUnityDocsIndexed: number,
  startTime: number,
): CallToolResult {
  const elapsed = Math.round((Date.now() - startTime) / 1000);
  const totalWork = totalScripts + totalUnityDocuments;
  const totalDone = scriptsIndexedCount + unityOffset;
  const pct = totalWork > 0 ? Math.round((totalDone / totalWork) * 100) : 0;

  const lines: string[] = [];
  lines.push(`Indexing in progress (${pct}% complete, ${elapsed}s elapsed):`);
  lines.push(`  Scripts: ${scriptsIndexedCount}/${totalScripts} indexed`);
  lines.push(`  Prefabs/scenes: ${unityOffset}/${totalUnityDocuments} collected, ${totalUnityDocsIndexed} indexed`);
  lines.push(`Please call index_project again to continue from the checkpoint.`);

  return { content: [{ type: 'text', text: lines.join('\n') }] };
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
  const isResume = checkpoint !== null && (
    checkpoint.scriptsIndexedCount < checkpoint.scriptPaths.length ||
    checkpoint.collectedUnityDocuments < checkpoint.totalUnityDocuments
  );

  if (!isResume) {
    checkpoint = null;
  }

  // ── Aggregate indexing stats across all batches ───────────────────
  let totalUnityDocsIndexed = 0;
  const stats = { skipped: 0, newlyUploaded: 0, alreadyUploaded: 0, bytesUploaded: 0 };

  function accumulateStats(batch: BatchIndexStats): void {
    stats.skipped += batch.skipped;
    stats.newlyUploaded += batch.newlyUploaded;
    stats.alreadyUploaded += batch.alreadyUploaded;
    stats.bytesUploaded += batch.bytesUploaded;
  }

  // ── Phase 0: Collect or restore data ──────────────────────────────
  const unityProjectRoot = process.cwd();
  let scriptPaths: string[];
  let scriptsIndexedCount: number;
  let totalUnityDocuments: number;
  let unityOffset: number;
  let packagePathMap: Record<string, string> = {};

  if (checkpoint) {
    scriptPaths = checkpoint.scriptPaths;
    scriptsIndexedCount = checkpoint.scriptsIndexedCount;
    totalUnityDocuments = checkpoint.totalUnityDocuments;
    unityOffset = checkpoint.collectedUnityDocuments;
    packagePathMap = checkpoint.packagePathMap ?? {};

    logger.info(`Resuming: ${scriptsIndexedCount}/${scriptPaths.length} scripts, ${unityOffset}/${totalUnityDocuments} Unity docs`);
    await sendProgress(extra, scriptsIndexedCount + unityOffset, scriptPaths.length + totalUnityDocuments,
      `Resuming from checkpoint...`, logger);
  } else {
    // Fresh run — collect script paths from Unity
    await sendProgress(extra, 0, 1, 'Collecting project assets from Unity...', logger);

    const firstPage = (await mcpUnity.sendRequest(
      { method: 'collect_project_assets', params: { offset: 0 } },
      { timeout: 300000 }
    )) as CollectProjectAssetsResponse;

    if (!firstPage.success) {
      throw new McpUnityError(ErrorType.TOOL_EXECUTION, firstPage.message || 'Failed to collect project assets');
    }

    totalUnityDocuments = firstPage.totalDocuments ?? (firstPage.documents?.length ?? 0);
    scriptPaths = firstPage.scriptPaths ?? [];
    packagePathMap = firstPage.packagePathMap ?? {};

    const mapEntries = Object.entries(packagePathMap);
    if (mapEntries.length > 0) {
      logger.info(`Package path map: ${mapEntries.map(([k, v]) => `${k} -> ${v}`).join(', ')}`);
    }
    logger.info(`Received ${scriptPaths.length} script paths from Unity`);

    // Clear index once at the start of a fresh run
    await contextEngine.clearIndex();

    // Index the first page of Unity documents (prefabs/scenes) immediately
    const firstPageDocs = firstPage.documents ?? [];
    if (firstPageDocs.length > 0) {
      totalUnityDocsIndexed += firstPageDocs.length;
      const isOnlyPage = (firstPage.nextOffset ?? firstPageDocs.length) >= totalUnityDocuments;
      // Don't finalize yet — scripts still need indexing
      accumulateStats(await contextEngine.indexBatch(firstPageDocs, isOnlyPage && scriptPaths.length === 0));
    }

    unityOffset = firstPage.nextOffset ?? (firstPage.documents?.length ?? 0);
    scriptsIndexedCount = 0;

    // Save initial checkpoint (only paths, not contents)
    saveCheckpoint({
      scriptPaths,
      scriptsIndexedCount: 0,
      totalUnityDocuments,
      collectedUnityDocuments: unityOffset,
      cleared: true,
      packagePathMap,
    }, logger);

    // Check deadline after collection + first page indexing
    if (isNearDeadline()) {
      logger.info('Approaching time limit after collection phase');
      return earlyReturn(scriptPaths.length, scriptsIndexedCount, totalUnityDocuments, unityOffset, totalUnityDocsIndexed, startTime);
    }
  }

  // ── Phase 1: Index scripts in batches (read from disk on demand) ──
  let totalScriptsRead = 0;
  let totalScriptReadErrors = 0;
  const sampleErrors: string[] = [];
  while (scriptsIndexedCount < scriptPaths.length) {
    if (isNearDeadline()) {
      logger.info('Approaching time limit during script indexing');
      return earlyReturn(scriptPaths.length, scriptsIndexedCount, totalUnityDocuments, unityOffset, totalUnityDocsIndexed, startTime);
    }

    const batchEnd = Math.min(scriptsIndexedCount + BATCH_SIZE, scriptPaths.length);
    const batchResult = readScriptBatch(scriptPaths, scriptsIndexedCount, batchEnd, unityProjectRoot, logger, packagePathMap);
    totalScriptsRead += batchResult.docs.length;
    totalScriptReadErrors += batchResult.readErrors;
    if (sampleErrors.length < 5) {
      sampleErrors.push(...batchResult.firstErrors.slice(0, 5 - sampleErrors.length));
    }
    const isLastScriptBatch = batchEnd >= scriptPaths.length;
    // Only finalize if this is both the last script batch AND all prefabs are done
    const isLastOverall = isLastScriptBatch && unityOffset >= totalUnityDocuments;

    const batchNum = Math.floor(scriptsIndexedCount / BATCH_SIZE) + 1;
    const totalBatches = Math.ceil(scriptPaths.length / BATCH_SIZE);
    const msg = `Indexing scripts batch ${batchNum}/${totalBatches} (${scriptsIndexedCount}/${scriptPaths.length})...`;
    logger.info(msg);
    await sendProgress(extra, scriptsIndexedCount, scriptPaths.length + totalUnityDocuments, msg, logger);

    if (batchResult.docs.length > 0) {
      accumulateStats(await contextEngine.indexBatch(batchResult.docs, isLastOverall));
    }
    scriptsIndexedCount = batchEnd;

    // Update checkpoint
    saveCheckpoint({
      scriptPaths,
      scriptsIndexedCount,
      totalUnityDocuments,
      collectedUnityDocuments: unityOffset,
      cleared: true,
      packagePathMap,
    }, logger);
  }

  logger.info(`Script indexing complete: ${totalScriptsRead} non-empty scripts read from ${scriptPaths.length} paths`);

  // Check if everything is done (small project, all fit in first page)
  if (unityOffset >= totalUnityDocuments) {
    deleteCheckpoint(logger);
    const indexedPaths = contextEngine.getIndexedPaths();
    const summary = formatSummary(scriptPaths.length, totalUnityDocsIndexed, indexedPaths.length, stats, isResume);
    await sendProgress(extra, scriptPaths.length + totalUnityDocuments, scriptPaths.length + totalUnityDocuments, summary, logger);
    return { content: [{ type: 'text', text: summary }] };
  }

  // ── Phase 2: Paginate remaining Unity documents ───────────────────
  while (unityOffset < totalUnityDocuments) {
    if (isNearDeadline()) {
      logger.info('Approaching time limit during prefab/scene collection');
      return earlyReturn(scriptPaths.length, scriptsIndexedCount, totalUnityDocuments, unityOffset, totalUnityDocsIndexed, startTime);
    }

    const pageMsg = `Collecting Unity assets (${unityOffset}/${totalUnityDocuments})...`;
    logger.info(pageMsg);
    await sendProgress(extra, scriptPaths.length + unityOffset, scriptPaths.length + totalUnityDocuments, pageMsg, logger);

    const page = (await mcpUnity.sendRequest(
      { method: 'collect_project_assets', params: { offset: unityOffset } },
      { timeout: 300000 }
    )) as CollectProjectAssetsResponse;

    if (!page.success) {
      throw new McpUnityError(ErrorType.TOOL_EXECUTION, page.message || `Failed to collect assets at offset ${unityOffset}`);
    }

    const pageDocs = page.documents ?? [];
    if (pageDocs.length === 0) {
      logger.info(`Empty page at offset ${unityOffset}, stopping pagination`);
      break;
    }

    totalUnityDocsIndexed += pageDocs.length;

    const newOffset = page.nextOffset ?? (unityOffset + pageDocs.length);
    const isLastPage = newOffset >= totalUnityDocuments;
    accumulateStats(await contextEngine.indexBatch(pageDocs, isLastPage));

    unityOffset = newOffset;

    // Update checkpoint
    saveCheckpoint({
      scriptPaths,
      scriptsIndexedCount,
      totalUnityDocuments,
      collectedUnityDocuments: unityOffset,
      cleared: true,
      packagePathMap,
    }, logger);
  }

  // ── Done ──────────────────────────────────────────────────────────
  deleteCheckpoint(logger);

  const indexedPaths = contextEngine.getIndexedPaths();
  let summary = formatSummary(scriptPaths.length, totalUnityDocsIndexed, indexedPaths.length, stats, isResume);

  if (totalScriptReadErrors > 0) {
    summary += `\n\nWarning: ${totalScriptReadErrors} scripts could not be read from disk.`;
    if (sampleErrors.length > 0) {
      summary += `\nSample errors:\n${sampleErrors.map(e => `  - ${e}`).join('\n')}`;
    }
  }

  // Log package path map for diagnostics
  const mapEntries = Object.entries(packagePathMap);
  if (mapEntries.length > 0) {
    summary += `\n\nPackage path mappings: ${mapEntries.map(([k, v]) => `${k} -> ${v}`).join(', ')}`;
  }

  await sendProgress(extra, scriptPaths.length + totalUnityDocuments, scriptPaths.length + totalUnityDocuments, summary, logger);

  logger.info('Completed project indexing run', {
    scriptPaths: scriptPaths.length,
    scriptsReadThisRun: totalScriptsRead,
    scriptReadErrors: totalScriptReadErrors,
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
