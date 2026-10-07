import * as React from "react";

import { cn } from "@/lib/utils";

const fieldClasses =
  "w-full rounded-md border border-input bg-transparent px-3 text-sm shadow-xs outline-none placeholder:text-muted-foreground focus-visible:ring-2 focus-visible:ring-ring disabled:opacity-50";

function Input({ className, ...props }: React.ComponentProps<"input">) {
  return <input className={cn(fieldClasses, "h-9", className)} {...props} />;
}

function Textarea({ className, ...props }: React.ComponentProps<"textarea">) {
  return <textarea className={cn(fieldClasses, "min-h-20 py-2", className)} {...props} />;
}

/** A native select styled like the shadcn Select: fully accessible and keyboard friendly without a popover. */
function Select({ className, ...props }: React.ComponentProps<"select">) {
  return <select className={cn(fieldClasses, "h-9 pr-8", className)} {...props} />;
}

function Label({ className, ...props }: React.ComponentProps<"label">) {
  return <label className={cn("text-sm font-medium leading-none", className)} {...props} />;
}

export { Input, Textarea, Select, Label };
