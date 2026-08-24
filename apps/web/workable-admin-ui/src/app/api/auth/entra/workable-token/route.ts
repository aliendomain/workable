import {
  authenticateAdminRequest,
  createEntraTargetAccessTokenResponse,
  failureHeaders,
} from "@/lib/admin-security";

const noStoreHeaders = {
  "cache-control": "no-store",
  "x-content-type-options": "nosniff",
};

export async function GET(request: Request) {
  const authentication = authenticateAdminRequest(request.headers, process.env, request);
  if (!authentication.ok) {
    return Response.json(
      { error: authentication.error },
      {
        status: authentication.status,
        headers: secureJsonHeaders(failureHeaders(authentication)),
      }
    );
  }

  const response = await createEntraTargetAccessTokenResponse(request);
  if (authentication.sessionCookieHeader) {
    response.headers.append("set-cookie", authentication.sessionCookieHeader);
  }
  return response;
}

function secureJsonHeaders(headers: HeadersInit = {}) {
  const result = new Headers(headers);
  for (const [name, value] of Object.entries(noStoreHeaders)) {
    result.set(name, value);
  }
  return result;
}
