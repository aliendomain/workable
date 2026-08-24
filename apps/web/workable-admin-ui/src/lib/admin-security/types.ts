export type AdminAuthProvider = "basic" | "entra";

export type AdminSecurityEnvironment = Record<string, string | undefined>;

export type AdminIdentity = {
  name: string;
  scheme: "anonymous" | "basic" | "session" | "entra";
  provider?: AdminAuthProvider;
  email?: string;
  entraSubject?: string;
};

export type AdminSecurityFailure = {
  ok: false;
  status: number;
  error: string;
  headers?: Record<string, string>;
  setCookieHeaders?: readonly string[];
};

export type AdminSecuritySuccess = {
  ok: true;
  identity: AdminIdentity;
  sessionCookieHeader?: string;
};

export type AdminSecurityResult = AdminSecuritySuccess | AdminSecurityFailure;

export type TargetUrlResult =
  | { ok: true; url: URL; baseUrl: URL }
  | { ok: false; error: string };

export type AdminSessionCookieResult =
  | {
      ok: true;
      header: string;
      identity: import("./session.ts").AdminSessionIdentity;
    }
  | AdminSecurityFailure;

export function authenticatedIdentity(
  name: string,
  scheme: AdminIdentity["scheme"],
  provider?: AdminAuthProvider,
  email?: string,
  sessionCookieHeader?: string,
  entraSubject?: string
): AdminSecuritySuccess {
  return {
    ok: true,
    identity: {
      name,
      scheme,
      provider,
      email,
      entraSubject,
    },
    sessionCookieHeader,
  };
}

export function securityFailure(
  status: number,
  error: string,
  headers?: Record<string, string>,
  setCookieHeaders?: readonly string[]
): AdminSecurityFailure {
  return {
    ok: false,
    status,
    error,
    headers,
    setCookieHeaders,
  };
}

export function serviceUnavailable(error: string): AdminSecurityFailure {
  return securityFailure(503, error);
}
