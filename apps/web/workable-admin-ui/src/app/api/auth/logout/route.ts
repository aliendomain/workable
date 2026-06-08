import {
  createExpiredAdminSessionCookie,
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
  for (const cookie of createExpiredEntraTargetTokenCookies()) {
    headers.append("set-cookie", cookie);
  }

  return Response.json(
    { ok: true },
    {
      headers,
    }
  );
}

function secureJsonHeaders(headers: Record<string, string> = {}) {
  return {
    ...headers,
    ...noStoreHeaders,
  };
}
