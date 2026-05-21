import {
  createExpiredAdminSessionCookie,
  createExpiredEntraTargetTokenCookies,
  failureHeaders,
  validateUnsafeRequestOrigin,
} from "@/lib/admin-security";

export async function POST(request: Request) {
  const csrf = validateUnsafeRequestOrigin(request);
  if (!csrf.ok) {
    return Response.json(
      { error: csrf.error },
      {
        status: csrf.status,
        headers: failureHeaders(csrf),
      }
    );
  }

  const headers = new Headers();
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
