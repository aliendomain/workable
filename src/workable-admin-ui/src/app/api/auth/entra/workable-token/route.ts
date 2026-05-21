import {
  authenticateAdminRequest,
  createEntraTargetAccessTokenResponse,
  failureHeaders,
} from "@/lib/admin-security";

export async function GET(request: Request) {
  const authentication = authenticateAdminRequest(request.headers);
  if (!authentication.ok) {
    return Response.json(
      { error: authentication.error },
      {
        status: authentication.status,
        headers: failureHeaders(authentication),
      }
    );
  }

  return createEntraTargetAccessTokenResponse(request);
}
