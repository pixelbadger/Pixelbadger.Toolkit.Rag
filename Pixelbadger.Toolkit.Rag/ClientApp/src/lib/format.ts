const UNITS = ["B", "KB", "MB", "GB", "TB"];

export function formatBytes(bytes: number): string {
  if (!Number.isFinite(bytes) || bytes < 0) return "—";
  let value = bytes;
  let unit = 0;
  while (value >= 1024 && unit < UNITS.length - 1) {
    value /= 1024;
    unit++;
  }
  const text = unit === 0 || value >= 10 ? Math.round(value).toString() : value.toFixed(1);
  return `${text} ${UNITS[unit]}`;
}

/** First 8 hex digits plus an ellipsis; the full id belongs in a tooltip/title. */
export function abbreviateGuid(id: string): string {
  return id.length > 8 ? `${id.slice(0, 8)}…` : id;
}

/** Local-time display of a UTC ISO timestamp. */
export function formatLocal(iso: string | null | undefined): string {
  if (!iso) return "—";
  const date = new Date(iso);
  if (Number.isNaN(date.getTime())) return iso;
  return date.toLocaleString();
}

export function formatDurationMs(ms: number): string {
  if (!Number.isFinite(ms) || ms < 0) return "—";
  if (ms < 1000) return `${Math.round(ms)} ms`;
  const totalSeconds = ms / 1000;
  if (totalSeconds < 60) return `${totalSeconds < 10 ? totalSeconds.toFixed(1) : Math.round(totalSeconds)} s`;
  const totalMinutes = Math.floor(totalSeconds / 60);
  const seconds = Math.round(totalSeconds - totalMinutes * 60);
  if (totalMinutes < 60) return `${totalMinutes}m ${seconds}s`;
  const hours = Math.floor(totalMinutes / 60);
  return `${hours}h ${totalMinutes % 60}m`;
}

/** completed - started, or now - started for a running job; "—" when it has not started. */
export function jobDuration(startedAtUtc: string | null, completedAtUtc: string | null, now = Date.now()): string {
  if (!startedAtUtc) return "—";
  const start = new Date(startedAtUtc).getTime();
  const end = completedAtUtc ? new Date(completedAtUtc).getTime() : now;
  return formatDurationMs(end - start);
}

export function formatSeconds(ms: number): string {
  return `${(ms / 1000).toFixed(1)}s`;
}

const GUID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;

export function isGuid(value: string): boolean {
  return GUID.test(value);
}

/** Splits on newlines/commas (and whitespace), trims, drops blanks, de-duplicates case-insensitively keeping order. */
export function normaliseDocumentIds(raw: string): string[] {
  const seen = new Set<string>();
  const ids: string[] = [];
  for (const part of raw.split(/[\n\r,]+/)) {
    const id = part.trim();
    const key = id.toLowerCase();
    if (id && !seen.has(key)) {
      seen.add(key);
      ids.push(id);
    }
  }
  return ids;
}
