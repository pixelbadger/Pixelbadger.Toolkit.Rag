import { AlertCircle } from "lucide-react";

import { ApiError } from "@/api/client";

/** Renders a failed API call: the Problem Details title/detail plus any validation `errors`. */
export function ErrorAlert({ error, title }: { error: unknown; title?: string }) {
  const apiError = error instanceof ApiError ? error : null;
  const heading = title ?? apiError?.problem?.title ?? "Something went wrong";
  const message = error instanceof Error ? error.message : String(error);
  const fieldErrors = apiError?.fieldErrors ?? [];

  return (
    <div role="alert" className="flex gap-3 rounded-lg border border-destructive/50 bg-destructive/5 p-4 text-sm">
      <AlertCircle className="mt-0.5 size-4 shrink-0 text-destructive" aria-hidden="true" />
      <div className="min-w-0 space-y-1">
        <p className="font-medium text-destructive">{heading}</p>
        {message && message !== heading && <p className="break-words">{message}</p>}
        {fieldErrors.length > 0 && (
          <ul className="list-disc space-y-0.5 pl-5">
            {fieldErrors.flatMap(([field, messages]) =>
              messages.map((m, i) => (
                <li key={`${field}-${i}`} className="break-words">
                  <span className="font-medium">{field}:</span> {m}
                </li>
              )),
            )}
          </ul>
        )}
      </div>
    </div>
  );
}
