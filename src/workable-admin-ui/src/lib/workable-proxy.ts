import {
  authenticateAdminRequest,
  createWorkableTargetUrl,
  failureHeaders,
  getMaxProxyBodyBytes,
  validateUnsafeRequestOrigin,
  type AdminSecurityEnvironment,
} from "./admin-security.ts";

const LOCAL_HTTPS_HOSTS = new Set(["localhost", "127.0.0.1", "::1"]);

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

  try {
    const response = await fetchWorkable(target.url, {
      method: request.method,
      headers: {
        accept: "application/json",
        "content-type": request.headers.get("content-type") ?? "application/json",
      },
      body: body.text,
      cache: "no-store",
    });

    const responseBody = await response.arrayBuffer();
    return new Response(responseBody, {
      status: response.status,
      statusText: response.statusText,
      headers: {
        "content-type": response.headers.get("content-type") ?? "application/json",
      },
    });
  } catch {
    return Response.json(
      {
        error: "Unable to reach the Workable HTTP API.",
      },
      { status: 502 }
    );
  }
}

function shouldAllowInsecureLocalHttps(url: URL) {
  return process.env.NODE_ENV !== "production" &&
    url.protocol === "https:" &&
    LOCAL_HTTPS_HOSTS.has(url.hostname);
}

async function fetchWorkable(url: URL, init: RequestInit) {
  if (!shouldAllowInsecureLocalHttps(url)) {
    return fetch(url, init);
  }

  const previous = process.env.NODE_TLS_REJECT_UNAUTHORIZED;
  process.env.NODE_TLS_REJECT_UNAUTHORIZED = "0";

  try {
    return await fetch(url, init);
  } finally {
    if (previous === undefined) {
      delete process.env.NODE_TLS_REJECT_UNAUTHORIZED;
    } else {
      process.env.NODE_TLS_REJECT_UNAUTHORIZED = previous;
    }
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
