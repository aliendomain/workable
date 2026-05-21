export function parseCookieHeader(value: string | null) {
  const cookies = new Map<string, string>();
  for (const pair of value?.split(";") ?? []) {
    const separator = pair.indexOf("=");
    if (separator < 0) {
      continue;
    }

    const name = pair.slice(0, separator).trim();
    const rawValue = pair.slice(separator + 1).trim();
    try {
      cookies.set(name, decodeURIComponent(rawValue));
    } catch {
      cookies.set(name, rawValue);
    }
  }

  return cookies;
}

export function serializeCookie(
  name: string,
  value: string,
  options: {
    maxAgeSeconds: number;
    secure: boolean;
    httpOnly?: boolean;
    sameSite?: "Lax" | "Strict";
  }
) {
  const attributes = [
    `${name}=${encodeURIComponent(value)}`,
    "Path=/",
    `SameSite=${options.sameSite ?? "Lax"}`,
    `Max-Age=${options.maxAgeSeconds}`,
  ];

  if (options.httpOnly !== false) {
    attributes.push("HttpOnly");
  }

  if (options.secure) {
    attributes.push("Secure");
  }

  return attributes.join("; ");
}

export function serializeExpiredCookie(name: string) {
  return `${name}=; HttpOnly; Path=/; SameSite=Lax; Max-Age=0`;
}

export function shouldSecureCookie(request: Request, isProduction: boolean) {
  return isProduction || new URL(request.url).protocol === "https:";
}
