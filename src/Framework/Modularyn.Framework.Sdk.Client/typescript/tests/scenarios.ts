import { FixtureClient } from "./fixture.g.ts";
import type { ItemView } from "./fixture.g.ts";
import { ApiClient, ApiFailure, AuthenticationFailure, AuthorizationFailure, CompatibilityFailure, ConcurrencyFailure,
  OperationConflictFailure, OriginBoundCredentialProvider, OutcomeUnknownFailure, ProtocolFailure, ValidationFailure, negotiateVersion } from "../runtime.ts";

export interface Fixture {
  origin: string;
  tenant: string;
  session: string;
  deniedSession: string;
  item: ItemView;
  before: string;
  after: string;
  recovery: string;
  edge: string;
  shape: string;
  scenario: string;
}
function assert(condition: unknown, message: string): asserts condition { if (!condition) throw new Error(message); }
function equal(actual: unknown, expected: unknown): void { assert(JSON.stringify(actual) === JSON.stringify(expected), "The authoritative values differ."); }
async function fails(action: () => Promise<unknown>, type: new (...args: never[]) => ApiFailure, code?: string): Promise<ApiFailure> {
  try { await action(); } catch (error) {
    assert(error instanceof type, "The failure type differs.");
    if (code) { equal(error.problem.code, code); assert(error.problem.correlationId.length > 0, "The failure lost its correlation."); }
    return error;
  }
  throw new Error("The operation unexpectedly succeeded.");
}

export async function run(fixture: Fixture): Promise<void> {
  const { origin, tenant, item, before, after, recovery, edge, shape, scenario } = fixture;
  let acquisitions = 0;
  const credentials = new OriginBoundCredentialProvider([origin], async () => {
    acquisitions++;
    return { scheme: "Bearer", value: fixture.session };
  });
  const transport = new ApiClient(origin, credentials);
  const client = new FixtureClient(transport);
  const checkpoint = async (id: string, state: string): Promise<void> => {
    for (let attempt = 0; attempt < 400; attempt++) {
      if ((await client.getCheckpoint(tenant, id)).state === state) return;
      await new Promise(resolve => setTimeout(resolve, 25));
    }
    throw new Error("The server did not reach its controlled checkpoint.");
  };
  switch (scenario) {
    case "positive": {
      equal(await client.getItem(tenant, item.id), item);
      const updated = await client.updateItem(tenant, item.id, { title: "changed", note: "", version: item.version }, crypto.randomUUID());
      assert(updated.version !== item.version, "The version did not advance.");
      equal(updated.note, "");
      equal(await client.getItem(tenant, item.id), updated);
      const bootstrap = await client.getConfiguration(tenant);
      equal(bootstrap.settings, { "Test.Items:items.page-size": "20" });
      equal(bootstrap.features, { "Test.Items:items.client-feature": false });
      equal(acquisitions, 4);
      // An unrelated origin never reaches the acquisition source.
      equal(await credentials.getCredential("https://unrelated.example.test"), null);
      equal(acquisitions, 4);
      break;
    }
    case "negative": {
      await fails(() => client.createItem(tenant, { title: "", note: null }, crypto.randomUUID()), ValidationFailure, "MAP001");
      await fails(() => client.updateItem(tenant, item.id, { title: "stale", note: null, version: crypto.randomUUID() }, crypto.randomUUID()), ConcurrencyFailure, "MAP002");
      await fails(() => new FixtureClient(new ApiClient(origin, credentials, 0)).getItem(tenant, item.id), CompatibilityFailure, "MAP003");
      const anonymous = new FixtureClient(new ApiClient(origin, { getCredential: async () => null }));
      await fails(() => anonymous.getItem(tenant, item.id), AuthenticationFailure);
      const forbidden = new FixtureClient(new ApiClient(origin, new OriginBoundCredentialProvider([origin], async () => ({ scheme: "Bearer", value: fixture.deniedSession }))));
      await fails(() => forbidden.getItem(tenant, item.id), AuthorizationFailure, "MAZ005");
      equal(await client.getItem(tenant, item.id), item);
      break;
    }
    case "boundary": {
      equal(negotiateVersion(1, 2, 1, 2), 2);
      await fails(async () => negotiateVersion(1, 2, 3, 4), CompatibilityFailure);
      const alreadyCancelled = new AbortController();
      alreadyCancelled.abort();
      const previousAcquisitions = acquisitions;
      let refusedBeforeSending = false;
      try { await client.createItem(tenant, { title: "not sent", note: null }, crypto.randomUUID(), alreadyCancelled.signal); }
      catch (error) { refusedBeforeSending = error instanceof DOMException && error.name === "AbortError"; }
      assert(refusedBeforeSending, "Cancellation before sending must remain local cancellation.");
      equal(acquisitions, previousAcquisitions);
      for (const version of [1, 2]) equal(await new FixtureClient(new ApiClient(origin, credentials, version)).getItem(tenant, item.id), item);
      const cancelled = new AbortController();
      const pending = fails(() => client.updateItem(tenant, item.id, { title: "cancelled", note: null, version: item.version }, before, cancelled.signal), OutcomeUnknownFailure);
      await checkpoint(before, "waiting");
      cancelled.abort();
      equal((await pending).problem.operationId, before);
      await checkpoint(before, "finished");
      equal((await client.getOperation(tenant, before)).state, "notObserved");
      equal(await client.getItem(tenant, item.id), item);
      const committedCancellation = new AbortController();
      const committed = fails(() => client.updateItem(tenant, item.id, { title: "committed", note: null, version: item.version }, after, committedCancellation.signal), OutcomeUnknownFailure);
      await checkpoint(after, "waiting");
      committedCancellation.abort();
      equal((await committed).problem.operationId, after);
      await checkpoint(after, "finished");
      const disposition = await client.getOperation(tenant, after);
      equal(disposition.state, "committed");
      equal(disposition.item?.title, "committed");
      equal(await client.getItem(tenant, item.id), disposition.item);
      assert(disposition.item, "A committed receipt needs its result.");
      const mismatch = await fails(() => client.updateItem(tenant, item.id, { title: "observed", note: null, version: disposition.item!.version }, edge), CompatibilityFailure, "MAP003");
      equal(mismatch.problem.operationId, edge);
      assert(mismatch instanceof CompatibilityFailure, "The compatibility failure must retain its observation.");
      equal((mismatch.authoritativeResponse as ItemView).title, "observed");
      equal((await client.getOperation(tenant, edge)).item?.title, "observed");
      const observed = (await client.getOperation(tenant, edge)).item;
      assert(observed, "The observed outcome needs its receipt.");
      const malformed = await fails(() => client.updateItem(tenant, item.id, { title: "shape", note: null, version: observed.version }, shape), ProtocolFailure, "MAP008");
      equal(malformed.problem.operationId, shape);
      assert(malformed instanceof ProtocolFailure, "The protocol failure must retain its observation.");
      equal((malformed.authoritativeResponse as ItemView).title, "shape");
      equal((await client.getOperation(tenant, shape)).item?.title, "shape");
      await fails(() => new FixtureClient(new ApiClient(origin, credentials, 0)).getItem(tenant, item.id), CompatibilityFailure, "MAP003");
      break;
    }
    case "recovery":
    case "restart": {
      if (scenario === "recovery")
        equal((await fails(() => client.createItem(tenant, { title: "recovered", note: null }, recovery), OutcomeUnknownFailure)).problem.operationId, recovery);
      const disposition = await client.getOperation(tenant, recovery);
      equal(disposition.state, "committed");
      const retried = await client.createItem(tenant, { title: "recovered", note: null }, recovery);
      equal(retried, disposition.item);
      equal(await client.getItem(tenant, retried.id), retried);
      await fails(() => client.createItem(tenant, { title: "different", note: null }, recovery), OperationConflictFailure, "MAP005");
      break;
    }
    case "concurrent": {
      const results = await Promise.all(Array.from({ length: 4 }, () => client.createItem(tenant, { title: "concurrent", note: null }, recovery)));
      for (const result of results) equal(result, results[0]);
      equal((await client.getOperation(tenant, recovery)).item, results[0]);
      break;
    }
    default: throw new Error("Unknown client scenario.");
  }
}
