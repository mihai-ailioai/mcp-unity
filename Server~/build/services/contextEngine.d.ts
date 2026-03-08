export declare class ContextEngineService {
    private readonly logger;
    private context;
    get isInitialized(): boolean;
    initialize(): Promise<void>;
    indexDocuments(documents: Array<{
        path: string;
        contents: string;
    }>): Promise<void>;
    search(query: string): Promise<string>;
    getIndexedPaths(): string[];
    saveState(): Promise<void>;
    private requireContext;
}
