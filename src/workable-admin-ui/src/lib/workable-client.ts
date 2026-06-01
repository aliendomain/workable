import type { WorkableConnection } from "./workable";

export class WorkableApiError extends Error {
  constructor(
    message: string,
    public readonly status: number,
    public readonly body: unknown
  ) {
    super(message);
  }
}

const inFlightGetRequests = new Map<string, Promise<unknown>>();
const inFlightQueryRequests = new Map<string, Promise<unknown>>();
const realtimeAccessTokenCache = new Map<string, {
  accessToken?: string;
  expiresAt: number;
}>();
const inFlightRealtimeAccessTokenRequests = new Map<string, Promise<string | undefined>>();
const realtimeAccessTokenTtlMs = 55 * 60 * 1000;
const realtimeMissingAccessTokenTtlMs = 5 * 60 * 1000;
let loginRedirectInFlight = false;
const adminUiAuthRequiredError = "Authentication is required for the Workable admin UI.";

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
  switch (state) {
    case "Running":
    case "Waiting":
      return "bg-emerald-500/15 text-emerald-300 border-emerald-500/30";
    case "Queued":
    case "Retrying":
    case "Paused":
    case "Interrupting":
    case "Interrupted":
      return "bg-sky-500/15 text-sky-300 border-sky-500/30";
    case "Failed":
    case "Canceled":
      return "bg-red-500/15 text-red-300 border-red-500/30";
    case "Completed":
      return "bg-teal-500/15 text-teal-300 border-teal-500/30";
    default:
      return "bg-muted text-muted-foreground";
  }
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
  init?: RequestInit
): Promise<T> {
  const scopedPath = createScopedWorkablePath(connection, path);
  const method = init?.method?.toUpperCase() ?? "GET";
  const requestKey =
    method === "GET"
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
  const response = await fetch(`/api/workable/${scopedPath}`, {
    ...init,
    headers: {
      "content-type": "application/json",
      "x-workable-api-url": connection.apiUrl,
      ...init?.headers,
    },
  });

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
  const cached = realtimeAccessTokenCache.get(apiUrl);
  const now = Date.now();
  if (cached && cached.expiresAt > now + 30_000) {
    return cached.accessToken;
  }

  const inFlight = inFlightRealtimeAccessTokenRequests.get(apiUrl);
  if (inFlight) {
    return inFlight;
  }

  const request = (async () => {
    const response = await fetch(
      `/api/auth/entra/workable-token?apiUrl=${encodeURIComponent(apiUrl)}`,
      {
        cache: "no-store",
        credentials: "same-origin",
      }
    );
    const body = await response.json().catch(() => ({}));
    if (shouldRedirectToLogin(response.status, body)) {
      redirectToLogin(apiUrl, "unauthorized");
    }

    if (!response.ok) {
      const message = typeof body?.error === "string" && body.error.trim()
        ? body.error
        : "Unable to acquire a hosted Workable API access token.";
      throw new Error(message);
    }

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
      expiresAt: now + realtimeAccessTokenTtlMs,
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
