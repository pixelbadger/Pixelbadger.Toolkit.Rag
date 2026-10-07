import { apiFetch } from "./client";
import type { DocumentsResponse } from "./types";

/** Extensions the server ingests (mirrors Domain/MediaTypes.cs). Client-side checks are advisory only. */
export const TEXT_EXTENSIONS = [".txt", ".md"] as const;
export const IMAGE_EXTENSIONS = [".png", ".jpg", ".jpeg", ".webp", ".gif", ".bmp", ".tif", ".tiff"] as const;
export const AUDIO_EXTENSIONS = [".wav", ".mp3", ".m4a", ".flac", ".ogg", ".opus", ".aac"] as const;
export const SUPPORTED_EXTENSIONS: readonly string[] = [...TEXT_EXTENSIONS, ...IMAGE_EXTENSIONS, ...AUDIO_EXTENSIONS];
export const ACCEPT_ATTRIBUTE = SUPPORTED_EXTENSIONS.join(",");

/** The current successfully indexed source of a document (not any historical job payload). */
export function documentContentUrl(documentId: string): string {
  return `/api/documents/${encodeURIComponent(documentId)}/content`;
}

export function documentDownloadUrl(documentId: string): string {
  return `${documentContentUrl(documentId)}?download=true`;
}

/** The multipart filename is the logical path: a directory-relative path when the browser provides one. */
export function logicalPathOf(file: File): string {
  return file.webkitRelativePath || file.name;
}

export function buildUploadForm(files: File[], maxChunkCharacters?: number): FormData {
  const formData = new FormData();
  for (const file of files) formData.append("files", file, logicalPathOf(file));
  if (maxChunkCharacters !== undefined) formData.append("maxChunkCharacters", String(maxChunkCharacters));
  return formData;
}

export function uploadDocuments(
  files: File[],
  maxChunkCharacters?: number,
  signal?: AbortSignal,
): Promise<DocumentsResponse> {
  return apiFetch<DocumentsResponse>("/api/documents", {
    method: "POST",
    body: buildUploadForm(files, maxChunkCharacters),
    signal,
  });
}
