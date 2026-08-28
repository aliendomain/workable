import type { AdminSecurityEnvironment, AdminSecurityFailure } from "./types.ts";
import {
  authenticatedIdentity,
  securityFailure,
  serviceUnavailable,
  type AdminSecurityResult,
} from "./types.ts";
import { getAdminSecuritySettings, type AdminSecuritySettings } from "./config.ts";
import {
  constantTimeEquals,
  decrypt,
  encrypt,
  sha256Base64Url,
  sign,
} from "./crypto.ts";
import {
  readUniqueCookie,
  serializeCookie,
  serializeExpiredCookie,
  shouldSecureCookie,
} from "./cookies.ts";
import {
  readAdminSessionState,
  sessionSecret,
  type AdminSessionIdentity,
} from "./session.ts";
import {
  fetchCachedEntraJson,
  fetchEntraJson,
  validateEntraBackchannelUrl,
  type EntraFetchLike,
} from "./entra-backchannel.ts";

const TOKEN_COOKIE_NAME = "__Host-workable_admin_entra_target_token";
const LEGACY_TOKEN_COOKIE_NAME = "workable_admin_entra_target_token_legacy";
const DEVELOPMENT_TOKEN_COOKIE_NAME = "workable_admin_entra_target_token";
const TOKEN_COOKIE_PURPOSE = "entra-target-token";
const TOKEN_COOKIE_CHUNK_SIZE = 3000;
const MAX_TOKEN_COOKIE_CHUNKS = 16;
const MAX_TOKEN_COOKIE_SNAPSHOTS = 4;
const MAX_TOKEN_COOKIE_SNAPSHOT_BYTES = 6 * 1024;
const MAX_ACCESS_TOKEN_CACHE_ENTRIES = 256;
const MAX_CLEARED_TOKEN_OWNERS = 256;
const MAX_REFRESH_COORDINATOR_KEYS = 8;
const MAX_ACCESS_TOKEN_BYTES = 64 * 1024;
const MAX_REFRESH_TOKEN_BYTES = 4 * 1024;
const MAX_ACCESS_TOKEN_LIFETIME_SECONDS = 24 * 60 * 60;
const TARGET_TOKEN_STATE_VERSION = 1;
const TOKEN_REFRESH_SKEW_SECONDS = 60;
const noStoreHeaders = {
  "cache-control": "no-store",
  "x-content-type-options": "nosniff",
};

type FetchLike = EntraFetchLike;

type EntraTargetTokenResponse = {
  access_token?: string;
  expires_in?: number;
  refresh_token?: string;
  scope?: string;
  token_type?: string;
  error?: string;
};

type StoredEntraTargetAccessToken = {
  accessToken: string;
  accessTokenExpiresAt: number;
  scopeRotationVersion: number;
};

type StoredEntraTargetTokenState = {
  ownerBinding: string;
  refreshToken?: string;
  issuedAt?: number;
  rotationVersion?: number;
  scopeRotations?: Record<string, number>;
  snapshotId?: string;
  sourceCookieNames?: string[];
};

type PersistedEntraTargetTokenState = {
  version: typeof TARGET_TOKEN_STATE_VERSION;
  ownerBinding: string;
  refreshToken?: string;
  issuedAt: number;
  rotationVersion: number;
  scopeRotations?: Record<string, number>;
};

type EntraOpenIdConfiguration = {
  token_endpoint?: string;
};

type EntraTargetTokenBinding = {
  apiUrl: string;
  key: string;
  scope: string;
  scopeRotationKey: string;
};

type EntraTargetAccessTokenSuccess = {
  ok: true;
  accessToken?: string;
  accessTokenExpiresAt?: number;
  setCookieHeaders: string[];
};

type EntraTargetAccessTokenFailure = AdminSecurityFailure & {
  setCookieHeaders: string[];
};

type EntraTargetAccessTokenOptions = {
  forceRefresh?: boolean;
  requestedApiUrl?: URL | null;
};

type EntraRefreshCoordinator = {
  keys: Set<string>;
  lastError?: unknown;
  latestState: StoredEntraTargetTokenState;
  tail: Promise<void>;
  version: number;
};

const refreshCoordinators = new Map<string, EntraRefreshCoordinator>();
const accessTokenCache = new Map<string, StoredEntraTargetAccessToken & {
  ownerBinding: string;
}>();
const clearedTokenOwners = new Map<string, true>();

export function getEntraTargetTokenBindings(settings: AdminSecuritySettings) {
  const bindings: EntraTargetTokenBinding[] = [];
  const seen = new Set<string>();
  const scopeRotationKeys = new Map<string, string>();

  for (const candidate of getConfiguredTargetApiCandidates(settings)) {
    const normalized = normalizeBinding(candidate.apiUrl, candidate.scope);
    if (!normalized || seen.has(normalized.key)) {
      continue;
    }

    seen.add(normalized.key);
    let scopeRotationKey = scopeRotationKeys.get(normalized.scope);
    if (scopeRotationKey === undefined) {
      scopeRotationKey = scopeRotationKeys.size.toString(36);
      scopeRotationKeys.set(normalized.scope, scopeRotationKey);
    }
    bindings.push({ ...normalized, scopeRotationKey });
  }

  return bindings;
}

export function findEntraTargetTokenBinding(
  settings: AdminSecuritySettings,
  requestedApiUrl: URL | null
) {
  if (!requestedApiUrl) {
    return null;
  }

  const normalizedApiUrl = normalizeApiUrl(requestedApiUrl);
  return getEntraTargetTokenBindings(settings).find(
    (binding) => binding.apiUrl === normalizedApiUrl
  ) ?? null;
}

export function createInteractiveEntraScopes(settings: AdminSecuritySettings) {
  const scopes = ["openid", "profile", "email"];
  const bindings = getEntraTargetTokenBindings(settings);
  if (bindings.length === 0) {
    return scopes.join(" ");
  }

  scopes.push("offline_access");
  if (bindings.length === 1) {
    scopes.push(bindings[0].scope);
  }

  return scopes.join(" ");
}

export function createExpiredEntraTargetTokenCookies(
  headers?: Headers,
  isProduction = process.env.NODE_ENV === "production"
) {
  const fixed = [DEVELOPMENT_TOKEN_COOKIE_NAME, LEGACY_TOKEN_COOKIE_NAME]
    .flatMap((name) => expireChunkedCookie(name));
  const observed = findTokenSnapshotCookieNames(headers, !isProduction)
    .filter((name) => name !== DEVELOPMENT_TOKEN_COOKIE_NAME && name !== LEGACY_TOKEN_COOKIE_NAME)
    .flatMap((name) => expireChunkedCookie(name, readChunkCount(headers!, name)));
  return fixed.concat(observed);
}

export function createEntraTargetTokenCookieHeaders(
  tokens: {
    access_token?: string;
    expires_in?: number;
    refresh_token?: string;
    scope?: string;
    token_type?: string;
  },
  request: Request,
  settings: AdminSecuritySettings,
  identity: AdminSessionIdentity
) {
  const secret = sessionSecret(settings);
  if (!secret || identity.provider !== "entra" || !identity.entraSubject?.trim()) {
    return createExpiredEntraTargetTokenCookies(request.headers, settings.isProduction);
  }
  if (!isValidRefreshToken(tokens.refresh_token)) {
    return createExpiredEntraTargetTokenCookies(request.headers, settings.isProduction);
  }

  const bindings = getEntraTargetTokenBindings(settings);
  const state: StoredEntraTargetTokenState = {
    ownerBinding: createTargetTokenOwnerBinding(identity, settings, secret),
    refreshToken: tokens.refresh_token,
  };
  if (!identity.sessionId?.trim()) {
    return createExpiredEntraTargetTokenCookies(request.headers, settings.isProduction);
  }

  const initialBinding = bindings.length === 1 ? bindings[0] : undefined;
  const initialAccessToken = initialBinding &&
    typeof tokens.access_token === "string" &&
    tokens.access_token.length > 0 &&
    isValidAccessTokenLifetime(tokens.expires_in)
    ? {
      accessToken: tokens.access_token,
      accessTokenExpiresAt: Math.floor(Date.now() / 1000) + tokens.expires_in,
      scopeRotationVersion: 0,
    } satisfies StoredEntraTargetAccessToken
    : undefined;

  try {
    const headers = serializeStateCookie(
      state,
      request,
      settings,
      Math.min(
        settings.sessionMaxAgeSeconds,
        settings.sessionAbsoluteMaxAgeSeconds
      )
    );
    if (initialBinding && initialAccessToken) {
      storeCachedAccessToken(
        state.ownerBinding,
        initialBinding.scope,
        secret,
        initialAccessToken
      );
    }
    return headers;
  } catch {
    return createExpiredEntraTargetTokenCookies(request.headers, settings.isProduction);
  }
}

export async function getEntraTargetAccessToken(
  request: Request,
  env: AdminSecurityEnvironment = process.env,
  fetcher: FetchLike = fetch,
  options: EntraTargetAccessTokenOptions = {}
): Promise<EntraTargetAccessTokenSuccess | EntraTargetAccessTokenFailure> {
  const settings = getAdminSecuritySettings(env);
  if (settings.authProvider !== "entra") {
    return {
      ok: true,
      setCookieHeaders: hasEntraTargetTokenCookie(request.headers, settings)
        ? createExpiredEntraTargetTokenCookies(request.headers, settings.isProduction)
        : [],
    };
  }

  const binding = findEntraTargetTokenBinding(
    settings,
    options.requestedApiUrl ?? getRequestedApiUrl(request, settings)
  );
  if (!binding) {
    return { ok: true, setCookieHeaders: [] };
  }

  const session = readAdminSessionState(request.headers, settings);
  const identity = session.identity;
  const secret = sessionSecret(settings);
  const expectedOwnerBinding = identity?.provider === "entra" && secret
    ? createTargetTokenOwnerBinding(identity, settings, secret)
    : undefined;
  const stored = readStoredTargetTokenState(
    request.headers,
    settings,
    expectedOwnerBinding
  );
  if (!stored) {
    return {
      ...securityFailure(
        401,
        "Microsoft Entra ID access to the hosted Workable API is not available. Sign in again."
      ),
      setCookieHeaders: createExpiredEntraTargetTokenCookies(request.headers, settings.isProduction),
    };
  }

  if (
    !identity ||
    identity.provider !== "entra" ||
    !secret ||
    !constantTimeEquals(
      stored.ownerBinding,
      createTargetTokenOwnerBinding(identity, settings, secret)
    )
  ) {
    return {
      ...securityFailure(
        401,
        "Microsoft Entra ID access to the hosted Workable API is not available. Sign in again."
      ),
      setCookieHeaders: createExpiredEntraTargetTokenCookies(request.headers, settings.isProduction),
    };
  }

  const existing = readCachedAccessToken(
    stored.ownerBinding,
    binding.scope,
    secret,
    stored.scopeRotations?.[binding.scopeRotationKey] ?? 0
  );
  if (existing && !options.forceRefresh) {
    return {
      ok: true,
      accessToken: existing.accessToken,
      accessTokenExpiresAt: existing.accessTokenExpiresAt,
      setCookieHeaders: shouldConsolidateState(stored, request, settings)
        ? serializeStateCookie(
            stored,
            request,
            settings,
            remainingAbsoluteSessionSeconds(session.absoluteExpiresAt)
          )
        : [],
    };
  }

  if (!stored.refreshToken) {
    return {
      ...securityFailure(
        401,
        "Microsoft Entra ID access to the hosted Workable API has expired. Sign in again."
      ),
      setCookieHeaders: createExpiredEntraTargetTokenCookies(request.headers, settings.isProduction),
    };
  }

  try {
    const coordinated = await coordinateEntraTargetAccessTokenRefresh(
      stored,
      binding,
      Boolean(options.forceRefresh),
      settings,
      fetcher
    );

    return {
      ok: true,
      accessToken: coordinated.accessToken.accessToken,
      accessTokenExpiresAt: coordinated.accessToken.accessTokenExpiresAt,
      setCookieHeaders: serializeStateCookie(
        {
          ...coordinated.state,
          sourceCookieNames: stored.sourceCookieNames,
        },
        request,
        settings,
        remainingAbsoluteSessionSeconds(session.absoluteExpiresAt)
      ),
    };
  } catch {
    return {
      ...securityFailure(
        401,
        "Microsoft Entra ID access to the hosted Workable API could not be refreshed. Sign in again."
      ),
      setCookieHeaders: createExpiredEntraTargetTokenCookies(request.headers, settings.isProduction),
    };
  }
}

async function coordinateEntraTargetAccessTokenRefresh(
  stored: StoredEntraTargetTokenState,
  binding: EntraTargetTokenBinding,
  forceRefresh: boolean,
  settings: AdminSecuritySettings,
  fetcher: FetchLike
) {
  const secret = sessionSecret(settings)!;
  const refreshToken = stored.refreshToken!;

  const coordinatorKey = createRefreshCoordinatorKey(
    refreshToken,
    stored.ownerBinding,
    secret
  );
  let coordinator = refreshCoordinators.get(coordinatorKey);
  if (!coordinator) {
    coordinator = {
      keys: new Set(),
      latestState: cloneStoredState(stored),
      tail: Promise.resolve(),
      version: 0,
    };
    registerRefreshCoordinator(coordinatorKey, coordinator);
  }

  const requestedVersion = coordinator.version;
  const preceding = coordinator.tail;
  let release!: () => void;
  const currentTurn = new Promise<void>((resolve) => {
    release = resolve;
  });
  coordinator.tail = currentTurn;
  await preceding;

  try {
    coordinator.latestState = mergeStoredStates(coordinator.latestState, stored);
    if (coordinator.version > requestedVersion && coordinator.lastError) {
      throw coordinator.lastError;
    }
    const coordinatedExisting = readCachedAccessToken(
      coordinator.latestState.ownerBinding,
      binding.scope,
      secret,
      coordinator.latestState.scopeRotations?.[binding.scopeRotationKey] ?? 0
    );
    if (
      coordinatedExisting &&
      (!forceRefresh || coordinator.version > requestedVersion)
    ) {
      return {
        state: cloneStoredState(coordinator.latestState),
        accessToken: coordinatedExisting,
      };
    }

    const currentRefreshToken = coordinator.latestState.refreshToken ?? refreshToken;
    let refreshed: EntraTargetTokenResponse;
    try {
      refreshed = await refreshEntraTargetAccessToken(
        currentRefreshToken,
        binding.scope,
        settings,
        fetcher
      );
    } catch (error) {
      coordinator.lastError = error;
      coordinator.version++;
      throw error;
    }
    const accessTokenExpiresAt = Math.floor(Date.now() / 1000) + (refreshed.expires_in ?? 0);
    const nextRefreshToken = refreshed.refresh_token ?? currentRefreshToken;
    const nextRotationVersion = (coordinator.latestState.rotationVersion ?? 0) + 1;
    const accessToken: StoredEntraTargetAccessToken = {
      accessToken: refreshed.access_token ?? "",
      accessTokenExpiresAt,
      scopeRotationVersion: nextRotationVersion,
    };
    coordinator.latestState = {
      ownerBinding: coordinator.latestState.ownerBinding,
      refreshToken: nextRefreshToken,
      issuedAt: coordinator.latestState.issuedAt,
      rotationVersion: nextRotationVersion,
      scopeRotations: recordScopeRotation(
        coordinator.latestState.scopeRotations,
        binding.scopeRotationKey,
        nextRotationVersion
      ),
      snapshotId: crypto.randomUUID(),
    };
    storeCachedAccessToken(
      coordinator.latestState.ownerBinding,
      binding.scope,
      secret,
      accessToken
    );
    coordinator.lastError = undefined;
    coordinator.version++;
    registerRefreshCoordinator(
      createRefreshCoordinatorKey(
        nextRefreshToken,
        coordinator.latestState.ownerBinding,
        secret
      ),
      coordinator
    );
    return { state: cloneStoredState(coordinator.latestState), accessToken };
  } finally {
    release();
    if (coordinator.tail === currentTurn) {
      removeRefreshCoordinator(coordinator);
    }
  }
}

function createRefreshCoordinatorKey(
  refreshToken: string,
  ownerBinding: string,
  secret: string
) {
  return sign(
    `workable.admin.entra.refresh.v2\0${ownerBinding}\0${refreshToken}`,
    secret
  );
}

function registerRefreshCoordinator(key: string, coordinator: EntraRefreshCoordinator) {
  refreshCoordinators.set(key, coordinator);
  coordinator.keys.add(key);
  while (coordinator.keys.size > MAX_REFRESH_COORDINATOR_KEYS) {
    const oldest = coordinator.keys.values().next().value as string;
    coordinator.keys.delete(oldest);
    if (refreshCoordinators.get(oldest) === coordinator) {
      refreshCoordinators.delete(oldest);
    }
  }
}

function removeRefreshCoordinator(coordinator: EntraRefreshCoordinator) {
  for (const key of coordinator.keys) {
    if (refreshCoordinators.get(key) === coordinator) {
      refreshCoordinators.delete(key);
    }
  }
  coordinator.keys.clear();
}

function cloneStoredState(state: StoredEntraTargetTokenState): StoredEntraTargetTokenState {
  return {
    ownerBinding: state.ownerBinding,
    refreshToken: state.refreshToken,
    issuedAt: state.issuedAt,
    rotationVersion: state.rotationVersion,
    scopeRotations: state.scopeRotations ? { ...state.scopeRotations } : undefined,
    snapshotId: state.snapshotId,
  };
}

function mergeStoredStates(
  latest: StoredEntraTargetTokenState,
  incoming: StoredEntraTargetTokenState
): StoredEntraTargetTokenState {
  return {
    ownerBinding: latest.ownerBinding,
    refreshToken: latest.refreshToken ?? incoming.refreshToken,
    issuedAt: Math.max(latest.issuedAt ?? 0, incoming.issuedAt ?? 0),
    rotationVersion: Math.max(
      latest.rotationVersion ?? 0,
      incoming.rotationVersion ?? 0
    ),
    scopeRotations: mergeScopeRotations(
      latest.scopeRotations,
      incoming.scopeRotations
    ),
    snapshotId: latest.snapshotId,
  };
}

export function resetEntraRefreshCoordinatorsForTests() {
  refreshCoordinators.clear();
  accessTokenCache.clear();
  clearedTokenOwners.clear();
}

export function entraTargetAccessTokenCacheSizeForTests() {
  return accessTokenCache.size;
}

export function entraRefreshCoordinatorSizeForTests() {
  return refreshCoordinators.size;
}

export function clearEntraTargetTokenServerState(
  request: Request,
  env: AdminSecurityEnvironment = process.env
) {
  const settings = getAdminSecuritySettings(env);
  const session = readAdminSessionState(request.headers, settings);
  const secret = sessionSecret(settings);
  const identity = session.identity;
  const expectedOwnerBinding = identity?.provider === "entra" && secret
    ? createTargetTokenOwnerBinding(identity, settings, secret)
    : undefined;
  if (expectedOwnerBinding) {
    removeServerStateForOwner(expectedOwnerBinding);
  }
}

export async function createEntraTargetAccessTokenResponse(
  request: Request,
  env: AdminSecurityEnvironment = process.env,
  fetcher: FetchLike = fetch
) {
  const token = await getEntraTargetAccessToken(request, env, fetcher, {
    forceRefresh: request.headers.get("x-workable-force-token-refresh") === "true",
  });
  if (!token.ok) {
    return Response.json(
      { error: token.error },
      {
        status: token.status,
        headers: withCookies(noStoreHeaders, token.setCookieHeaders),
      }
    );
  }

  if (!token.accessToken) {
    return Response.json(
      { accessToken: null },
      {
        headers: withCookies(noStoreHeaders, token.setCookieHeaders),
      }
    );
  }

  return Response.json(
    {
      accessToken: token.accessToken,
      accessTokenExpiresInSeconds: token.accessTokenExpiresAt === undefined
        ? null
        : Math.max(0, token.accessTokenExpiresAt - Math.floor(Date.now() / 1000)),
    },
    {
      headers: withCookies(noStoreHeaders, token.setCookieHeaders),
    }
  );
}

export function validateEntraTargetTokenConfiguration(
  settings: AdminSecuritySettings
): AdminSecurityResult {
  if (settings.authProvider !== "entra") {
    return authenticatedIdentity("entra-target-not-required", "anonymous");
  }

  const issues = getTargetBindingConfigurationIssues(settings);
  if (issues.length > 0) {
    return serviceUnavailable(issues[0]);
  }

  return authenticatedIdentity("entra-target-configured", "anonymous");
}

function getTargetBindingConfigurationIssues(settings: AdminSecuritySettings) {
  const issues: string[] = [];
  for (const [index, binding] of settings.entraId.targetApis.entries()) {
    const hasUrl = Boolean(binding.apiUrl?.trim());
    const hasScope = Boolean(binding.scope?.trim());
    if (hasUrl !== hasScope) {
      issues.push(
        `Microsoft Entra ID target API entry ${index + 1} requires both apiUrl and scope.`
      );
      continue;
    }

    if (!hasUrl || !hasScope) {
      continue;
    }

    const normalized = normalizeBinding(binding.apiUrl, binding.scope);
    if (!normalized) {
      issues.push(
        `Microsoft Entra ID target API entry ${index + 1} must use a valid http or https apiUrl.`
      );
    } else if (settings.isProduction && new URL(normalized.apiUrl).protocol !== "https:") {
      issues.push(
        `Microsoft Entra ID target API entry ${index + 1} must use https in production.`
      );
    }
  }

  return issues;
}

function readStoredTargetTokenState(
  headers: Headers,
  settings: AdminSecuritySettings,
  expectedOwnerBinding?: string
): StoredEntraTargetTokenState | null {
  const secret = sessionSecret(settings);
  if (!secret) {
    return null;
  }

  const states: Array<StoredEntraTargetTokenState & { cookieName: string }> = [];
  for (const name of findTokenSnapshotCookieNames(headers, !settings.isProduction)) {
    const cookieValue = readChunkedCookie(headers, name);
    if (!cookieValue) continue;
    try {
      const decoded = JSON.parse(decrypt(cookieValue, secret, TOKEN_COOKIE_PURPOSE)) as unknown;
      if (!decoded || typeof decoded !== "object") {
        continue;
      }
      const parsed = decoded as Partial<PersistedEntraTargetTokenState>;
      const hasValidRotationVersion = Number.isSafeInteger(parsed.rotationVersion) &&
        (parsed.rotationVersion ?? -1) >= 0;
      const rotationVersion = hasValidRotationVersion ? parsed.rotationVersion ?? 0 : 0;
      if (typeof parsed.ownerBinding !== "string" ||
        (expectedOwnerBinding && !constantTimeEquals(parsed.ownerBinding, expectedOwnerBinding)) ||
        parsed.version !== TARGET_TOKEN_STATE_VERSION ||
        !hasValidRotationVersion ||
        !isValidRefreshToken(parsed.refreshToken) ||
        !isValidScopeRotations(parsed.scopeRotations, settings, rotationVersion)) {
        continue;
      }
      states.push({
        ownerBinding: parsed.ownerBinding,
        refreshToken: typeof parsed.refreshToken === "string" ? parsed.refreshToken : undefined,
        issuedAt: typeof parsed.issuedAt === "number" ? parsed.issuedAt : 0,
        rotationVersion,
        scopeRotations: parsed.scopeRotations,
        cookieName: name,
      });
    } catch {
      // Invalid snapshots are ignored and will be expired by the caller.
    }
  }
  if (states.length === 0) return null;
  states.sort((left, right) =>
    (left.rotationVersion ?? 0) - (right.rotationVersion ?? 0) ||
    (left.issuedAt ?? 0) - (right.issuedAt ?? 0));
  const newest = states.at(-1)!;
  return {
    ownerBinding: newest.ownerBinding,
    refreshToken: newest.refreshToken,
    issuedAt: newest.issuedAt,
    rotationVersion: newest.rotationVersion,
    scopeRotations: states.reduce<Record<string, number> | undefined>(
      (merged, state) => mergeScopeRotations(merged, state.scopeRotations),
      undefined
    ),
    sourceCookieNames: states.map((state) => state.cookieName),
  };
}

function createTargetTokenOwnerBinding(
  identity: AdminSessionIdentity,
  settings: AdminSecuritySettings,
  secret: string
) {
  return sign(JSON.stringify([
    "workable.admin.entra.target-token-owner.v2",
    identity.provider,
    identity.entraSubject ?? "",
    identity.sessionId ?? "",
    settings.entraId.tenantId ?? "",
    settings.entraId.clientId ?? "",
    settings.entraId.authorityHost,
    ...getEntraTargetTokenBindings(settings)
      .flatMap((binding) => [binding.apiUrl, binding.scope]),
  ]), secret);
}

function hasEntraTargetTokenCookie(headers: Headers, settings: AdminSecuritySettings) {
  return findTokenSnapshotCookieNames(headers, !settings.isProduction).length > 0;
}

async function refreshEntraTargetAccessToken(
  refreshToken: string,
  scope: string,
  settings: AdminSecuritySettings,
  fetcher: FetchLike,
  signal?: AbortSignal
) {
  const metadata = await fetchOpenIdConfiguration(settings, fetcher, signal);
  const tokenEndpoint = validateEntraBackchannelUrl(
    metadata.token_endpoint ??
      createAuthorityUrl(settings, "oauth2/v2.0/token").toString(),
    settings.entraId.authorityHost,
    "token endpoint"
  );
  const body = new URLSearchParams({
    client_id: settings.entraId.clientId ?? "",
    grant_type: "refresh_token",
    refresh_token: refreshToken,
    scope,
  });

  if (settings.entraId.clientSecret) {
    body.set("client_secret", settings.entraId.clientSecret);
  }

  const { response, value: tokens } = await fetchEntraJson<EntraTargetTokenResponse>(
    fetcher,
    tokenEndpoint,
    {
      method: "POST",
      headers: {
        "content-type": "application/x-www-form-urlencoded",
      },
      body,
    },
    signal
  );
  if (!response.ok || tokens.error || typeof tokens.access_token !== "string" ||
      tokens.access_token.length === 0 ||
      !isValidAccessTokenLifetime(tokens.expires_in) ||
      Buffer.byteLength(tokens.access_token) > MAX_ACCESS_TOKEN_BYTES ||
      !isValidRefreshToken(tokens.refresh_token)) {
    throw new Error("Microsoft Entra ID token refresh failed.");
  }

  return tokens;
}

async function fetchOpenIdConfiguration(
  settings: AdminSecuritySettings,
  fetcher: FetchLike,
  signal?: AbortSignal
): Promise<EntraOpenIdConfiguration> {
  const metadataUrl = validateEntraBackchannelUrl(
    createAuthorityUrl(
      settings,
      "v2.0/.well-known/openid-configuration"
    ).toString(),
    settings.entraId.authorityHost,
    "metadata endpoint"
  );
  return await fetchCachedEntraJson(
    fetcher,
    `metadata:${metadataUrl}`,
    metadataUrl,
    isOpenIdConfiguration,
    signal
  );
}

function isOpenIdConfiguration(value: unknown): value is EntraOpenIdConfiguration {
  return Boolean(
    value &&
      typeof value === "object" &&
      typeof (value as EntraOpenIdConfiguration).token_endpoint === "string"
  );
}

function getConfiguredTargetApiCandidates(settings: AdminSecuritySettings) {
  return settings.entraId.targetApis
    .map((binding) => ({
      apiUrl: binding.apiUrl,
      scope: binding.scope,
    }))
    .filter((binding) => Boolean(binding.apiUrl?.trim() && binding.scope?.trim()));
}

function normalizeBinding(apiUrl?: string, scope?: string) {
  const normalizedScope = scope?.trim();
  const normalizedApiUrl = apiUrl?.trim();
  if (!normalizedScope || !normalizedApiUrl) {
    return null;
  }

  try {
    const url = new URL(normalizedApiUrl);
    if (!["http:", "https:"].includes(url.protocol)) {
      return null;
    }

    const canonicalApiUrl = normalizeApiUrl(url);
    return {
      apiUrl: canonicalApiUrl,
      key: createBindingKey(canonicalApiUrl),
      scope: normalizedScope,
    };
  } catch {
    return null;
  }
}

function serializeStateCookie(
  state: StoredEntraTargetTokenState,
  request: Request,
  settings: AdminSecuritySettings,
  maximumAgeSeconds: number
) {
  const secret = sessionSecret(settings);
  const maxAgeSeconds = Math.min(
    settings.sessionMaxAgeSeconds,
    Math.floor(maximumAgeSeconds)
  );
  if (!secret || maxAgeSeconds <= 0) {
    return createExpiredEntraTargetTokenCookies(request.headers, settings.isProduction);
  }

  const cookieRoot = shouldSecureCookie(request, settings.isProduction)
    ? TOKEN_COOKIE_NAME
    : DEVELOPMENT_TOKEN_COOKIE_NAME;
  const cookieName = `${cookieRoot}.${state.snapshotId ?? crypto.randomUUID()}`;
  const persistedState: PersistedEntraTargetTokenState = {
    version: TARGET_TOKEN_STATE_VERSION,
    ownerBinding: state.ownerBinding,
    refreshToken: state.refreshToken,
    issuedAt: Date.now(),
    rotationVersion: state.rotationVersion ?? 0,
    scopeRotations: state.scopeRotations,
  };
  const payload = encrypt(JSON.stringify(persistedState), secret, TOKEN_COOKIE_PURPOSE);
  return serializeChunkedCookie(
    cookieName,
    payload,
    {
      maxAgeSeconds,
      secure: shouldSecureCookie(request, settings.isProduction),
    }
  ).concat((state.sourceCookieNames ?? []).flatMap((name) =>
    expireChunkedCookie(name, readChunkCount(request.headers, name))));
}

function remainingAbsoluteSessionSeconds(absoluteExpiresAt: number | undefined) {
  return (absoluteExpiresAt ?? 0) - Math.floor(Date.now() / 1000);
}

function shouldConsolidateState(
  state: StoredEntraTargetTokenState,
  request: Request,
  settings: AdminSecuritySettings
) {
  const names = state.sourceCookieNames ?? [];
  if (names.length !== 1) return true;
  const expectedRoot = shouldSecureCookie(request, settings.isProduction)
    ? TOKEN_COOKIE_NAME
    : DEVELOPMENT_TOKEN_COOKIE_NAME;
  return !names[0]?.startsWith(`${expectedRoot}.`);
}

function createAuthorityUrl(settings: AdminSecuritySettings, path: string) {
  const authority = new URL(settings.entraId.authorityHost);
  const normalizedPath = path.replace(/^\/+/, "");
  authority.pathname = `/${encodeURIComponent(settings.entraId.tenantId ?? "")}/${normalizedPath}`;
  authority.search = "";
  return authority;
}

function getRequestedApiUrl(request: Request, settings: AdminSecuritySettings) {
  const requestUrl = new URL(request.url);
  const candidate = requestUrl.searchParams.get("apiUrl")?.trim() ||
    request.headers.get("x-workable-api-url") ||
    settings.apiUrl;
  if (!candidate) {
    return null;
  }

  try {
    return new URL(candidate);
  } catch {
    return null;
  }
}

function normalizeApiUrl(url: URL) {
  const clone = new URL(url.toString());
  clone.hash = "";
  clone.search = "";
  clone.pathname = clone.pathname.replace(/\/+$/, "");
  return clone.toString();
}

function createBindingKey(apiUrl: string) {
  return sha256Base64Url(apiUrl);
}

function recordScopeRotation(
  existing: Record<string, number> | undefined,
  key: string,
  rotationVersion: number
) {
  return {
    ...existing,
    [key]: rotationVersion,
  };
}

function mergeScopeRotations(
  latest: Record<string, number> | undefined,
  incoming: Record<string, number> | undefined
) {
  const merged = { ...incoming };
  for (const [key, value] of Object.entries(latest ?? {})) {
    merged[key] = Math.max(merged[key] ?? 0, value);
  }
  return Object.keys(merged).length > 0 ? merged : undefined;
}

function isValidScopeRotations(
  value: unknown,
  settings: AdminSecuritySettings,
  maximumRotationVersion: number
): value is Record<string, number> | undefined {
  if (value === undefined) return true;
  if (!value || typeof value !== "object" || Array.isArray(value)) return false;
  const entries = Object.entries(value);
  const scopeCount = new Set(
    getEntraTargetTokenBindings(settings).map((binding) => binding.scope)
  ).size;
  if (entries.length > Math.min(scopeCount, MAX_ACCESS_TOKEN_CACHE_ENTRIES)) return false;
  return entries.every(([key, rotationVersion]) => {
    const index = Number.parseInt(key, 36);
    return Number.isSafeInteger(index) && index >= 0 && index < scopeCount &&
      index.toString(36) === key && Number.isSafeInteger(rotationVersion) &&
      rotationVersion >= 0 && rotationVersion <= maximumRotationVersion;
  });
}

function serializeChunkedCookie(
  name: string,
  value: string,
  options: {
    maxAgeSeconds: number;
    secure: boolean;
  }
) {
  const chunks = splitIntoChunks(value, TOKEN_COOKIE_CHUNK_SIZE);
  if (chunks.length > MAX_TOKEN_COOKIE_CHUNKS) {
    throw new Error("Microsoft Entra ID token state exceeds the supported cookie size.");
  }

  const headers = [
    serializeCookie(`${name}.parts`, String(chunks.length), options),
    ...chunks.map((chunk, index) => serializeCookie(`${name}.${index}`, chunk, options)),
  ];
  const requestBytes = headers.reduce((total, header) =>
    total + Buffer.byteLength(header.split(";", 1)[0]!) + 2, 0);
  if (requestBytes > MAX_TOKEN_COOKIE_SNAPSHOT_BYTES) {
    throw new Error("Microsoft Entra ID refresh state exceeds the supported cookie budget.");
  }

  return headers;
}

function readChunkedCookie(headers: Headers, name: string) {
  const count = readChunkCount(headers, name);
  if (count === 0) {
    return null;
  }

  const chunks: string[] = [];
  for (let index = 0; index < count; index += 1) {
    const chunkCookie = readUniqueCookie(headers.get("cookie"), `${name}.${index}`);
    if (!chunkCookie.ok || !chunkCookie.value) {
      return null;
    }

    chunks.push(chunkCookie.value);
  }

  return chunks.join("");
}

function readChunkCount(headers: Headers, name: string) {
  const countCookie = readUniqueCookie(headers.get("cookie"), `${name}.parts`);
  if (!countCookie.ok) return 0;
  const count = Number.parseInt(countCookie.value ?? "", 10);
  return Number.isFinite(count) && count > 0 && count <= MAX_TOKEN_COOKIE_CHUNKS
    ? count
    : 0;
}

function findTokenSnapshotCookieNames(headers?: Headers, includeDevelopmentSnapshots = false) {
  if (!headers) return [];
  const secureGeneratedNames = new Set<string>();
  const developmentGeneratedNames = new Set<string>();
  const fixedNames = new Set<string>();
  const secureGeneratedName = /^__Host-workable_admin_entra_target_token\.[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/i;
  const developmentGeneratedName = /^workable_admin_entra_target_token\.[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/i;
  for (const pair of headers.get("cookie")?.split(";") ?? []) {
    const separator = pair.indexOf("=");
    if (separator <= 0) continue;
    const rawName = pair.slice(0, separator).trim();
    if (!rawName.endsWith(".parts")) continue;
    const name = rawName.slice(0, -".parts".length);
    if (secureGeneratedName.test(name)) secureGeneratedNames.add(name);
    else if (includeDevelopmentSnapshots && developmentGeneratedName.test(name)) {
      developmentGeneratedNames.add(name);
    } else if (name === TOKEN_COOKIE_NAME ||
      name === DEVELOPMENT_TOKEN_COOKIE_NAME || name === LEGACY_TOKEN_COOKIE_NAME) {
      fixedNames.add(name);
    }
  }
  return [
    ...secureGeneratedNames,
    ...developmentGeneratedNames,
    ...fixedNames,
  ].slice(0, MAX_TOKEN_COOKIE_SNAPSHOTS);
}

function expireChunkedCookie(name: string, chunkCount = MAX_TOKEN_COOKIE_CHUNKS) {
  return [
    serializeExpiredCookie(`${name}.parts`),
    ...Array.from({ length: chunkCount }, (_, index) =>
      serializeExpiredCookie(`${name}.${index}`)
    ),
  ];
}

function splitIntoChunks(value: string, chunkSize: number) {
  const chunks: string[] = [];
  for (let offset = 0; offset < value.length; offset += chunkSize) {
    chunks.push(value.slice(offset, offset + chunkSize));
  }

  return chunks;
}

function isExpired(expiresAt: number) {
  return expiresAt <= Math.floor(Date.now() / 1000) + TOKEN_REFRESH_SKEW_SECONDS;
}

function isValidAccessTokenLifetime(value: unknown): value is number {
  return Number.isSafeInteger(value) &&
    (value as number) > 0 &&
    (value as number) <= MAX_ACCESS_TOKEN_LIFETIME_SECONDS;
}

function isValidRefreshToken(value: unknown): value is string | undefined {
  return value === undefined ||
    (typeof value === "string" && value.length > 0 &&
      Buffer.byteLength(value) <= MAX_REFRESH_TOKEN_BYTES);
}

function createAccessTokenCacheKey(
  ownerBinding: string,
  scope: string,
  secret: string
) {
  return sign(`workable.admin.entra.access-cache.v1\0${ownerBinding}\0${scope}`, secret);
}

function readCachedAccessToken(
  ownerBinding: string,
  scope: string,
  secret: string,
  minimumRotationVersion: number
) {
  const key = createAccessTokenCacheKey(ownerBinding, scope, secret);
  const cached = accessTokenCache.get(key);
  if (!cached) return undefined;
  if (cached.scopeRotationVersion < minimumRotationVersion ||
      isExpired(cached.accessTokenExpiresAt)) {
    accessTokenCache.delete(key);
    return undefined;
  }

  accessTokenCache.delete(key);
  accessTokenCache.set(key, cached);
  return cached;
}

function storeCachedAccessToken(
  ownerBinding: string,
  scope: string,
  secret: string,
  token: StoredEntraTargetAccessToken
) {
  if (!token.accessToken || Buffer.byteLength(token.accessToken) > MAX_ACCESS_TOKEN_BYTES ||
      isExpired(token.accessTokenExpiresAt) ||
      clearedTokenOwners.has(ownerBinding)) return;
  const key = createAccessTokenCacheKey(ownerBinding, scope, secret);
  accessTokenCache.delete(key);
  while (accessTokenCache.size >= MAX_ACCESS_TOKEN_CACHE_ENTRIES) {
    const oldest = accessTokenCache.keys().next().value as string;
    accessTokenCache.delete(oldest);
  }
  accessTokenCache.set(key, { ...token, ownerBinding });
}

function removeServerStateForOwner(ownerBinding: string) {
  clearedTokenOwners.delete(ownerBinding);
  while (clearedTokenOwners.size >= MAX_CLEARED_TOKEN_OWNERS) {
    const oldest = clearedTokenOwners.keys().next().value as string;
    clearedTokenOwners.delete(oldest);
  }
  clearedTokenOwners.set(ownerBinding, true);
  for (const [key, cached] of accessTokenCache) {
    if (constantTimeEquals(cached.ownerBinding, ownerBinding)) {
      accessTokenCache.delete(key);
    }
  }
  for (const coordinator of new Set(refreshCoordinators.values())) {
    if (constantTimeEquals(coordinator.latestState.ownerBinding, ownerBinding)) {
      removeRefreshCoordinator(coordinator);
    }
  }
}

function withCookies(
  headers: Record<string, string>,
  cookies: readonly string[]
) {
  const responseHeaders = new Headers(headers);
  for (const cookie of cookies) {
    responseHeaders.append("set-cookie", cookie);
  }

  return responseHeaders;
}
