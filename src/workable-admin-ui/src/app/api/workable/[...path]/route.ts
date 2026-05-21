import { proxyWorkableRequest } from "@/lib/workable-proxy";

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
  return proxyWorkableRequest(request, path);
}
