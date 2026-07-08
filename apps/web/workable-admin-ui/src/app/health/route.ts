const healthHeaders = {
  "cache-control": "no-store",
  "x-content-type-options": "nosniff",
};

export const dynamic = "force-dynamic";

export async function GET() {
  return Response.json(
    {
      status: "ok",
      service: "workable-admin-ui",
      timestamp: new Date().toISOString(),
    },
    {
      headers: healthHeaders,
    }
  );
}

export async function HEAD() {
  return new Response(null, {
    headers: healthHeaders,
    status: 200,
  });
}
