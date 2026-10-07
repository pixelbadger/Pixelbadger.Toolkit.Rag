import { CheckCircle2, CircleDashed, Loader2, MinusCircle, XCircle } from "lucide-react";

import { Badge } from "@/components/ui/badge";
import type { IndexStatus, IngestJobStatus } from "@/api/types";

type Status = IngestJobStatus | IndexStatus;

/** Colour is never the only signal: every status keeps its text and an icon. */
export function StatusBadge({ status }: { status: Status }) {
  switch (status) {
    case "Queued":
      return (
        <Badge variant="secondary">
          <CircleDashed aria-hidden="true" />
          {status}
        </Badge>
      );
    case "Processing":
      return (
        <Badge variant="default">
          <Loader2 aria-hidden="true" className="animate-spin motion-reduce:animate-none" />
          {status}
        </Badge>
      );
    case "Succeeded":
    case "Indexed":
      return (
        <Badge variant="success">
          <CheckCircle2 aria-hidden="true" />
          {status}
        </Badge>
      );
    case "Skipped":
      return (
        <Badge variant="outline">
          <MinusCircle aria-hidden="true" />
          {status}
        </Badge>
      );
    case "Failed":
      return (
        <Badge variant="destructive">
          <XCircle aria-hidden="true" />
          {status}
        </Badge>
      );
  }
}
