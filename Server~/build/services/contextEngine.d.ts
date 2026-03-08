declare const BATCH_SIZE = 100;
export { BATCH_SIZE };
/** Stats returned from a single indexBatch call. */
export interface BatchIndexStats {
    /** Documents skipped due to exceeding size limit. */
    skipped: number;
    /** Documents newly uploaded to the backend. */
    newlyUploaded: number;
    /** Documents already present on the backend (unchanged). */
    alreadyUploaded: number;
    /** Total bytes uploaded in this batch. */
    bytesUploaded: number;
}
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
     * Automatically skips documents exceeding the 1MB blob size limit.
     * @param batch The documents to index in this batch.
     * @param isLastBatch If true, waits for indexing to complete and persists state.
     * @returns Stats about the batch: skipped, newlyUploaded, alreadyUploaded, bytesUploaded.
     */
    indexBatch(batch: Array<{
        path: string;
        contents: string;
    }>, isLastBatch: boolean): Promise<BatchIndexStats>;
    search(query: string): Promise<string>;
    getIndexedPaths(): string[];
    saveState(): Promise<void>;
    private requireContext;
}
