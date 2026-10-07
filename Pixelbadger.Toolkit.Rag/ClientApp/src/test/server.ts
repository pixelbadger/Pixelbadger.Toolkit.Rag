import { setupServer } from "msw/node";

// Handlers are registered per test with server.use(...); unhandled requests fail the test (see setup.ts).
export const server = setupServer();
