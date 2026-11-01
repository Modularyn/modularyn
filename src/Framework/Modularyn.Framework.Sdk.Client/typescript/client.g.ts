// Generated from the endpoint inventory by ApiGenerator.
import { ApiClient, validate } from "./runtime.ts";

export interface BootstrapConfiguration {
  settings: Record<string, string | boolean | null>;
  features: Record<string, string | boolean | null>;
}

const models = [{"name":"BootstrapConfiguration","fields":[{"name":"Settings","type":"values","nullable":false},{"name":"Features","type":"values","nullable":false}]}];

export class ModularynClient {
  static readonly oldestVersion = 1;
  static readonly currentVersion = 2;
  private readonly transport: ApiClient;
  constructor(transport: ApiClient) { this.transport = transport; }
  async getConfiguration(tenant: string, signal?: AbortSignal): Promise<BootstrapConfiguration> {
    const result = await this.transport.send("GET", "/api/v{version}/t/{tenant}/configuration", { tenant }, undefined, undefined, signal, value => validate(value, "BootstrapConfiguration", models));
    return result as BootstrapConfiguration;
  }
}
