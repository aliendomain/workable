import {
  createAdminSessionCookie,
  createExpiredEntraTargetTokenCookies,
  failureHeaders,
  validateUnsafeRequestOrigin,
  verifyAdminCredentials,
} from "@/lib/admin-security";

const noStoreHeaders = {
  "cache-control": "no-store",
  "x-content-type-options": "nosniff",
};
const maximumLoginBodyBytes = 16 * 1024;

export async function POST(request: Request) {
  const loginStartedAt = Date.now();
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

  const credentials = await readCredentials(request);
  if (credentials.status === "too-large") {
    return Response.json(
      { error: "The login request body is too large." },
      { status: 413, headers: secureJsonHeaders() }
    );
  }
  if (credentials.status === "invalid") {
    return Response.json(
      { error: "Username and password are required." },
      { status: 400, headers: secureJsonHeaders() }
    );
  }

  const verification = verifyAdminCredentials(
    credentials.value.userName,
    credentials.value.password,
    process.env,
    request.headers
  );
  if (!verification.ok) {
    return Response.json(
      { error: verification.error },
      {
        status: verification.status,
        headers: secureJsonHeaders(failureHeaders(verification)),
      }
    );
  }

  const cookie = createAdminSessionCookie(
    verification.identity.name,
    request,
    process.env,
    undefined,
    undefined,
    loginStartedAt
  );
  if (!cookie.ok) {
    return Response.json(
      { error: cookie.error },
      {
        status: cookie.status,
        headers: secureJsonHeaders(failureHeaders(cookie)),
      }
    );
  }

  const headers = new Headers(noStoreHeaders);
  headers.append("set-cookie", cookie.header);
  for (const staleCookie of createExpiredEntraTargetTokenCookies(request.headers)) {
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

function secureJsonHeaders(headers: HeadersInit = {}) {
  const result = new Headers(headers);
  for (const [name, value] of Object.entries(noStoreHeaders)) {
    result.set(name, value);
  }
  return result;
}

async function readCredentials(request: Request) {
  try {
    const body = await readBoundedBody(request.body);
    if (!body) {
      return { status: "too-large" } as const;
    }

    const contentType = request.headers.get("content-type") ?? "";
    if (contentType.includes("application/json")) {
      const parsed = JSON.parse(new TextDecoder().decode(body));
      const credentials = normalizeCredentials(parsed);
      return credentials
        ? { status: "ok", value: credentials } as const
        : { status: "invalid" } as const;
    }

    const boundedRequest = new Request(request.url, {
      method: "POST",
      headers: { "content-type": contentType },
      body,
    });
    const form = await boundedRequest.formData();
    const credentials = normalizeCredentials({
      userName: form.get("userName"),
      password: form.get("password"),
    });
    return credentials
      ? { status: "ok", value: credentials } as const
      : { status: "invalid" } as const;
  } catch {
    return { status: "invalid" } as const;
  }
}

async function readBoundedBody(body: ReadableStream<Uint8Array> | null) {
  if (!body) {
    return new Uint8Array();
  }

  const reader = body.getReader();
  const chunks: Uint8Array[] = [];
  let length = 0;
  try {
    while (true) {
      const { done, value } = await reader.read();
      if (done) {
        break;
      }

      length += value.byteLength;
      if (length > maximumLoginBodyBytes) {
        try {
          await reader.cancel();
        } catch {
          // The request is already rejected; cancellation is best effort.
        }
        return null;
      }

      chunks.push(value);
    }
  } finally {
    reader.releaseLock();
  }

  const combined = new Uint8Array(length);
  let offset = 0;
  for (const chunk of chunks) {
    combined.set(chunk, offset);
    offset += chunk.byteLength;
  }
  return combined;
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
