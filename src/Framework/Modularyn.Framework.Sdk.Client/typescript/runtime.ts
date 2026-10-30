export interface ApiProblem {
  type: string;
  title: string;
  status: number;
  code: string;
  correlationId: string;
  operationId?: string;
  errors?: Record<string, string[]>;
}

export class ApiFailure extends Error {
  readonly problem: ApiProblem;
  constructor(problem: ApiProblem) { super(problem.code); this.problem = problem; }
}
export class ValidationFailure extends ApiFailure {}
export class ConcurrencyFailure extends ApiFailure {}
export class AuthenticationFailure extends ApiFailure {}
export class AuthorizationFailure extends ApiFailure {}
export class MissingFailure extends ApiFailure {}
export class AvailabilityFailure extends ApiFailure {}
export class OperationConflictFailure extends ApiFailure {}
export class CompatibilityFailure extends ApiFailure {
  readonly authoritativeResponse: unknown;
  constructor(problem: ApiProblem, response?: unknown) { super(problem); this.authoritativeResponse = response; }
}
export class ProtocolFailure extends ApiFailure {
  readonly authoritativeResponse: unknown;
  constructor(problem: ApiProblem, response?: unknown) { super(problem); this.authoritativeResponse = response; }
}
export class OutcomeUnknownFailure extends ApiFailure {
  constructor(operation: string | ApiProblem) {
    super(typeof operation === "string" ? problem("MAP006", 0, "", operation) : operation);
  }
}

// The SEC-03 provider seam: obtain an explicit credential for each exact HTTPS origin and each request.
// The provider owns acquisition, expiry and rotation. This client stores no credential and uses no ambient cookies.
export interface ClientCredential { readonly scheme: string; readonly value: string }
export interface ClientCredentialProvider {
  getCredential(origin: string, signal?: AbortSignal): Promise<ClientCredential | null>;
}
export class OriginBoundCredentialProvider implements ClientCredentialProvider {
  private readonly origins: ReadonlySet<string>;
  private readonly source: (origin: string, signal?: AbortSignal) => Promise<ClientCredential>;
  constructor(origins: readonly string[], source: (origin: string, signal?: AbortSignal) => Promise<ClientCredential>) {
    if (origins.length === 0) throw new TypeError("At least one credential origin is required.");
    this.origins = new Set(origins.map(exactOrigin));
    this.source = source;
  }
  async getCredential(origin: string, signal?: AbortSignal): Promise<ClientCredential | null> {
    signal?.throwIfAborted();
    const target = exactOrigin(origin);
    if (!this.origins.has(target)) return null;
    const credential = await this.source(target, signal);
    if (!credential) throw new TypeError("The provider returned no credential for its configured origin.");
    return credential;
  }
}

function exactOrigin(value: string): string {
  if (!value.startsWith("https://") || !/^[\x21-\x7e]+$/.test(value) || /[\\?#%]/.test(value)) throw new TypeError("An exact HTTPS origin is required.");
  const url = new URL(value);
  if (url.username || url.password || url.pathname !== "/" || url.hostname.endsWith(".") || value.slice(8).replace(/\/+$/, "").includes("/"))
    throw new TypeError("An exact HTTPS origin is required.");
  return url.origin;
}
function problem(code: string, status: number, correlationId: string, operationId?: string): ApiProblem {
  return { type: `urn:modularyn:problem:${code}`, title: "The request could not be completed.", status, code, correlationId, operationId };
}
const uuid = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;

export function negotiateVersion(clientOldest: number, clientCurrent: number, serverOldest: number, serverCurrent: number): number {
  if (![clientOldest, clientCurrent, serverOldest, serverCurrent].every(Number.isInteger)
      || clientOldest < 1 || serverOldest < 1 || clientCurrent < clientOldest || serverCurrent < serverOldest)
    throw new TypeError("A version window must be valid.");
  const selected = Math.min(clientCurrent, serverCurrent);
  if (selected < Math.max(clientOldest, serverOldest)) throw new CompatibilityFailure(problem("MAP003", 426, ""));
  return selected;
}

export class ApiClient {
  private readonly origin: string;
  private readonly credentials: ClientCredentialProvider;
  readonly version: number;
  constructor(origin: string, credentials: ClientCredentialProvider, version = 2) {
    this.origin = exactOrigin(origin);
    this.credentials = credentials;
    if (!Number.isInteger(version) || version < 0) throw new TypeError("A version is a nonnegative integer.");
    this.version = version;
  }
  async send(method: string, template: string, parameters: Record<string, string>, body?: unknown,
      operationId?: string, signal?: AbortSignal, check?: (value: unknown) => void): Promise<unknown> {
    signal?.throwIfAborted();
    if (method !== "GET" && (!operationId || !uuid.test(operationId) || /^0{8}-0{4}-0{4}-0{4}-0{12}$/.test(operationId)))
      throw new TypeError("A mutation requires a stable operation identity.");
    let path = template.replaceAll("{version}", String(this.version));
    for (const [name, value] of Object.entries(parameters)) {
      if (!value || value === "." || value === ".." || /[/\\]/.test(value)) throw new TypeError("A route argument is one nonempty path segment.");
      path = path.replaceAll(`{${name}}`, encodeURIComponent(value));
    }
    if (!path.startsWith("/") || path.startsWith("//") || /[{}]/.test(path)) throw new TypeError("The endpoint binding is incomplete.");
    const target = new URL(path, this.origin);
    if (target.origin !== this.origin) throw new TypeError("The endpoint must stay on its configured origin.");
    const headers = new Headers({ Accept: "application/json" });
    if (operationId) headers.set("Idempotency-Key", operationId);
    const encoded = body === undefined ? undefined : JSON.stringify(body);
    if (encoded !== undefined) headers.set("Content-Type", "application/json");
    const credential = await this.credentials.getCredential(target.origin, signal);
    if (credential) {
      if (!/^[A-Za-z0-9!#$%&'*+.^_`|~-]{1,64}$/.test(credential.scheme) || !credential.value || /[\r\n]/.test(credential.value))
        throw new TypeError("An explicit credential must fit a single authorization header.");
      headers.set("Authorization", `${credential.scheme} ${credential.value}`);
    }
    signal?.throwIfAborted();
    let response: Response;
    let text: string;
    try {
      response = await fetch(target, { method, headers, body: encoded, signal, redirect: "manual", credentials: "omit", cache: "no-store" });
      text = await response.text();
    } catch (error) {
      if (operationId) throw new OutcomeUnknownFailure(operationId);
      throw error;
    }
    // A fully received result wins a cancellation that arrives after it. No signal check follows this point.
    let value: unknown;
    try { value = JSON.parse(text); } catch { value = undefined; }
    const correlation = response.headers.get("Modularyn-Correlation") ?? "";
    if (!response.ok) {
      const supplied = isObject(value) && typeof value.code === "string" ? value.code : undefined;
      const defaults: Record<number, string> = { 400: "MAP001", 404: "MAP004", 409: "MAP002", 426: "MAP003", 503: "MAP007" };
      const detail = problem(supplied ?? defaults[response.status] ?? "MAP008", response.status, correlation, operationId);
      if (isObject(value) && isObject(value.errors)) detail.errors = value.errors as Record<string, string[]>;
      if (response.status === 401) throw new AuthenticationFailure(detail);
      if (response.status === 403) throw new AuthorizationFailure(detail);
      switch (detail.code) {
        case "MAP001": throw new ValidationFailure(detail);
        case "MAP002": throw new ConcurrencyFailure(detail);
        case "MAP003": throw new CompatibilityFailure(detail);
        case "MAP004": throw new MissingFailure(detail);
        case "MAP005": throw new OperationConflictFailure(detail);
        case "MAP007": throw new AvailabilityFailure(detail);
        case "MAP006": throw new OutcomeUnknownFailure(detail);
        case "MAP008": throw new ProtocolFailure(detail);
        default:
          if (response.status === 404) throw new MissingFailure(detail);
          if (response.status === 503) throw new AvailabilityFailure(detail);
          throw new ApiFailure(detail);
      }
    }
    if (response.headers.get("Modularyn-Api-Version") !== String(this.version))
      throw new CompatibilityFailure(problem("MAP003", response.status, correlation, operationId), value);
    if (value === undefined) throw new ProtocolFailure(problem("MAP008", response.status, correlation, operationId));
    try { check?.(value); }
    catch { throw new ProtocolFailure(problem("MAP008", response.status, correlation, operationId), value); }
    return value;
  }
}

interface Model { name: string; fields: readonly { name: string; type: string; nullable: boolean }[] }
function isObject(value: unknown): value is Record<string, unknown> { return typeof value === "object" && value !== null && !Array.isArray(value); }
export function validate(value: unknown, type: string, models: readonly Model[]): void {
  const model = models.find(model => model.name === type);
  let valid = true;
  if (model) {
    valid = isObject(value);
    if (valid) for (const field of model.fields) {
      const key = field.name[0].toLowerCase() + field.name.slice(1);
      const item = (value as Record<string, unknown>)[key];
      if (item === null && field.nullable) continue;
      validate(item, field.type, models);
    }
  } else {
    switch (type) {
      case "string": valid = typeof value === "string"; break;
      case "uuid": valid = typeof value === "string" && uuid.test(value); break;
      case "integer": valid = typeof value === "number" && Number.isInteger(value) && value >= -2147483648 && value <= 2147483647; break;
      case "boolean": valid = typeof value === "boolean"; break;
      case "values": valid = isObject(value) && Object.values(value).every(item => item === null || typeof item === "string" || typeof item === "boolean"); break;
      default: valid = false;
    }
  }
  if (!valid) throw new ProtocolFailure(problem("MAP008", 200, ""), value);
}
