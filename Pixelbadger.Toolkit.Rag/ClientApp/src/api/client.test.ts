import { http, HttpResponse } from "msw";

import { ApiError, apiFetch } from "@/api/client";
import { buildUploadForm, documentContentUrl, documentDownloadUrl } from "@/api/documents";
import { server } from "@/test/server";

describe("api client", () => {
  it("builds relative content and download urls", () => {
    expect(documentContentUrl("abc")).toBe("/api/documents/abc/content");
    expect(documentDownloadUrl("abc")).toBe("/api/documents/abc/content?download=true");
  });

  it("parses problem+json into a typed ApiError", async () => {
    server.use(
      http.get("/api/x", () =>
        HttpResponse.json({ title: "Not Found", status: 404, detail: "Document was not found." }, {
          status: 404,
          headers: { "Content-Type": "application/problem+json" },
        }),
      ),
    );
    const error = await apiFetch("/api/x").catch((e: unknown) => e);
    expect(error).toBeInstanceOf(ApiError);
    expect((error as ApiError).status).toBe(404);
    expect((error as ApiError).message).toBe("Document was not found.");
    expect((error as ApiError).problem?.title).toBe("Not Found");
  });

  it("falls back to a generic message for non-JSON failures", async () => {
    server.use(http.get("/api/x", () => new HttpResponse("oops", { status: 502 })));
    const error = (await apiFetch("/api/x").catch((e: unknown) => e)) as ApiError;
    expect(error.status).toBe(502);
    expect(error.problem).toBeNull();
    expect(error.message).toBeTruthy();
  });

  it("supports cancellation through an AbortSignal", async () => {
    server.use(http.get("/api/slow", async () => new Promise(() => {})));
    const controller = new AbortController();
    const pending = apiFetch("/api/slow", { signal: controller.signal });
    controller.abort();
    await expect(pending).rejects.toMatchObject({ name: "AbortError" });
  });

  it("sends JSON with a Content-Type but leaves FormData's to the browser", async () => {
    const seen: (string | null)[] = [];
    server.use(
      http.post("/api/json", ({ request }) => {
        seen.push(request.headers.get("content-type"));
        return HttpResponse.json({});
      }),
    );
    await apiFetch("/api/json", { method: "POST", body: { a: 1 } });
    expect(seen[0]).toBe("application/json");

    const form = buildUploadForm([new File(["x"], "a.md")], 100);
    expect(form.getAll("files")).toHaveLength(1);
    expect(form.get("maxChunkCharacters")).toBe("100");
  });
});
