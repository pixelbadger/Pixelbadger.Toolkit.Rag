import { useState } from "react";
import { Check, Copy } from "lucide-react";

import { Button } from "@/components/ui/button";
import { abbreviateGuid } from "@/lib/format";

/** Abbreviated GUID with the full value in the title, and a copy-to-clipboard button. */
export function CopyId({ id }: { id: string }) {
  const [copied, setCopied] = useState(false);

  async function copy() {
    try {
      await navigator.clipboard.writeText(id);
      setCopied(true);
      setTimeout(() => setCopied(false), 1500);
    } catch {
      // Clipboard may be unavailable (insecure context); the full id is still in the title.
    }
  }

  return (
    <span className="inline-flex items-center gap-1">
      <code title={id} className="font-mono text-xs">
        {abbreviateGuid(id)}
      </code>
      <Button variant="ghost" size="icon-sm" aria-label={`Copy document id ${id}`} onClick={copy}>
        {copied ? <Check /> : <Copy />}
      </Button>
    </span>
  );
}
