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
export const DEFAULT_SECURE_SESSION_COOKIE_NAME = "__Host-workable_admin_session";
export const DEFAULT_SESSION_MAX_AGE_SECONDS = 8 * 60 * 60;
export const DEFAULT_SESSION_ABSOLUTE_MAX_AGE_SECONDS = 24 * 60 * 60;
export const MINIMUM_SESSION_SECRET_BYTES = 32;
const DEFAULT_ENTRA_AUTHORITY_HOST = "https://login.microsoftonline.com";
const PRODUCTION_CONFIG_FILE_ERROR =
  "Workable admin UI configuration could not be loaded.";
const reportedConfigFileErrors = new Set<string>();

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
    enabled?: boolean;
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
  sessionAbsoluteMaxAgeSeconds?: number;
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
  basicAuthEnabled: boolean;
  userName?: string;
  password?: string;
  entraId: EntraIdSettings;
  sessionSecret?: string;
  sessionCookieName: string;
  sessionMaxAgeSeconds: number;
  sessionAbsoluteMaxAgeSeconds: number;
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
  const basicAuthEnabledResult = parseConfiguredBoolean(
    "WORKABLE_ADMIN_UI_BASIC_AUTH_ENABLED",
    env.WORKABLE_ADMIN_UI_BASIC_AUTH_ENABLED,
    config?.basicAuth?.enabled,
    false
  );
  const entraIdResult = getEntraIdSettings(env, config);
  const entraId = entraIdResult.settings;
  const sessionSecretValue = env.WORKABLE_ADMIN_UI_SESSION_SECRET ?? config?.sessionSecret;
  const configuredSessionCookieName = env.WORKABLE_ADMIN_UI_SESSION_COOKIE_NAME?.trim() ||
    config?.sessionCookieName?.trim();
  const sessionCookieName = configuredSessionCookieName ||
    (isProductionEnvironment
      ? DEFAULT_SECURE_SESSION_COOKIE_NAME
      : DEFAULT_SESSION_COOKIE_NAME);
  const sessionCookieNameError = validateSessionCookieName(
    sessionCookieName,
    isProductionEnvironment
  );
  const requestedAnonymousAccess = parseBoolean(env.WORKABLE_ADMIN_UI_ALLOW_ANONYMOUS) ??
    config?.allowAnonymous ??
    false;
  const anonymousAccessError = requestedAnonymousAccess && isProductionEnvironment
    ? "Workable admin UI anonymous access is only allowed outside production."
    : undefined;
  const authProvider = providerResult.provider ??
    inferAuthProvider(Boolean(userName || password), hasEntraSettings(entraId));
  const sessionSecretError = validateSessionSecret(
    authProvider,
    basicAuthEnabledResult.value,
    sessionSecretValue,
    password
  );
  const sessionMaxAge = parsePositiveIntegerSetting(
    "WORKABLE_ADMIN_UI_SESSION_MAX_AGE_SECONDS",
    env.WORKABLE_ADMIN_UI_SESSION_MAX_AGE_SECONDS,
    config?.sessionMaxAgeSeconds,
    DEFAULT_SESSION_MAX_AGE_SECONDS
  );
  const sessionAbsoluteMaxAge = parsePositiveIntegerSetting(
    "WORKABLE_ADMIN_UI_SESSION_ABSOLUTE_MAX_AGE_SECONDS",
    env.WORKABLE_ADMIN_UI_SESSION_ABSOLUTE_MAX_AGE_SECONDS,
    config?.sessionAbsoluteMaxAgeSeconds,
    DEFAULT_SESSION_ABSOLUTE_MAX_AGE_SECONDS
  );
  const maxProxyBodyBytes = parsePositiveIntegerSetting(
    "WORKABLE_ADMIN_UI_MAX_BODY_BYTES",
    env.WORKABLE_ADMIN_UI_MAX_BODY_BYTES,
    config?.maxProxyBodyBytes,
    1_048_576
  );

  return {
    authProvider,
    apiUrl: env.WORKABLE_API_URL ?? config?.apiUrl,
    allowedApiUrls: env.WORKABLE_ALLOWED_API_URLS
      ? parseList(env.WORKABLE_ALLOWED_API_URLS)
      : config?.allowedApiUrls ?? [],
    allowAnonymous: requestedAnonymousAccess && !isProductionEnvironment,
    basicAuthEnabled: basicAuthEnabledResult.value,
    userName,
    password,
    entraId,
    sessionSecret: sessionSecretValue,
    sessionCookieName,
    sessionMaxAgeSeconds: sessionMaxAge.value,
    sessionAbsoluteMaxAgeSeconds: sessionAbsoluteMaxAge.value,
    maxProxyBodyBytes: maxProxyBodyBytes.value,
    configError: error ??
      providerResult.error ??
      basicAuthEnabledResult.error ??
      entraIdResult.error ??
      sessionSecretError ??
      sessionCookieNameError ??
      sessionMaxAge.error ??
      sessionAbsoluteMaxAge.error ??
      maxProxyBodyBytes.error ??
      anonymousAccessError,
    isProduction: isProductionEnvironment,
  };
}

function validateSessionCookieName(name: string, isProductionEnvironment: boolean) {
  // RFC 6265 cookie-name is a token. Reject separators/control characters instead
  // of allowing configuration to create an invalid or ambiguous Set-Cookie field.
  if (!/^[!#$%&'*+\-.^_`|~0-9A-Za-z]+$/.test(name)) {
    return "Workable admin UI sessionCookieName must be a valid cookie name.";
  }

  if (isProductionEnvironment && !name.startsWith("__Host-")) {
    return "Workable admin UI sessionCookieName must use the __Host- prefix in production.";
  }

  return undefined;
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
): { settings: EntraIdSettings; error?: string } {
  const targetApis = normalizeTargetApis(
    env.WORKABLE_ADMIN_ENTRA_TARGET_APIS_JSON,
    config?.entraId?.targetApis
  );
  return {
    settings: {
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
      targetApis: targetApis.value,
      allowedEmails: env.WORKABLE_ADMIN_ENTRA_ALLOWED_EMAILS
        ? parseList(env.WORKABLE_ADMIN_ENTRA_ALLOWED_EMAILS)
        : config?.entraId?.allowedEmails ?? [],
      allowedEmailDomains: env.WORKABLE_ADMIN_ENTRA_ALLOWED_EMAIL_DOMAINS
        ? parseList(env.WORKABLE_ADMIN_ENTRA_ALLOWED_EMAIL_DOMAINS)
        : config?.entraId?.allowedEmailDomains ?? [],
    },
    error: targetApis.error,
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
): { value: EntraTargetApiBindingConfig[]; error?: string } {
  const parsed = parseTargetApisJson(jsonValue);
  if (parsed.error) {
    return { value: [], error: parsed.error };
  }

  const value: unknown = jsonValue !== undefined ? parsed.value : configuredValue;
  if (value === undefined) {
    return { value: [] };
  }
  if (!Array.isArray(value)) {
    return {
      value: [],
      error: "Microsoft Entra ID targetApis must be an array.",
    };
  }

  const normalized: EntraTargetApiBindingConfig[] = [];
  for (const [index, item] of value.entries()) {
    if (!item || typeof item !== "object" || Array.isArray(item)) {
      return {
        value: [],
        error: `Microsoft Entra ID target API entry ${index + 1} must be an object with string apiUrl and scope values.`,
      };
    }

    const binding = item as Record<string, unknown>;
    if (typeof binding.apiUrl !== "string" || typeof binding.scope !== "string") {
      return {
        value: [],
        error: `Microsoft Entra ID target API entry ${index + 1} must provide string apiUrl and scope values.`,
      };
    }

    if (!binding.apiUrl.trim() || !binding.scope.trim()) {
      return {
        value: [],
        error: `Microsoft Entra ID target API entry ${index + 1} requires non-empty apiUrl and scope values.`,
      };
    }

    normalized.push({
      apiUrl: binding.apiUrl.trim(),
      scope: binding.scope.trim(),
    });
  }

  return { value: normalized };
}

function parseTargetApisJson(value?: string): {
  value?: unknown[];
  error?: string;
} {
  if (value === undefined) {
    return {};
  }

  try {
    const parsed = JSON.parse(value) as unknown;
    return Array.isArray(parsed)
      ? { value: parsed }
      : {
          error: "WORKABLE_ADMIN_ENTRA_TARGET_APIS_JSON must contain a JSON array.",
        };
  } catch {
    return {
      error: "WORKABLE_ADMIN_ENTRA_TARGET_APIS_JSON must contain valid JSON.",
    };
  }
}

function validateSessionSecret(
  authProvider: AdminAuthProvider,
  basicAuthEnabled: boolean,
  configuredSecret: string | undefined,
  basicPassword: string | undefined
) {
  const secretIsRequired = authProvider === "entra" ||
    (authProvider === "basic" && basicAuthEnabled);
  if (configuredSecret === undefined) {
    return secretIsRequired
      ? "Workable admin UI authentication requires an independent sessionSecret."
      : undefined;
  }
  if (authProvider === "basic" && basicAuthEnabled &&
    configuredSecret === basicPassword) {
    return "Workable admin UI sessionSecret must be different from the Basic password.";
  }
  return configuredSecret.trim() &&
      Buffer.byteLength(configuredSecret, "utf8") >= MINIMUM_SESSION_SECRET_BYTES
    ? undefined
    : `Workable admin UI sessionSecret must contain at least ${MINIMUM_SESSION_SECRET_BYTES} UTF-8 bytes.`;
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
    if (!explicitPath) {
      return {};
    }

    const detail = `Workable admin UI config file was not found: ${candidates[0]}`;
    reportConfigFileError(detail);
    return {
      error: publicConfigFileError(env, detail),
    };
  }

  try {
    return validateConfigFile(JSON.parse(readFileSync(configPath, "utf8")) as unknown);
  } catch {
    const detail =
      `Workable admin UI config file could not be read or is not valid JSON: ${configPath}`;
    reportConfigFileError(detail);
    return {
      error: publicConfigFileError(env, detail),
    };
  }
}

function validateConfigFile(value: unknown): {
  config?: WorkableAdminConfig;
  error?: string;
} {
  if (!isConfigObject(value)) {
    return { error: "Workable admin UI config must contain a JSON object." };
  }

  const topLevelError = validateOptionalProperties(
    value,
    "Workable admin UI config",
    ["authProvider", "apiUrl", "sessionSecret", "sessionCookieName"],
    (item) => typeof item === "string",
    "a string"
  ) ?? validateOptionalProperties(
    value,
    "Workable admin UI config",
    ["allowedApiUrls"],
    isStringArray,
    "an array of strings"
  ) ?? validateOptionalProperties(
    value,
    "Workable admin UI config",
    ["allowAnonymous"],
    (item) => typeof item === "boolean",
    "a boolean"
  );
  if (topLevelError) {
    return { error: topLevelError };
  }

  const basicAuth = value.basicAuth;
  if (basicAuth !== undefined) {
    if (!isConfigObject(basicAuth)) {
      return { error: "Workable admin UI config basicAuth must be a JSON object." };
    }

    const basicAuthError = validateOptionalProperties(
      basicAuth,
      "Workable admin UI config basicAuth",
      ["enabled"],
      (item) => typeof item === "boolean",
      "a boolean"
    ) ?? validateOptionalProperties(
      basicAuth,
      "Workable admin UI config basicAuth",
      ["username", "password"],
      (item) => typeof item === "string",
      "a string"
    );
    if (basicAuthError) {
      return { error: basicAuthError };
    }
  }

  const entraId = value.entraId;
  if (entraId !== undefined) {
    if (!isConfigObject(entraId)) {
      return { error: "Workable admin UI config entraId must be a JSON object." };
    }

    const entraIdError = validateOptionalProperties(
      entraId,
      "Workable admin UI config entraId",
      ["tenantId", "clientId", "clientSecret", "redirectUri", "authorityHost"],
      (item) => typeof item === "string",
      "a string"
    ) ?? validateOptionalProperties(
      entraId,
      "Workable admin UI config entraId",
      ["allowedEmails", "allowedEmailDomains"],
      isStringArray,
      "an array of strings"
    );
    if (entraIdError) {
      return { error: entraIdError };
    }
  }

  return { config: value as WorkableAdminConfig };
}

function validateOptionalProperties(
  value: Record<string, unknown>,
  prefix: string,
  propertyNames: readonly string[],
  isValid: (value: unknown) => boolean,
  expectedType: string
) {
  for (const propertyName of propertyNames) {
    const propertyValue = value[propertyName];
    if (propertyValue !== undefined && !isValid(propertyValue)) {
      return `${prefix}.${propertyName} must be ${expectedType}.`;
    }
  }

  return undefined;
}

function isConfigObject(value: unknown): value is Record<string, unknown> {
  return Boolean(value) && typeof value === "object" && !Array.isArray(value);
}

function isStringArray(value: unknown) {
  return Array.isArray(value) && value.every((item) => typeof item === "string");
}

function publicConfigFileError(
  env: AdminSecurityEnvironment,
  diagnosticDetail: string
) {
  return isProduction(env) ? PRODUCTION_CONFIG_FILE_ERROR : diagnosticDetail;
}

function reportConfigFileError(detail: string) {
  if (reportedConfigFileErrors.has(detail)) {
    return;
  }

  reportedConfigFileErrors.add(detail);
  console.error(`[workable-admin-ui] ${detail}`);
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

function parsePositiveIntegerSetting(
  name: string,
  value: string | undefined,
  fallback: unknown,
  defaultValue: number
): { value: number; error?: string } {
  if (value !== undefined) {
    const normalized = value.trim();
    if (!/^[1-9]\d*$/.test(normalized)) {
      return { value: defaultValue, error: `${name} must be a positive integer.` };
    }

    const parsed = Number(normalized);
    return Number.isSafeInteger(parsed)
      ? { value: parsed }
      : { value: defaultValue, error: `${name} must be a positive safe integer.` };
  }

  if (fallback === undefined) {
    return { value: defaultValue };
  }

  return typeof fallback === "number" && Number.isSafeInteger(fallback) && fallback > 0
    ? { value: fallback }
    : { value: defaultValue, error: `${name} must be a positive safe integer.` };
}

function parseConfiguredBoolean(
  name: string,
  value: string | undefined,
  fallback: unknown,
  defaultValue: boolean
): { value: boolean; error?: string } {
  if (value !== undefined) {
    const parsed = parseBoolean(value);
    return parsed === undefined
      ? { value: defaultValue, error: `${name} must be either 'true' or 'false'.` }
      : { value: parsed };
  }

  if (fallback === undefined) {
    return { value: defaultValue };
  }

  return typeof fallback === "boolean"
    ? { value: fallback }
    : { value: defaultValue, error: "Workable admin UI basicAuth.enabled must be a boolean." };
}
