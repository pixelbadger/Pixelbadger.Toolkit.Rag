import { screen, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { http, HttpResponse } from "msw";

import { server } from "@/test/server";
import { jobsResponse, renderApp } from "@/test/utils";

describe("app shell", () => {
  beforeEach(() => {
    server.use(http.get("/api/jobs", () => HttpResponse.json(jobsResponse([]))));
  });

  it("lists Jobs, Query and Ingest in the sidebar", async () => {
    renderApp("/");
    const nav = screen.getByRole("navigation", { name: "Primary" });
    expect(within(nav).getByRole("link", { name: "Jobs" })).toBeInTheDocument();
    expect(within(nav).getByRole("link", { name: "Query" })).toBeInTheDocument();
    expect(within(nav).getByRole("link", { name: "Ingest" })).toBeInTheDocument();
    await screen.findByText(/no jobs yet/i);
  });

  it("navigates between pages and updates the breadcrumb", async () => {
    const user = userEvent.setup();
    renderApp("/");
    const crumbs = screen.getByRole("navigation", { name: "Breadcrumb" });
    expect(within(crumbs).getByText("Jobs")).toHaveAttribute("aria-current", "page");
    await screen.findByText(/no jobs yet/i);

    const nav = screen.getByRole("navigation", { name: "Primary" });
    await user.click(within(nav).getByRole("link", { name: "Query" }));
    expect(screen.getByRole("heading", { name: "Query", level: 1 })).toBeInTheDocument();
    expect(within(crumbs).getByText("Query")).toHaveAttribute("aria-current", "page");

    await user.click(within(nav).getByRole("link", { name: "Ingest" }));
    expect(screen.getByRole("heading", { name: "Ingest", level: 1 })).toBeInTheDocument();
    expect(within(crumbs).getByText("Ingest")).toHaveAttribute("aria-current", "page");
  });

  it("renders the deep-linked route directly", () => {
    renderApp("/ingest");
    expect(screen.getByRole("heading", { name: "Ingest", level: 1 })).toBeInTheDocument();
  });
});
