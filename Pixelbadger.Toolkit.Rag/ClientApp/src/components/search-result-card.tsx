import { useState } from "react";
import { Download, ExternalLink, ImageOff } from "lucide-react";

import { documentContentUrl, documentDownloadUrl } from "@/api/documents";
import type { SearchResult } from "@/api/types";
import { CopyId } from "@/components/copy-id";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Card, CardContent } from "@/components/ui/card";
import { abbreviateGuid } from "@/lib/format";
import { formatLocator } from "@/lib/locator";

function SourceActions({ documentId, path }: { documentId: string; path: string }) {
  return (
    <div className="flex flex-wrap items-center gap-1">
      <Button asChild variant="outline" size="sm">
        <a href={documentContentUrl(documentId)} target="_blank" rel="noreferrer" aria-label={`Open source of ${path}`}>
          <ExternalLink /> Open source
        </a>
      </Button>
      <Button asChild variant="ghost" size="sm">
        <a href={documentDownloadUrl(documentId)} aria-label={`Download source of ${path}`}>
          <Download /> Download source
        </a>
      </Button>
    </div>
  );
}

function ImagePreview({ result, onUnavailable }: { result: SearchResult; onUnavailable: () => void }) {
  return (
    <a href={documentContentUrl(result.documentId)} target="_blank" rel="noreferrer" aria-label={`Open full image ${result.sourcePath}`}>
      <img
        src={documentContentUrl(result.documentId)}
        alt={`Preview of ${result.sourcePath}`}
        loading="lazy"
        onError={onUnavailable}
        className="max-h-64 max-w-full rounded-md border object-contain"
      />
    </a>
  );
}

export function SearchResultCard({ result }: { result: SearchResult }) {
  const [imageFailed, setImageFailed] = useState(false);
  const locator = formatLocator(result);
  const isImage = result.modality === "Image";

  return (
    <Card>
      <CardContent className="space-y-3">
        <div className="flex flex-wrap items-center justify-between gap-2">
          <h3 className="break-all font-medium">{result.sourcePath}</h3>
          <Badge variant="secondary">{result.modality}</Badge>
        </div>

        {isImage &&
          (imageFailed ? (
            <p className="flex items-center gap-2 text-sm text-muted-foreground">
              <ImageOff className="size-4" aria-hidden="true" /> Source unavailable
            </p>
          ) : (
            <ImagePreview result={result} onUnavailable={() => setImageFailed(true)} />
          ))}

        {result.content && (
          <p className="max-h-80 overflow-auto whitespace-pre-wrap break-words rounded-md bg-muted/50 p-3 text-sm">{result.content}</p>
        )}

        {locator && <p className="text-sm text-muted-foreground">{locator}</p>}

        {!(isImage && imageFailed) && <SourceActions documentId={result.documentId} path={result.sourcePath} />}

        <dl className="flex flex-wrap items-center gap-x-4 gap-y-1 border-t pt-3 text-xs text-muted-foreground">
          <div className="flex gap-1">
            <dt title="Cosine similarity (1 - cosine distance); higher is more similar">Similarity</dt>
            <dd className="font-mono">{result.score.toFixed(4)}</dd>
          </div>
          <div className="flex gap-1">
            <dt>Chunk</dt>
            <dd>
              {result.ordinal} (<code title={result.chunkId}>{abbreviateGuid(result.chunkId)}</code>)
            </dd>
          </div>
          <div className="flex items-center gap-1">
            <dt>Document</dt>
            <dd>
              <CopyId id={result.documentId} />
            </dd>
          </div>
        </dl>
      </CardContent>
    </Card>
  );
}
