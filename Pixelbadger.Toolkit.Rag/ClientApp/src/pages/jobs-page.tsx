import { useState } from "react";
import { keepPreviousData, useQuery } from "@tanstack/react-query";
import { AlertCircle, ExternalLink, RefreshCw } from "lucide-react";

import { documentContentUrl } from "@/api/documents";
import { jobsQueryKey, listJobs } from "@/api/jobs";
import { INGEST_JOB_STATUSES, type IngestJobStatus, type JobListItem } from "@/api/types";
import { CopyId } from "@/components/copy-id";
import { ErrorAlert } from "@/components/error-alert";
import { PageHeader } from "@/components/page-header";
import { StatusBadge } from "@/components/status-badge";
import { Button } from "@/components/ui/button";
import { Card } from "@/components/ui/card";
import { Dialog, DialogContent, DialogDescription, DialogHeader, DialogTitle, DialogTrigger } from "@/components/ui/dialog";
import { Select } from "@/components/ui/form";
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "@/components/ui/table";
import { formatBytes, formatLocal, jobDuration } from "@/lib/format";
import { pollInterval } from "@/lib/polling";

const PAGE_SIZES = [10, 25, 50];
function ErrorDetail({ job }: { job: JobListItem }) {
  return (
    <Dialog>
      <DialogTrigger asChild>
        <Button variant="ghost" size="sm" aria-label={`Show error for ${job.path}`} className="text-destructive">
          <AlertCircle /> Error
        </Button>
      </DialogTrigger>
      <DialogContent>
        <DialogHeader>
          <DialogTitle>Ingest error</DialogTitle>
          <DialogDescription className="break-all">
            {job.path} · attempt {job.attempts}
          </DialogDescription>
        </DialogHeader>
        <pre className="max-h-96 overflow-auto whitespace-pre-wrap break-words rounded-md bg-muted p-3 text-xs">{job.error}</pre>
      </DialogContent>
    </Dialog>
  );
}

function Timestamp({ iso }: { iso: string | null }) {
  if (!iso) return <span>—</span>;
  return (
    <time dateTime={iso} title={`${iso} (UTC)`}>
      {formatLocal(iso)}
    </time>
  );
}

function JobRow({ job }: { job: JobListItem }) {
  return (
    <TableRow>
      <TableCell>
        <StatusBadge status={job.status} />
      </TableCell>
      <TableCell className="max-w-64 break-all">{job.path}</TableCell>
      <TableCell>
        <CopyId id={job.documentId} />
      </TableCell>
      <TableCell className="whitespace-nowrap">{formatBytes(job.sizeBytes)}</TableCell>
      <TableCell>{job.attempts}</TableCell>
      <TableCell>{job.chunkCount ?? "—"}</TableCell>
      <TableCell className="whitespace-nowrap">
        <Timestamp iso={job.createdAtUtc} />
      </TableCell>
      <TableCell className="whitespace-nowrap">{jobDuration(job.startedAtUtc, job.completedAtUtc)}</TableCell>
      <TableCell className="whitespace-nowrap">
        <div className="flex items-center gap-1">
          {job.status === "Failed" && job.error && <ErrorDetail job={job} />}
          {job.status === "Succeeded" && (
            <Button asChild variant="ghost" size="sm">
              <a
                href={documentContentUrl(job.documentId)}
                target="_blank"
                rel="noreferrer"
                title="Opens the document's current indexed source, which may be newer than this job's upload"
                aria-label={`Open current source of ${job.path}`}
              >
                <ExternalLink /> Current source
              </a>
            </Button>
          )}
        </div>
      </TableCell>
    </TableRow>
  );
}

export function JobsPage() {
  const [status, setStatus] = useState<IngestJobStatus | "">("");
  const [pageSize, setPageSize] = useState(25);
  const [page, setPage] = useState(1);

  const { data, error, isPending, isFetching, refetch } = useQuery({
    queryKey: [...jobsQueryKey, { page, pageSize, status }],
    queryFn: ({ signal }) => listJobs({ page, pageSize, status: status || undefined }, signal),
    placeholderData: keepPreviousData,
    refetchInterval: (query) => pollInterval(query.state.data),
  });

  const first = data && data.jobs.length > 0 ? (data.page - 1) * data.pageSize + 1 : 0;
  const last = data ? first + data.jobs.length - (data.jobs.length > 0 ? 1 : 0) : 0;

  return (
    <>
      <PageHeader title="Jobs" subtitle="Ingest queue and processing history" />

      <div className="flex flex-wrap items-end gap-3">
        <div className="flex flex-col gap-1.5">
          <label htmlFor="job-status" className="text-xs font-medium text-muted-foreground">
            Status
          </label>
          <Select
            id="job-status"
            value={status}
            onChange={(e) => {
              setStatus(e.target.value as IngestJobStatus | "");
              setPage(1);
            }}
            className="w-40"
          >
            <option value="">All</option>
            {INGEST_JOB_STATUSES.map((s) => (
              <option key={s} value={s}>
                {s}
              </option>
            ))}
          </Select>
        </div>
        <div className="flex flex-col gap-1.5">
          <label htmlFor="job-page-size" className="text-xs font-medium text-muted-foreground">
            Page size
          </label>
          <Select
            id="job-page-size"
            value={pageSize}
            onChange={(e) => {
              setPageSize(Number(e.target.value));
              setPage(1);
            }}
            className="w-24"
          >
            {PAGE_SIZES.map((s) => (
              <option key={s} value={s}>
                {s}
              </option>
            ))}
          </Select>
        </div>
        <Button variant="outline" onClick={() => void refetch()}>
          <RefreshCw className={isFetching ? "animate-spin motion-reduce:animate-none" : undefined} /> Refresh
        </Button>
      </div>

      {error && !data ? (
        <ErrorAlert error={error} title="Could not load jobs" />
      ) : isPending ? (
        <p role="status" className="text-sm text-muted-foreground">
          Loading jobs…
        </p>
      ) : data && data.jobs.length === 0 ? (
        <Card className="items-center p-10 text-center text-sm text-muted-foreground">
          {status ? `No ${status} jobs.` : "No jobs yet. Ingest a document to see it here."}
        </Card>
      ) : data ? (
        <>
          {error && <ErrorAlert error={error} title="Could not refresh jobs" />}
          <Card className="py-0">
            <Table>
              <TableHeader>
                <TableRow>
                  <TableHead>Status</TableHead>
                  <TableHead>Path</TableHead>
                  <TableHead>Document</TableHead>
                  <TableHead>Size</TableHead>
                  <TableHead>Attempts</TableHead>
                  <TableHead>Chunks</TableHead>
                  <TableHead>Created</TableHead>
                  <TableHead>Duration</TableHead>
                  <TableHead>
                    <span className="sr-only">Actions</span>
                  </TableHead>
                </TableRow>
              </TableHeader>
              <TableBody>
                {data.jobs.map((job) => (
                  <JobRow key={job.jobId} job={job} />
                ))}
              </TableBody>
            </Table>
          </Card>
          <div className="flex items-center justify-between gap-3">
            <p className="text-sm text-muted-foreground" aria-live="polite">
              {first}–{last} of {data.totalCount}
            </p>
            <div className="flex gap-2">
              <Button variant="outline" size="sm" disabled={data.page <= 1} onClick={() => setPage(data.page - 1)}>
                Previous
              </Button>
              <Button variant="outline" size="sm" disabled={data.page >= data.totalPages} onClick={() => setPage(data.page + 1)}>
                Next
              </Button>
            </div>
          </div>
        </>
      ) : null}
    </>
  );
}
