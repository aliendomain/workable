import {
  authenticateBasicRequest,
  verifyBasicCredentials,
} from "./admin-security/basic.ts";
import {
  getAdminSecuritySettings,
  getDefaultApiUrl,
  isSafeMethod,
} from "./admin-security/config.ts";
import {
  validateEntraTargetTokenConfiguration,
} from "./admin-security/entra-downstream.ts";
import {
  createExpiredSessionCookie,
  createSignedAdminSessionCookie,
  readAdminSessionState,
  sessionSecret,
  shouldRenewAdminSession,
} from "./admin-security/session.ts";
import {
  authenticatedIdentity,
  securityFailure,
  serviceUnavailable,
  type AdminAuthProvider,
  type AdminIdentity,
  type AdminSecurityEnvironment,
  type AdminSecurityFailure,
  type AdminSecurityResult,
  type AdminSessionCookieResult,
  type TargetUrlResult,
} from "./admin-security/types.ts";

export {
  createEntraAuthorizationResponse,
  completeEntraLogin,
  getAdminAuthProvider,
} from "./admin-security/entra.ts";
export {
  createEntraTargetAccessTokenResponse,
  createExpiredEntraTargetTokenCookies,
  getEntraTargetAccessToken,
} from "./admin-security/entra-downstream.ts";

export type {
  AdminAuthProvider,
  AdminIdentity,
  AdminSecurityEnvironment,
  AdminSecurityFailure,
  AdminSecurityResult,
  AdminSessionCookieResult,
  TargetUrlResult,
};

export function authenticateAdminRequest(
  headers: Headers,
  env: AdminSecurityEnvironment = process.env,
  request?: Request
): AdminSecurityResult {
  const settings = getAdminSecuritySettings(env);
  if (settings.configError) {
    return serviceUnavailable(settings.configError);
  }

  if (settings.allowAnonymous) {
    return authenticatedIdentity("anonymous", "anonymous");
  }

  if (
    settings.authProvider === "entra" &&
    (!settings.entraId.tenantId || !settings.entraId.clientId || !sessionSecret(settings))
  ) {
    return serviceUnavailable(
      "Microsoft Entra ID authentication requires entraId.tenantId, entraId.clientId, and sessionSecret."
    );
  }

  const targetTokenConfiguration = validateEntraTargetTokenConfiguration(settings);
  if (!targetTokenConfiguration.ok) {
    return targetTokenConfiguration;
  }

  const session = readAdminSessionState(headers, settings);
  if (session.identity) {
    const renewedCookie = request && shouldRenewAdminSession(session.expiresAt, settings)
      ? createSignedAdminSessionCookie(session.identity, request, settings)
      : null;

    return authenticatedIdentity(
      session.identity.name,
      "session",
      session.identity.provider,
      session.identity.email,
      renewedCookie?.ok ? renewedCookie.header : undefined
    );
  }

  if (settings.authProvider === "basic") {
    const basicAuthentication = authenticateBasicRequest(headers, settings);
    if (basicAuthentication.ok || !session.shouldClear) {
      return basicAuthentication;
    }

    return securityFailure(
      basicAuthentication.status,
      basicAuthentication.error,
      {
        ...(basicAuthentication.headers ?? {}),
        "set-cookie": createExpiredSessionCookie(settings),
      }
    );
  }

  return securityFailure(
    401,
    "Authentication is required for the Workable admin UI.",
    session.shouldClear
      ? { "set-cookie": createExpiredSessionCookie(settings) }
      : undefined
  );
}

export function verifyAdminCredentials(
  userName: string,
  password: string,
  env: AdminSecurityEnvironment = process.env
): AdminSecurityResult {
  const settings = getAdminSecuritySettings(env);
  if (settings.configError) {
    return serviceUnavailable(settings.configError);
  }

  return verifyBasicCredentials(userName, password, settings);
}

export function createAdminSessionCookie(
  userName: string,
  request: Request,
  env: AdminSecurityEnvironment = process.env,
  provider?: AdminAuthProvider
): AdminSessionCookieResult {
  const settings = getAdminSecuritySettings(env);
  if (settings.configError) {
    return serviceUnavailable(settings.configError);
  }

  return createSignedAdminSessionCookie(
    {
      name: userName,
      provider: provider ?? settings.authProvider,
    },
    request,
    settings
  );
}

export function createExpiredAdminSessionCookie(
  env: AdminSecurityEnvironment = process.env
) {
  return createExpiredSessionCookie(getAdminSecuritySettings(env));
}

export function validateUnsafeRequestOrigin(request: Request): AdminSecurityResult {
  if (isSafeMethod(request.method)) {
    return authenticatedIdentity("csrf-not-required", "anonymous");
  }

  const requestUrl = new URL(request.url);
  const origin = request.headers.get("origin");
  if (!origin) {
    return securityFailure(
      403,
      "Unsafe Workable admin UI requests require a same-origin Origin header."
    );
  }

  if (origin !== requestUrl.origin) {
    return securityFailure(
      403,
      "Unsafe Workable admin UI requests must come from the admin UI origin."
    );
  }

  return authenticatedIdentity("csrf-ok", "anonymous");
}

export function createWorkableTargetUrl(
  request: Request,
  path: readonly string[],
  env: AdminSecurityEnvironment = process.env
): TargetUrlResult {
  const settings = getAdminSecuritySettings(env);
  if (settings.configError) {
    return { ok: false, error: settings.configError };
  }

  const defaultApiUrl = getDefaultApiUrl(settings);
  const base = request.headers.get("x-workable-api-url") ?? defaultApiUrl;
  if (!base) {
    return {
      ok: false,
      error: "WORKABLE_API_URL or apiUrl must be configured before the Workable admin UI proxy can connect to a host.",
    };
  }

  try {
    const baseUrl = new URL(base);
    if (!["http:", "https:"].includes(baseUrl.protocol)) {
      return { ok: false, error: "Workable API URL must use http or https." };
    }

    if (!isAllowedApiUrl(baseUrl, env)) {
      return {
        ok: false,
        error:
          "Workable API URL is not allowed. Configure WORKABLE_API_URL, WORKABLE_ALLOWED_API_URLS, apiUrl, or allowedApiUrls.",
      };
    }

    const requestUrl = new URL(request.url);
    const normalizedBase = baseUrl.pathname.replace(/\/+$/, "");
    const targetUrl = new URL(baseUrl.toString());
    targetUrl.pathname = `${normalizedBase}/${path.map(encodeURIComponent).join("/")}`;
    targetUrl.search = requestUrl.search;
    return { ok: true, url: targetUrl, baseUrl };
  } catch {
    return { ok: false, error: "Workable API URL is not valid." };
  }
}

export function isAllowedApiUrl(
  url: URL,
  env: AdminSecurityEnvironment = process.env
) {
  const settings = getAdminSecuritySettings(env);
  return parseAllowedApiUrls(settings).has(normalizeApiUrl(url)) ||
    (!settings.isProduction && isLoopbackHost(url.hostname));
}

export function getMaxProxyBodyBytes(env: AdminSecurityEnvironment = process.env) {
  return getAdminSecuritySettings(env).maxProxyBodyBytes;
}

export function failureHeaders(failure: AdminSecurityFailure) {
  return failure.headers ?? {};
}

function parseAllowedApiUrls(settings: ReturnType<typeof getAdminSecuritySettings>) {
  const urls = new Set<string>();
  addAllowedApiUrl(urls, getDefaultApiUrl(settings));

  for (const item of settings.allowedApiUrls) {
    addAllowedApiUrl(urls, item);
  }

  return urls;
}

function addAllowedApiUrl(urls: Set<string>, value?: string) {
  const candidate = value?.trim();
  if (!candidate) {
    return;
  }

  try {
    const url = new URL(candidate);
    if (["http:", "https:"].includes(url.protocol)) {
      urls.add(normalizeApiUrl(url));
    }
  } catch {
    // Invalid allow-list entries are ignored so a typo does not open the proxy.
  }
}

function normalizeApiUrl(url: URL) {
  const clone = new URL(url.toString());
  clone.hash = "";
  clone.search = "";
  clone.pathname = clone.pathname.replace(/\/+$/, "");
  return clone.toString();
}

function isLoopbackHost(hostname: string) {
  const normalized = hostname.toLowerCase();
  return normalized === "localhost" ||
    normalized === "127.0.0.1" ||
    normalized === "::1" ||
    normalized === "[::1]";
}
