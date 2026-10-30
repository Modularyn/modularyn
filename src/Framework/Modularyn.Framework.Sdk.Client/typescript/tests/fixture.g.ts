// Generated from the endpoint inventory by ApiGenerator.
import { ApiClient, validate } from "../runtime.ts";

export interface BootstrapConfiguration {
  settings: Record<string, string | boolean | null>;
  features: Record<string, string | boolean | null>;
}
export interface ItemView {
  id: string;
  title: string;
  note: string | null;
  version: string;
}
export interface CreateItemRequest {
  title: string;
  note: string | null;
}
export interface UpdateItemRequest {
  title: string;
  note: string | null;
  version: string;
}
export interface OperationView {
  operationId: string;
  state: string;
  item: ItemView | null;
}
export interface CheckpointView {
  state: string;
}

const models = [{"name":"BootstrapConfiguration","fields":[{"name":"Settings","type":"values","nullable":false},{"name":"Features","type":"values","nullable":false}]},{"name":"ItemView","fields":[{"name":"Id","type":"uuid","nullable":false},{"name":"Title","type":"string","nullable":false},{"name":"Note","type":"string","nullable":true},{"name":"Version","type":"uuid","nullable":false}]},{"name":"CreateItemRequest","fields":[{"name":"Title","type":"string","nullable":false},{"name":"Note","type":"string","nullable":true}]},{"name":"UpdateItemRequest","fields":[{"name":"Title","type":"string","nullable":false},{"name":"Note","type":"string","nullable":true},{"name":"Version","type":"uuid","nullable":false}]},{"name":"OperationView","fields":[{"name":"OperationId","type":"uuid","nullable":false},{"name":"State","type":"string","nullable":false},{"name":"Item","type":"ItemView","nullable":true}]},{"name":"CheckpointView","fields":[{"name":"State","type":"string","nullable":false}]}];

export class FixtureClient {
  static readonly oldestVersion = 1;
  static readonly currentVersion = 2;
  private readonly transport: ApiClient;
  constructor(transport: ApiClient) { this.transport = transport; }
  async getConfiguration(tenant: string, signal?: AbortSignal): Promise<BootstrapConfiguration> {
    const result = await this.transport.send("GET", "/api/v{version}/t/{tenant}/configuration", { tenant }, undefined, undefined, signal, value => validate(value, "BootstrapConfiguration", models));
    return result as BootstrapConfiguration;
  }
  async getItem(tenant: string, id: string, signal?: AbortSignal): Promise<ItemView> {
    const result = await this.transport.send("GET", "/api/v{version}/t/{tenant}/items/{id}", { tenant, id }, undefined, undefined, signal, value => validate(value, "ItemView", models));
    return result as ItemView;
  }
  async createItem(tenant: string, body: CreateItemRequest, operationId: string, signal?: AbortSignal): Promise<ItemView> {
    const result = await this.transport.send("POST", "/api/v{version}/t/{tenant}/items", { tenant }, body, operationId, signal, value => validate(value, "ItemView", models));
    return result as ItemView;
  }
  async updateItem(tenant: string, id: string, body: UpdateItemRequest, operationId: string, signal?: AbortSignal): Promise<ItemView> {
    const result = await this.transport.send("PUT", "/api/v{version}/t/{tenant}/items/{id}", { tenant, id }, body, operationId, signal, value => validate(value, "ItemView", models));
    return result as ItemView;
  }
  async getOperation(tenant: string, id: string, signal?: AbortSignal): Promise<OperationView> {
    const result = await this.transport.send("GET", "/api/v{version}/t/{tenant}/operations/{id}", { tenant, id }, undefined, undefined, signal, value => validate(value, "OperationView", models));
    return result as OperationView;
  }
  async getCheckpoint(tenant: string, id: string, signal?: AbortSignal): Promise<CheckpointView> {
    const result = await this.transport.send("GET", "/api/v{version}/t/{tenant}/checkpoints/{id}", { tenant, id }, undefined, undefined, signal, value => validate(value, "CheckpointView", models));
    return result as CheckpointView;
  }
}
