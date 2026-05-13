const DEFAULT_API_URL = process.env.WORKABLE_API_URL ?? "http://localhost:61932/workable";

type RouteContext = {
  params: Promise<{ path: string[] }>;
};

export async function GET(request: Request, context: RouteContext) {
  return proxyWorkable(request, context);
}

export async function POST(request: Request, context: RouteContext) {
  return proxyWorkable(request, context);
}

async function proxyWorkable(request: Request, context: RouteContext) {
  const { path } = await context.params;
  const target = createTargetUrl(request, path);

  if (!target.ok) {
    return Response.json({ error: target.error }, { status: 400 });
  }

  const body =
    request.method === "GET" || request.method === "HEAD"
      ? undefined
      : await request.text();

  try {
    const response = await fetch(target.url, {
      method: request.method,
      headers: {
        accept: "application/json",
        "content-type": request.headers.get("content-type") ?? "application/json",
      },
      body,
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
  } catch (error) {
    return Response.json(
      {
        error:
          error instanceof Error
            ? error.message
            : "Unable to reach the Workable HTTP API.",
      },
      { status: 502 }
    );
  }
}

function createTargetUrl(request: Request, path: string[]) {
  const base = request.headers.get("x-workable-api-url") ?? DEFAULT_API_URL;

  try {
    const baseUrl = new URL(base);
    if (!["http:", "https:"].includes(baseUrl.protocol)) {
      return { ok: false as const, error: "Workable API URL must use http or https." };
    }

    const requestUrl = new URL(request.url);
    const normalizedBase = baseUrl.pathname.replace(/\/+$/, "");
    baseUrl.pathname = `${normalizedBase}/${path.map(encodeURIComponent).join("/")}`;
    baseUrl.search = requestUrl.search;
    return { ok: true as const, url: baseUrl };
  } catch {
    return { ok: false as const, error: "Workable API URL is not valid." };
  }
}
