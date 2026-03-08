import fs from 'fs';
import path from 'path';
import os from 'os';
import crypto from 'crypto';
import { Logger } from '../utils/logger.js';

// ── Constants ───────────────────────────────────────────────────────────
const STATE_FILE_PATH = path.resolve(process.cwd(), 'ProjectSettings/.augment-context-state.json');
const BATCH_SIZE = 50;
const BLOB_NAMING_VERSION = 2023102300;
const MAX_BLOB_SIZE = 1024 * 1024; // 1 MiB – skip oversized blobs

// ── Types ───────────────────────────────────────────────────────────────
interface AugmentCredentials {
  accessToken: string;
  tenantURL: string;
}

interface BlobEntry {
  blobName: string;
  path: string;
  contents: string;
}

interface ContextState {
  /** path → blobName for every indexed document */
  indexed: Record<string, string>;
}

// ── Helpers ─────────────────────────────────────────────────────────────

function readCredentials(): AugmentCredentials {
  // Env-var overrides first (same precedence as the SDK)
  const envToken = process.env.AUGMENT_API_TOKEN;
  const envUrl = process.env.AUGMENT_API_URL;
  if (envToken && envUrl) {
    return { accessToken: envToken, tenantURL: envUrl };
  }

  const sessionPath = path.join(os.homedir(), '.augment', 'session.json');
  if (!fs.existsSync(sessionPath)) {
    throw new Error(
      `Augment session file not found at ${sessionPath}. Run "auggie login" to authenticate.`
    );
  }

  const raw = JSON.parse(fs.readFileSync(sessionPath, 'utf-8'));
  const accessToken: string | undefined = raw.accessToken;
  const tenantURL: string | undefined = raw.tenantURL;

  if (!accessToken || !tenantURL) {
    throw new Error(
      'Augment session.json is missing accessToken or tenantURL. Run "auggie login" to re-authenticate.'
    );
  }

  return { accessToken, tenantURL };
}

function computeBlobName(filePath: string, contents: string): string {
  const encoder = new TextEncoder();
  const hash = crypto.createHash('sha256');
  hash.update(encoder.encode(filePath));
  hash.update(encoder.encode(contents));
  return hash.digest('hex');
}

async function apiPost(
  tenantURL: string,
  accessToken: string,
  endpoint: string,
  body: unknown
): Promise<any> {
  const url = `${tenantURL.replace(/\/+$/, '')}/${endpoint.replace(/^\/+/, '')}`;
  const res = await fetch(url, {
    method: 'POST',
    headers: {
      'Content-Type': 'application/json',
      Authorization: `Bearer ${accessToken}`,
    },
    body: JSON.stringify(body),
  });

  if (!res.ok) {
    const text = await res.text().catch(() => '');
    throw new Error(`Augment API ${endpoint} returned ${res.status}: ${text}`);
  }

  return res.json();
}

// ── State persistence ───────────────────────────────────────────────────

function loadState(): ContextState {
  try {
    if (fs.existsSync(STATE_FILE_PATH)) {
      return JSON.parse(fs.readFileSync(STATE_FILE_PATH, 'utf-8'));
    }
  } catch {
    // corrupt or missing – start fresh
  }
  return { indexed: {} };
}

function saveState(state: ContextState): void {
  const dir = path.dirname(STATE_FILE_PATH);
  if (!fs.existsSync(dir)) {
    fs.mkdirSync(dir, { recursive: true });
  }
  fs.writeFileSync(STATE_FILE_PATH, JSON.stringify(state, null, 2));
}

// ── Service ─────────────────────────────────────────────────────────────

export class ContextEngineService {
  private readonly logger = new Logger('ContextEngine');
  private credentials: AugmentCredentials | null = null;
  private state: ContextState = { indexed: {} };

  public get isInitialized(): boolean {
    return this.credentials !== null;
  }

  /**
   * Read credentials + restore persisted state.
   * Non-blocking in startServer – if auth fails the service stays uninitialised
   * and tools return a helpful error.
   */
  public async initialize(): Promise<void> {
    if (this.credentials) {
      this.logger.info('Context engine already initialized');
      return;
    }

    this.credentials = readCredentials();
    this.state = loadState();
    this.logger.info(
      `Context engine initialized – ${Object.keys(this.state.indexed).length} previously indexed paths`
    );
  }

  /**
   * Index (upload) documents to Augment's Context Engine.
   *
   * Flow (mirrors the SDK internals):
   *  1. Compute blob names for every document.
   *  2. Call `find-missing` to see which blobs the server doesn't have yet.
   *  3. Upload only the missing blobs in batches via `batch-upload`.
   *  4. Checkpoint all blobs via `checkpoint-blobs` so the server knows
   *     what our current corpus looks like.
   *  5. Persist local state.
   */
  public async indexDocuments(
    documents: Array<{ path: string; contents: string }>
  ): Promise<void> {
    const creds = this.requireCredentials();

    if (documents.length === 0) {
      this.logger.info('No documents received for indexing');
      return;
    }

    this.logger.info(`Preparing ${documents.length} documents for indexing`);

    // 1. Build blob entries, skipping oversized files
    const blobs: BlobEntry[] = [];
    for (const doc of documents) {
      const size = Buffer.byteLength(doc.contents, 'utf-8');
      if (size > MAX_BLOB_SIZE) {
        this.logger.warn(`Skipping oversized document (${size} bytes): ${doc.path}`);
        continue;
      }
      blobs.push({
        blobName: computeBlobName(doc.path, doc.contents),
        path: doc.path,
        contents: doc.contents,
      });
    }

    if (blobs.length === 0) {
      this.logger.info('All documents exceeded size limit – nothing to index');
      return;
    }

    // 2. Ask the server which blobs it already has
    const allBlobNames = blobs.map((b) => b.blobName);
    const findMissingRes = await apiPost(creds.tenantURL, creds.accessToken, 'find-missing', {
      blobNames: allBlobNames,
      blobNamingVersion: BLOB_NAMING_VERSION,
    });
    const missingSet = new Set<string>(findMissingRes.missingBlobNames ?? []);
    this.logger.info(`Server already has ${blobs.length - missingSet.size}/${blobs.length} blobs`);

    // 3. Upload missing blobs in batches
    const missingBlobs = blobs.filter((b) => missingSet.has(b.blobName));
    for (let offset = 0; offset < missingBlobs.length; offset += BATCH_SIZE) {
      const batch = missingBlobs.slice(offset, offset + BATCH_SIZE);
      const batchNum = Math.floor(offset / BATCH_SIZE) + 1;
      const totalBatches = Math.ceil(missingBlobs.length / BATCH_SIZE);
      this.logger.info(
        `Uploading batch ${batchNum}/${totalBatches} (${batch.length} blobs)`
      );

      await apiPost(creds.tenantURL, creds.accessToken, 'batch-upload', {
        blobs: batch.map((b) => ({
          blobName: b.blobName,
          content: b.contents,
        })),
        blobNamingVersion: BLOB_NAMING_VERSION,
      });
    }

    // 4. Checkpoint – tell the server "this is our current corpus"
    this.logger.info('Checkpointing blobs');
    await apiPost(creds.tenantURL, creds.accessToken, 'checkpoint-blobs', {
      blobs: blobs.map((b) => ({
        blobName: b.blobName,
        path: b.path,
      })),
      blobNamingVersion: BLOB_NAMING_VERSION,
      waitForIndexing: true,
    });

    // 5. Update & persist local state
    const indexed: Record<string, string> = {};
    for (const b of blobs) {
      indexed[b.path] = b.blobName;
    }
    this.state.indexed = indexed;
    saveState(this.state);
    this.logger.info(`Indexed ${blobs.length} documents, state persisted`);
  }

  /**
   * Semantic search via `agents/codebase-retrieval`.
   */
  public async search(query: string): Promise<string> {
    const creds = this.requireCredentials();
    this.logger.info(`Searching context engine for: ${query}`);

    const res = await apiPost(creds.tenantURL, creds.accessToken, 'agents/codebase-retrieval', {
      chatMessages: [{ role: 'user', content: query }],
    });

    // The response shape from the SDK is { response: string }
    const text: string = typeof res.response === 'string' ? res.response : JSON.stringify(res);
    return text;
  }

  public getIndexedPaths(): string[] {
    return Object.keys(this.state.indexed);
  }

  public async saveState(): Promise<void> {
    saveState(this.state);
    this.logger.info('Context engine state saved');
  }

  // ── Internal ────────────────────────────────────────────────────────
  private requireCredentials(): AugmentCredentials {
    if (!this.credentials) {
      this.logger.error('Context engine accessed before initialization');
      throw new Error(
        'Context engine is not initialized. Ensure Augment authentication is configured (run "auggie login").'
      );
    }
    return this.credentials;
  }
}
