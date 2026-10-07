import { QueryClient } from "@tanstack/react-query";

import { ApiError } from "@/api/client";

export function createQueryClient() {
  return new QueryClient({
    defaultOptions: {
      queries: {
        // Do not retry 4xx answers (they will not change); retry transient failures once.
        retry: (count, error) => !(error instanceof ApiError && error.status < 500) && count < 1,
        refetchOnWindowFocus: false,
      },
    },
  });
}
