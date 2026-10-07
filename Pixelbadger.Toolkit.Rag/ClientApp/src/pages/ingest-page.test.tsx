import { screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { http, HttpResponse } from "msw";

import { ACCEPT_ATTRIBUTE } from "@/api/documents";
import type { DocumentDto } from "@/api/types";
import { server } from "@/test/server";
import { DOC_ID, jobsResponse, renderApp } from "@/test/utils";

function readText(file: File): Promise<string> {
  return new Promise((resolve, reject) => {
    const reader = new FileReader();
    reader.onload = () => resolve(String(reader.result));
    reader.onerror = () => reject(reader.error);
    reader.readAsText(file);
  });
}

interface Captured {
  files: { name: string; text: string }[];
  fields: Record<string, string>;
  headers: Record<string, string>;
}

/**
 * Answers POST /api/documents through MSW. The multipart body is inspected on the FormData handed to fetch
 * (jsdom's FormData does not survive Node's fetch serialisation faithfully, so the filename would be lost on the wire).
 */
function mockUpload(documents: DocumentDto[]) {
  const captured: Captured[] = [];
  const realFetch = globalThis.fetch;
  vi.spyOn(globalThis, "fetch").mockImplementation(async (input, init) => {
    if (init?.body instanceof FormData) {
      const form = init.body;
      const files = await Promise.all(
        form.getAll("files").map(async (f) => ({ name: (f as File).name, text: await readText(f as File) })),
      );
      const fields: Record<string, string> = {};
      form.forEach((v, k) => {
        if (typeof v === "string") fields[k] = v;
      });
      captured.push({ files, fields, headers: Object.fromEntries(new Headers(init.headers).entries()) });
    }
    return realFetch(input, init);
  });
  server.use(http.post("/api/documents", () => HttpResponse.json({ documents }, { status: 202 })));
  return captured;
}

const doc = (path: string, id = DOC_ID): DocumentDto => ({
  documentId: id,
  path,
  title: null,
  modality: "Text",
  indexStatus: "Queued",
  chunkCount: 0,
  updatedAtUtc: "2026-10-07T00:00:00Z",
  latestJob: null,
});

const file = (name: string, body = "hello") => new File([body], name, { type: "text/markdown" });

describe("ingest page", () => {
  it("offers a file picker restricted to the supported extensions", () => {
    renderApp("/ingest");
    const input = screen.getByLabelText("Files to ingest");
    expect(input).toHaveAttribute("multiple");
    expect(input).toHaveAttribute("accept", ACCEPT_ATTRIBUTE);
    for (const ext of [".txt", ".md", ".png", ".tiff", ".wav", ".opus", ".aac"]) expect(ACCEPT_ATTRIBUTE).toContain(ext);
    expect(screen.getByRole("button", { name: "Upload" })).toBeDisabled();
  });

  it("selects multiple files, lists name and size, removes one and clears all", async () => {
    const user = userEvent.setup();
    renderApp("/ingest");
    await user.upload(screen.getByLabelText("Files to ingest"), [file("a.md", "x".repeat(2048)), file("b.md"), file("c.md")]);

    expect(screen.getByText("3 files selected")).toBeInTheDocument();
    expect(screen.getByText("a.md")).toBeInTheDocument();
    expect(screen.getByText(/2\.0 KB/)).toBeInTheDocument();

    await user.click(screen.getByRole("button", { name: "Remove b.md" }));
    expect(screen.queryByText("b.md")).not.toBeInTheDocument();
    expect(screen.getByText("2 files selected")).toBeInTheDocument();

    await user.click(screen.getByRole("button", { name: /clear all/i }));
    expect(screen.queryByText("a.md")).not.toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Upload" })).toBeDisabled();
  });

  it("accepts dropped files", async () => {
    const { fireEvent } = await import("@testing-library/react");
    renderApp("/ingest");
    fireEvent.drop(screen.getByTestId("drop-zone"), { dataTransfer: { files: [file("dropped.md")] } });
    expect(screen.getByText("dropped.md")).toBeInTheDocument();
  });

  it("posts every file as a 'files' part without setting Content-Type manually, omitting maxChunkCharacters by default", async () => {
    const captured = mockUpload([doc("a.md"), doc("b.md", "019a1b2c-3d4e-7f50-8a6b-7c8d9e0f1a2c")]);
    const user = userEvent.setup();
    renderApp("/ingest");
    await user.upload(screen.getByLabelText("Files to ingest"), [file("a.md", "AAA"), file("b.md", "BBB")]);
    await user.click(screen.getByRole("button", { name: "Upload" }));

    await waitFor(() => expect(captured).toHaveLength(1));
    expect(captured[0].files).toEqual([
      { name: "a.md", text: "AAA" },
      { name: "b.md", text: "BBB" },
    ]);
    expect(captured[0].fields).not.toHaveProperty("maxChunkCharacters");
    expect(captured[0].headers).not.toHaveProperty("content-type");
  });

  it("uses webkitRelativePath as the multipart filename when present", async () => {
    const captured = mockUpload([doc("docs/guide.md")]);
    const user = userEvent.setup();
    renderApp("/ingest");
    const f = file("guide.md");
    Object.defineProperty(f, "webkitRelativePath", { value: "docs/guide.md" });
    await user.upload(screen.getByLabelText("Files to ingest"), f);
    await user.click(screen.getByRole("button", { name: "Upload" }));
    await waitFor(() => expect(captured[0]?.files[0].name).toBe("docs/guide.md"));
  });

  it("includes maxChunkCharacters when set (blank by default)", async () => {
    const captured = mockUpload([doc("a.md")]);
    const user = userEvent.setup();
    renderApp("/ingest");
    await user.click(screen.getByText("Advanced"));
    expect(screen.getByLabelText("Max chunk characters")).toHaveValue(null);
    await user.type(screen.getByLabelText("Max chunk characters"), "500");
    await user.upload(screen.getByLabelText("Files to ingest"), file("a.md"));
    await user.click(screen.getByRole("button", { name: "Upload" }));
    await waitFor(() => expect(captured[0]?.fields.maxChunkCharacters).toBe("500"));
  });

  it("shows accepted documents with path, id and status, links to jobs and refreshes the jobs query", async () => {
    mockUpload([doc("docs/a.md")]);
    let jobsCalls = 0;
    server.use(
      http.get("/api/jobs", () => {
        jobsCalls++;
        return HttpResponse.json(jobsResponse([]));
      }),
    );
    const user = userEvent.setup();
    const { queryClient, router } = renderApp("/ingest");
    // A cached jobs query that should be invalidated by a successful upload.
    await queryClient.prefetchQuery({ queryKey: ["jobs", { page: 1 }], queryFn: async () => jobsResponse([]) });
    const observed = queryClient.getQueryState(["jobs", { page: 1 }])!.dataUpdatedAt;

    await user.upload(screen.getByLabelText("Files to ingest"), file("a.md"));
    await user.click(screen.getByRole("button", { name: "Upload" }));

    const status = await screen.findByRole("status", { name: "Upload accepted" });
    expect(status).toHaveTextContent("1 document accepted");
    expect(within(status).getByText("docs/a.md")).toBeInTheDocument();
    expect(within(status).getByTitle(DOC_ID)).toBeInTheDocument();
    expect(within(status).getByText("Queued")).toBeInTheDocument();
    expect(queryClient.getQueryState(["jobs", { page: 1 }])!.isInvalidated || queryClient.getQueryState(["jobs", { page: 1 }])!.dataUpdatedAt > observed).toBe(true);
    // No source/download until indexing has succeeded.
    expect(within(status).queryByRole("link", { name: /source|download/i })).not.toBeInTheDocument();
    // The selection is cleared but the success state stays.
    expect(screen.queryByText(/files? selected/)).not.toBeInTheDocument();

    await user.click(within(status).getByRole("link", { name: "View jobs" }));
    expect(router.state.location.pathname).toBe("/");
    expect(jobsCalls).toBeGreaterThan(0);
  });

  it("renders Problem Details validation errors per file field", async () => {
    server.use(
      http.post("/api/documents", () =>
        HttpResponse.json(
          {
            title: "One or more validation errors occurred.",
            status: 400,
            errors: { files: ["'notes.exe' has an unsupported extension."], maxChunkCharacters: ["Must be positive."] },
          },
          { status: 400, headers: { "Content-Type": "application/problem+json" } },
        ),
      ),
    );
    const user = userEvent.setup();
    renderApp("/ingest");
    await user.upload(screen.getByLabelText("Files to ingest"), file("notes.md"));
    await user.click(screen.getByRole("button", { name: "Upload" }));

    const alert = await screen.findByRole("alert");
    expect(alert).toHaveTextContent("Upload rejected");
    expect(alert).toHaveTextContent("files: 'notes.exe' has an unsupported extension.");
    expect(alert).toHaveTextContent("maxChunkCharacters: Must be positive.");
    // The selection is kept so the user can fix and retry.
    expect(screen.getByText("notes.md")).toBeInTheDocument();
  });
});
