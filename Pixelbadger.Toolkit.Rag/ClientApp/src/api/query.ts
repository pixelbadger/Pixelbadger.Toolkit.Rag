import { apiFetch } from "./client";
import type { QueryRequest, QueryResponse } from "./types";

export function runQuery(request: QueryRequest, signal?: AbortSignal): Promise<QueryResponse> {
  return apiFetch<QueryResponse>("/api/query", { method: "POST", body: request, signal });
}
