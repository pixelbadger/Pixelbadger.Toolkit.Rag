/// <reference types="vitest/config" />
import path from "node:path";
import { fileURLToPath } from "node:url";
import { defineConfig } from "vite";
import react from "@vitejs/plugin-react";
import tailwindcss from "@tailwindcss/vite";

const here = path.dirname(fileURLToPath(import.meta.url));

// The ASP.NET host (see Properties/launchSettings.json). Override with PBRAG_DEV_API for another host/port.
const apiTarget = process.env.PBRAG_DEV_API ?? "http://localhost:8080";

export default defineConfig({
  plugins: [react(), tailwindcss()],
  resolve: { alias: { "@": path.resolve(here, "src") } },
  build: { outDir: "dist", emptyOutDir: true },
  server: {
    // Same-origin in production, so the SPA only ever uses relative URLs; no CORS needed in dev either.
    proxy: {
      "/api": { target: apiTarget },
      "/health": { target: apiTarget },
      "/mcp": { target: apiTarget },
    },
  },
  test: {
    globals: true,
    environment: "jsdom",
    setupFiles: ["./src/test/setup.ts"],
    css: false,
    restoreMocks: true,
  },
});
