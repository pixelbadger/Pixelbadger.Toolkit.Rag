import type { ProblemDetails } from "./types";

/** A failed API call: HTTP status plus the parsed Problem Details when the server sent them. */
export class ApiError extends Error {
  readonly status: number;
  readonly problem: ProblemDetails | null;

  constructor(status: number, message: string, problem: ProblemDetails | null = null) {
    super(message);
    this.name = "ApiError";
    this.status = status;
    this.problem = problem;
  }

  /** Validation errors as [field, messages] pairs (empty when the response had none). */
  get fieldErrors(): [string, string[]][] {
    return Object.entries(this.problem?.errors ?? {});
  }
}

export interface RequestOptions {
  method?: string;
  /** A FormData body is sent as multipart (the browser sets the Content-Type boundary); anything else is JSON. */
  body?: unknown;
  signal?: AbortSignal;
}

function isJson(response: Response): boolean {
  const type = response.headers.get("content-type") ?? "";
  return type.includes("json");
}

async function toApiError(response: Response): Promise<ApiError> {
  let problem: ProblemDetails | null = null;
  if (isJson(response)) {
    try {
      problem = (await response.json()) as ProblemDetails;
    } catch {
      problem = null;
    }
  }
  const message =
    problem?.detail ?? problem?.title ?? (response.statusText || `Request failed with status ${response.status}`);
  return new ApiError(response.status, message, problem);
}

export async function apiFetch<T>(path: string, { method, body, signal }: RequestOptions = {}): Promise<T> {
  const init: RequestInit = { method: method ?? (body === undefined ? "GET" : "POST"), signal };
  const headers: Record<string, string> = { Accept: "application/json, application/problem+json" };

  if (body instanceof FormData) {
    init.body = body; // no Content-Type: the browser adds the multipart boundary
  } else if (body !== undefined) {
    headers["Content-Type"] = "application/json";
    init.body = JSON.stringify(body);
  }
  init.headers = headers;

  const response = await fetch(path, init);
  if (!response.ok) throw await toApiError(response);
  if (response.status === 204) return undefined as T;
  return (await response.json()) as T;
}
