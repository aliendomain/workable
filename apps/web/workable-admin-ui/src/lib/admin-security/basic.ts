import type { AdminSecuritySettings } from "./config.ts";
import { constantTimeEquals } from "./crypto.ts";
import {
  authenticatedIdentity,
  securityFailure,
  serviceUnavailable,
  type AdminSecurityResult,
} from "./types.ts";

const MAXIMUM_FAILED_ATTEMPTS = 5;
const MAXIMUM_ACCOUNT_FAILED_ATTEMPTS = 20;
const MAXIMUM_GLOBAL_FAILED_ATTEMPTS = 100;
const FAILED_ATTEMPT_WINDOW_MS = 60_000;
const BLOCKED_ATTEMPT_WINDOW_MS = 60_000;
const MAXIMUM_ATTEMPT_BUCKETS = 4_096;
const GLOBAL_ATTEMPT_KEY = "global";

type AttemptState = {
  failedAttempts: number[];
  blockedUntil: number;
};

type AttemptBucket = {
  key: string;
  maximumFailedAttempts: number;
  clearOnSuccess: boolean;
};

const attemptStates = new Map<string, AttemptState>();

export function authenticateBasicRequest(
  headers: Headers,
  settings: AdminSecuritySettings
): AdminSecurityResult {
  const configuration = validateBasicSettings(settings);
  if (!configuration.ok) {
    return configuration;
  }

  const credentials = parseBasicAuthorization(headers.get("authorization"));
  const attemptBuckets = credentials
    ? createAttemptBuckets(headers, credentials.userName)
    : [];

  if (credentials) {
    const attemptLimit = checkAttemptLimits(attemptBuckets);
    if (attemptLimit) {
      return attemptLimit;
    }
  }

  if (
    credentials &&
    constantTimeEquals(credentials.userName, settings.userName ?? "") &&
    constantTimeEquals(credentials.password, settings.password ?? "")
  ) {
    clearAttemptStates(attemptBuckets);
    return authenticatedIdentity(credentials.userName, "basic", "basic");
  }

  if (credentials) {
    const failure = recordFailedAttempts(attemptBuckets);
    if (failure) {
      return failure;
    }
  }

  return securityFailure(
    401,
    "Authentication is required for the Workable admin UI."
  );
}

export function verifyBasicCredentials(
  userName: string,
  password: string,
  settings: AdminSecuritySettings,
  headers?: Headers
): AdminSecurityResult {
  const configuration = validateBasicSettings(settings);
  if (!configuration.ok) {
    return configuration;
  }

  const attemptBuckets = createAttemptBuckets(headers, userName);
  const attemptLimit = checkAttemptLimits(attemptBuckets);
  if (attemptLimit) {
    return attemptLimit;
  }

  const credentialsMatch =
    constantTimeEquals(userName, settings.userName ?? "") &&
    constantTimeEquals(password, settings.password ?? "");
  if (!credentialsMatch) {
    const failure = recordFailedAttempts(attemptBuckets);
    if (failure) {
      return failure;
    }
    return securityFailure(401, "The username or password is not valid.");
  }

  clearAttemptStates(attemptBuckets);
  return authenticatedIdentity(settings.userName ?? userName, "session", "basic");
}

function validateBasicSettings(settings: AdminSecuritySettings): AdminSecurityResult {
  if (settings.authProvider !== "basic") {
    return securityFailure(400, "Basic admin UI authentication is not enabled.");
  }

  if (!settings.basicAuthEnabled) {
    return serviceUnavailable(
      "Workable admin UI Basic authentication is disabled. Set basicAuth.enabled to true or WORKABLE_ADMIN_UI_BASIC_AUTH_ENABLED=true to enable it explicitly."
    );
  }

  if (!settings.userName || !settings.password) {
    return serviceUnavailable(
      "Workable admin UI Basic authentication is not configured. Configure basicAuth in workable-admin.config.local.json or WORKABLE_ADMIN_UI_USERNAME and WORKABLE_ADMIN_UI_PASSWORD."
    );
  }

  return authenticatedIdentity("basic-configured", "basic", "basic");
}

function checkAttemptLimits(buckets: readonly AttemptBucket[]): AdminSecurityResult | null {
  const now = Date.now();
  for (const bucket of buckets) {
    const state = attemptStates.get(bucket.key);
    if (!state) {
      continue;
    }

    pruneFailedAttempts(state, now);
    if (state.blockedUntil > now) {
      return tooManyAttempts(state.blockedUntil - now);
    }
  }
  return null;
}

function recordFailedAttempts(buckets: readonly AttemptBucket[]): AdminSecurityResult | null {
  const now = Date.now();
  let blocked = false;
  for (const bucket of buckets) {
    const state = getOrCreateAttemptState(bucket.key);
    pruneFailedAttempts(state, now);
    state.failedAttempts.push(now);
    if (state.failedAttempts.length < bucket.maximumFailedAttempts) {
      continue;
    }

    state.blockedUntil = now + BLOCKED_ATTEMPT_WINDOW_MS;
    state.failedAttempts = [];
    blocked = true;
  }
  return blocked ? tooManyAttempts(BLOCKED_ATTEMPT_WINDOW_MS) : null;
}

function pruneFailedAttempts(state: AttemptState, now: number) {
  state.failedAttempts = state.failedAttempts.filter(
    (attemptedAt) => attemptedAt > now - FAILED_ATTEMPT_WINDOW_MS
  );
  if (state.blockedUntil <= now) {
    state.blockedUntil = 0;
  }
}

function getOrCreateAttemptState(key: string) {
  const existing = attemptStates.get(key);
  if (existing) {
    return existing;
  }

  if (attemptStates.size >= MAXIMUM_ATTEMPT_BUCKETS) {
    let oldestKey: string | undefined;
    for (const candidate of attemptStates.keys()) {
      if (candidate !== GLOBAL_ATTEMPT_KEY) {
        oldestKey = candidate;
        break;
      }
    }
    if (oldestKey) {
      attemptStates.delete(oldestKey);
    }
  }

  const created: AttemptState = {
    failedAttempts: [],
    blockedUntil: 0,
  };
  attemptStates.set(key, created);
  return created;
}

function tooManyAttempts(remainingMs: number): AdminSecurityResult {
  return securityFailure(
    429,
    "Too many failed Basic authentication attempts. Try again later.",
    { "retry-after": String(Math.max(1, Math.ceil(remainingMs / 1000))) }
  );
}

function clearAttemptStates(buckets: readonly AttemptBucket[]) {
  for (const bucket of buckets) {
    if (bucket.clearOnSuccess) {
      attemptStates.delete(bucket.key);
    }
  }
}

export function resetBasicAuthenticationAttemptsForTests() {
  attemptStates.clear();
}

export function basicAuthenticationAttemptBucketCountForTests() {
  return attemptStates.size;
}

function createAttemptBuckets(headers: Headers | undefined, userName: string) {
  const normalizedUserName = userName.trim().toLowerCase().slice(0, 256);
  const buckets: AttemptBucket[] = [
    {
      key: `account\n${normalizedUserName}`,
      maximumFailedAttempts: MAXIMUM_ACCOUNT_FAILED_ATTEMPTS,
      clearOnSuccess: true,
    },
    {
      key: GLOBAL_ATTEMPT_KEY,
      maximumFailedAttempts: MAXIMUM_GLOBAL_FAILED_ATTEMPTS,
      clearOnSuccess: false,
    },
  ];
  if (!headers) {
    return buckets;
  }

  const source = requestSource(headers);
  if (source) {
    buckets.unshift({
      key: `source\n${source}\n${normalizedUserName}`,
      maximumFailedAttempts: MAXIMUM_FAILED_ATTEMPTS,
      clearOnSuccess: true,
    });
  }
  return buckets;
}

function requestSource(headers: Headers) {
  const forwardedFor = headers.get("x-forwarded-for")
    ?.split(",", 1)[0]
    ?.trim();
  const source = headers.get("cf-connecting-ip")?.trim() ||
    headers.get("x-vercel-forwarded-for")?.split(",", 1)[0]?.trim() ||
    forwardedFor ||
    headers.get("x-real-ip")?.trim();
  return source ? source.slice(0, 256) : null;
}

function parseBasicAuthorization(value: string | null) {
  if (!value?.toLowerCase().startsWith("basic ")) {
    return null;
  }

  try {
    const decoded = Buffer.from(value.slice(6).trim(), "base64").toString("utf8");
    const separator = decoded.indexOf(":");
    if (separator < 0) {
      return null;
    }

    return {
      userName: decoded.slice(0, separator),
      password: decoded.slice(separator + 1),
    };
  } catch {
    return null;
  }
}
