const normalizationOrigin = "https://workable.invalid";

export function normalizeAdminReturnPath(value: string | null | undefined) {
  if (!value?.startsWith("/")) {
    return "/";
  }

  try {
    const base = new URL(normalizationOrigin);
    const target = new URL(value, base);
    if (target.origin !== base.origin) {
      return "/";
    }

    return `${target.pathname}${target.search}${target.hash}`;
  } catch {
    return "/";
  }
}
