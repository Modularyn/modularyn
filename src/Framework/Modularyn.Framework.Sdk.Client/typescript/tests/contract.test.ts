import { test } from "vitest";
import { run } from "./scenarios.ts";
import type { Fixture } from "./scenarios.ts";

declare const process: { env: Record<string, string | undefined> };
const source = process.env.MODULARYN_CLIENT_FIXTURE;
if (!source) throw new Error("Run through the .NET PostgreSQL fixture with MODULARYN_CLIENT_TEST_VITEST=1.");
const fixture = JSON.parse(source) as Fixture;
test(`AC-008 ${fixture.scenario} through the generated TypeScript client`, async () => { await run(fixture); }, 60000);
