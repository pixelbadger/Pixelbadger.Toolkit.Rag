import { useRef, useState, type DragEvent, type FormEvent } from "react";
import { useMutation, useQueryClient } from "@tanstack/react-query";
import { Link } from "react-router-dom";
import { CheckCircle2, FileUp, Loader2, Trash2, Upload, X } from "lucide-react";

import { ACCEPT_ATTRIBUTE, SUPPORTED_EXTENSIONS, logicalPathOf, uploadDocuments } from "@/api/documents";
import { jobsQueryKey } from "@/api/jobs";
import { CopyId } from "@/components/copy-id";
import { ErrorAlert } from "@/components/error-alert";
import { PageHeader } from "@/components/page-header";
import { StatusBadge } from "@/components/status-badge";
import { Button } from "@/components/ui/button";
import { Card, CardContent, CardHeader, CardTitle } from "@/components/ui/card";
import { Input, Label } from "@/components/ui/form";
import { formatBytes } from "@/lib/format";
import { cn } from "@/lib/utils";

function isSupported(file: File): boolean {
  const name = file.name.toLowerCase();
  return SUPPORTED_EXTENSIONS.some((ext) => name.endsWith(ext));
}

export function IngestPage() {
  const queryClient = useQueryClient();
  const inputRef = useRef<HTMLInputElement>(null);
  const [files, setFiles] = useState<File[]>([]);
  const [maxChunk, setMaxChunk] = useState("");
  const [dragging, setDragging] = useState(false);

  const upload = useMutation({
    mutationFn: (vars: { files: File[]; maxChunkCharacters?: number }) => uploadDocuments(vars.files, vars.maxChunkCharacters),
    onSuccess: async () => {
      setFiles([]);
      await queryClient.invalidateQueries({ queryKey: jobsQueryKey });
    },
  });

  const maxChunkValue = maxChunk.trim() === "" ? undefined : Number(maxChunk);
  const maxChunkInvalid = maxChunkValue !== undefined && (!Number.isInteger(maxChunkValue) || maxChunkValue < 1);

  function addFiles(list: FileList | File[]) {
    const added = Array.from(list);
    if (added.length > 0) setFiles((current) => [...current, ...added]);
  }

  function onDrop(e: DragEvent) {
    e.preventDefault();
    setDragging(false);
    addFiles(e.dataTransfer.files);
  }

  function submit(e: FormEvent) {
    e.preventDefault();
    if (files.length === 0 || maxChunkInvalid) return;
    upload.mutate({ files, maxChunkCharacters: maxChunkValue });
  }

  const accepted = upload.data?.documents;

  return (
    <>
      <PageHeader title="Ingest" subtitle="Upload documents, images and audio to be indexed" />

      <form onSubmit={submit} className="max-w-3xl space-y-4">
        <div
          onDragOver={(e) => {
            e.preventDefault();
            setDragging(true);
          }}
          onDragLeave={() => setDragging(false)}
          onDrop={onDrop}
          data-testid="drop-zone"
          className={cn(
            "flex flex-col items-center gap-3 rounded-lg border-2 border-dashed p-8 text-center",
            dragging && "border-ring bg-accent",
          )}
        >
          <FileUp className="size-8 text-muted-foreground" aria-hidden="true" />
          <p className="text-sm">Drag and drop files here, or</p>
          <Button type="button" variant="outline" onClick={() => inputRef.current?.click()}>
            <Upload /> Choose files
          </Button>
          <input
            ref={inputRef}
            type="file"
            multiple
            accept={ACCEPT_ATTRIBUTE}
            aria-label="Files to ingest"
            className="sr-only"
            tabIndex={-1}
            onChange={(e) => {
              addFiles(e.target.files ?? []);
              e.target.value = ""; // allow choosing the same file again
            }}
          />
          <p className="text-xs text-muted-foreground">Text ({".txt, .md"}), images and audio. The server validates every file.</p>
        </div>

        {files.length > 0 && (
          <Card>
            <CardHeader className="flex-row items-center justify-between">
              <CardTitle>
                {files.length} file{files.length === 1 ? "" : "s"} selected
              </CardTitle>
              <Button type="button" variant="ghost" size="sm" onClick={() => setFiles([])}>
                <Trash2 /> Clear all
              </Button>
            </CardHeader>
            <CardContent>
              <ul className="divide-y">
                {files.map((file, i) => (
                  <li key={`${logicalPathOf(file)}-${i}`} className="flex items-center justify-between gap-3 py-2 text-sm">
                    <div className="min-w-0">
                      <p className="break-all">{logicalPathOf(file)}</p>
                      <p className="text-xs text-muted-foreground">
                        {formatBytes(file.size)}
                        {!isSupported(file) && <span className="text-destructive"> · unsupported file type (the server will reject it)</span>}
                      </p>
                    </div>
                    <Button
                      type="button"
                      variant="ghost"
                      size="icon-sm"
                      aria-label={`Remove ${logicalPathOf(file)}`}
                      onClick={() => setFiles((current) => current.filter((_, j) => j !== i))}
                    >
                      <X />
                    </Button>
                  </li>
                ))}
              </ul>
            </CardContent>
          </Card>
        )}

        <details className="rounded-md border p-3">
          <summary className="cursor-pointer text-sm font-medium">Advanced</summary>
          <div className="mt-3 space-y-1.5">
            <Label htmlFor="max-chunk">Max chunk characters</Label>
            <Input
              id="max-chunk"
              type="number"
              min={1}
              inputMode="numeric"
              value={maxChunk}
              onChange={(e) => setMaxChunk(e.target.value)}
              placeholder="Server default"
              className="w-48"
              aria-invalid={maxChunkInvalid}
            />
            <p className="text-xs text-muted-foreground">
              Optional. This can lower the server's chunk size limit but never exceed it. Leave blank to use the server default.
            </p>
            {maxChunkInvalid && (
              <p role="status" className="text-xs text-destructive">
                Enter a whole number of at least 1.
              </p>
            )}
          </div>
        </details>

        <Button type="submit" disabled={files.length === 0 || maxChunkInvalid || upload.isPending}>
          {upload.isPending ? <Loader2 className="animate-spin motion-reduce:animate-none" /> : <Upload />} Upload
        </Button>
      </form>

      {upload.isError && (
        <div className="max-w-3xl">
          <ErrorAlert error={upload.error} title="Upload rejected" />
        </div>
      )}

      {accepted && (
        <Card className="max-w-3xl" role="status" aria-label="Upload accepted">
          <CardHeader className="flex-row items-center justify-between">
            <CardTitle className="flex items-center gap-2">
              <CheckCircle2 className="size-4 text-success" aria-hidden="true" />
              {accepted.length} document{accepted.length === 1 ? "" : "s"} accepted
            </CardTitle>
            <Button asChild size="sm">
              <Link to="/">View jobs</Link>
            </Button>
          </CardHeader>
          <CardContent>
            <p className="mb-2 text-xs text-muted-foreground">Indexing runs in the background; sources become available once indexing succeeds.</p>
            <ul className="divide-y">
              {accepted.map((doc) => (
                <li key={doc.documentId} className="flex flex-wrap items-center justify-between gap-2 py-2 text-sm">
                  <span className="break-all">{doc.path}</span>
                  <span className="flex items-center gap-2">
                    <CopyId id={doc.documentId} />
                    <StatusBadge status={doc.latestJob?.status ?? doc.indexStatus} />
                  </span>
                </li>
              ))}
            </ul>
          </CardContent>
        </Card>
      )}
    </>
  );
}
