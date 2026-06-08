import { completeEntraLogin } from "@/lib/admin-security";

export async function GET(request: Request) {
  return completeEntraLogin(request);
}
