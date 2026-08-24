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
  apiUrl: string;
  scope: string;
  tokenType: string;
};

type StoredEntraTargetTokenState = {
  ownerBinding: string;
  refreshToken?: string;
  bindings: Record<string, StoredEntraTargetAccessToken>;
  issuedAt?: number;
  sourceCookieNames?: string[];
};

type EntraOpenIdConfiguration = {
  token_endpoint?: string;
};

type EntraTargetTokenBinding = {
  apiUrl: string;
  key: string;
  scope: string;
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
  lastError?: unknown;
  latestState: StoredEntraTargetTokenState;
  tail: Promise<void>;
  version: number;
};

const refreshCoordinators = new Map<string, EntraRefreshCoordinator>();

export function getEntraTargetTokenBindings(settings: AdminSecuritySettings) {
  const bindings: EntraTargetTokenBinding[] = [];
  const seen = new Set<string>();

  for (const candidate of getConfiguredTargetApiCandidates(settings)) {
    const normalized = normalizeBinding(candidate.apiUrl, candidate.scope);
    if (!normalized || seen.has(normalized.key)) {
      continue;
    }

    seen.add(normalized.key);
    bindings.push(normalized);
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

  const bindings = getEntraTargetTokenBindings(settings);
  const state: StoredEntraTargetTokenState = {
    ownerBinding: createTargetTokenOwnerBinding(identity, settings, secret),
    bindings: {},
    refreshToken: tokens.refresh_token,
  };
  if (!identity.sessionId?.trim()) {
    return createExpiredEntraTargetTokenCookies(request.headers, settings.isProduction);
  }

  if (
    bindings.length === 1 &&
    tokens.access_token &&
    tokens.expires_in &&
    tokens.expires_in > 0
  ) {
    const binding = bindings[0];
    state.bindings[binding.key] = {
      accessToken: tokens.access_token,
      accessTokenExpiresAt: Math.floor(Date.now() / 1000) + tokens.expires_in,
      apiUrl: binding.apiUrl,
      scope: binding.scope,
      tokenType: tokens.token_type ?? "Bearer",
    };
  }

  try {
    return serializeStateCookie(
      state,
      request,
      settings,
      Math.min(
        settings.sessionMaxAgeSeconds,
        settings.sessionAbsoluteMaxAgeSeconds
      )
    );
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

  const existing = stored.bindings[binding.key];
  if (existing && !options.forceRefresh && !isExpired(existing.accessTokenExpiresAt)) {
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
      accessToken: coordinated.state.bindings[binding.key]?.accessToken,
      accessTokenExpiresAt: coordinated.state.bindings[binding.key]?.accessTokenExpiresAt,
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
    const coordinatedExisting = coordinator.latestState.bindings[binding.key];
    if (
      coordinatedExisting &&
      !isExpired(coordinatedExisting.accessTokenExpiresAt) &&
      (!forceRefresh || coordinator.version > requestedVersion)
    ) {
      return { state: cloneStoredState(coordinator.latestState) };
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
    coordinator.latestState = {
      ownerBinding: coordinator.latestState.ownerBinding,
      refreshToken: nextRefreshToken,
      issuedAt: coordinator.latestState.issuedAt,
      bindings: {
        ...coordinator.latestState.bindings,
        [binding.key]: {
          accessToken: refreshed.access_token ?? "",
          accessTokenExpiresAt,
          apiUrl: binding.apiUrl,
          scope: binding.scope,
          tokenType: refreshed.token_type ?? coordinatedExisting?.tokenType ?? "Bearer",
        },
      },
    };
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
    return { state: cloneStoredState(coordinator.latestState) };
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
}

function removeRefreshCoordinator(coordinator: EntraRefreshCoordinator) {
  for (const [key, candidate] of refreshCoordinators) {
    if (candidate === coordinator) {
      refreshCoordinators.delete(key);
    }
  }
}

function cloneStoredState(state: StoredEntraTargetTokenState): StoredEntraTargetTokenState {
  return {
    ownerBinding: state.ownerBinding,
    refreshToken: state.refreshToken,
    bindings: { ...state.bindings },
    issuedAt: state.issuedAt,
  };
}

function mergeStoredStates(
  latest: StoredEntraTargetTokenState,
  incoming: StoredEntraTargetTokenState
): StoredEntraTargetTokenState {
  const bindings = { ...incoming.bindings };
  for (const [key, value] of Object.entries(latest.bindings)) {
    // The coordinator's state is authoritative for bindings it has already
    // refreshed. A concurrent request can carry an older token with a later
    // advertised expiry even though that token was rejected downstream.
    bindings[key] = value;
  }
  return {
    ownerBinding: latest.ownerBinding,
    refreshToken: latest.refreshToken ?? incoming.refreshToken,
    bindings,
    issuedAt: Math.max(latest.issuedAt ?? 0, incoming.issuedAt ?? 0),
  };
}

export function resetEntraRefreshCoordinatorsForTests() {
  refreshCoordinators.clear();
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
      const parsed = JSON.parse(decrypt(cookieValue, secret, TOKEN_COOKIE_PURPOSE)) as StoredEntraTargetTokenState;
      if (!parsed || typeof parsed !== "object" ||
        typeof parsed.ownerBinding !== "string" ||
        (expectedOwnerBinding && !constantTimeEquals(parsed.ownerBinding, expectedOwnerBinding)) ||
        typeof parsed.bindings !== "object" || parsed.bindings === null) {
        continue;
      }
      states.push({
        ownerBinding: parsed.ownerBinding,
        refreshToken: typeof parsed.refreshToken === "string" ? parsed.refreshToken : undefined,
        bindings: Object.fromEntries(Object.entries(parsed.bindings).filter(([, value]) => isStoredBinding(value))),
        issuedAt: typeof parsed.issuedAt === "number" ? parsed.issuedAt : 0,
        cookieName: name,
      });
    } catch {
      // Invalid snapshots are ignored and will be expired by the caller.
    }
  }
  if (states.length === 0) return null;
  states.sort((left, right) => (left.issuedAt ?? 0) - (right.issuedAt ?? 0));
  const newest = states.at(-1)!;
  return {
    ownerBinding: newest.ownerBinding,
    refreshToken: newest.refreshToken,
    bindings: Object.assign({}, ...states.map((state) => state.bindings)),
    issuedAt: newest.issuedAt,
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
  if (!response.ok || tokens.error || !tokens.access_token || !tokens.expires_in) {
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
    } satisfies EntraTargetTokenBinding;
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
  const cookieName = `${cookieRoot}.${crypto.randomUUID()}`;
  const persistedState: StoredEntraTargetTokenState = {
    ownerBinding: state.ownerBinding,
    refreshToken: state.refreshToken,
    bindings: state.bindings,
    issuedAt: Date.now(),
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

function isStoredBinding(value: unknown): value is StoredEntraTargetAccessToken {
  if (!value || typeof value !== "object") {
    return false;
  }

  const binding = value as Partial<StoredEntraTargetAccessToken>;
  return typeof binding.accessToken === "string" &&
    typeof binding.accessTokenExpiresAt === "number" &&
    typeof binding.apiUrl === "string" &&
    typeof binding.scope === "string" &&
    typeof binding.tokenType === "string";
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

  return [
    serializeCookie(`${name}.parts`, String(chunks.length), options),
    ...chunks.map((chunk, index) => serializeCookie(`${name}.${index}`, chunk, options)),
  ];
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
