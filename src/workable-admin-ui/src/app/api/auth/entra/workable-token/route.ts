import {
  authenticateAdminRequest,
  createEntraTargetAccessTokenResponse,
  failureHeaders,
} from "@/lib/admin-security";

export async function GET(request: Request) {
  const authentication = authenticateAdminRequest(request.headers, process.env, request);
  if (!authentication.ok) {
    return Response.json(
      { error: authentication.error },
      {
        status: authentication.status,
        headers: failureHeaders(authentication),
      }
    );
  }

  const response = await createEntraTargetAccessTokenResponse(request);
  if (authentication.sessionCookieHeader) {
    response.headers.append("set-cookie", authentication.sessionCookieHeader);
  }
  return response;
}
