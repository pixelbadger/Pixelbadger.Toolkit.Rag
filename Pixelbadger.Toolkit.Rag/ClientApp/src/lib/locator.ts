import type { SearchResult } from "@/api/types";
import { formatSeconds } from "@/lib/format";

/** Human-readable locator: characters for text, seconds for audio (the API sends milliseconds), none for images. */
export function formatLocator(result: SearchResult): string | null {
  const { locatorStart: start, locatorEnd: end } = result;
  if (start === null || end === null) return null;
  if (result.modality === "Audio") return `${formatSeconds(start)}–${formatSeconds(end)}`;
  if (result.modality === "Text") return `characters ${start}–${end}`;
  return null;
}
