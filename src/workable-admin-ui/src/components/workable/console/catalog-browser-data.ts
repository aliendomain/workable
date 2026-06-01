"use client";

import { useEffect, useState, useSyncExternalStore } from "react";
import type { Loadable } from "@/components/features/console/types";
import {
  WorkableApiError,
  workableFetch,
  type WorkOverviewCatalogCategoryItem,
  type WorkOverviewDefinitionItem,
  type WorkableConnection,
  type WorkInfo,
} from "@/lib/workable";
import { normalizeCategoryFilter } from "@/components/workable/console/catalog-path";
export {
  normalizeCategoryFilter,
  splitCatalogPath,
} from "@/components/workable/console/catalog-path";

export type DefinitionCatalogLevel = {
  categories: WorkOverviewCatalogCategoryItem[];
  definitions: WorkOverviewDefinitionItem[];
};

const definitionCatalogLevelCache = new Map<string, DefinitionCatalogLevel>();
const definitionCatalogLevelCacheListeners = new Set<() => void>();
let definitionCatalogLevelCacheVersion = 0;

export function clearDefinitionCatalogLevelCache() {
  definitionCatalogLevelCache.clear();
  publishDefinitionCatalogLevelCacheChange();
}

export function createDefinitionCatalogLevelPath(category: string) {
  const query = new URLSearchParams({ level: "true" });
  const normalizedCategory = normalizeCategoryFilter(category);
  if (normalizedCategory) {
    query.set("category", normalizedCategory);
  }

  return `definitions?${query.toString()}`;
}

export function invalidateDefinitionCatalogLevelCache(connection: WorkableConnection) {
  const keyPrefix = createDefinitionCatalogConnectionKey(connection);
  let changed = false;
  for (const key of definitionCatalogLevelCache.keys()) {
    if (key.startsWith(`${keyPrefix}\n`)) {
      definitionCatalogLevelCache.delete(key);
      changed = true;
    }
  }
  if (changed) {
    publishDefinitionCatalogLevelCacheChange();
  }
}

export function invalidateDefinitionCatalogLevelCacheByApiUrl(apiUrl: string) {
  const normalizedApiUrl = apiUrl.trim();
  let changed = false;
  for (const key of definitionCatalogLevelCache.keys()) {
    if (key.startsWith(`${normalizedApiUrl}\n`)) {
      definitionCatalogLevelCache.delete(key);
      changed = true;
    }
  }
  if (changed) {
    publishDefinitionCatalogLevelCacheChange();
  }
}

export async function fetchDefinitionCatalogInfo(
  connection: WorkableConnection,
  definitionId: string
) {
  try {
    return await workableFetch<WorkInfo>(connection, `definitions/${definitionId}/info`);
  } catch (error) {
    if (error instanceof WorkableApiError && error.status === 404) {
      invalidateDefinitionCatalogLevelCache(connection);
    }
    throw error;
  }
}

export function useDefinitionCatalogLevel(
  connection: WorkableConnection | null,
  path: string | null,
  refreshToken: number
): Loadable<DefinitionCatalogLevel> {
  const cacheVersion = useSyncExternalStore(
    subscribeToDefinitionCatalogLevelCache,
    getDefinitionCatalogLevelCacheVersion,
    getDefinitionCatalogLevelCacheVersion
  );
  const cacheKey = connection && path
    ? createDefinitionCatalogLevelCacheKey(connection, path)
    : null;
  const cachedLevel = cacheKey
    ? definitionCatalogLevelCache.get(cacheKey)
    : undefined;
  const [state, setState] = useState<Loadable<DefinitionCatalogLevel>>({
    data: cachedLevel,
    loading: Boolean(connection && path && !cachedLevel),
  });
  const apiUrl = connection?.apiUrl ?? "";
  const systemName = connection?.systemName;

  useEffect(() => {
    if (!connection || !path) {
      queueMicrotask(() => setState({ loading: false }));
      return;
    }

    const cached = cacheKey ? definitionCatalogLevelCache.get(cacheKey) : undefined;
    if (cached) {
      queueMicrotask(() => setState({
        data: cached,
        loading: false,
        refreshing: false,
      }));
      return;
    }

    let canceled = false;
    queueMicrotask(() => {
      if (!canceled) {
        setState((current) => ({
          ...current,
          error: undefined,
          loading: current.data === undefined,
          refreshing: current.data !== undefined,
        }));
      }
    });

    const requestConnection = { apiUrl, systemName };
    workableFetch<DefinitionCatalogLevel>(requestConnection, path)
      .then((data) => {
        if (!canceled) {
          if (cacheKey) {
            definitionCatalogLevelCache.set(cacheKey, data);
          }
          setState({ data, loading: false, refreshing: false });
        }
      })
      .catch((error) => {
        if (!canceled) {
          if (error instanceof WorkableApiError && error.status === 404) {
            clearDefinitionCatalogLevelCache();
          }
          const detail = error instanceof Error ? error.message : "Request failed.";
          setState((current) =>
            current.error === detail && !current.loading && !current.refreshing
              ? current
              : {
                  data: current.data,
                  error: detail,
                  loading: false,
                  refreshing: false,
                }
          );
        }
      });

    return () => {
      canceled = true;
    };
  }, [apiUrl, systemName, path, refreshToken, connection, cacheKey, cacheVersion]);

  return state;
}

function createDefinitionCatalogConnectionKey(connection: WorkableConnection) {
  return `${connection.apiUrl.trim()}\n${connection.systemName?.trim() ?? ""}`;
}

function createDefinitionCatalogLevelCacheKey(
  connection: WorkableConnection,
  path: string
) {
  return `${createDefinitionCatalogConnectionKey(connection)}\n${path}`;
}

function subscribeToDefinitionCatalogLevelCache(listener: () => void) {
  definitionCatalogLevelCacheListeners.add(listener);
  return () => {
    definitionCatalogLevelCacheListeners.delete(listener);
  };
}

function getDefinitionCatalogLevelCacheVersion() {
  return definitionCatalogLevelCacheVersion;
}

function publishDefinitionCatalogLevelCacheChange() {
  definitionCatalogLevelCacheVersion += 1;
  for (const listener of definitionCatalogLevelCacheListeners) {
    listener();
  }
}
