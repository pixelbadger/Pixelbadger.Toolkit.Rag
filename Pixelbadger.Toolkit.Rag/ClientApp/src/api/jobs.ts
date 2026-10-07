import { apiFetch } from "./client";
import type { JobsQuery, JobsResponse } from "./types";

export const jobsQueryKey = ["jobs"] as const;

export function listJobs({ page, pageSize, status }: JobsQuery, signal?: AbortSignal): Promise<JobsResponse> {
  const params = new URLSearchParams({ page: String(page), pageSize: String(pageSize) });
  if (status) params.set("status", status);
  return apiFetch<JobsResponse>(`/api/jobs?${params}`, { signal });
}
