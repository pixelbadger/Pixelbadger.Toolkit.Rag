import { screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { http, HttpResponse } from "msw";

import { documentContentUrl } from "@/api/documents";
import type { JobListItem } from "@/api/types";
import { ACTIVE_POLL_MS, IDLE_POLL_MS, pollInterval } from "@/lib/polling";
import { server } from "@/test/server";
import { DOC_ID, job, jobsResponse, renderApp } from "@/test/utils";

function captureJobs(respond: (url: URL) => Response | ReturnType<typeof HttpResponse.json>) {
  const urls: URL[] = [];
  server.use(
    http.get("/api/jobs", ({ request }) => {
      const url = new URL(request.url);
      urls.push(url);
      return respond(url);
    }),
  );
  return urls;
}

describe("jobs page", () => {
  it("renders returned jobs with status badges, size, document id and local time with UTC title", async () => {
    captureJobs(() =>
      HttpResponse.json(
        jobsResponse([
          job({ jobId: "j1", path: "a/queued.md", status: "Queued", startedAtUtc: null, completedAtUtc: null, chunkCount: null }),
          job({ jobId: "j2", path: "a/ok.md", status: "Succeeded", sizeBytes: 2048 }),
          job({ jobId: "j3", path: "a/skipped.md", status: "Skipped" }),
          job({ jobId: "j4", path: "a/bad.md", status: "Failed", error: "boom" }),
        ]),
      ),
    );
    renderApp("/");

    const row = (await screen.findByText("a/ok.md")).closest("tr")!;
    expect(within(row).getByText("Succeeded")).toBeInTheDocument();
    expect(within(row).getByText("2.0 KB")).toBeInTheDocument();
    expect(within(row).getByText("2.0 s")).toBeInTheDocument(); // completed - started
    expect(within(row).getByTitle(DOC_ID)).toHaveTextContent("019a1b2c…");
    expect(within(row).getByRole("button", { name: /copy document id/i })).toBeInTheDocument();
    const time = row.querySelector("time")!;
    expect(time).toHaveAttribute("title", "2026-10-07T00:00:00Z (UTC)");

    expect(within(screen.getByText("a/queued.md").closest("tr")!).getByText("Queued")).toBeInTheDocument();
    expect(within(screen.getByText("a/skipped.md").closest("tr")!).getByText("Skipped")).toBeInTheDocument();
    expect(within(screen.getByText("a/bad.md").closest("tr")!).getByText("Failed")).toBeInTheDocument();
  });

  it("offers the current source for succeeded jobs only, labelled as current", async () => {
    captureJobs(() =>
      HttpResponse.json(jobsResponse([job({ path: "a/ok.md" }), job({ jobId: "j2", path: "a/bad.md", status: "Failed", error: "x" })])),
    );
    renderApp("/");
    const link = await screen.findByRole("link", { name: /open current source of a\/ok\.md/i });
    expect(link).toHaveAttribute("href", documentContentUrl(DOC_ID));
    expect(screen.queryByRole("link", { name: /a\/bad\.md/ })).not.toBeInTheDocument();
  });

  it("requests page 1 / size 25 by default and refetches with the chosen status, resetting the page", async () => {
    const user = userEvent.setup();
    const many: JobListItem[] = Array.from({ length: 25 }, (_, i) => job({ jobId: `j${i}`, path: `f${i}.md` }));
    const urls = captureJobs((url) =>
      HttpResponse.json(
        jobsResponse(many, { page: Number(url.searchParams.get("page")), totalCount: 60, totalPages: 3 }),
      ),
    );
    renderApp("/");
    await screen.findByText("f0.md");
    expect(urls[0].searchParams.get("page")).toBe("1");
    expect(urls[0].searchParams.get("pageSize")).toBe("25");
    expect(urls[0].searchParams.has("status")).toBe(false);

    await user.click(screen.getByRole("button", { name: "Next" }));
    await waitFor(() => expect(urls.at(-1)!.searchParams.get("page")).toBe("2"));

    await user.selectOptions(screen.getByLabelText("Status"), "Failed");
    await waitFor(() => expect(urls.at(-1)!.searchParams.get("status")).toBe("Failed"));
    expect(urls.at(-1)!.searchParams.get("page")).toBe("1");

    await user.click(screen.getByRole("button", { name: "Next" }));
    await waitFor(() => expect(urls.at(-1)!.searchParams.get("page")).toBe("2"));
    await user.selectOptions(screen.getByLabelText("Page size"), "50");
    await waitFor(() => expect(urls.at(-1)!.searchParams.get("pageSize")).toBe("50"));
    expect(urls.at(-1)!.searchParams.get("page")).toBe("1");
  });

  it("shows the range, total and disables Previous/Next at the edges", async () => {
    const user = userEvent.setup();
    const many: JobListItem[] = Array.from({ length: 25 }, (_, i) => job({ jobId: `j${i}`, path: `f${i}.md` }));
    captureJobs((url) =>
      HttpResponse.json(jobsResponse(many, { page: Number(url.searchParams.get("page")), totalCount: 137, totalPages: 6 })),
    );
    renderApp("/");
    expect(await screen.findByText("1–25 of 137")).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Previous" })).toBeDisabled();
    expect(screen.getByRole("button", { name: "Next" })).toBeEnabled();

    await user.click(screen.getByRole("button", { name: "Next" }));
    expect(await screen.findByText("26–50 of 137")).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Previous" })).toBeEnabled();
  });

  it("shows an empty state", async () => {
    captureJobs(() => HttpResponse.json(jobsResponse([])));
    renderApp("/");
    expect(await screen.findByText(/no jobs yet/i)).toBeInTheDocument();
  });

  it("surfaces API failures from Problem Details", async () => {
    captureJobs(() =>
      HttpResponse.json({ title: "Internal Server Error", status: 500, detail: "database is down" }, {
        status: 500,
        headers: { "Content-Type": "application/problem+json" },
      }),
    );
    renderApp("/");
    const alert = await screen.findByRole("alert");
    expect(alert).toHaveTextContent("Could not load jobs");
    expect(alert).toHaveTextContent("database is down");
  });

  it("makes the full error of a failed job available in a dialog", async () => {
    const user = userEvent.setup();
    const longError = "Unsupported codec\n" + "x".repeat(500);
    captureJobs(() => HttpResponse.json(jobsResponse([job({ status: "Failed", error: longError, path: "a/bad.mp3" })])));
    renderApp("/");
    await user.click(await screen.findByRole("button", { name: /show error for a\/bad\.mp3/i }));
    const dialog = await screen.findByRole("dialog");
    expect(dialog).toHaveTextContent("Unsupported codec");
    expect(within(dialog).getByText(/x{500}/)).toBeInTheDocument();
  });

  it("polls every 2s while a page has Queued/Processing jobs and every 10s otherwise", () => {
    expect(ACTIVE_POLL_MS).toBe(2000);
    expect(IDLE_POLL_MS).toBe(10000);
    expect(pollInterval(jobsResponse([job({ status: "Queued" })]))).toBe(2000);
    expect(pollInterval(jobsResponse([job({ status: "Processing" })]))).toBe(2000);
    expect(pollInterval(jobsResponse([job({ status: "Succeeded" }), job({ status: "Failed" })]))).toBe(10000);
    expect(pollInterval(undefined)).toBe(10000);
  });
});
