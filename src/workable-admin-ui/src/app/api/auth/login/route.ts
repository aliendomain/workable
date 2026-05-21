import {
  createAdminSessionCookie,
  createExpiredEntraTargetTokenCookies,
  failureHeaders,
  validateUnsafeRequestOrigin,
  verifyAdminCredentials,
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

  const credentials = await readCredentials(request);
  if (!credentials) {
    return Response.json(
      { error: "Username and password are required." },
      { status: 400 }
    );
  }

  const verification = verifyAdminCredentials(
    credentials.userName,
    credentials.password
  );
  if (!verification.ok) {
    return Response.json(
      { error: verification.error },
      {
        status: verification.status,
        headers: failureHeaders(verification),
      }
    );
  }

  const cookie = createAdminSessionCookie(verification.identity.name, request);
  if (!cookie.ok) {
    return Response.json(
      { error: cookie.error },
      {
        status: cookie.status,
        headers: failureHeaders(cookie),
      }
    );
  }

  const headers = new Headers();
  headers.append("set-cookie", cookie.header);
  for (const staleCookie of createExpiredEntraTargetTokenCookies()) {
    headers.append("set-cookie", staleCookie);
  }

  return Response.json(
    {
      userName: verification.identity.name,
    },
    {
      headers,
    }
  );
}

async function readCredentials(request: Request) {
  try {
    const contentType = request.headers.get("content-type") ?? "";
    if (contentType.includes("application/json")) {
      const body = await request.json();
      return normalizeCredentials(body);
    }

    const form = await request.formData();
    return normalizeCredentials({
      userName: form.get("userName"),
      password: form.get("password"),
    });
  } catch {
    return null;
  }
}

function normalizeCredentials(value: unknown) {
  if (!value || typeof value !== "object") {
    return null;
  }

  const record = value as Record<string, unknown>;
  const userName = record.userName ?? record.username;
  const password = record.password;
  return typeof userName === "string" && typeof password === "string"
    ? { userName, password }
    : null;
}
