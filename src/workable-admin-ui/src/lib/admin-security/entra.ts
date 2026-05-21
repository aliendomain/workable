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
  sha256Base64Url,
} from "./crypto.ts";
import {
  parseCookieHeader,
  serializeCookie,
  serializeExpiredCookie,
  shouldSecureCookie,
} from "./cookies.ts";
import {
  createSignedAdminSessionCookie,
  sessionSecret,
} from "./session.ts";

const STATE_COOKIE_NAME = "workable_admin_entra_state";
const NONCE_COOKIE_NAME = "workable_admin_entra_nonce";
const VERIFIER_COOKIE_NAME = "workable_admin_entra_verifier";
const NEXT_COOKIE_NAME = "workable_admin_entra_next";
const OAUTH_COOKIE_MAX_AGE_SECONDS = 10 * 60;

type FetchLike = typeof fetch;

type EntraTokenResponse = {
  id_token?: string;
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
      { status: validation.status }
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
  const nextPath = normalizeNextPath(new URL(request.url).searchParams.get("next"));

  for (const cookie of [
    serializeCookie(STATE_COOKIE_NAME, state, {
      maxAgeSeconds: OAUTH_COOKIE_MAX_AGE_SECONDS,
      secure,
    }),
    serializeCookie(NONCE_COOKIE_NAME, nonce, {
      maxAgeSeconds: OAUTH_COOKIE_MAX_AGE_SECONDS,
      secure,
    }),
    serializeCookie(VERIFIER_COOKIE_NAME, verifier, {
      maxAgeSeconds: OAUTH_COOKIE_MAX_AGE_SECONDS,
      secure,
    }),
    serializeCookie(NEXT_COOKIE_NAME, nextPath, {
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
    return createFailedEntraCallbackResponse(request, validation.error, settings);
  }

  const requestUrl = new URL(request.url);
  if (requestUrl.searchParams.has("error")) {
    return createFailedEntraCallbackResponse(
      request,
      "Microsoft Entra ID sign-in was not completed.",
      settings
    );
  }

  const cookies = parseCookieHeader(request.headers.get("cookie"));
  const state = requestUrl.searchParams.get("state") ?? "";
  const expectedState = cookies.get(STATE_COOKIE_NAME) ?? "";
  const code = requestUrl.searchParams.get("code") ?? "";
  const verifier = cookies.get(VERIFIER_COOKIE_NAME) ?? "";
  const nonce = cookies.get(NONCE_COOKIE_NAME) ?? "";

  if (!state || !expectedState || !constantTimeEquals(state, expectedState)) {
    return createFailedEntraCallbackResponse(
      request,
      "Microsoft Entra ID sign-in state is not valid.",
      settings
    );
  }

  if (!code || !verifier || !nonce) {
    return createFailedEntraCallbackResponse(
      request,
      "Microsoft Entra ID sign-in response is incomplete.",
      settings
    );
  }

  try {
    const openIdConfiguration = await fetchOpenIdConfiguration(settings, fetcher);
    const tokens = await exchangeAuthorizationCode(
      settings,
      request,
      code,
      verifier,
      openIdConfiguration,
      fetcher
    );
    const claims = await validateEntraIdToken(
      tokens.id_token ?? "",
      nonce,
      settings,
      openIdConfiguration,
      fetcher
    );
    const identity = createEntraIdentity(claims, settings);
    if (!identity.ok) {
      return createFailedEntraCallbackResponse(request, identity.error, settings);
    }

    const sessionCookie = createSignedAdminSessionCookie(
      {
        name: identity.identity.name,
        provider: "entra",
        email: identity.identity.email,
      },
      request,
      settings
    );
    if (!sessionCookie.ok) {
      return createFailedEntraCallbackResponse(
        request,
        sessionCookie.error,
        settings
      );
    }

    const nextPath = normalizeNextPath(cookies.get(NEXT_COOKIE_NAME));
    const response = createRedirectResponse(new URL(nextPath, request.url), 303);
    response.headers.append("set-cookie", sessionCookie.header);
    appendExpiredEntraCookies(response);
    return response;
  } catch {
    return createFailedEntraCallbackResponse(
      request,
      "Microsoft Entra ID sign-in could not be completed.",
      settings
    );
  }
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
  authorizationUrl.searchParams.set("scope", "openid profile email");
  authorizationUrl.searchParams.set("state", values.state);
  authorizationUrl.searchParams.set("nonce", values.nonce);
  authorizationUrl.searchParams.set("code_challenge", sha256Base64Url(values.verifier));
  authorizationUrl.searchParams.set("code_challenge_method", "S256");
  return authorizationUrl;
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

async function exchangeAuthorizationCode(
  settings: AdminSecuritySettings,
  request: Request,
  code: string,
  verifier: string,
  openIdConfiguration: EntraOpenIdConfiguration,
  fetcher: FetchLike
): Promise<EntraTokenResponse> {
  const tokenEndpoint = openIdConfiguration.token_endpoint ??
    createAuthorityUrl(settings, "oauth2/v2.0/token").toString();
  const body = new URLSearchParams({
    client_id: settings.entraId.clientId ?? "",
    code,
    code_verifier: verifier,
    grant_type: "authorization_code",
    redirect_uri: getRedirectUri(settings, request),
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
  const tokens = await response.json() as EntraTokenResponse;
  if (!response.ok || tokens.error || !tokens.id_token) {
    throw new Error("Microsoft Entra ID token exchange failed.");
  }

  return tokens;
}

async function validateEntraIdToken(
  idToken: string,
  expectedNonce: string,
  settings: AdminSecuritySettings,
  openIdConfiguration: EntraOpenIdConfiguration,
  fetcher: FetchLike
) {
  const parsed = parseJwt(idToken);
  if (parsed.header.alg !== "RS256" || !parsed.header.kid) {
    throw new Error("Microsoft Entra ID token uses an unsupported signature.");
  }

  const jwksUri = openIdConfiguration.jwks_uri;
  if (!jwksUri) {
    throw new Error("Microsoft Entra ID metadata did not include signing keys.");
  }

  const jwksResponse = await fetcher(jwksUri, { cache: "no-store" });
  if (!jwksResponse.ok) {
    throw new Error("Unable to load Microsoft Entra ID signing keys.");
  }

  const jwks = await jwksResponse.json() as JsonWebKeySet;
  const jwk = jwks.keys?.find((key) => key.kid === parsed.header.kid);
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

  if (!isAllowedEntraUser(email, settings)) {
    return securityFailure(
      403,
      "Microsoft Entra ID user is not allowed to access this admin UI."
    );
  }

  return authenticatedIdentity(name, "entra", "entra", email || undefined);
}

function isAllowedEntraUser(email: string, settings: AdminSecuritySettings) {
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
  error: string,
  settings: AdminSecuritySettings
) {
  const loginUrl = new URL("/login", request.url);
  loginUrl.searchParams.set("error", error);
  const response = createRedirectResponse(loginUrl, 303);
  appendExpiredEntraCookies(response);
  response.headers.append("set-cookie", serializeExpiredCookie(settings.sessionCookieName));
  return response;
}

function appendExpiredEntraCookies(response: Response) {
  for (const name of [
    STATE_COOKIE_NAME,
    NONCE_COOKIE_NAME,
    VERIFIER_COOKIE_NAME,
    NEXT_COOKIE_NAME,
  ]) {
    response.headers.append("set-cookie", serializeExpiredCookie(name));
  }
}

function createRedirectResponse(location: URL, status = 302) {
  return new Response(null, {
    status,
    headers: {
      location: location.toString(),
    },
  });
}

function normalizeNextPath(value: string | null | undefined) {
  if (!value?.startsWith("/") || value.startsWith("//")) {
    return "/";
  }

  return value;
}
