import path from 'path';
import { DirectContext } from '@augmentcode/auggie-sdk';
import { Logger } from '../utils/logger.js';
const STATE_FILE_PATH = path.resolve(process.cwd(), 'ProjectSettings/.augment-context-state.json');
const BATCH_SIZE = 100;
export class ContextEngineService {
    logger = new Logger('ContextEngine');
    context = null;
    get isInitialized() {
        return this.context !== null;
    }
    async initialize() {
        if (this.context) {
            this.logger.info('Context engine already initialized');
            return;
        }
        try {
            this.logger.info(`Initializing context engine from state file: ${STATE_FILE_PATH}`);
            this.context = await DirectContext.importFromFile(STATE_FILE_PATH);
            this.logger.info('Context engine restored from saved state');
        }
        catch (error) {
            this.logger.warn('Failed to restore context engine state, creating a new context', error);
            this.context = await DirectContext.create();
            this.logger.info('Created new context engine state');
        }
    }
    async indexDocuments(documents) {
        const context = this.requireContext();
        if (documents.length === 0) {
            this.logger.info('No documents received for indexing');
            return;
        }
        // Clear stale index so it reflects exactly what the editor is configured to index
        this.logger.info('Clearing previous index before re-indexing');
        await context.clearIndex();
        this.logger.info(`Indexing ${documents.length} documents`);
        for (let offset = 0; offset < documents.length; offset += BATCH_SIZE) {
            const batch = documents.slice(offset, offset + BATCH_SIZE);
            const batchNumber = Math.floor(offset / BATCH_SIZE) + 1;
            const totalBatches = Math.ceil(documents.length / BATCH_SIZE);
            const isLastBatch = offset + BATCH_SIZE >= documents.length;
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
    async search(query) {
        const context = this.requireContext();
        this.logger.info(`Searching context engine for query: ${query}`);
        return await context.search(query);
    }
    getIndexedPaths() {
        if (!this.context) {
            this.logger.warn('Requested indexed paths before context engine initialization');
            return [];
        }
        const paths = this.context.getIndexedPaths();
        this.logger.info(`Retrieved ${paths.length} indexed paths`);
        return paths;
    }
    async saveState() {
        const context = this.requireContext();
        this.logger.info(`Saving context engine state to ${STATE_FILE_PATH}`);
        await context.exportToFile(STATE_FILE_PATH);
    }
    requireContext() {
        if (!this.context) {
            this.logger.error('Context engine accessed before initialization');
            throw new Error('Context engine is not initialized');
        }
        return this.context;
    }
}
