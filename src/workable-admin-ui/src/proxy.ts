import { NextResponse, type NextRequest } from "next/server";
import {
  authenticateAdminRequest,
  failureHeaders,
} from "@/lib/admin-security";

export function proxy(request: NextRequest) {
  if (isPublicAdminRoute(request.nextUrl.pathname)) {
    return NextResponse.next();
  }

  const authentication = authenticateAdminRequest(request.headers);
  if (authentication.ok) {
    return NextResponse.next();
  }

  if (request.nextUrl.pathname.startsWith("/api/")) {
    return Response.json(
      { error: authentication.error },
      {
        status: authentication.status,
        headers: failureHeaders(authentication),
      }
    );
  }

  const loginUrl = new URL("/login", request.url);
  loginUrl.searchParams.set(
    "next",
    `${request.nextUrl.pathname}${request.nextUrl.search}`
  );
  return NextResponse.redirect(loginUrl);
}

function isPublicAdminRoute(pathname: string) {
  return pathname === "/login" ||
    pathname === "/api/auth/login" ||
    pathname === "/api/auth/logout" ||
    pathname === "/api/auth/entra/login" ||
    pathname === "/api/auth/entra/callback";
}

export const config = {
  matcher: [
    "/((?!_next/static|_next/image|favicon.ico|workable-favicon.png|workable-logo.*\\.png|.*\\.(?:svg|png|jpg|jpeg|gif|webp|ico|css|js|map|txt|xml|webmanifest)$).*)",
  ],
};
