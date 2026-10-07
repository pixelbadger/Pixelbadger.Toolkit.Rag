import type { JobsResponse } from "@/api/types";

export const ACTIVE_POLL_MS = 2000;
export const IDLE_POLL_MS = 10000;

/** 2s while the visible page has work in flight, otherwise 10s (TanStack pauses polling in hidden tabs). */
export function pollInterval(data: JobsResponse | undefined): number {
  const active = data?.jobs.some((j) => j.status === "Queued" || j.status === "Processing");
  return active ? ACTIVE_POLL_MS : IDLE_POLL_MS;
}
