import { fireEvent, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { http, HttpResponse } from "msw";

import type { QueryRequest, SearchResult } from "@/api/types";
import { server } from "@/test/server";
import { DOC_ID, renderApp, result } from "@/test/utils";

const ID_A = "019a1b2c-3d4e-7f50-8a6b-7c8d9e0f1a2b";
const ID_B = "019a1b2c-3d4e-7f50-8a6b-7c8d9e0f1a2c";

function mockQuery(results: SearchResult[]) {
  const bodies: QueryRequest[] = [];
  server.use(
    http.post("/api/query", async ({ request }) => {
      bodies.push((await request.json()) as QueryRequest);
      return HttpResponse.json({ results });
    }),
  );
  return bodies;
}

async function search(text = "mars") {
  const user = userEvent.setup();
  await user.type(screen.getByLabelText("Query"), text);
  await user.click(screen.getByRole("button", { name: /search/i }));
  return user;
}

describe("query page", () => {
  it("defaults max results to 10 and posts the expected request", async () => {
    const bodies = mockQuery([]);
    renderApp("/query");
    expect(screen.getByLabelText("Max results")).toHaveValue("10");
    const options = within(screen.getByLabelText("Max results")).getAllByRole("option").map((o) => o.textContent);
    expect(options).toEqual(["5", "10", "20", "50", "100"]);

    await search("mars");
    await waitFor(() => expect(bodies).toHaveLength(1));
    expect(bodies[0]).toEqual({ query: "mars", maxResults: 10, documentIds: [] });
  });

  it("sends the selected max results", async () => {
    const bodies = mockQuery([]);
    renderApp("/query");
    const user = userEvent.setup();
    await user.selectOptions(screen.getByLabelText("Max results"), "50");
    await user.type(screen.getByLabelText("Query"), "x");
    await user.click(screen.getByRole("button", { name: /search/i }));
    await waitFor(() => expect(bodies[0]?.maxResults).toBe(50));
  });

  it("normalises document ids: split on newlines/commas, trimmed, de-duplicated", async () => {
    const bodies = mockQuery([]);
    renderApp("/query");
    const user = userEvent.setup();
    await user.click(screen.getByText("Advanced"));
    await user.type(screen.getByLabelText("Restrict to document ids"), `  ${ID_A} ,${ID_B}\n\n${ID_A}\n`);
    expect(screen.getByText("2 id(s) will be sent.")).toBeInTheDocument();
    await user.type(screen.getByLabelText("Query"), "mars");
    await user.click(screen.getByRole("button", { name: /search/i }));
    await waitFor(() => expect(bodies).toHaveLength(1));
    expect(bodies[0].documentIds).toEqual([ID_A, ID_B]);
  });

  it("warns (advisory only) about ids that are not GUIDs and still submits them", async () => {
    const bodies = mockQuery([]);
    renderApp("/query");
    const user = userEvent.setup();
    await user.click(screen.getByText("Advanced"));
    await user.type(screen.getByLabelText("Restrict to document ids"), "not-a-guid");
    expect(screen.getByText(/does not look like a guid: not-a-guid/i)).toBeInTheDocument();
    await user.type(screen.getByLabelText("Query"), "q");
    await user.click(screen.getByRole("button", { name: /search/i }));
    await waitFor(() => expect(bodies[0]?.documentIds).toEqual(["not-a-guid"]));
  });

  it("renders a text result with content, locator, ranking diagnostics and source links built from documentId", async () => {
    mockQuery([result()]);
    renderApp("/query");
    await search();

    expect(await screen.findByText("Mars is red.")).toBeInTheDocument();
    expect(screen.getByText("docs/mars.md")).toBeInTheDocument();
    expect(screen.getByText("Text")).toBeInTheDocument();
    expect(screen.getByText("characters 10–40")).toBeInTheDocument();
    expect(screen.getByText("0.8123")).toBeInTheDocument();
    expect(screen.getByText(/^2 \(/)).toBeInTheDocument();
    expect(screen.getByTitle(DOC_ID)).toBeInTheDocument();

    expect(screen.getByRole("link", { name: /open source of docs\/mars\.md/i })).toHaveAttribute(
      "href",
      `/api/documents/${DOC_ID}/content`,
    );
    expect(screen.getByRole("link", { name: /download source of docs\/mars\.md/i })).toHaveAttribute(
      "href",
      `/api/documents/${DOC_ID}/content?download=true`,
    );
    // Retrieval only: no synthesised answer.
    expect(screen.queryByText(/answer/i)).not.toBeInTheDocument();
  });

  it("shows the cosine similarity score", async () => {
    mockQuery([result({ score: 0.81234 })]);
    renderApp("/query");
    await search();
    expect(await screen.findByText("Similarity")).toBeInTheDocument();
    expect(screen.getByText("0.8123")).toBeInTheDocument();
  });

  it("converts audio locators from milliseconds to seconds", async () => {
    mockQuery([
      result({ modality: "Audio", sourcePath: "talks/a.mp3", locatorStart: 12300, locatorEnd: 18900, content: null }),
    ]);
    renderApp("/query");
    await search();
    expect(await screen.findByText("12.3s–18.9s")).toBeInTheDocument();
    expect(screen.getByRole("link", { name: /open source of talks\/a\.mp3/i })).toBeInTheDocument();
  });

  it("previews image results from the canonical content endpoint, lazily", async () => {
    mockQuery([result({ modality: "Image", sourcePath: "img/cat.png", locatorStart: null, locatorEnd: null, content: null })]);
    renderApp("/query");
    await search();
    const img = await screen.findByRole("img", { name: /preview of img\/cat\.png/i });
    expect(img).toHaveAttribute("src", `/api/documents/${DOC_ID}/content`);
    expect(img).toHaveAttribute("loading", "lazy");
    expect(screen.getByRole("link", { name: /open full image img\/cat\.png/i })).toHaveAttribute("href", `/api/documents/${DOC_ID}/content`);
    expect(screen.getByRole("link", { name: /download source of img\/cat\.png/i })).toHaveAttribute(
      "href",
      `/api/documents/${DOC_ID}/content?download=true`,
    );
  });

  it("degrades gracefully to 'Source unavailable' when the image source cannot be loaded", async () => {
    mockQuery([result({ modality: "Image", sourcePath: "img/old.png", locatorStart: null, locatorEnd: null, content: null })]);
    renderApp("/query");
    await search();
    fireEvent.error(await screen.findByRole("img", { name: /preview of img\/old\.png/i }));
    expect(await screen.findByText("Source unavailable")).toBeInTheDocument();
    expect(screen.queryByRole("img")).not.toBeInTheDocument();
    // The hit itself is still shown.
    expect(screen.getByText("img/old.png")).toBeInTheDocument();
    expect(screen.queryByRole("alert")).not.toBeInTheDocument();
  });

  it("renders the empty state", async () => {
    mockQuery([]);
    renderApp("/query");
    await search("nothing");
    expect(await screen.findByText("No results found.")).toBeInTheDocument();
  });

  it("renders validation problems from the API", async () => {
    server.use(
      http.post("/api/query", () =>
        HttpResponse.json(
          { title: "One or more validation errors occurred.", status: 400, errors: { query: ["Query is required."] } },
          { status: 400, headers: { "Content-Type": "application/problem+json" } },
        ),
      ),
    );
    renderApp("/query");
    await search("   x");
    const alert = await screen.findByRole("alert");
    expect(alert).toHaveTextContent("Search failed");
    expect(alert).toHaveTextContent("query: Query is required.");
  });

  it("renders a problem detail such as a bad document id", async () => {
    server.use(
      http.post("/api/query", () =>
        HttpResponse.json({ title: "Bad Request", status: 400, detail: "'zzz' is not a valid document id." }, {
          status: 400,
          headers: { "Content-Type": "application/problem+json" },
        }),
      ),
    );
    renderApp("/query");
    await search();
    expect(await screen.findByRole("alert")).toHaveTextContent("'zzz' is not a valid document id.");
  });
});
