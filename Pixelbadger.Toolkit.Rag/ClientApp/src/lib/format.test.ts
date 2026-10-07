import { abbreviateGuid, formatBytes, jobDuration, normaliseDocumentIds } from "@/lib/format";

describe("format helpers", () => {
  it("humanises sizes", () => {
    expect(formatBytes(0)).toBe("0 B");
    expect(formatBytes(1536)).toBe("1.5 KB");
    expect(formatBytes(48321)).toBe("47 KB");
    expect(formatBytes(10 * 1024 * 1024)).toBe("10 MB");
  });

  it("abbreviates guids", () => {
    expect(abbreviateGuid("019a1b2c-3d4e-7f50-8a6b-7c8d9e0f1a2b")).toBe("019a1b2c…");
  });

  it("computes durations from started/completed, or from now for running jobs", () => {
    expect(jobDuration(null, null)).toBe("—");
    expect(jobDuration("2026-10-07T00:00:00Z", "2026-10-07T00:01:05Z")).toBe("1m 5s");
    expect(jobDuration("2026-10-07T00:00:00Z", null, Date.parse("2026-10-07T00:00:30Z"))).toBe("30 s");
  });

  it("normalises document ids", () => {
    expect(normaliseDocumentIds(" a, b\nA\r\n\n c ,")).toEqual(["a", "b", "c"]);
    expect(normaliseDocumentIds("")).toEqual([]);
  });
});
