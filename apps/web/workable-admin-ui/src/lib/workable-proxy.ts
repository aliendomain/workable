import {
  authenticateAdminRequest,
  createWorkableTargetUrl,
  failureHeaders,
  getEntraTargetAccessToken,
  getMaxProxyBodyBytes,
  validateUnsafeRequestOrigin,
  type AdminSecurityEnvironment,
} from "./admin-security.ts";

type WorkableProxyOptions = {
  env?: AdminSecurityEnvironment;
  fetch?: typeof fetch;
};

const noStoreHeaders = {
  "cache-control": "no-store",
  "x-content-type-options": "nosniff",
};
const workableUpstreamResponseHeader = "x-workable-upstream-response";

const hostedIssuerMismatchError =
  "The hosted Workable API rejected the bearer token because the token issuer does not match its Entra configuration. Check that the target API app registration is configured to issue v2 access tokens.";
const hostedAudienceMismatchError =
  "The hosted Workable API rejected the bearer token because the token audience does not match its Entra configuration. Check that the admin UI target scope and the hosted API accepted audiences refer to the same app registration.";
const hostedInvalidTokenError =
  "The hosted Workable API rejected the bearer token. Check the target API token version, audience, and delegated scope configuration.";

export async function proxyWorkableRequest(
  request: Request,
  path: readonly string[],
  options: WorkableProxyOptions = {}
) {
  const env = options.env;
  const authentication = authenticateAdminRequest(request.headers, env, request);
  if (!authentication.ok) {
    return Response.json(
      { error: authentication.error },
      {
        status: authentication.status,
        headers: secureJsonHeaders(failureHeaders(authentication)),
      }
    );
  }

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

  const body = await readBody(request, getMaxProxyBodyBytes(env));
  if (!body.ok) {
    return Response.json(
      { error: body.error },
      { status: body.status, headers: secureJsonHeaders() }
    );
  }

  const target = createWorkableTargetUrl(request, path, env);

  if (!target.ok) {
    return Response.json(
      { error: target.error },
      { status: 400, headers: secureJsonHeaders() }
    );
  }

  const targetAccessToken = await getEntraTargetAccessToken(
    request,
    env,
    options.fetch ?? fetch,
    {
      requestedApiUrl: target.baseUrl,
    }
  );
  if (!targetAccessToken.ok) {
    return Response.json(
      { error: targetAccessToken.error },
      {
        status: targetAccessToken.status,
        headers: withCookies(
          secureJsonHeaders(failureHeaders(targetAccessToken)),
          targetAccessToken.setCookieHeaders
        ),
      }
    );
  }

  try {
    const response = await (options.fetch ?? fetch)(target.url, {
      method: request.method,
      headers: {
        accept: "application/json",
        ...(targetAccessToken.accessToken
          ? { authorization: `Bearer ${targetAccessToken.accessToken}` }
          : {}),
        "content-type": request.headers.get("content-type") ?? "application/json",
      },
      body: body.text,
      cache: "no-store",
      redirect: "error",
      signal: request.signal,
    });

    const hostedAuthenticationError =
      response.status === 401
        ? createHostedAuthenticationError(response.headers.get("www-authenticate"))
        : null;
    if (hostedAuthenticationError) {
      await cancelResponseBody(response.body);
      return Response.json(
        { error: hostedAuthenticationError },
        {
          status: response.status,
          headers: withCookies(
            secureJsonHeaders(),
            [
              ...targetAccessToken.setCookieHeaders,
              ...(authentication.sessionCookieHeader ? [authentication.sessionCookieHeader] : []),
            ]
          ),
        }
      );
    }

    const responseBody =
      request.method === "HEAD" || isNullBodyStatus(response.status)
        ? null
        : response.body;
    return new Response(responseBody, {
      status: response.status,
      statusText: response.statusText,
      headers: withCookies(
        {
          ...noStoreHeaders,
          "content-type": createSafeProxyContentType(
            response.headers.get("content-type")
          ),
          [workableUpstreamResponseHeader]: "true",
        },
        [
          ...targetAccessToken.setCookieHeaders,
          ...(authentication.sessionCookieHeader ? [authentication.sessionCookieHeader] : []),
        ]
      ),
    });
  } catch {
    return Response.json(
      {
        error: createProxyReachabilityError(target.url),
      },
      {
        status: 502,
        headers: withCookies(
          secureJsonHeaders(),
          [
            ...targetAccessToken.setCookieHeaders,
            ...(authentication.sessionCookieHeader ? [authentication.sessionCookieHeader] : []),
          ]
        ),
      }
    );
  }
}

async function cancelResponseBody(body: ReadableStream<Uint8Array> | null) {
  if (!body) {
    return;
  }

  try {
    await body.cancel();
  } catch {
    // Releasing the upstream response is best effort; keep the client-safe response stable.
  }
}

function createHostedAuthenticationError(challenge: string | null) {
  if (!challenge || !/\bbearer\b/i.test(challenge)) {
    return null;
  }

  const error = readBearerChallengeParameter(challenge, "error")?.toLowerCase() ?? "";
  const description = readBearerChallengeParameter(challenge, "error_description") ?? "";
  const normalizedDescription = description.toLowerCase();

  if (
    description.includes("IDX10205") ||
    normalizedDescription.includes("issuer validation failed")
  ) {
    return hostedIssuerMismatchError;
  }

  if (
    description.includes("IDX10214") ||
    normalizedDescription.includes("audience validation failed")
  ) {
    return hostedAudienceMismatchError;
  }

  if (error === "invalid_token") {
    return hostedInvalidTokenError;
  }

  return null;
}

function readBearerChallengeParameter(challenge: string, name: string) {
  const match = new RegExp(`${name}="([^"]+)"`, "i").exec(challenge);
  return match?.[1]?.trim() ?? null;
}

async function readBody(request: Request, maximumBytes: number) {
  if (request.method === "GET" || request.method === "HEAD") {
    return { ok: true as const, text: undefined };
  }

  const reader = request.body?.getReader();
  if (!reader) {
    return { ok: true as const, text: "" };
  }

  const chunks: Uint8Array[] = [];
  let totalBytes = 0;
  try {
    while (true) {
      const { done, value } = await reader.read();
      if (done) {
        break;
      }

      totalBytes += value.byteLength;
      if (totalBytes > maximumBytes) {
        await cancelRequestBody(reader);
        return {
          ok: false as const,
          status: 413,
          error: "Workable admin UI proxy request body is too large.",
        };
      }

      chunks.push(value);
    }
  } catch {
    return {
      ok: false as const,
      status: 400,
      error: "Workable admin UI proxy request body could not be read.",
    };
  } finally {
    reader.releaseLock();
  }

  const buffer = new Uint8Array(totalBytes);
  let offset = 0;
  for (const chunk of chunks) {
    buffer.set(chunk, offset);
    offset += chunk.byteLength;
  }

  return { ok: true as const, text: new TextDecoder().decode(buffer) };
}

async function cancelRequestBody(reader: ReadableStreamDefaultReader<Uint8Array>) {
  try {
    await reader.cancel();
  } catch {
    // The request is already rejected; cancellation is best effort.
  }
}

function createProxyReachabilityError(url: URL) {
  if (url.protocol === "https:" && isLoopbackHost(url.hostname)) {
    return "Unable to reach the Workable HTTP API. Local HTTPS loopback hosts must present a trusted development certificate to the admin UI proxy.";
  }

  return "Unable to reach the Workable HTTP API.";
}

function isLoopbackHost(hostname: string) {
  const normalized = hostname.toLowerCase();
  return normalized === "localhost" ||
    normalized === "127.0.0.1" ||
    normalized === "::1" ||
    normalized === "[::1]";
}

function withCookies(
  headers: HeadersInit,
  cookies: readonly string[]
) {
  const responseHeaders = new Headers(headers);
  for (const cookie of cookies) {
    responseHeaders.append("set-cookie", cookie);
  }

  return responseHeaders;
}

function secureJsonHeaders(headers: HeadersInit = {}) {
  const result = new Headers(headers);
  for (const [name, value] of Object.entries(noStoreHeaders)) {
    result.set(name, value);
  }
  return result;
}

function createSafeProxyContentType(contentType: string | null) {
  const normalized = contentType?.toLowerCase() ?? "";
  if (
    normalized.includes("application/json") ||
    normalized.includes("+json")
  ) {
    return "application/json; charset=utf-8";
  }

  return "text/plain; charset=utf-8";
}

function isNullBodyStatus(status: number) {
  return status === 204 || status === 205 || status === 304;
}
