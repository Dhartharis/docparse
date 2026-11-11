export type Tokens = {
  accessToken: string;
  refreshToken: string;
  expiresIn: number;
};
export type Organization = {
  id: string;
  name: string;
  role: "Admin" | "Viewer";
};
export type Bot = {
  id: string;
  name: string;
  welcomeMessage: string;
  accentColor?: string;
  logoVersion?: string | null;
};
export type Answer = {
  code?: string | null;
  answer: string;
  sources: { documentId: string; title: string; pageNumber?: number | null }[];
};
export type ErrorCode =
  | "invitation"
  | "account_exists"
  | "password"
  | "credentials"
  | "validation"
  | "network"
  | "rate"
  | "session"
  | "access"
  | "processing"
  | "usage_limit"
  | "usage_unavailable"
  | "billing_unavailable"
  | "billing_conflict"
  | "billing_reconciliation"
  | "admin_access"
  | "last_admin"
  | "knowledge_base_linked"
  | "document_duplicate"
  | "server";
export class ApiError extends Error {
  code: ErrorCode;
  constructor(code: ErrorCode) {
    super(code);
    this.code = code;
  }
}

// Development bearer sessions stay in memory. Passwords and tokens never enter browser storage.
let tokens: Tokens | null = null;
let expiresAt = 0;
let generation = 0;
let refreshing: Promise<void> | null = null;
// DOM listeners survive a development-module replacement; credentials remain in memory.
const sessionExpiredEvent = "docparse:session-expired";
function notifyExpired() {
  window.dispatchEvent(new Event(sessionExpiredEvent));
}
export function onSessionExpired(handler: () => void) {
  window.addEventListener(sessionExpiredEvent, handler);
  return () => window.removeEventListener(sessionExpiredEvent, handler);
}
if (import.meta.hot)
  import.meta.hot.dispose(() => {
    if (tokens) {
      clearSession();
      notifyExpired();
    }
  });
export function clearSession() {
  tokens = null;
  expiresAt = 0;
  generation++;
  refreshing = null;
}

async function send(
  path: string,
  data?: unknown,
  token?: string,
  signal?: AbortSignal,
  method?: string,
) {
  try {
    return await fetch(`/api${path}`, {
      method: method ?? (data === undefined ? "GET" : "POST"),
      credentials: "omit",
      headers: {
        ...(data === undefined || data instanceof FormData
          ? {}
          : { "Content-Type": "application/json" }),
        ...(token ? { Authorization: `Bearer ${token}` } : {}),
      },
      body:
        data === undefined
          ? undefined
          : data instanceof FormData
            ? data
            : JSON.stringify(data),
      signal: signal
        ? AbortSignal.any([
            signal,
            AbortSignal.timeout(data instanceof FormData ? 300_000 : 120_000),
          ])
        : AbortSignal.timeout(data instanceof FormData ? 300_000 : 120_000),
    });
  } catch (error) {
    if (signal?.aborted) throw error;
    throw new ApiError("network");
  }
}
function failure(response: Response, login = false): ApiError {
  if (response.status === 503) return new ApiError("processing");
  if (response.status === 429) return new ApiError("rate");
  if (response.status === 401)
    return new ApiError(login ? "credentials" : "session");
  if (response.status === 403 || response.status === 404)
    return new ApiError("access");
  if (response.status === 400 || response.status === 409)
    return new ApiError("validation");
  return new ApiError("server");
}
async function read<T>(response: Response): Promise<T> {
  if (response.status === 204 || response.headers.get("content-length") === "0")
    return undefined as T;
  if (!response.headers.get("content-type")?.includes("json"))
    return undefined as T;
  return response.json() as Promise<T>;
}
function acceptTokens(value: Tokens) {
  if (
    !value.accessToken ||
    !value.refreshToken ||
    !Number.isFinite(value.expiresIn)
  )
    throw new ApiError("server");
  tokens = value;
  expiresAt = Date.now() + value.expiresIn * 1000;
}
async function refresh() {
  if (refreshing) return refreshing;
  const current = tokens;
  const currentGeneration = generation;
  if (!current) throw new ApiError("session");
  const promise = (async () => {
    const response = await send("/auth/refresh", {
      refreshToken: current.refreshToken,
    });
    if (currentGeneration !== generation) throw new ApiError("session");
    if (!response.ok)
      throw response.status === 401 || response.status === 403
        ? new ApiError("session")
        : failure(response);
    const value = await read<Tokens>(response);
    if (currentGeneration !== generation) throw new ApiError("session");
    acceptTokens(value);
  })();
  refreshing = promise;
  try {
    await promise;
  } finally {
    if (refreshing === promise) refreshing = null;
  }
}
export async function publicRequest<T>(
  path: string,
  data: unknown,
  signal?: AbortSignal,
) {
  const response = await send(path, data, undefined, signal);
  if (response.status === 400 || response.status === 403 || response.status === 409) {
    const problem = await response
      .clone()
      .json()
      .catch(() => ({}));
    if (problem.code === "account_exists") throw new ApiError("account_exists");
    if (problem.code === "invitation_required")
      throw new ApiError("invitation");
    if (
      Object.keys(problem.errors ?? {}).some((code) =>
        code.startsWith("Password"),
      )
    )
      throw new ApiError("password");
  }
  if (!response.ok) throw failure(response, path === "/auth/login");
  return read<T>(response);
}
export async function signIn(
  email: string,
  password: string,
  signal?: AbortSignal,
) {
  const value = await publicRequest<Tokens>(
    "/auth/login",
    { email, password },
    signal,
  );
  if (signal?.aborted) return;
  clearSession();
  acceptTokens(value);
}
export async function api<T>(
  path: string,
  data?: unknown,
  signal?: AbortSignal,
  method?: string,
  responseType?: "blob",
): Promise<T> {
  const currentGeneration = generation;
  try {
    if (!tokens) throw new ApiError("session");
    if (Date.now() >= expiresAt - 5000) await refresh();
    if (currentGeneration !== generation) throw new ApiError("session");
    let response = await send(path, data, tokens?.accessToken, signal, method);
    if (response.status === 401) {
      await refresh();
      if (currentGeneration !== generation) throw new ApiError("session");
      response = await send(path, data, tokens?.accessToken, signal, method);
    }
    if (currentGeneration !== generation) throw new ApiError("session");
    if (response.status === 403 || response.status === 401) {
      clearSession();
      notifyExpired();
      throw new ApiError("session");
    }
    if (response.status === 429 || response.status === 503) {
      const problem = await response.clone().json().catch(() => ({}));
      if (problem.code === "usage_limit") throw new ApiError("usage_limit");
      if (problem.code === "usage_unavailable") throw new ApiError("usage_unavailable");
      if (problem.code === "billing_unavailable") throw new ApiError("billing_unavailable");
      if (problem.code === "billing_reconciliation") throw new ApiError("billing_reconciliation");
    }
    if (response.status === 409) {
      const problem = await response
        .clone()
        .json()
        .catch(() => ({}));
      if (problem.code === "knowledge_base_linked") throw new ApiError("knowledge_base_linked");
      if (problem.code === "document_duplicate") throw new ApiError("document_duplicate");
      if (problem.code === "last_admin") throw new ApiError("last_admin");
      if (problem.code === "admin_access") throw new ApiError("admin_access");
      if (problem.code === "billing_conflict") throw new ApiError("billing_conflict");
    }
    if (!response.ok) throw failure(response);
    return responseType === "blob"
      ? ((await response.blob()) as T)
      : await read<T>(response);
  } catch (error) {
    if (
      error instanceof ApiError &&
      error.code === "session" &&
      currentGeneration === generation
    ) {
      clearSession();
      notifyExpired();
    }
    throw error;
  }
}
