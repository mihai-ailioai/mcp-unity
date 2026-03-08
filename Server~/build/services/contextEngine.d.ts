declare const BATCH_SIZE = 100;
export { BATCH_SIZE };
export declare class ContextEngineService {
    private readonly logger;
    private context;
    get isInitialized(): boolean;
    initialize(): Promise<void>;
    indexDocuments(documents: Array<{
        path: string;
        contents: string;
    }>): Promise<void>;
    /**
     * Clear the index. Called once at the start of a fresh indexing run.
     */
    clearIndex(): Promise<void>;
    /**
     * Index a single batch of documents. Used by checkpoint-based resumable indexing.
     * @param batch The documents to index in this batch.
     * @param isLastBatch If true, waits for indexing to complete and persists state.
     */
    indexBatch(batch: Array<{
        path: string;
        contents: string;
    }>, isLastBatch: boolean): Promise<void>;
    search(query: string): Promise<string>;
    getIndexedPaths(): string[];
    saveState(): Promise<void>;
    private requireContext;
}
