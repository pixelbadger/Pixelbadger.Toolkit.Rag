import { Link, useMatches } from "react-router-dom";
import { ChevronRight } from "lucide-react";

export interface RouteHandle {
  /** Breadcrumb label for this route; nested routes each contribute a crumb. */
  crumb?: string;
}

/** Breadcrumbs derived from the matched routes' `handle.crumb`, so nested routes need no shell changes. */
export function AppBreadcrumbs() {
  const crumbs = useMatches()
    .map((m) => ({ pathname: m.pathname, label: (m.handle as RouteHandle | undefined)?.crumb }))
    .filter((c): c is { pathname: string; label: string } => Boolean(c.label));

  return (
    <nav aria-label="Breadcrumb">
      <ol className="flex items-center gap-1.5 text-sm text-muted-foreground">
        {crumbs.map((crumb, i) => {
          const last = i === crumbs.length - 1;
          return (
            <li key={crumb.pathname} className="flex items-center gap-1.5">
              {last ? (
                <span aria-current="page" className="font-medium text-foreground">
                  {crumb.label}
                </span>
              ) : (
                <>
                  <Link to={crumb.pathname} className="hover:text-foreground">
                    {crumb.label}
                  </Link>
                  <ChevronRight className="size-3.5" aria-hidden="true" />
                </>
              )}
            </li>
          );
        })}
      </ol>
    </nav>
  );
}
