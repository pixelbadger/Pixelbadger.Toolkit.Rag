import { createBrowserRouter, type RouteObject } from "react-router-dom";

import { AppLayout } from "@/app/layout";
import type { RouteHandle } from "@/components/app-breadcrumbs";
import { IngestPage } from "@/pages/ingest-page";
import { JobsPage } from "@/pages/jobs-page";
import { QueryPage } from "@/pages/query-page";

const handle = (crumb: string): RouteHandle => ({ crumb });

/** Shared by the browser router and the tests' memory router. Nested routes add crumbs via `handle`. */
export const routes: RouteObject[] = [
  {
    path: "/",
    element: <AppLayout />,
    children: [
      { index: true, element: <JobsPage />, handle: handle("Jobs") },
      { path: "query", element: <QueryPage />, handle: handle("Query") },
      { path: "ingest", element: <IngestPage />, handle: handle("Ingest") },
    ],
  },
];

export const createAppRouter = () => createBrowserRouter(routes);
