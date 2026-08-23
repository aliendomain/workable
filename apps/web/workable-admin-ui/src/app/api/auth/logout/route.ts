import {
  createExpiredAdminSessionCookie,
  createAdminLogoutTombstoneCookie,
  createExpiredEntraTransactionCookies,
  createExpiredEntraTargetTokenCookies,
  failureHeaders,
  validateUnsafeRequestOrigin,
} from "@/lib/admin-security";

const noStoreHeaders = {
  "cache-control": "no-store",
  "x-content-type-options": "nosniff",
};

export async function POST(request: Request) {
  const csrf = validateUnsafeRequestOrigin(request);
  if (!csrf.ok) {
    return Response.json(
      { error: csrf.error },
      {
        status: csrf.status,
        headers: secureJsonHeaders(failureHeaders(csrf)),
      }
    );
  }

  const headers = new Headers(noStoreHeaders);
  headers.append("set-cookie", createExpiredAdminSessionCookie());
  headers.append("set-cookie", createAdminLogoutTombstoneCookie(request));
  for (const cookie of createExpiredEntraTransactionCookies()) {
    headers.append("set-cookie", cookie);
  }
  for (const cookie of createExpiredEntraTargetTokenCookies(request.headers)) {
    headers.append("set-cookie", cookie);
  }

  return Response.json(
    { ok: true },
    {
      headers,
    }
  );
}

function secureJsonHeaders(headers: HeadersInit = {}) {
  const result = new Headers(headers);
  for (const [name, value] of Object.entries(noStoreHeaders)) {
    result.set(name, value);
  }
  return result;
}
