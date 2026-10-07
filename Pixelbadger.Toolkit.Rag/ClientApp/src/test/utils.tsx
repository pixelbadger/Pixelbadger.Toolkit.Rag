import { render } from "@testing-library/react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { RouterProvider, createMemoryRouter } from "react-router-dom";

import { routes } from "@/app/router";
import type { JobListItem, JobsResponse, SearchResult } from "@/api/types";

export function renderApp(path = "/") {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false, refetchOnWindowFocus: false } } });
  const router = createMemoryRouter(routes, { initialEntries: [path] });
  const utils = render(
    <QueryClientProvider client={queryClient}>
      <RouterProvider router={router} />
    </QueryClientProvider>,
  );
  return { ...utils, router, queryClient };
}

export const DOC_ID = "019a1b2c-3d4e-7f50-8a6b-7c8d9e0f1a2b";

export function job(overrides: Partial<JobListItem> = {}): JobListItem {
  return {
    jobId: "019a0000-0000-7000-8000-000000000001",
    documentId: DOC_ID,
    path: "docs/guide.md",
    status: "Succeeded",
    attempts: 1,
    maxChunkCharacters: 20000,
    sizeBytes: 2048,
    chunkCount: 4,
    createdAtUtc: "2026-10-07T00:00:00Z",
    startedAtUtc: "2026-10-07T00:00:01Z",
    completedAtUtc: "2026-10-07T00:00:03Z",
    error: null,
    ...overrides,
  };
}

export function jobsResponse(jobs: JobListItem[], overrides: Partial<JobsResponse> = {}): JobsResponse {
  return { jobs, page: 1, pageSize: 25, totalCount: jobs.length, totalPages: 1, ...overrides };
}

export function result(overrides: Partial<SearchResult> = {}): SearchResult {
  return {
    score: 0.0328,
    chunkId: "019a9999-0000-7000-8000-00000000abcd",
    documentId: DOC_ID,
    sourcePath: "docs/mars.md",
    sourceFile: "mars.md",
    ordinal: 2,
    modality: "Text",
    locatorStart: 10,
    locatorEnd: 40,
    content: "Mars is red.",
    keywordRank: 1,
    vectorRank: 3,
    ...overrides,
  };
}
