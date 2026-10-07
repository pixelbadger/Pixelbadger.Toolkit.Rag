import { useState, type FormEvent } from "react";
import { useMutation } from "@tanstack/react-query";
import { Loader2, Search } from "lucide-react";

import { runQuery } from "@/api/query";
import { ErrorAlert } from "@/components/error-alert";
import { PageHeader } from "@/components/page-header";
import { SearchResultCard } from "@/components/search-result-card";
import { Button } from "@/components/ui/button";
import { Card } from "@/components/ui/card";
import { Label, Select, Textarea } from "@/components/ui/form";
import { isGuid, normaliseDocumentIds } from "@/lib/format";

const MAX_RESULTS_OPTIONS = [5, 10, 20, 50, 100];
const DEFAULT_MAX_RESULTS = 10;

export function QueryPage() {
  const [query, setQuery] = useState("");
  const [maxResults, setMaxResults] = useState(DEFAULT_MAX_RESULTS);
  const [documentIdsText, setDocumentIdsText] = useState("");

  const search = useMutation({
    mutationFn: (vars: { query: string; maxResults: number; documentIds: string[] }) => runQuery(vars),
  });

  const documentIds = normaliseDocumentIds(documentIdsText);
  const invalidIds = documentIds.filter((id) => !isGuid(id));

  function submit(e: FormEvent) {
    e.preventDefault();
    if (!query.trim()) return;
    search.mutate({ query: query.trim(), maxResults, documentIds });
  }

  const results = search.data?.results;

  return (
    <>
      <PageHeader title="Query" subtitle="Hybrid (BM25 + vector) retrieval over indexed documents" />

      <form onSubmit={submit} className="max-w-3xl space-y-4">
        <div className="space-y-1.5">
          <Label htmlFor="query-text">Query</Label>
          <Textarea id="query-text" value={query} onChange={(e) => setQuery(e.target.value)} placeholder="What are you looking for?" rows={3} />
        </div>

        <div className="space-y-1.5">
          <Label htmlFor="max-results">Max results</Label>
          <Select id="max-results" value={maxResults} onChange={(e) => setMaxResults(Number(e.target.value))} className="w-28">
            {MAX_RESULTS_OPTIONS.map((n) => (
              <option key={n} value={n}>
                {n}
              </option>
            ))}
          </Select>
        </div>

        <details className="rounded-md border p-3">
          <summary className="cursor-pointer text-sm font-medium">Advanced</summary>
          <div className="mt-3 space-y-1.5">
            <Label htmlFor="document-ids">Restrict to document ids</Label>
            <Textarea
              id="document-ids"
              value={documentIdsText}
              onChange={(e) => setDocumentIdsText(e.target.value)}
              placeholder="One GUID per line, or comma separated"
              className="font-mono"
              rows={3}
            />
            <p className="text-xs text-muted-foreground">
              {documentIds.length > 0 ? `${documentIds.length} id(s) will be sent.` : "Leave blank to search every document."}
            </p>
            {invalidIds.length > 0 && (
              <p role="status" className="text-xs text-destructive">
                Does not look like a GUID: {invalidIds.join(", ")}. The server has the final say.
              </p>
            )}
          </div>
        </details>

        <Button type="submit" disabled={search.isPending || !query.trim()}>
          {search.isPending ? <Loader2 className="animate-spin motion-reduce:animate-none" /> : <Search />} Search
        </Button>
      </form>

      <section aria-label="Results" className="space-y-3">
        {search.isPending && (
          <p role="status" className="text-sm text-muted-foreground">
            Searching…
          </p>
        )}
        {search.isError && <ErrorAlert error={search.error} title="Search failed" />}
        {results && results.length === 0 && (
          <Card className="items-center p-10 text-center text-sm text-muted-foreground">No results found.</Card>
        )}
        {results && results.length > 0 && (
          <>
            <p className="text-sm text-muted-foreground">
              {results.length} result{results.length === 1 ? "" : "s"}, ranked
            </p>
            <ol className="space-y-3">
              {results.map((result) => (
                <li key={result.chunkId}>
                  <SearchResultCard result={result} />
                </li>
              ))}
            </ol>
          </>
        )}
      </section>
    </>
  );
}
