// Mirrors the server contract (JSON enums are strings, properties are camelCase).

export const INGEST_JOB_STATUSES = ["Queued", "Processing", "Succeeded", "Skipped", "Failed"] as const;
export type IngestJobStatus = (typeof INGEST_JOB_STATUSES)[number];

export type IndexStatus = "Queued" | "Processing" | "Indexed" | "Failed";

export type Modality = "Text" | "Image" | "Audio";

export interface JobListItem {
  jobId: string;
  documentId: string;
  path: string;
  status: IngestJobStatus;
  attempts: number;
  maxChunkCharacters: number;
  sizeBytes: number;
  chunkCount: number | null;
  createdAtUtc: string;
  startedAtUtc: string | null;
  completedAtUtc: string | null;
  error: string | null;
}

export interface JobsResponse {
  jobs: JobListItem[];
  page: number;
  pageSize: number;
  totalCount: number;
  totalPages: number;
}

export interface JobsQuery {
  page: number;
  pageSize: number;
  status?: IngestJobStatus;
}

export interface IngestJobDto {
  jobId: string;
  status: IngestJobStatus;
  attempts: number;
  createdAtUtc: string;
  startedAtUtc: string | null;
  completedAtUtc: string | null;
  error: string | null;
}

export interface DocumentDto {
  documentId: string;
  path: string;
  title: string | null;
  modality: Modality;
  indexStatus: IndexStatus;
  chunkCount: number;
  updatedAtUtc: string;
  latestJob: IngestJobDto | null;
}

export interface DocumentsResponse {
  documents: DocumentDto[];
}

export interface QueryRequest {
  query: string;
  maxResults?: number;
  documentIds?: string[];
}

export interface SearchResult {
  score: number;
  chunkId: string;
  documentId: string;
  sourcePath: string;
  sourceFile: string;
  ordinal: number;
  modality: Modality;
  /** Char offset (text) or milliseconds (audio); null for images. */
  locatorStart: number | null;
  locatorEnd: number | null;
  content: string | null;
  keywordRank: number | null;
  vectorRank: number | null;
}

export interface QueryResponse {
  results: SearchResult[];
}

/** RFC 9457 Problem Details, as returned by ProblemDetails / ValidationProblem. */
export interface ProblemDetails {
  type?: string;
  title?: string;
  status?: number;
  detail?: string;
  instance?: string;
  errors?: Record<string, string[]>;
}
