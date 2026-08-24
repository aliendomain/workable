const BACKCHANNEL_TIMEOUT_MS = 10_000;
const CACHE_TTL_MS = 5 * 60_000;
const FORCED_REFRESH_COOLDOWN_MS = 30_000;
const MAXIMUM_CACHE_ENTRIES_PER_FETCHER = 32;
export const MAXIMUM_ENTRA_JSON_BYTES = 1024 * 1024;

export type EntraFetchLike = typeof fetch;

type CacheEntry = {
  expiresAt: number;
  value: Promise<unknown>;
  forcedRefresh?: Promise<unknown>;
  lastForcedRefreshAt?: number;
};

let caches = new WeakMap<EntraFetchLike, Map<string, CacheEntry>>();

export async function fetchEntraJson<T>(
  fetcher: EntraFetchLike,
  input: string | URL,
  init: RequestInit = {},
  signal?: AbortSignal
): Promise<{ response: Response; value: T }> {
  const controller = new AbortController();
  const abort = () => controller.abort();
  if (signal?.aborted) {
    controller.abort();
  } else {
    signal?.addEventListener("abort", abort, { once: true });
  }
  const timeout = setTimeout(abort, BACKCHANNEL_TIMEOUT_MS);

  try {
    const response = await fetcher(input, {
      ...init,
      cache: "no-store",
      redirect: "error",
      signal: controller.signal,
    });
    return { response, value: await readBoundedJson<T>(response) };
  } finally {
    clearTimeout(timeout);
    signal?.removeEventListener("abort", abort);
  }
}

export function validateEntraBackchannelUrl(
  value: string,
  authorityHost: string,
  endpointName: string
) {
  let endpoint: URL;
  let authority: URL;
  try {
    endpoint = new URL(value);
    authority = new URL(authorityHost);
  } catch {
    throw new Error(`Microsoft Entra ID ${endpointName} is not a valid URL.`);
  }

  if (
    endpoint.protocol !== "https:" ||
    endpoint.origin !== authority.origin ||
    endpoint.username ||
    endpoint.password ||
    endpoint.hash
  ) {
    throw new Error(
      `Microsoft Entra ID ${endpointName} must use the configured HTTPS authority origin.`
    );
  }

  return endpoint.toString();
}

async function readBoundedJson<T>(response: Response): Promise<T> {
  const declaredLength = Number.parseInt(
    response.headers.get("content-length") ?? "",
    10
  );
  if (Number.isFinite(declaredLength) && declaredLength > MAXIMUM_ENTRA_JSON_BYTES) {
    await cancelBody(response.body);
    throw new Error("Microsoft Entra ID backchannel response was too large.");
  }

  const reader = response.body?.getReader();
  if (!reader) {
    throw new Error("Microsoft Entra ID backchannel response did not include JSON.");
  }

  const chunks: Uint8Array[] = [];
  let totalBytes = 0;
  try {
    while (true) {
      const { done, value } = await reader.read();
      if (done) {
        break;
      }

      totalBytes += value.byteLength;
      if (totalBytes > MAXIMUM_ENTRA_JSON_BYTES) {
        try {
          await reader.cancel();
        } catch {
          // The oversized response is already rejected; cancellation is best effort.
        }
        throw new Error("Microsoft Entra ID backchannel response was too large.");
      }
      chunks.push(value);
    }
  } finally {
    reader.releaseLock();
  }

  const body = new Uint8Array(totalBytes);
  let offset = 0;
  for (const chunk of chunks) {
    body.set(chunk, offset);
    offset += chunk.byteLength;
  }
  return JSON.parse(new TextDecoder().decode(body)) as T;
}

async function cancelBody(body: ReadableStream<Uint8Array> | null) {
  if (!body) {
    return;
  }

  try {
    await body.cancel();
  } catch {
    // The oversized response is already rejected; cancellation is best effort.
  }
}

export async function fetchCachedEntraJson<T>(
  fetcher: EntraFetchLike,
  cacheKey: string,
  input: string | URL,
  validate: (value: unknown) => value is T,
  signal?: AbortSignal
): Promise<T> {
  const now = Date.now();
  const cache = getCache(fetcher);
  const cached = cache.get(cacheKey);
  if (cached && cached.expiresAt > now) {
    return await awaitWithSignal(cached.value as Promise<T>, signal);
  }
  if (cached) {
    cache.delete(cacheKey);
  }

  if (cache.size >= MAXIMUM_CACHE_ENTRIES_PER_FETCHER) {
    cache.delete(cache.keys().next().value as string);
  }

  const value = loadAndValidateJson(fetcher, input, validate);
  cache.set(cacheKey, { expiresAt: now + CACHE_TTL_MS, value });
  void value.catch(() => {
    if (cache.get(cacheKey)?.value === value) {
      cache.delete(cacheKey);
    }
  });
  return await awaitWithSignal(value, signal);
}

export async function refreshCachedEntraJson<T>(
  fetcher: EntraFetchLike,
  cacheKey: string,
  input: string | URL,
  validate: (value: unknown) => value is T,
  staleValue: T,
  signal?: AbortSignal
): Promise<T> {
  const cache = getCache(fetcher);
  const cached = cache.get(cacheKey);
  if (!cached) {
    return await fetchCachedEntraJson(fetcher, cacheKey, input, validate, signal);
  }

  const current = await awaitWithSignal(cached.value as Promise<T>, signal);
  if (current !== staleValue || cache.get(cacheKey) !== cached) {
    return await fetchCachedEntraJson(fetcher, cacheKey, input, validate, signal);
  }
  if (cached.forcedRefresh) {
    return await awaitWithSignal(cached.forcedRefresh as Promise<T>, signal);
  }

  const now = Date.now();
  if (cached.lastForcedRefreshAt !== undefined &&
    now - cached.lastForcedRefreshAt < FORCED_REFRESH_COOLDOWN_MS) {
    return current;
  }

  const refresh = loadAndValidateJson(fetcher, input, validate);
  cached.lastForcedRefreshAt = now;
  cached.forcedRefresh = refresh;
  void refresh.then(
    (refreshed) => {
      if (cache.get(cacheKey) === cached && cached.forcedRefresh === refresh) {
        cache.set(cacheKey, {
          expiresAt: Date.now() + CACHE_TTL_MS,
          value: Promise.resolve(refreshed),
          lastForcedRefreshAt: now,
        });
      }
    },
    () => {
      if (cache.get(cacheKey) === cached && cached.forcedRefresh === refresh) {
        cached.forcedRefresh = undefined;
      }
    }
  );
  return await awaitWithSignal(refresh, signal);
}

async function loadAndValidateJson<T>(
  fetcher: EntraFetchLike,
  input: string | URL,
  validate: (value: unknown) => value is T
) {
  const { response, value } = await fetchEntraJson<unknown>(fetcher, input);
  if (!response.ok) {
    throw new Error(`Microsoft Entra ID backchannel request failed (${response.status}).`);
  }
  if (!validate(value)) {
    throw new Error("Microsoft Entra ID backchannel response was malformed.");
  }
  return value;
}

function getCache(fetcher: EntraFetchLike) {
  let cache = caches.get(fetcher);
  if (!cache) {
    cache = new Map();
    caches.set(fetcher, cache);
  }
  return cache;
}

function awaitWithSignal<T>(value: Promise<T>, signal?: AbortSignal) {
  if (!signal) {
    return value;
  }
  if (signal.aborted) {
    return Promise.reject(new Error("Microsoft Entra ID backchannel request was cancelled."));
  }

  return new Promise<T>((resolve, reject) => {
    const abort = () => {
      reject(new Error("Microsoft Entra ID backchannel request was cancelled."));
    };
    signal.addEventListener("abort", abort, { once: true });
    void value.then(resolve, reject).finally(() => {
      signal.removeEventListener("abort", abort);
    });
  });
}

export function resetEntraBackchannelCachesForTests() {
  caches = new WeakMap();
}
