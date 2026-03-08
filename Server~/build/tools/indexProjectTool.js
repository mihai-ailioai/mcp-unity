import * as z from 'zod';
import * as fs from 'fs';
import * as path from 'path';
import { BATCH_SIZE } from '../services/contextEngine.js';
import { McpUnityError, ErrorType } from '../utils/errors.js';
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
// ── Checkpoint persistence ──────────────────────────────────────────────
const CHECKPOINT_PATH = path.resolve(process.cwd(), 'Library/.context-engine-index-checkpoint.json');
function loadCheckpoint(logger) {
    try {
        if (!fs.existsSync(CHECKPOINT_PATH))
            return null;
        const raw = fs.readFileSync(CHECKPOINT_PATH, 'utf-8');
        const data = JSON.parse(raw);
        if (typeof data.totalUnityDocuments !== 'number' || typeof data.collectedUnityDocuments !== 'number')
            return null;
        // Migrate old checkpoints that stored full scriptDocuments instead of paths
        if (!Array.isArray(data.scriptPaths) && Array.isArray(data.scriptDocuments)) {
            data.scriptPaths = data.scriptDocuments.map((d) => d.path);
            delete data.scriptDocuments;
        }
        if (typeof data.scriptsIndexedCount !== 'number') {
            data.scriptsIndexedCount = data.scriptsIndexed ? data.scriptPaths.length : 0;
        }
        const checkpoint = data;
        logger.info(`Loaded checkpoint: ${checkpoint.scriptsIndexedCount}/${checkpoint.scriptPaths.length} scripts indexed, ${checkpoint.collectedUnityDocuments}/${checkpoint.totalUnityDocuments} Unity docs collected`);
        return checkpoint;
    }
    catch {
        return null;
    }
}
function saveCheckpoint(checkpoint, logger) {
    try {
        fs.writeFileSync(CHECKPOINT_PATH, JSON.stringify(checkpoint), 'utf-8');
    }
    catch (err) {
        logger.error(`Failed to save checkpoint: ${err.message}`);
    }
}
function deleteCheckpoint(logger) {
    try {
        if (fs.existsSync(CHECKPOINT_PATH)) {
            fs.unlinkSync(CHECKPOINT_PATH);
            logger.info('Deleted indexing checkpoint');
        }
    }
    catch (err) {
        logger.error(`Failed to delete checkpoint: ${err.message}`);
    }
}
// ── Progress notification helper ────────────────────────────────────────
async function sendProgress(extra, progress, total, message, logger) {
    const progressToken = extra?._meta?.progressToken;
    if (!progressToken || !extra.sendNotification)
        return;
    try {
        await extra.sendNotification({
            method: 'notifications/progress',
            params: { progressToken, progress, total, message },
        });
    }
    catch (err) {
        logger.error(`Failed to send progress notification: ${err.message}`);
    }
}
// ── Tool registration ───────────────────────────────────────────────────
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
// ── Script reading helper ──────────────────────────────────────────────
/**
 * Resolves a Unity AssetDatabase path to an actual disk path using the package path map.
 * E.g. "Packages/com.evlppy.core/Runtime/Foo.cs" -> "Packages/Core-Module/Runtime/Foo.cs"
 */
function resolveDiskPath(assetPath, packagePathMap) {
    for (const [assetDbPrefix, diskPrefix] of Object.entries(packagePathMap)) {
        if (assetPath.startsWith(assetDbPrefix + '/')) {
            return diskPrefix + assetPath.substring(assetDbPrefix.length);
        }
    }
    return assetPath;
}
/**
 * Reads a slice of script paths from disk and returns documents.
 * Skips empty files and logs errors for unreadable files.
 */
function readScriptBatch(scriptPaths, startIdx, endIdx, projectRoot, logger, packagePathMap = {}) {
    const docs = [];
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
        }
        catch (err) {
            logger.error(`Failed to read script ${scriptPath}: ${err.message}`);
        }
    }
    return docs;
}
// ── Early-return helper ────────────────────────────────────────────────
function earlyReturn(totalScripts, scriptsIndexedCount, totalUnityDocuments, unityOffset, totalUnityDocsIndexed, startTime) {
    const elapsed = Math.round((Date.now() - startTime) / 1000);
    const totalWork = totalScripts + totalUnityDocuments;
    const totalDone = scriptsIndexedCount + unityOffset;
    const pct = totalWork > 0 ? Math.round((totalDone / totalWork) * 100) : 0;
    const lines = [];
    lines.push(`Indexing in progress (${pct}% complete, ${elapsed}s elapsed):`);
    lines.push(`  Scripts: ${scriptsIndexedCount}/${totalScripts} indexed`);
    lines.push(`  Prefabs/scenes: ${unityOffset}/${totalUnityDocuments} collected, ${totalUnityDocsIndexed} indexed`);
    lines.push(`Please call index_project again to continue from the checkpoint.`);
    return { content: [{ type: 'text', text: lines.join('\n') }] };
}
// ── Core handler ────────────────────────────────────────────────────────
async function toolHandler(mcpUnity, contextEngine, rawParams, extra, logger) {
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
    function isNearDeadline() {
        return Date.now() >= deadline;
    }
    // ── Try to resume from checkpoint ─────────────────────────────────
    let checkpoint = loadCheckpoint(logger);
    const isResume = checkpoint !== null && (checkpoint.scriptsIndexedCount < checkpoint.scriptPaths.length ||
        checkpoint.collectedUnityDocuments < checkpoint.totalUnityDocuments);
    if (!isResume) {
        checkpoint = null;
    }
    // ── Aggregate indexing stats across all batches ───────────────────
    let totalUnityDocsIndexed = 0;
    const stats = { skipped: 0, newlyUploaded: 0, alreadyUploaded: 0, bytesUploaded: 0 };
    function accumulateStats(batch) {
        stats.skipped += batch.skipped;
        stats.newlyUploaded += batch.newlyUploaded;
        stats.alreadyUploaded += batch.alreadyUploaded;
        stats.bytesUploaded += batch.bytesUploaded;
    }
    // ── Phase 0: Collect or restore data ──────────────────────────────
    const unityProjectRoot = process.cwd();
    let scriptPaths;
    let scriptsIndexedCount;
    let totalUnityDocuments;
    let unityOffset;
    let packagePathMap = {};
    if (checkpoint) {
        scriptPaths = checkpoint.scriptPaths;
        scriptsIndexedCount = checkpoint.scriptsIndexedCount;
        totalUnityDocuments = checkpoint.totalUnityDocuments;
        unityOffset = checkpoint.collectedUnityDocuments;
        packagePathMap = checkpoint.packagePathMap ?? {};
        logger.info(`Resuming: ${scriptsIndexedCount}/${scriptPaths.length} scripts, ${unityOffset}/${totalUnityDocuments} Unity docs`);
        await sendProgress(extra, scriptsIndexedCount + unityOffset, scriptPaths.length + totalUnityDocuments, `Resuming from checkpoint...`, logger);
    }
    else {
        // Fresh run — collect script paths from Unity
        await sendProgress(extra, 0, 1, 'Collecting project assets from Unity...', logger);
        const firstPage = (await mcpUnity.sendRequest({ method: 'collect_project_assets', params: { offset: 0 } }, { timeout: 300000 }));
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
    while (scriptsIndexedCount < scriptPaths.length) {
        if (isNearDeadline()) {
            logger.info('Approaching time limit during script indexing');
            return earlyReturn(scriptPaths.length, scriptsIndexedCount, totalUnityDocuments, unityOffset, totalUnityDocsIndexed, startTime);
        }
        const batchEnd = Math.min(scriptsIndexedCount + BATCH_SIZE, scriptPaths.length);
        const batch = readScriptBatch(scriptPaths, scriptsIndexedCount, batchEnd, unityProjectRoot, logger, packagePathMap);
        totalScriptsRead += batch.length;
        const isLastScriptBatch = batchEnd >= scriptPaths.length;
        // Only finalize if this is both the last script batch AND all prefabs are done
        const isLastOverall = isLastScriptBatch && unityOffset >= totalUnityDocuments;
        const batchNum = Math.floor(scriptsIndexedCount / BATCH_SIZE) + 1;
        const totalBatches = Math.ceil(scriptPaths.length / BATCH_SIZE);
        const msg = `Indexing scripts batch ${batchNum}/${totalBatches} (${scriptsIndexedCount}/${scriptPaths.length})...`;
        logger.info(msg);
        await sendProgress(extra, scriptsIndexedCount, scriptPaths.length + totalUnityDocuments, msg, logger);
        if (batch.length > 0) {
            accumulateStats(await contextEngine.indexBatch(batch, isLastOverall));
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
        const page = (await mcpUnity.sendRequest({ method: 'collect_project_assets', params: { offset: unityOffset } }, { timeout: 300000 }));
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
    const summary = formatSummary(scriptPaths.length, totalUnityDocsIndexed, indexedPaths.length, stats, isResume);
    await sendProgress(extra, scriptPaths.length + totalUnityDocuments, scriptPaths.length + totalUnityDocuments, summary, logger);
    logger.info('Completed project indexing run', {
        scriptPaths: scriptPaths.length,
        scriptsReadThisRun: totalScriptsRead,
        unityDocumentCount: totalUnityDocsIndexed,
        indexedPathCount: indexedPaths.length,
        ...stats,
        resumed: isResume,
    });
    return { content: [{ type: 'text', text: summary }] };
}
// ── Summary formatting ─────────────────────────────────────────────────
function formatBytes(bytes) {
    if (bytes < 1024)
        return `${bytes}B`;
    if (bytes < 1024 * 1024)
        return `${(bytes / 1024).toFixed(1)}KB`;
    return `${(bytes / (1024 * 1024)).toFixed(1)}MB`;
}
function formatSummary(scriptCount, unityDocCount, indexedPathCount, stats, isResume) {
    const parts = [];
    parts.push(`Indexed ${scriptCount} scripts + ${unityDocCount} prefabs/scenes`);
    // Upload details
    const details = [];
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
