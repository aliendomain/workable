import {
  createPublicKey,
  verify,
  type JsonWebKey as NodeJsonWebKey,
} from "node:crypto";
import type { AdminSecurityEnvironment } from "./types.ts";
import {
  authenticatedIdentity,
  securityFailure,
  serviceUnavailable,
  type AdminSecurityResult,
} from "./types.ts";
import {
  getAdminSecuritySettings,
  type AdminSecuritySettings,
} from "./config.ts";
import {
  base64UrlDecode,
  constantTimeEquals,
  randomBase64Url,
  sign,
  sha256Base64Url,
} from "./crypto.ts";
import {
  readUniqueCookie,
  serializeCookie,
  serializeExpiredCookie,
  shouldSecureCookie,
} from "./cookies.ts";
import {
  createSignedAdminSessionCookie,
  readAdminLogoutGeneration,
  sessionSecret,
} from "./session.ts";
import { normalizeAdminReturnPath } from "./return-path.ts";
import {
  createEntraTargetTokenCookieHeaders,
  createInteractiveEntraScopes,
} from "./entra-downstream.ts";
import {
  fetchCachedEntraJson,
  fetchEntraJson,
  refreshCachedEntraJson,
  validateEntraBackchannelUrl,
  type EntraFetchLike,
} from "./entra-backchannel.ts";

const STATE_COOKIE_NAME = "workable_admin_entra_state";
const NONCE_COOKIE_NAME = "workable_admin_entra_nonce";
const VERIFIER_COOKIE_NAME = "workable_admin_entra_verifier";
const NEXT_COOKIE_NAME = "workable_admin_entra_next";
const HOST_STATE_COOKIE_NAME = "__Host-workable_admin_entra_state";
const HOST_NONCE_COOKIE_NAME = "__Host-workable_admin_entra_nonce";
const HOST_VERIFIER_COOKIE_NAME = "__Host-workable_admin_entra_verifier";
const HOST_NEXT_COOKIE_NAME = "__Host-workable_admin_entra_next";
const OAUTH_COOKIE_MAX_AGE_SECONDS = 10 * 60;
const noStoreHeaders = {
  "cache-control": "no-store",
  "x-content-type-options": "nosniff",
};

type FetchLike = EntraFetchLike;

type EntraTokenResponse = {
  access_token?: string;
  expires_in?: number;
  id_token?: string;
  refresh_token?: string;
  scope?: string;
  token_type?: string;
  error?: string;
  error_description?: string;
};

type EntraOpenIdConfiguration = {
  issuer?: string;
  jwks_uri?: string;
  token_endpoint?: string;
};

type JsonWebKeySet = {
  keys?: EntraJsonWebKey[];
};

type EntraJsonWebKey = NodeJsonWebKey & {
  kid?: string;
};

type JwtHeader = {
  alg?: string;
  kid?: string;
};

type EntraIdTokenClaims = {
  aud?: string | string[];
  exp?: number;
  iat?: number;
  iss?: string;
  nbf?: number;
  nonce?: string;
  oid?: string;
  preferred_username?: string;
  email?: string;
  name?: string;
  sub?: string;
  tid?: string;
};

export function getAdminAuthProvider(
  env: AdminSecurityEnvironment = process.env
) {
  return getAdminSecuritySettings(env).authProvider;
}

export function createEntraAuthorizationResponse(
  request: Request,
  env: AdminSecurityEnvironment = process.env
) {
  const settings = getAdminSecuritySettings(env);
  const validation = validateEntraSettings(settings, request);
  if (!validation.ok) {
    return Response.json(
      { error: validation.error },
      { status: validation.status, headers: noStoreHeaders }
    );
  }

  const state = randomBase64Url();
  const nonce = randomBase64Url();
  const verifier = randomBase64Url(64);
  const authorizationUrl = createAuthorizationUrl(settings, request, {
    state,
    nonce,
    verifier,
  });
  const response = createRedirectResponse(authorizationUrl);
  const secure = shouldSecureCookie(request, settings.isProduction);
  const cookieNames = getOAuthCookieNames(secure);
  const nextPath = normalizeAdminReturnPath(
    new URL(request.url).searchParams.get("next")
  );
  const logoutGeneration = readAdminLogoutGeneration(request.headers, settings);
  if (logoutGeneration === null) {
    return Response.json(
      { error: "Workable admin UI logout state is invalid. Sign in again." },
      { status: 401, headers: noStoreHeaders }
    );
  }

  for (const cookie of [
    serializeCookie(cookieNames.state, createSignedStateCookieValue(
      state,
      logoutGeneration,
      settings
    ), {
      maxAgeSeconds: OAUTH_COOKIE_MAX_AGE_SECONDS,
      secure,
    }),
    serializeCookie(cookieNames.nonce, nonce, {
      maxAgeSeconds: OAUTH_COOKIE_MAX_AGE_SECONDS,
      secure,
    }),
    serializeCookie(cookieNames.verifier, verifier, {
      maxAgeSeconds: OAUTH_COOKIE_MAX_AGE_SECONDS,
      secure,
    }),
    serializeCookie(cookieNames.next, nextPath, {
      maxAgeSeconds: OAUTH_COOKIE_MAX_AGE_SECONDS,
      secure,
    }),
  ]) {
    response.headers.append("set-cookie", cookie);
  }

  return response;
}

export async function completeEntraLogin(
  request: Request,
  env: AdminSecurityEnvironment = process.env,
  fetcher: FetchLike = fetch
) {
  const settings = getAdminSecuritySettings(env);
  const validation = validateEntraSettings(settings, request);
  if (!validation.ok) {
    return createFailedEntraCallbackResponse(request, validation.error);
  }

  const requestUrl = new URL(request.url);
  if (requestUrl.searchParams.has("error")) {
    return createFailedEntraCallbackResponse(
      request,
      "Microsoft Entra ID sign-in was not completed."
    );
  }

  const cookieNames = getOAuthCookieNames(
    shouldSecureCookie(request, settings.isProduction)
  );
  const cookieHeader = request.headers.get("cookie");
  const expectedStateCookie = readUniqueCookie(cookieHeader, cookieNames.state);
  const verifierCookie = readUniqueCookie(cookieHeader, cookieNames.verifier);
  const nonceCookie = readUniqueCookie(cookieHeader, cookieNames.nonce);
  const nextCookie = readUniqueCookie(cookieHeader, cookieNames.next);
  const state = requestUrl.searchParams.get("state") ?? "";
  const expectedState = expectedStateCookie.ok ? expectedStateCookie.value : "";
  const code = requestUrl.searchParams.get("code") ?? "";
  const verifier = verifierCookie.ok ? verifierCookie.value : "";
  const nonce = nonceCookie.ok ? nonceCookie.value : "";

  const transaction = state
    ? readSignedStateCookie(state, expectedState, settings)
    : null;
  if (transaction === null) {
    return createFailedEntraCallbackResponse(
      request,
      "Microsoft Entra ID sign-in state is not valid."
    );
  }

  const currentLogoutGeneration = readAdminLogoutGeneration(request.headers, settings);
  if (currentLogoutGeneration === null || !constantTimeEquals(
    transaction.logoutGeneration,
    currentLogoutGeneration
  )) {
    return createFailedEntraCallbackResponse(
      request,
      "Microsoft Entra ID sign-in was invalidated by logout."
    );
  }

  if (!code || !verifier || !nonce) {
    return createFailedEntraCallbackResponse(
      request,
      "Microsoft Entra ID sign-in response is incomplete."
    );
  }

  try {
    const openIdConfiguration = await fetchOpenIdConfiguration(
      settings,
      fetcher,
      request.signal
    );
    const tokens = await exchangeAuthorizationCode(
      settings,
      request,
      code,
      verifier,
      openIdConfiguration,
      fetcher,
      request.signal
    );
    const claims = await validateEntraIdToken(
      tokens.id_token ?? "",
      nonce,
      settings,
      openIdConfiguration,
      fetcher,
      request.signal
    );
    const identity = createEntraIdentity(claims, settings);
    if (!identity.ok) {
      return createFailedEntraCallbackResponse(request, identity.error);
    }

    const sessionIdentity = {
      name: identity.identity.name,
      provider: "entra" as const,
      email: identity.identity.email,
      entraSubject: identity.identity.entraSubject,
      sessionStartedAt: transaction.startedAt,
      logoutGeneration: transaction.logoutGeneration,
    };
    const sessionCookie = createSignedAdminSessionCookie(
      sessionIdentity,
      request,
      settings
    );
    if (!sessionCookie.ok) {
      return createFailedEntraCallbackResponse(
        request,
        sessionCookie.error
      );
    }

    const nextPath = normalizeAdminReturnPath(nextCookie.ok ? nextCookie.value : undefined);
    const response = createRedirectResponse(new URL(nextPath, request.url), 303);
    response.headers.append("set-cookie", sessionCookie.header);
    for (const cookie of createEntraTargetTokenCookieHeaders(
      tokens,
      request,
      settings,
      sessionCookie.identity
    )) {
      response.headers.append("set-cookie", cookie);
    }
    appendExpiredEntraCookies(response);
    return response;
  } catch (error) {
    logEntraCallbackFailure(error);
    return createFailedEntraCallbackResponse(
      request,
      formatEntraCallbackError(error, settings)
    );
  }
}

function createSignedStateCookieValue(
  state: string,
  logoutGeneration: string,
  settings: AdminSecuritySettings
) {
  const secret = sessionSecret(settings);
  if (!secret) {
    throw new Error("Microsoft Entra ID authentication requires session signing.");
  }
  const startedAt = Date.now();
  const value = `${state}.${startedAt}.${logoutGeneration}`;
  return `${value}.${sign(value, secret)}`;
}

function readSignedStateCookie(
  state: string,
  cookieValue: string,
  settings: AdminSecuritySettings
) {
  const secret = sessionSecret(settings);
  const separator = cookieValue.lastIndexOf(".");
  if (!secret || separator < 1) {
    return null;
  }

  const value = cookieValue.slice(0, separator);
  const signature = cookieValue.slice(separator + 1);
  const logoutGenerationSeparator = value.lastIndexOf(".");
  const startedAtSeparator = value.lastIndexOf(".", logoutGenerationSeparator - 1);
  if (!signature || startedAtSeparator < 1 || logoutGenerationSeparator < 1 ||
    !constantTimeEquals(signature, sign(value, secret))) return null;
  const cookieState = value.slice(0, startedAtSeparator);
  const startedAt = Number(value.slice(startedAtSeparator + 1, logoutGenerationSeparator));
  const logoutGeneration = value.slice(logoutGenerationSeparator + 1);
  const now = Date.now();
  return constantTimeEquals(state, cookieState) &&
      Number.isSafeInteger(startedAt) &&
      (logoutGeneration === "initial" ||
        /^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/i.test(logoutGeneration)) &&
      startedAt <= now + 5 * 60 * 1000 &&
      startedAt > now - OAUTH_COOKIE_MAX_AGE_SECONDS * 1000
    ? { startedAt, logoutGeneration }
    : null;
}

function validateEntraSettings(
  settings: AdminSecuritySettings,
  request: Request
): AdminSecurityResult {
  if (settings.authProvider !== "entra") {
    return securityFailure(400, "Microsoft Entra ID authentication is not enabled.");
  }

  if (!settings.entraId.tenantId || !settings.entraId.clientId) {
    return serviceUnavailable(
      "Microsoft Entra ID authentication requires entraId.tenantId and entraId.clientId."
    );
  }

  if (!sessionSecret(settings)) {
    return serviceUnavailable(
      "Microsoft Entra ID authentication requires sessionSecret for admin UI session signing."
    );
  }

  try {
    const authority = new URL(settings.entraId.authorityHost);
    if (authority.protocol !== "https:") {
      return securityFailure(
        400,
        "Microsoft Entra ID authorityHost must use https."
      );
    }

    new URL(getRedirectUri(settings, request));
  } catch {
    return securityFailure(
      400,
      "Microsoft Entra ID authentication is not configured with valid URLs."
    );
  }

  return authenticatedIdentity("entra-configured", "entra", "entra");
}

function createAuthorizationUrl(
  settings: AdminSecuritySettings,
  request: Request,
  values: {
    state: string;
    nonce: string;
    verifier: string;
  }
) {
  const authorizationUrl = createAuthorityUrl(settings, "oauth2/v2.0/authorize");
  authorizationUrl.searchParams.set("client_id", settings.entraId.clientId ?? "");
  authorizationUrl.searchParams.set("response_type", "code");
  authorizationUrl.searchParams.set("redirect_uri", getRedirectUri(settings, request));
  authorizationUrl.searchParams.set("response_mode", "query");
  authorizationUrl.searchParams.set("scope", createInteractiveEntraScopes(settings));
  authorizationUrl.searchParams.set("state", values.state);
  authorizationUrl.searchParams.set("nonce", values.nonce);
  authorizationUrl.searchParams.set("code_challenge", sha256Base64Url(values.verifier));
  authorizationUrl.searchParams.set("code_challenge_method", "S256");
  return authorizationUrl;
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

async function exchangeAuthorizationCode(
  settings: AdminSecuritySettings,
  request: Request,
  code: string,
  verifier: string,
  openIdConfiguration: EntraOpenIdConfiguration,
  fetcher: FetchLike,
  signal?: AbortSignal
): Promise<EntraTokenResponse> {
  const tokenEndpoint = validateEntraBackchannelUrl(
    openIdConfiguration.token_endpoint ??
      createAuthorityUrl(settings, "oauth2/v2.0/token").toString(),
    settings.entraId.authorityHost,
    "token endpoint"
  );
  const body = new URLSearchParams({
    client_id: settings.entraId.clientId ?? "",
    code,
    code_verifier: verifier,
    grant_type: "authorization_code",
    redirect_uri: getRedirectUri(settings, request),
    scope: createInteractiveEntraScopes(settings),
  });

  if (settings.entraId.clientSecret) {
    body.set("client_secret", settings.entraId.clientSecret);
  }

  const { response, value: tokens } = await fetchEntraJson<EntraTokenResponse>(
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
  if (!response.ok || tokens.error || !tokens.id_token) {
    const tokenError = tokens.error_description || tokens.error;
    throw new Error(
      tokenError
        ? `Microsoft Entra ID token exchange failed: ${tokenError}`
        : `Microsoft Entra ID token exchange failed (${response.status}).`
    );
  }

  return tokens;
}

async function validateEntraIdToken(
  idToken: string,
  expectedNonce: string,
  settings: AdminSecuritySettings,
  openIdConfiguration: EntraOpenIdConfiguration,
  fetcher: FetchLike,
  signal?: AbortSignal
) {
  const parsed = parseJwt(idToken);
  if (parsed.header.alg !== "RS256" || !parsed.header.kid) {
    throw new Error("Microsoft Entra ID token uses an unsupported signature.");
  }

  const jwksUri = openIdConfiguration.jwks_uri;
  if (!jwksUri) {
    throw new Error("Microsoft Entra ID metadata did not include signing keys.");
  }
  const validatedJwksUri = validateEntraBackchannelUrl(
    jwksUri,
    settings.entraId.authorityHost,
    "signing-key endpoint"
  );

  const jwksCacheKey = `jwks:${validatedJwksUri}`;
  let jwks = await fetchCachedEntraJson(
    fetcher,
    jwksCacheKey,
    validatedJwksUri,
    isJsonWebKeySet,
    signal
  );
  let jwk = jwks.keys?.find((key) => key.kid === parsed.header.kid);
  if (!jwk) {
    jwks = await refreshCachedEntraJson(
      fetcher,
      jwksCacheKey,
      validatedJwksUri,
      isJsonWebKeySet,
      jwks,
      signal
    );
    jwk = jwks.keys?.find((key) => key.kid === parsed.header.kid);
  }
  if (!jwk) {
    throw new Error("Microsoft Entra ID signing key was not found.");
  }

  const publicKey = createPublicKey({ key: jwk, format: "jwk" });
  const signatureIsValid = verify(
    "RSA-SHA256",
    Buffer.from(parsed.signedContent),
    publicKey,
    Buffer.from(parsed.signature, "base64url")
  );
  if (!signatureIsValid) {
    throw new Error("Microsoft Entra ID token signature is not valid.");
  }

  const claims = parsed.payload as EntraIdTokenClaims;
  validateIdTokenClaims(claims, expectedNonce, settings, openIdConfiguration);
  return claims;
}

function isOpenIdConfiguration(value: unknown): value is EntraOpenIdConfiguration {
  if (!value || typeof value !== "object") {
    return false;
  }
  const candidate = value as EntraOpenIdConfiguration;
  return typeof candidate.issuer === "string" &&
    typeof candidate.jwks_uri === "string" &&
    typeof candidate.token_endpoint === "string";
}

function isJsonWebKeySet(value: unknown): value is JsonWebKeySet {
  return Boolean(
    value &&
      typeof value === "object" &&
      Array.isArray((value as JsonWebKeySet).keys)
  );
}

function validateIdTokenClaims(
  claims: EntraIdTokenClaims,
  expectedNonce: string,
  settings: AdminSecuritySettings,
  openIdConfiguration: EntraOpenIdConfiguration
) {
  const now = Math.floor(Date.now() / 1000);
  const clockSkewSeconds = 300;
  if (!claims.exp || claims.exp <= now - clockSkewSeconds) {
    throw new Error("Microsoft Entra ID token is expired.");
  }

  if (claims.nbf && claims.nbf > now + clockSkewSeconds) {
    throw new Error("Microsoft Entra ID token is not valid yet.");
  }

  const audiences = Array.isArray(claims.aud) ? claims.aud : [claims.aud];
  if (!audiences.includes(settings.entraId.clientId)) {
    throw new Error("Microsoft Entra ID token audience is not valid.");
  }

  if (!claims.nonce || !constantTimeEquals(claims.nonce, expectedNonce)) {
    throw new Error("Microsoft Entra ID token nonce is not valid.");
  }

  if (
    !claims.iss ||
    !issuerMatches(openIdConfiguration.issuer ?? "", claims.iss, claims.tid)
  ) {
    throw new Error("Microsoft Entra ID token issuer is not valid.");
  }
}

function issuerMatches(expectedIssuer: string, actualIssuer: string, tenantId?: string) {
  if (actualIssuer === expectedIssuer) {
    return true;
  }

  return Boolean(
    tenantId &&
      expectedIssuer.includes("{tenantid}") &&
      actualIssuer === expectedIssuer.replace("{tenantid}", tenantId)
  );
}

function createEntraIdentity(
  claims: EntraIdTokenClaims,
  settings: AdminSecuritySettings
): AdminSecurityResult {
  const email = (claims.email || claims.preferred_username || "").trim();
  const name = (claims.name || email || claims.oid || claims.sub || "").trim();
  if (!name) {
    return securityFailure(
      401,
      "Microsoft Entra ID token did not include an identifiable user."
    );
  }

  const entraSubject = createStableEntraSubject(claims);
  if (!entraSubject) {
    return securityFailure(
      401,
      "Microsoft Entra ID token did not include a stable subject."
    );
  }

  if (!isAllowedEntraUser(email, settings)) {
    return securityFailure(
      403,
      "Microsoft Entra ID user is not allowed to access this admin UI."
    );
  }

  return authenticatedIdentity(
    name,
    "entra",
    "entra",
    email || undefined,
    undefined,
    entraSubject
  );
}

function createStableEntraSubject(claims: EntraIdTokenClaims) {
  const tenantId = claims.tid?.trim().toLowerCase();
  const objectId = claims.oid?.trim().toLowerCase();
  if (tenantId && objectId) {
    return JSON.stringify(["workable.entra.subject.v1", "oid", tenantId, objectId]);
  }

  const issuer = claims.iss?.trim();
  const subject = claims.sub?.trim();
  return issuer && subject
    ? JSON.stringify(["workable.entra.subject.v1", "sub", issuer, subject])
    : null;
}

export function isAllowedEntraUser(email: string, settings: AdminSecuritySettings) {
  const allowedEmails = new Set(
    settings.entraId.allowedEmails.map((value) => value.toLowerCase())
  );
  const allowedDomains = new Set(
    settings.entraId.allowedEmailDomains
      .map((value) => value.trim().replace(/^@/, "").toLowerCase())
      .filter(Boolean)
  );

  if (allowedEmails.size === 0 && allowedDomains.size === 0) {
    return true;
  }

  const normalizedEmail = email.toLowerCase();
  const domain = normalizedEmail.includes("@")
    ? normalizedEmail.slice(normalizedEmail.lastIndexOf("@") + 1)
    : "";

  return allowedEmails.has(normalizedEmail) ||
    Boolean(domain && allowedDomains.has(domain));
}

function parseJwt(value: string) {
  const [encodedHeader, encodedPayload, signature] = value.split(".");
  if (!encodedHeader || !encodedPayload || !signature) {
    throw new Error("Microsoft Entra ID token is not a valid JWT.");
  }

  return {
    header: JSON.parse(base64UrlDecode(encodedHeader)) as JwtHeader,
    payload: JSON.parse(base64UrlDecode(encodedPayload)) as unknown,
    signature,
    signedContent: `${encodedHeader}.${encodedPayload}`,
  };
}

function createAuthorityUrl(settings: AdminSecuritySettings, path: string) {
  const authority = new URL(settings.entraId.authorityHost);
  const normalizedPath = path.replace(/^\/+/, "");
  authority.pathname = `/${encodeURIComponent(settings.entraId.tenantId ?? "")}/${normalizedPath}`;
  authority.search = "";
  return authority;
}

function getRedirectUri(settings: AdminSecuritySettings, request: Request) {
  return settings.entraId.redirectUri ??
    new URL("/api/auth/entra/callback", request.url).toString();
}

function createFailedEntraCallbackResponse(
  request: Request,
  error: string
) {
  const loginUrl = new URL("/login", request.url);
  loginUrl.searchParams.set("error", error);
  const response = createRedirectResponse(loginUrl, 303);
  appendExpiredEntraCookies(response);
  return response;
}

function appendExpiredEntraCookies(response: Response) {
  for (const cookie of createExpiredEntraTransactionCookies()) {
    response.headers.append("set-cookie", cookie);
  }
}

export function createExpiredEntraTransactionCookies() {
  return [
    STATE_COOKIE_NAME,
    NONCE_COOKIE_NAME,
    VERIFIER_COOKIE_NAME,
    NEXT_COOKIE_NAME,
    HOST_STATE_COOKIE_NAME,
    HOST_NONCE_COOKIE_NAME,
    HOST_VERIFIER_COOKIE_NAME,
    HOST_NEXT_COOKIE_NAME,
  ].map((name) => serializeExpiredCookie(name));
}

function getOAuthCookieNames(secure: boolean) {
  return secure
    ? {
        state: HOST_STATE_COOKIE_NAME,
        nonce: HOST_NONCE_COOKIE_NAME,
        verifier: HOST_VERIFIER_COOKIE_NAME,
        next: HOST_NEXT_COOKIE_NAME,
      }
    : {
        state: STATE_COOKIE_NAME,
        nonce: NONCE_COOKIE_NAME,
        verifier: VERIFIER_COOKIE_NAME,
        next: NEXT_COOKIE_NAME,
      };
}

function createRedirectResponse(location: URL, status = 302) {
  return new Response(null, {
    status,
    headers: {
      ...noStoreHeaders,
      location: location.toString(),
    },
  });
}

function formatEntraCallbackError(
  error: unknown,
  settings: AdminSecuritySettings
) {
  const message = error instanceof Error ? error.message.trim() : "";
  if (!message || settings.isProduction) {
    return "Microsoft Entra ID sign-in could not be completed.";
  }

  return `Microsoft Entra ID sign-in could not be completed. ${message}`;
}

function logEntraCallbackFailure(error: unknown) {
  console.error("Microsoft Entra ID callback failed.", error);
}
