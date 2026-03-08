import path from 'path';
import { DirectContext, type File as ContextDocument } from '@augmentcode/auggie-sdk';
import { Logger } from '../utils/logger.js';

const STATE_FILE_PATH = path.resolve(process.cwd(), 'ProjectSettings/.augment-context-state.json');
const BATCH_SIZE = 100;

/** Augment Context Engine max blob size (1 MB). Documents exceeding this are skipped. */
const MAX_BLOB_BYTES = 1_048_576;

export { BATCH_SIZE };

export class ContextEngineService {
  private readonly logger = new Logger('ContextEngine');
  private context: DirectContext | null = null;

  public get isInitialized(): boolean {
    return this.context !== null;
  }

  public async initialize(): Promise<void> {
    if (this.context) {
      this.logger.info('Context engine already initialized');
      return;
    }

    try {
      this.logger.info(`Initializing context engine from state file: ${STATE_FILE_PATH}`);
      this.context = await DirectContext.importFromFile(STATE_FILE_PATH);
      this.logger.info('Context engine restored from saved state');
    } catch (error) {
      this.logger.warn('Failed to restore context engine state, creating a new context', error);
      this.context = await DirectContext.create();
      this.logger.info('Created new context engine state');
    }
  }

  public async indexDocuments(documents: Array<{ path: string; contents: string }>): Promise<void> {
    const context = this.requireContext();

    if (documents.length === 0) {
      this.logger.info('No documents received for indexing');
      return;
    }

    // Clear stale index so it reflects exactly what the editor is configured to index
    this.logger.info('Clearing previous index before re-indexing');
    await context.clearIndex();

    // Filter out documents that exceed the Context Engine blob size limit
    const validDocs = documents.filter(doc => {
      const byteLength = Buffer.byteLength(doc.contents, 'utf-8');
      if (byteLength > MAX_BLOB_BYTES) {
        this.logger.warn(`Skipping oversized document (${(byteLength / 1024).toFixed(0)}KB > 1MB limit): ${doc.path}`);
        return false;
      }
      return true;
    });

    this.logger.info(`Indexing ${validDocs.length} documents (${documents.length - validDocs.length} skipped as oversized)`);

    for (let offset = 0; offset < validDocs.length; offset += BATCH_SIZE) {
      const batch = validDocs.slice(offset, offset + BATCH_SIZE) as ContextDocument[];
      const batchNumber = Math.floor(offset / BATCH_SIZE) + 1;
      const totalBatches = Math.ceil(validDocs.length / BATCH_SIZE);
      const isLastBatch = offset + BATCH_SIZE >= validDocs.length;

      this.logger.info(`Uploading indexing batch ${batchNumber}/${totalBatches}`, {
        batchSize: batch.length,
        waitForIndexing: isLastBatch,
      });

      await context.addToIndex(batch, {
        waitForIndexing: isLastBatch,
      });
    }

    this.logger.info('Waiting for context engine indexing to finish');
    await context.waitForIndexing();

    this.logger.info(`Persisting context engine state to ${STATE_FILE_PATH}`);
    await context.exportToFile(STATE_FILE_PATH);
  }

  /**
   * Clear the index. Called once at the start of a fresh indexing run.
   */
  public async clearIndex(): Promise<void> {
    const context = this.requireContext();
    this.logger.info('Clearing previous index');
    await context.clearIndex();
  }

  /**
   * Index a single batch of documents. Used by checkpoint-based resumable indexing.
   * Automatically skips documents exceeding the 1MB blob size limit.
   * @param batch The documents to index in this batch.
   * @param isLastBatch If true, waits for indexing to complete and persists state.
   * @returns Number of documents skipped due to size limits.
   */
  public async indexBatch(
    batch: Array<{ path: string; contents: string }>,
    isLastBatch: boolean
  ): Promise<number> {
    const context = this.requireContext();

    // Filter out documents that exceed the Context Engine blob size limit
    const validDocs: Array<{ path: string; contents: string }> = [];
    let skipped = 0;
    for (const doc of batch) {
      const byteLength = Buffer.byteLength(doc.contents, 'utf-8');
      if (byteLength > MAX_BLOB_BYTES) {
        this.logger.warn(`Skipping oversized document (${(byteLength / 1024).toFixed(0)}KB > 1MB limit): ${doc.path}`);
        skipped++;
      } else {
        validDocs.push(doc);
      }
    }

    if (validDocs.length > 0) {
      await context.addToIndex(validDocs as ContextDocument[], {
        waitForIndexing: isLastBatch,
      });
    }

    if (isLastBatch) {
      this.logger.info('Waiting for context engine indexing to finish');
      await context.waitForIndexing();
      this.logger.info(`Persisting context engine state to ${STATE_FILE_PATH}`);
      await context.exportToFile(STATE_FILE_PATH);
    }

    return skipped;
  }

  public async search(query: string): Promise<string> {
    const context = this.requireContext();
    this.logger.info(`Searching context engine for query: ${query}`);
    return await context.search(query);
  }

  public getIndexedPaths(): string[] {
    if (!this.context) {
      this.logger.warn('Requested indexed paths before context engine initialization');
      return [];
    }

    const paths = this.context.getIndexedPaths();
    this.logger.info(`Retrieved ${paths.length} indexed paths`);
    return paths;
  }

  public async saveState(): Promise<void> {
    const context = this.requireContext();
    this.logger.info(`Saving context engine state to ${STATE_FILE_PATH}`);
    await context.exportToFile(STATE_FILE_PATH);
  }

  private requireContext(): DirectContext {
    if (!this.context) {
      this.logger.error('Context engine accessed before initialization');
      throw new Error('Context engine is not initialized');
    }

    return this.context;
  }
}
