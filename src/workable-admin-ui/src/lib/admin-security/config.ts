import { existsSync, readFileSync } from "node:fs";
import { isAbsolute, join } from "node:path";
import type {
  AdminAuthProvider,
  AdminSecurityEnvironment,
} from "./types.ts";

const DEVELOPMENT_API_URL = "http://localhost:61932/workable";
const DEFAULT_CONFIG_FILE = "workable-admin.config.local.json";
const DEFAULT_SHARED_CONFIG_FILE = "workable-admin.config.json";
export const DEFAULT_SESSION_COOKIE_NAME = "workable_admin_session";
export const DEFAULT_SESSION_MAX_AGE_SECONDS = 8 * 60 * 60;
const DEFAULT_ENTRA_AUTHORITY_HOST = "https://login.microsoftonline.com";

type EntraTargetApiBindingConfig = {
  apiUrl?: string;
  scope?: string;
};

export type WorkableAdminConfig = {
  authProvider?: string;
  apiUrl?: string;
  allowedApiUrls?: string[];
  allowAnonymous?: boolean;
  basicAuth?: {
    username?: string;
    password?: string;
  };
  entraId?: {
    tenantId?: string;
    clientId?: string;
    clientSecret?: string;
    redirectUri?: string;
    authorityHost?: string;
    targetApis?: EntraTargetApiBindingConfig[];
    allowedEmails?: string[];
    allowedEmailDomains?: string[];
  };
  sessionSecret?: string;
  sessionCookieName?: string;
  sessionMaxAgeSeconds?: number;
  maxProxyBodyBytes?: number;
};

export type EntraIdSettings = {
  tenantId?: string;
  clientId?: string;
  clientSecret?: string;
  redirectUri?: string;
  authorityHost: string;
  targetApis: ReadonlyArray<EntraTargetApiBindingConfig>;
  allowedEmails: readonly string[];
  allowedEmailDomains: readonly string[];
};

export type AdminSecuritySettings = {
  authProvider: AdminAuthProvider;
  apiUrl?: string;
  allowedApiUrls: readonly string[];
  allowAnonymous: boolean;
  userName?: string;
  password?: string;
  entraId: EntraIdSettings;
  sessionSecret?: string;
  sessionCookieName: string;
  sessionMaxAgeSeconds: number;
  maxProxyBodyBytes: number;
  configError?: string;
  isProduction: boolean;
};

export function getAdminSecuritySettings(
  env: AdminSecurityEnvironment = process.env
): AdminSecuritySettings {
  const { config, error } = readConfigFile(env);
  const providerResult = parseAuthProvider(
    env.WORKABLE_ADMIN_UI_AUTH_PROVIDER ?? config?.authProvider
  );
  const isProductionEnvironment = isProduction(env);
  const userName = env.WORKABLE_ADMIN_UI_USERNAME?.trim() ||
    config?.basicAuth?.username?.trim();
  const password = env.WORKABLE_ADMIN_UI_PASSWORD ?? config?.basicAuth?.password;
  const entraId = getEntraIdSettings(env, config);
  const sessionSecretValue = env.WORKABLE_ADMIN_UI_SESSION_SECRET ?? config?.sessionSecret;
  const sessionCookieName = env.WORKABLE_ADMIN_UI_SESSION_COOKIE_NAME?.trim() ||
    config?.sessionCookieName?.trim() ||
    DEFAULT_SESSION_COOKIE_NAME;

  return {
    authProvider: providerResult.provider ??
      inferAuthProvider(Boolean(userName || password), hasEntraSettings(entraId)),
    apiUrl: env.WORKABLE_API_URL ?? config?.apiUrl,
    allowedApiUrls: env.WORKABLE_ALLOWED_API_URLS
      ? parseList(env.WORKABLE_ALLOWED_API_URLS)
      : config?.allowedApiUrls ?? [],
    allowAnonymous: parseBoolean(env.WORKABLE_ADMIN_UI_ALLOW_ANONYMOUS) ??
      config?.allowAnonymous ??
      false,
    userName,
    password,
    entraId,
    sessionSecret: sessionSecretValue,
    sessionCookieName,
    sessionMaxAgeSeconds: parsePositiveInteger(
      env.WORKABLE_ADMIN_UI_SESSION_MAX_AGE_SECONDS,
      config?.sessionMaxAgeSeconds,
      DEFAULT_SESSION_MAX_AGE_SECONDS
    ),
    maxProxyBodyBytes: parsePositiveInteger(
      env.WORKABLE_ADMIN_UI_MAX_BODY_BYTES,
      config?.maxProxyBodyBytes,
      1_048_576
    ),
    configError: error ?? providerResult.error,
    isProduction: isProductionEnvironment,
  };
}

export function getDefaultApiUrl(settings: AdminSecuritySettings) {
  return settings.apiUrl ?? (settings.isProduction ? "" : DEVELOPMENT_API_URL);
}

export function isSafeMethod(method: string) {
  const normalized = method.toUpperCase();
  return normalized === "GET" || normalized === "HEAD" || normalized === "OPTIONS";
}

export function parseList(value: string) {
  return value
    .split(",")
    .map((item) => item.trim())
    .filter(Boolean);
}

export function parseBoolean(value?: string) {
  if (value === undefined) {
    return undefined;
  }

  const normalized = value.trim().toLowerCase();
  if (normalized === "true") {
    return true;
  }

  if (normalized === "false") {
    return false;
  }

  return undefined;
}

function getEntraIdSettings(
  env: AdminSecurityEnvironment,
  config?: WorkableAdminConfig
): EntraIdSettings {
  return {
    tenantId: env.WORKABLE_ADMIN_ENTRA_TENANT_ID?.trim() ||
      config?.entraId?.tenantId?.trim(),
    clientId: env.WORKABLE_ADMIN_ENTRA_CLIENT_ID?.trim() ||
      config?.entraId?.clientId?.trim(),
    clientSecret: env.WORKABLE_ADMIN_ENTRA_CLIENT_SECRET ??
      config?.entraId?.clientSecret,
    redirectUri: env.WORKABLE_ADMIN_ENTRA_REDIRECT_URI?.trim() ||
      config?.entraId?.redirectUri?.trim(),
    authorityHost: normalizeAuthorityHost(
      env.WORKABLE_ADMIN_ENTRA_AUTHORITY_HOST?.trim() ||
        config?.entraId?.authorityHost?.trim() ||
        DEFAULT_ENTRA_AUTHORITY_HOST
    ),
    targetApis: normalizeTargetApis(
      env.WORKABLE_ADMIN_ENTRA_TARGET_APIS_JSON?.trim(),
      config?.entraId?.targetApis
    ),
    allowedEmails: env.WORKABLE_ADMIN_ENTRA_ALLOWED_EMAILS
      ? parseList(env.WORKABLE_ADMIN_ENTRA_ALLOWED_EMAILS)
      : config?.entraId?.allowedEmails ?? [],
    allowedEmailDomains: env.WORKABLE_ADMIN_ENTRA_ALLOWED_EMAIL_DOMAINS
      ? parseList(env.WORKABLE_ADMIN_ENTRA_ALLOWED_EMAIL_DOMAINS)
      : config?.entraId?.allowedEmailDomains ?? [],
  };
}

function inferAuthProvider(hasBasicCredentials: boolean, hasEntra: boolean): AdminAuthProvider {
  return hasEntra && !hasBasicCredentials ? "entra" : "basic";
}

function hasEntraSettings(settings: EntraIdSettings) {
  return Boolean(settings.tenantId || settings.clientId || settings.clientSecret);
}

function normalizeTargetApis(
  jsonValue: string | undefined,
  configuredValue?: EntraTargetApiBindingConfig[]
) {
  const parsed = parseTargetApisJson(jsonValue);
  const value = parsed ?? configuredValue;
  if (!Array.isArray(value)) {
    return [];
  }

  return value
    .filter((item) => item && typeof item === "object")
    .map((item) => ({
      apiUrl: typeof item.apiUrl === "string" ? item.apiUrl.trim() : undefined,
      scope: typeof item.scope === "string" ? item.scope.trim() : undefined,
    }));
}

function parseTargetApisJson(value?: string) {
  if (!value) {
    return undefined;
  }

  try {
    const parsed = JSON.parse(value) as unknown;
    return Array.isArray(parsed) ? parsed : undefined;
  } catch {
    return undefined;
  }
}

function parseAuthProvider(value?: string): {
  provider?: AdminAuthProvider;
  error?: string;
} {
  if (!value?.trim()) {
    return {};
  }

  const normalized = value.trim().toLowerCase();
  if (normalized === "basic") {
    return { provider: "basic" };
  }

  if (normalized === "entra" || normalized === "entry") {
    return { provider: "entra" };
  }

  return {
    error:
      "Workable admin UI authProvider must be either 'basic' or 'entra'.",
  };
}

function readConfigFile(env: AdminSecurityEnvironment): {
  config?: WorkableAdminConfig;
  error?: string;
} {
  if (parseBoolean(env.WORKABLE_ADMIN_CONFIG_DISABLED) === true) {
    return {};
  }

  const explicitPath = env.WORKABLE_ADMIN_CONFIG_PATH?.trim();
  const candidates = explicitPath
    ? [resolveConfigPath(explicitPath)]
    : [
        join(/* turbopackIgnore: true */ process.cwd(), DEFAULT_CONFIG_FILE),
        join(/* turbopackIgnore: true */ process.cwd(), DEFAULT_SHARED_CONFIG_FILE),
      ];

  const configPath = candidates.find(existsSync);
  if (!configPath) {
    return explicitPath
      ? {
          error: `Workable admin UI config file was not found: ${explicitPath}`,
        }
      : {};
  }

  try {
    return {
      config: JSON.parse(readFileSync(configPath, "utf8")) as WorkableAdminConfig,
    };
  } catch {
    return {
      error: `Workable admin UI config file is not valid JSON: ${configPath}`,
    };
  }
}

function resolveConfigPath(value: string) {
  return isAbsolute(value)
    ? value
    : join(/* turbopackIgnore: true */ process.cwd(), value);
}

function normalizeAuthorityHost(value: string) {
  return value.replace(/\/+$/, "");
}

function isProduction(env: AdminSecurityEnvironment) {
  return env.NODE_ENV === "production";
}

function parsePositiveInteger(
  value: string | undefined,
  fallback: number | undefined,
  defaultValue: number
) {
  const parsed = Number.parseInt(value ?? "", 10);
  return Number.isFinite(parsed) && parsed > 0
    ? parsed
    : fallback && fallback > 0
      ? fallback
      : defaultValue;
}
