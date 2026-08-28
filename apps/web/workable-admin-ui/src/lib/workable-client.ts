import type { WorkableConnection } from "./workable";
import {
  semanticBadgeToneClass,
  semanticToneForStateName,
} from "@/lib/ui/state-tones";

export class WorkableApiError extends Error {
  constructor(
    message: string,
    public readonly status: number,
    public readonly body: unknown
  ) {
    super(message);
  }
}

export class WorkableRealtimeAuthenticationError extends Error {
  constructor(message: string) {
    super(message);
    this.name = "WorkableRealtimeAuthenticationError";
  }
}

export class WorkableRequestHeadersTooLargeError extends Error {
  public readonly status = 431;

  constructor() {
    super(
      "The Workable admin session has exceeded the request-header limit. " +
      "Automatic requests have stopped; clear the admin UI site data and sign in again."
    );
    this.name = "WorkableRequestHeadersTooLargeError";
  }
}

const inFlightGetRequests = new Map<string, Promise<unknown>>();
const inFlightQueryRequests = new Map<string, Promise<unknown>>();
const realtimeAccessTokenCache = new Map<string, {
  accessToken?: string;
  expiresAt: number;
}>();
const inFlightRealtimeAccessTokenRequests = new Map<string, Promise<string | undefined>>();
const realtimeAccessTokenForceRefresh = new Set<string>();
const realtimeAccessTokenFallbackTtlMs = 5 * 60 * 1000;
const realtimeMissingAccessTokenTtlMs = 5 * 60 * 1000;
const realtimeAccessTokenRefreshSkewMs = 60 * 1000;
let loginRedirectInFlight = false;
let requestHeadersTooLargeFailure: WorkableRequestHeadersTooLargeError | null = null;
const adminUiAuthRequiredError = "Authentication is required for the Workable admin UI.";
const workableUpstreamResponseHeader = "x-workable-upstream-response";

export function hasWorkableRequestHeadersTooLargeFailure() {
  return requestHeadersTooLargeFailure !== null;
}

export function isWorkableRequestHeadersTooLargeError(error: unknown) {
  if (error instanceof WorkableRequestHeadersTooLargeError) {
    return true;
  }

  if (error instanceof WorkableApiError && error.status === 431) {
    return true;
  }

  if (error && typeof error === "object") {
    const candidate = error as { status?: unknown; statusCode?: unknown };
    if (Number(candidate.status) === 431 || Number(candidate.statusCode) === 431) {
      return true;
    }
  }

  const message = error instanceof Error ? error.message : String(error ?? "");
  return /request header fields too large/i.test(message) ||
    /\bstatus(?:\s+code)?\b[^0-9]{0,40}431\b/i.test(message) ||
    /\bhttp(?:\/\d(?:\.\d)?)?\s+431\b/i.test(message);
}

export function stopWorkableRequestsForOversizedHeaders(error?: unknown) {
  if (!requestHeadersTooLargeFailure &&
      (error === undefined || isWorkableRequestHeadersTooLargeError(error))) {
    requestHeadersTooLargeFailure = error instanceof WorkableRequestHeadersTooLargeError
      ? error
      : new WorkableRequestHeadersTooLargeError();
  }

  return requestHeadersTooLargeFailure;
}

export function resetWorkableRequestHeadersTooLargeFailureForTests() {
  requestHeadersTooLargeFailure = null;
}

export function formatDateTime(value?: string | null) {
  if (!value) {
    return "Never";
  }

  return new Intl.DateTimeFormat(undefined, {
    dateStyle: "medium",
    timeStyle: "medium",
  }).format(new Date(value));
}

export function stateTone(state: string) {
  return semanticBadgeToneClass(semanticToneForStateName(state));
}

export function safeJsonParse(value: string) {
  if (!value.trim()) {
    return undefined;
  }

  try {
    return JSON.parse(value);
  } catch {
    throw new Error("Input must be valid JSON.");
  }
}

export async function workableFetch<T>(
  connection: WorkableConnection,
  path: string,
  init?: RequestInit,
  options: { coalesce?: boolean } = {}
): Promise<T> {
  const scopedPath = createScopedWorkablePath(connection, path);
  const method = init?.method?.toUpperCase() ?? "GET";
  const requestKey =
    method === "GET" && options.coalesce !== false
      ? `${method}:${connection.apiUrl}:${scopedPath}`
      : undefined;

  if (requestKey) {
    const existing = inFlightGetRequests.get(requestKey);
    if (existing) {
      return existing as Promise<T>;
    }
  }

  const request = fetchWorkable<T>(connection, scopedPath, init);
  if (requestKey) {
    inFlightGetRequests.set(requestKey, request);
    request.then(
      () => inFlightGetRequests.delete(requestKey),
      () => inFlightGetRequests.delete(requestKey)
    );
  }

  return request;
}

export async function workableQueryFetch<T>(
  connection: WorkableConnection,
  path: string,
  init?: RequestInit
): Promise<T> {
  const scopedPath = createScopedWorkablePath(connection, path);
  const method = init?.method?.toUpperCase() ?? "GET";
  const body =
    typeof init?.body === "string"
      ? init.body
      : init?.body === undefined
        ? ""
        : null;

  if (body === null) {
    return fetchWorkable<T>(connection, scopedPath, init);
  }

  const requestKey = `${method}:${connection.apiUrl}:${scopedPath}\n${body}`;
  const existing = inFlightQueryRequests.get(requestKey);
  if (existing) {
    return existing as Promise<T>;
  }

  const request = fetchWorkable<T>(connection, scopedPath, init);
  inFlightQueryRequests.set(requestKey, request);
  request.then(
    () => inFlightQueryRequests.delete(requestKey),
    () => inFlightQueryRequests.delete(requestKey)
  );

  return request;
}

async function fetchWorkable<T>(
  connection: WorkableConnection,
  scopedPath: string,
  init?: RequestInit
): Promise<T> {
  if (requestHeadersTooLargeFailure) {
    throw requestHeadersTooLargeFailure;
  }

  const response = await fetch(`/api/workable/${scopedPath}`, {
    ...init,
    headers: {
      "content-type": "application/json",
      "x-workable-api-url": connection.apiUrl,
      ...init?.headers,
    },
  });

  if (response.status === 431 &&
      response.headers.get(workableUpstreamResponseHeader) !== "true") {
    throw stopWorkableRequestsForOversizedHeaders()!;
  }

  const contentType = response.headers.get("content-type") ?? "";
  const responseText = await response.text();
  const body = contentType.includes("application/json") && responseText.trim()
    ? JSON.parse(responseText)
    : responseText;

  if (!response.ok) {
    const message = getWorkableErrorMessage(response.status, body);
    if (shouldRedirectToLogin(response.status, body)) {
      redirectToLogin(connection.apiUrl, "unauthorized");
    }
    throw new WorkableApiError(message, response.status, body);
  }

  return body as T;
}

export function createWorkableRealtimeUrl(connection: WorkableConnection) {
  const hubPath = connection.realtimeHubPath?.trim();
  if (!hubPath) {
    return null;
  }

  try {
    const apiUrl = new URL(connection.apiUrl);
    let hubUrl: URL;
    if (/^https?:\/\//i.test(hubPath)) {
      hubUrl = new URL(hubPath);
    } else {
      if (/^[a-z][a-z0-9+.-]*:/i.test(hubPath) || hubPath.startsWith("//")) {
        return null;
      }

      hubUrl = hubPath.startsWith("/")
        ? new URL(resolveRootedHubPath(apiUrl, hubPath), apiUrl.origin)
        : new URL(hubPath, createDirectoryUrl(apiUrl));
    }

    if (!["http:", "https:"].includes(hubUrl.protocol) || hubUrl.origin !== apiUrl.origin) {
      return null;
    }

    return hubUrl.toString();
  } catch {
    return null;
  }
}

export async function getWorkableRealtimeAccessToken(apiUrl: string) {
  if (requestHeadersTooLargeFailure) {
    throw requestHeadersTooLargeFailure;
  }

  const cached = realtimeAccessTokenCache.get(apiUrl);
  const now = Date.now();
  const forceRefresh = realtimeAccessTokenForceRefresh.has(apiUrl);
  if (!forceRefresh && cached && cached.expiresAt > now + realtimeAccessTokenRefreshSkewMs) {
    return cached.accessToken;
  }

  const inFlight = inFlightRealtimeAccessTokenRequests.get(apiUrl);
  if (inFlight) {
    return inFlight;
  }

  const request = (async () => {
    const query = new URLSearchParams({ apiUrl });
    const response = await fetch(
      `/api/auth/entra/workable-token?${query.toString()}`,
      {
        cache: "no-store",
        credentials: "same-origin",
        headers: forceRefresh
          ? { "x-workable-force-token-refresh": "true" }
          : undefined,
      }
    );
    if (response.status === 431) {
      throw stopWorkableRequestsForOversizedHeaders()!;
    }

    const body = await response.json().catch(() => ({}));
    if (response.status === 401) {
      redirectToLogin(apiUrl, "unauthorized");
    }

    if (!response.ok) {
      const message = typeof body?.error === "string" && body.error.trim()
        ? body.error
        : "Unable to acquire a hosted Workable API access token.";
      if (response.status === 401) {
        throw new WorkableRealtimeAuthenticationError(message);
      }
      throw new Error(message);
    }

    realtimeAccessTokenForceRefresh.delete(apiUrl);

    const accessToken = typeof body?.accessToken === "string" ? body.accessToken.trim() : "";
    if (!accessToken) {
      realtimeAccessTokenCache.set(apiUrl, {
        accessToken: undefined,
        expiresAt: now + realtimeMissingAccessTokenTtlMs,
      });
      return undefined;
    }

    realtimeAccessTokenCache.set(apiUrl, {
      accessToken,
      expiresAt: getRealtimeAccessTokenExpiration(body, now),
    });
    return accessToken;
  })();

  inFlightRealtimeAccessTokenRequests.set(apiUrl, request);
  request.then(
    () => inFlightRealtimeAccessTokenRequests.delete(apiUrl),
    () => inFlightRealtimeAccessTokenRequests.delete(apiUrl)
  );
  return request;
}

export function invalidateWorkableRealtimeAccessToken(
  apiUrl: string,
  forceRefresh = false
) {
  realtimeAccessTokenCache.delete(apiUrl);
  if (forceRefresh) {
    realtimeAccessTokenForceRefresh.add(apiUrl);
  }
}

export function isWorkableRealtimeAuthenticationError(
  error: unknown
): error is WorkableRealtimeAuthenticationError {
  return error instanceof WorkableRealtimeAuthenticationError;
}

function getRealtimeAccessTokenExpiration(body: unknown, now: number) {
  if (body && typeof body === "object" && "accessTokenExpiresInSeconds" in body) {
    const expiresInSeconds = Number(body.accessTokenExpiresInSeconds);
    if (Number.isFinite(expiresInSeconds) && expiresInSeconds > 0) {
      return now + expiresInSeconds * 1000;
    }
  }

  return now + realtimeAccessTokenFallbackTtlMs;
}

function redirectToLogin(error?: string, reason?: "unauthorized") {
  if (typeof window === "undefined" || loginRedirectInFlight) {
    return;
  }

  const nextPath = `${window.location.pathname}${window.location.search}`;
  if (window.location.pathname === "/login") {
    return;
  }

  const params = new URLSearchParams({
    next: nextPath,
  });
  if (error?.trim()) {
    params.set("error", error.trim());
  }
  if (reason) {
    params.set("reason", reason);
  }

  loginRedirectInFlight = true;
  window.location.replace(`/login?${params.toString()}`);
}

function createDirectoryUrl(url: URL) {
  return new URL(url.pathname.endsWith("/") ? url.pathname : `${url.pathname}/`, url.origin);
}

function resolveRootedHubPath(apiUrl: URL, hubPath: string) {
  const normalizedApiPath = apiUrl.pathname.replace(/\/+$/, "");
  const apiWorkableSuffix = "/workable";

  if (
    normalizedApiPath.length > apiWorkableSuffix.length &&
    normalizedApiPath.toLowerCase().endsWith(apiWorkableSuffix) &&
    hubPath.toLowerCase().startsWith(apiWorkableSuffix)
  ) {
    return `${normalizedApiPath.slice(0, -apiWorkableSuffix.length)}${hubPath}`;
  }

  return hubPath;
}

function getWorkableErrorMessage(status: number, body: unknown) {
  if (typeof body === "object" && body) {
    if ("error" in body && typeof body.error === "string" && body.error.trim()) {
      return body.error;
    }

    if ("messages" in body && Array.isArray(body.messages)) {
      const details = body.messages
        .map((message) => {
          if (typeof message === "object" && message && "text" in message) {
            return String(message.text ?? "").trim();
          }

          return "";
        })
        .filter(Boolean)
        .join(" ");
      if (details) {
        return details;
      }
    }

    if ("detail" in body && typeof body.detail === "string" && body.detail.trim()) {
      return body.detail;
    }

    if ("errors" in body && typeof body.errors === "object" && body.errors) {
      const details = Object.values(body.errors)
        .flatMap((value) => Array.isArray(value) ? value : [value])
        .map((value) => String(value ?? "").trim())
        .filter(Boolean)
        .join(" ");
      if (details) {
        return details;
      }
    }

    if ("title" in body && typeof body.title === "string" && body.title.trim()) {
      return body.title;
    }
  }

  if (typeof body === "string" && body.trim()) {
    return body.trim();
  }

  return `Workable request failed with ${status}.`;
}

function shouldRedirectToLogin(status: number, body: unknown) {
  return status === 401 && getWorkableErrorMessage(status, body) === adminUiAuthRequiredError;
}

function createScopedWorkablePath(connection: WorkableConnection, path: string) {
  const normalizedPath = path.replace(/^\/+/, "");
  const systemName = connection.systemName?.trim();

  if (!systemName) {
    return normalizedPath;
  }

  return `systems/${encodeURIComponent(systemName)}/${normalizedPath}`;
}
