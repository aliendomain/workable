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

export async function proxyWorkableRequest(
  request: Request,
  path: readonly string[],
  options: WorkableProxyOptions = {}
) {
  const env = options.env;
  const authentication = authenticateAdminRequest(request.headers, env);
  if (!authentication.ok) {
    return Response.json(
      { error: authentication.error },
      {
        status: authentication.status,
        headers: failureHeaders(authentication),
      }
    );
  }

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

  const body = await readBody(request, getMaxProxyBodyBytes(env));
  if (!body.ok) {
    return Response.json({ error: body.error }, { status: 413 });
  }

  const target = createWorkableTargetUrl(request, path, env);

  if (!target.ok) {
    return Response.json({ error: target.error }, { status: 400 });
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
          failureHeaders(targetAccessToken),
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
    });

    const responseBody = await response.arrayBuffer();
    return new Response(responseBody, {
      status: response.status,
      statusText: response.statusText,
      headers: withCookies(
        {
          "content-type": response.headers.get("content-type") ?? "application/json",
        },
        targetAccessToken.setCookieHeaders
      ),
    });
  } catch {
    return Response.json(
      {
        error: createProxyReachabilityError(target.url),
      },
      { status: 502 }
    );
  }
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
  while (true) {
    const { done, value } = await reader.read();
    if (done) {
      break;
    }

    totalBytes += value.byteLength;
    if (totalBytes > maximumBytes) {
      await reader.cancel();
      return {
        ok: false as const,
        error: "Workable admin UI proxy request body is too large.",
      };
    }

    chunks.push(value);
  }

  const buffer = new Uint8Array(totalBytes);
  let offset = 0;
  for (const chunk of chunks) {
    buffer.set(chunk, offset);
    offset += chunk.byteLength;
  }

  return { ok: true as const, text: new TextDecoder().decode(buffer) };
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
  headers: Record<string, string>,
  cookies: readonly string[]
) {
  const responseHeaders = new Headers(headers);
  for (const cookie of cookies) {
    responseHeaders.append("set-cookie", cookie);
  }

  return responseHeaders;
}
