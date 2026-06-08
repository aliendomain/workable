import type { AdminSecuritySettings } from "./config.ts";
import { constantTimeEquals } from "./crypto.ts";
import {
  authenticatedIdentity,
  securityFailure,
  serviceUnavailable,
  type AdminSecurityResult,
} from "./types.ts";

export function authenticateBasicRequest(
  headers: Headers,
  settings: AdminSecuritySettings
): AdminSecurityResult {
  const configuration = validateBasicSettings(settings);
  if (!configuration.ok) {
    return configuration;
  }

  const credentials = parseBasicAuthorization(headers.get("authorization"));
  if (
    credentials &&
    constantTimeEquals(credentials.userName, settings.userName ?? "") &&
    constantTimeEquals(credentials.password, settings.password ?? "")
  ) {
    return authenticatedIdentity(credentials.userName, "basic", "basic");
  }

  return securityFailure(
    401,
    "Authentication is required for the Workable admin UI."
  );
}

export function verifyBasicCredentials(
  userName: string,
  password: string,
  settings: AdminSecuritySettings
): AdminSecurityResult {
  const configuration = validateBasicSettings(settings);
  if (!configuration.ok) {
    return configuration;
  }

  if (
    !constantTimeEquals(userName, settings.userName ?? "") ||
    !constantTimeEquals(password, settings.password ?? "")
  ) {
    return securityFailure(401, "The username or password is not valid.");
  }

  return authenticatedIdentity(settings.userName ?? userName, "session", "basic");
}

function validateBasicSettings(settings: AdminSecuritySettings): AdminSecurityResult {
  if (settings.authProvider !== "basic") {
    return securityFailure(400, "Basic admin UI authentication is not enabled.");
  }

  if (!settings.userName || !settings.password) {
    return serviceUnavailable(
      "Workable admin UI Basic authentication is not configured. Configure basicAuth in workable-admin.config.local.json or WORKABLE_ADMIN_UI_USERNAME and WORKABLE_ADMIN_UI_PASSWORD."
    );
  }

  return authenticatedIdentity("basic-configured", "basic", "basic");
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
