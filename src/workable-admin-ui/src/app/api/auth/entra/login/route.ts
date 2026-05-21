import { createEntraAuthorizationResponse } from "@/lib/admin-security";

export async function GET(request: Request) {
  return createEntraAuthorizationResponse(request);
}
