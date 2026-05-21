import {
  createExpiredAdminSessionCookie,
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

  return Response.json(
    { ok: true },
    {
      headers: {
        "set-cookie": createExpiredAdminSessionCookie(),
      },
    }
  );
}
