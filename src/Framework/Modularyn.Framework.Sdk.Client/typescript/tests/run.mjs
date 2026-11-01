import { run } from "./scenarios.ts";

const fixture = JSON.parse(process.env.MODULARYN_CLIENT_FIXTURE ?? "null");
if (!fixture) throw new Error("A live PostgreSQL contract fixture is required.");
await run(fixture);
process.stdout.write("TypeScript contract passed.\n");
