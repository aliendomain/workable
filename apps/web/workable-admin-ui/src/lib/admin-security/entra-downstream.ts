import type { AdminSecurityEnvironment, AdminSecurityFailure } from "./types.ts";
import {
  authenticatedIdentity,
  securityFailure,
  serviceUnavailable,
  type AdminSecurityResult,
} from "./types.ts";
import { getAdminSecuritySettings, type AdminSecuritySettings } from "./config.ts";
import { decrypt, encrypt, sha256Base64Url } from "./crypto.ts";
import {
  parseCookieHeader,
  serializeCookie,
  serializeExpiredCookie,
  shouldSecureCookie,
} from "./cookies.ts";
import { sessionSecret } from "./session.ts";

const TOKEN_COOKIE_NAME = "workable_admin_entra_target_token";
const LEGACY_TOKEN_COOKIE_NAME = "workable_admin_entra_target_token_legacy";
const TOKEN_COOKIE_PURPOSE = "entra-target-token";
const TOKEN_COOKIE_CHUNK_SIZE = 3000;
const MAX_TOKEN_COOKIE_CHUNKS = 16;
const TOKEN_REFRESH_SKEW_SECONDS = 60;
const noStoreHeaders = {
  "cache-control": "no-store",
  "x-content-type-options": "nosniff",
};

type FetchLike = typeof fetch;

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
  refreshToken?: string;
  bindings: Record<string, StoredEntraTargetAccessToken>;
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

export function createExpiredEntraTargetTokenCookies() {
  return expireChunkedCookie(TOKEN_COOKIE_NAME).concat(
    expireChunkedCookie(LEGACY_TOKEN_COOKIE_NAME)
  );
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
  settings: AdminSecuritySettings
) {
  const secret = sessionSecret(settings);
  if (!secret) {
    return createExpiredEntraTargetTokenCookies();
  }

  const bindings = getEntraTargetTokenBindings(settings);
  const state: StoredEntraTargetTokenState = {
    bindings: {},
    refreshToken: tokens.refresh_token,
  };

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
    return serializeStateCookie(state, request, settings);
  } catch {
    return createExpiredEntraTargetTokenCookies();
  }
}

export async function getEntraTargetAccessToken(
  request: Request,
  env: AdminSecurityEnvironment = process.env,
  fetcher: FetchLike = fetch,
  options: EntraTargetAccessTokenOptions = {}
): Promise<EntraTargetAccessTokenSuccess | EntraTargetAccessTokenFailure> {
  const settings = getAdminSecuritySettings(env);
  const binding = findEntraTargetTokenBinding(
    settings,
    options.requestedApiUrl ?? getRequestedApiUrl(request, settings)
  );
  if (!binding) {
    return { ok: true, setCookieHeaders: [] };
  }

  const stored = readStoredTargetTokenState(request.headers, settings);
  if (!stored) {
    return {
      ...securityFailure(
        401,
        "Microsoft Entra ID access to the hosted Workable API is not available. Sign in again."
      ),
      setCookieHeaders: createExpiredEntraTargetTokenCookies(),
    };
  }

  const existing = stored.bindings[binding.key];
  if (existing && !options.forceRefresh && !isExpired(existing.accessTokenExpiresAt)) {
    return {
      ok: true,
      accessToken: existing.accessToken,
      accessTokenExpiresAt: existing.accessTokenExpiresAt,
      setCookieHeaders: [],
    };
  }

  if (!stored.refreshToken) {
    return {
      ...securityFailure(
        401,
        "Microsoft Entra ID access to the hosted Workable API has expired. Sign in again."
      ),
      setCookieHeaders: createExpiredEntraTargetTokenCookies(),
    };
  }

  try {
    const refreshed = await refreshEntraTargetAccessToken(
      stored.refreshToken,
      binding.scope,
      settings,
      fetcher
    );
    const accessTokenExpiresAt = Math.floor(Date.now() / 1000) + (refreshed.expires_in ?? 0);
    const nextState: StoredEntraTargetTokenState = {
      refreshToken: refreshed.refresh_token ?? stored.refreshToken,
      bindings: {
        ...stored.bindings,
        [binding.key]: {
          accessToken: refreshed.access_token ?? "",
          accessTokenExpiresAt,
          apiUrl: binding.apiUrl,
          scope: binding.scope,
          tokenType: refreshed.token_type ?? existing?.tokenType ?? "Bearer",
        },
      },
    };

    return {
      ok: true,
      accessToken: nextState.bindings[binding.key]?.accessToken,
      accessTokenExpiresAt,
      setCookieHeaders: serializeStateCookie(nextState, request, settings),
    };
  } catch {
    return {
      ...securityFailure(
        401,
        "Microsoft Entra ID access to the hosted Workable API could not be refreshed. Sign in again."
      ),
      setCookieHeaders: createExpiredEntraTargetTokenCookies(),
    };
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

    if (!normalizeBinding(binding.apiUrl, binding.scope)) {
      issues.push(
        `Microsoft Entra ID target API entry ${index + 1} must use a valid http or https apiUrl.`
      );
    }
  }

  return issues;
}

function readStoredTargetTokenState(
  headers: Headers,
  settings: AdminSecuritySettings
): StoredEntraTargetTokenState | null {
  const secret = sessionSecret(settings);
  if (!secret) {
    return null;
  }

  const cookieValue = readChunkedCookie(headers, TOKEN_COOKIE_NAME) ??
    readChunkedCookie(headers, LEGACY_TOKEN_COOKIE_NAME);
  if (!cookieValue) {
    return null;
  }

  try {
    const parsed = JSON.parse(
      decrypt(cookieValue, secret, TOKEN_COOKIE_PURPOSE)
    ) as StoredEntraTargetTokenState;
    if (!parsed || typeof parsed !== "object" || typeof parsed.bindings !== "object") {
      return null;
    }

    return {
      refreshToken: typeof parsed.refreshToken === "string"
        ? parsed.refreshToken
        : undefined,
      bindings: Object.fromEntries(
        Object.entries(parsed.bindings)
          .filter(([, value]) => isStoredBinding(value))
      ),
    };
  } catch {
    return null;
  }
}

async function refreshEntraTargetAccessToken(
  refreshToken: string,
  scope: string,
  settings: AdminSecuritySettings,
  fetcher: FetchLike
) {
  const metadata = await fetchOpenIdConfiguration(settings, fetcher);
  const tokenEndpoint = metadata.token_endpoint ??
    createAuthorityUrl(settings, "oauth2/v2.0/token").toString();
  const body = new URLSearchParams({
    client_id: settings.entraId.clientId ?? "",
    grant_type: "refresh_token",
    refresh_token: refreshToken,
    scope,
  });

  if (settings.entraId.clientSecret) {
    body.set("client_secret", settings.entraId.clientSecret);
  }

  const response = await fetcher(tokenEndpoint, {
    method: "POST",
    headers: {
      "content-type": "application/x-www-form-urlencoded",
    },
    body,
    cache: "no-store",
  });
  const tokens = await response.json() as EntraTargetTokenResponse;
  if (!response.ok || tokens.error || !tokens.access_token || !tokens.expires_in) {
    throw new Error("Microsoft Entra ID token refresh failed.");
  }

  return tokens;
}

async function fetchOpenIdConfiguration(
  settings: AdminSecuritySettings,
  fetcher: FetchLike
): Promise<EntraOpenIdConfiguration> {
  const metadataUrl = createAuthorityUrl(
    settings,
    "v2.0/.well-known/openid-configuration"
  );
  const response = await fetcher(metadataUrl, { cache: "no-store" });
  if (!response.ok) {
    throw new Error("Unable to load Microsoft Entra ID metadata.");
  }

  return await response.json() as EntraOpenIdConfiguration;
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
  settings: AdminSecuritySettings
) {
  const secret = sessionSecret(settings);
  if (!secret) {
    return createExpiredEntraTargetTokenCookies();
  }

  const payload = encrypt(JSON.stringify(state), secret, TOKEN_COOKIE_PURPOSE);
  return serializeChunkedCookie(
    TOKEN_COOKIE_NAME,
    payload,
    {
      maxAgeSeconds: settings.sessionMaxAgeSeconds,
      secure: shouldSecureCookie(request, settings.isProduction),
    }
  );
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
  const cookies = parseCookieHeader(headers.get("cookie"));
  const countValue = cookies.get(`${name}.parts`);
  const count = Number.parseInt(countValue ?? "", 10);
  if (!Number.isFinite(count) || count <= 0 || count > MAX_TOKEN_COOKIE_CHUNKS) {
    return null;
  }

  const chunks: string[] = [];
  for (let index = 0; index < count; index += 1) {
    const chunk = cookies.get(`${name}.${index}`);
    if (!chunk) {
      return null;
    }

    chunks.push(chunk);
  }

  return chunks.join("");
}

function expireChunkedCookie(name: string) {
  return [
    serializeExpiredCookie(`${name}.parts`),
    ...Array.from({ length: MAX_TOKEN_COOKIE_CHUNKS }, (_, index) =>
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
