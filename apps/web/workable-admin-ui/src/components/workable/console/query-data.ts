"use client";

import { useCallback, useEffect, useRef, useState } from "react";
import { createOverviewComponentScope } from "@/components/workable/console/catalog-path";
import {
  createWorkComponentRequest,
  getWorkComponentData,
  getWorkComponentErrors,
} from "@/components/workable/console/component-results";
import {
  workableQueryFetch,
  type WorkComponentQueryResult,
  type WorkCompletionStatus,
  type WorkKeyKind,
  type WorkViewIterationGridDetailed,
  type WorkViewWorkerGridDetailed,
  type WorkableConnection,
  type WorkerIterationQueryResult,
  type WorkerQueryResult,
  type WorkerState,
} from "@/lib/workable";

export type InfiniteLoadable<TItem> = {
  error?: string;
  hasMore: boolean;
  items: TItem[];
  loading: boolean;
  loadingMore: boolean;
  loadMore: () => void;
  refreshLoadedWindow?: () => void;
  totalCount?: number;
};

export type WorkerQueryFilters = {
  category?: string;
  definitionName?: string;
  includeSubcategories?: boolean;
  keyKind?: WorkKeyKind;
  keyType?: string;
  keyValue?: string;
  states?: WorkerState[];
};

export type IterationQueryFilters = {
  category?: string;
  definitionName?: string;
  keyKind?: WorkKeyKind;
  keyType?: string;
  keyValue?: string;
  statuses?: WorkCompletionStatus[];
};

const queryPageTake = 50;
const maxQueryTake = 50;
const minQueryTake = 1;

export function useInfiniteWorkerQuery(
  connection: WorkableConnection,
  query: WorkerQueryFilters,
  refreshToken: number,
  enabled: boolean
): InfiniteLoadable<WorkViewWorkerGridDetailed> {
  const [state, setState] = useState<{
    error?: string;
    items: WorkViewWorkerGridDetailed[];
    loading: boolean;
    loadingMore: boolean;
    nextSkip: number;
    totalCount?: number;
  }>({
    items: [],
    loading: true,
    loadingMore: false,
    nextSkip: 0,
  });
  const stateRef = useRef(state);
  const requestIdRef = useRef(0);
  const inFlightSkipRef = useRef<number | null>(null);
  const apiUrl = connection.apiUrl;
  const systemName = connection.systemName;
  const key = JSON.stringify(query);
  const resetKey = `${apiUrl}\n${systemName ?? ""}\n${key}`;
  const requestKey = `${resetKey}\n${refreshToken}`;
  const resetKeyRef = useRef<string | undefined>(undefined);
  const loadedRequestKeyRef = useRef<string | undefined>(undefined);
  const boundedTake = Math.min(maxQueryTake, Math.max(minQueryTake, queryPageTake));

  useEffect(() => {
    stateRef.current = state;
  }, [state]);

  const fetchPage = useCallback(async (skip: number) => {
    const parsedQuery = JSON.parse(key) as WorkerQueryFilters;
    const requestConnection = { apiUrl, systemName };

    const result = await workableQueryFetch<WorkComponentQueryResult>(requestConnection, "views/workers", {
      method: "POST",
      body: JSON.stringify({
        components: [
          createWorkComponentRequest("workerGrid", "workerGrid", "detailed", {
            keyKind: parsedQuery.keyKind,
            keyType: parsedQuery.keyType,
            keyValue: parsedQuery.keyValue,
            states: parsedQuery.states,
            skip,
            take: boundedTake,
          }),
        ],
        scope: createOverviewComponentScope({
          category: parsedQuery.category,
          definitionName: parsedQuery.definitionName,
          includeSubcategories: parsedQuery.includeSubcategories,
        }, { emptyValue: undefined, includeSubcategoriesForDefinition: true }),
      }),
    });
    const data = getWorkComponentData<WorkerQueryResult>(result, "workerGrid");
    if (!data) {
      throw new Error(getWorkComponentErrors(result)[0] ?? "Worker grid failed to load.");
    }

    return data;
  }, [apiUrl, boundedTake, key, systemName]);

  const loadPage = useCallback(async (skip: number, append: boolean, requestId: number) => {
    if (!enabled) {
      return;
    }

    setState((current) => ({
      ...current,
      error: undefined,
      loading: !append,
      loadingMore: append,
    }));

    try {
      const data = await fetchPage(skip);

      if (requestIdRef.current !== requestId) {
        return;
      }

      setState((current) => {
        const items = append
          ? appendUniqueWorkers(current.items, data.workers)
          : data.workers;

        return {
          items,
          loading: false,
          loadingMore: false,
          nextSkip: Math.max(current.nextSkip, data.skip + data.workers.length),
          totalCount: data.totalCount,
        };
      });
      if (inFlightSkipRef.current === skip) {
        inFlightSkipRef.current = null;
      }
    } catch (error) {
      if (requestIdRef.current !== requestId) {
        return;
      }
      if (inFlightSkipRef.current === skip) {
        inFlightSkipRef.current = null;
      }

      const detail = error instanceof Error ? error.message : "Request failed.";
      const nextError = `Worker query failed. ${detail}`;
      setState((current) =>
        current.error === nextError && !current.loading && !current.loadingMore
          ? current
          : {
              ...current,
              error: nextError,
              loading: false,
              loadingMore: false,
            }
      );
    }
  }, [enabled, fetchPage]);

  const refreshLoadedWindow = useCallback(() => {
    const current = stateRef.current;
    if (!enabled || current.loading || current.loadingMore) {
      return;
    }

    const requestId = requestIdRef.current + 1;
    requestIdRef.current = requestId;
    inFlightSkipRef.current = null;
    const targetCount = Math.max(
      boundedTake,
      current.nextSkip,
      current.items.length
    );

    setState((currentState) => ({
      ...currentState,
      error: undefined,
      loading: true,
      loadingMore: false,
    }));

    void (async () => {
      try {
        let refreshedWorkers: WorkViewWorkerGridDetailed[] = [];
        let nextSkip = 0;
        let totalCount: number | undefined;

        while (nextSkip < targetCount) {
          const data = await fetchPage(nextSkip);
          if (requestIdRef.current !== requestId) {
            return;
          }

          refreshedWorkers = appendUniqueWorkers(refreshedWorkers, data.workers);
          totalCount = data.totalCount;

          const pageNextSkip = data.skip + data.workers.length;
          if (
            data.workers.length === 0 ||
            pageNextSkip <= nextSkip ||
            (totalCount !== undefined && pageNextSkip >= totalCount)
          ) {
            nextSkip = pageNextSkip;
            break;
          }

          nextSkip = pageNextSkip;
        }

        if (requestIdRef.current !== requestId) {
          return;
        }

        setState({
          items: refreshedWorkers,
          loading: false,
          loadingMore: false,
          nextSkip,
          totalCount,
        });
      } catch (error) {
        if (requestIdRef.current !== requestId) {
          return;
        }

        const detail = error instanceof Error ? error.message : "Request failed.";
        const nextError = `Worker query failed. ${detail}`;
        setState((current) => ({
          ...current,
          error: nextError,
          loading: false,
          loadingMore: false,
        }));
      }
    })();
  }, [boundedTake, enabled, fetchPage]);

  useEffect(() => {
    if (!enabled) {
      requestIdRef.current += 1;
      inFlightSkipRef.current = null;
      return;
    }

    const requestId = requestIdRef.current + 1;
    requestIdRef.current = requestId;
    inFlightSkipRef.current = null;
    const shouldResetQuery = resetKeyRef.current !== resetKey;
    resetKeyRef.current = resetKey;
    if (
      !shouldResetQuery &&
      loadedRequestKeyRef.current === requestKey &&
      stateRef.current.items.length > 0
    ) {
      return;
    }

    queueMicrotask(() => {
      if (requestIdRef.current !== requestId) {
        return;
      }

      setState((current) => ({
        ...current,
        error: undefined,
        loading: true,
        loadingMore: false,
        nextSkip: 0,
      }));
      loadedRequestKeyRef.current = requestKey;
      void loadPage(0, false, requestId);
    });
  }, [enabled, loadPage, requestKey, resetKey]);

  const loadMore = useCallback(() => {
    if (!enabled) {
      return;
    }

    const current = stateRef.current;
    if (
      current.loading ||
      current.loadingMore ||
      inFlightSkipRef.current === current.nextSkip ||
      (current.totalCount !== undefined && current.nextSkip >= current.totalCount)
    ) {
      return;
    }

    inFlightSkipRef.current = current.nextSkip;
    void loadPage(current.nextSkip, true, requestIdRef.current);
  }, [enabled, loadPage]);

  return {
    error: state.error,
    hasMore: state.totalCount === undefined || state.nextSkip < state.totalCount,
    items: state.items,
    loading: state.loading,
    loadingMore: state.loadingMore,
    loadMore,
    refreshLoadedWindow,
    totalCount: state.totalCount,
  };
}

export function useInfiniteIterationQuery(
  connection: WorkableConnection,
  query: IterationQueryFilters,
  refreshToken: number,
  enabled: boolean
): InfiniteLoadable<WorkViewIterationGridDetailed> {
  const [state, setState] = useState<{
    error?: string;
    items: WorkViewIterationGridDetailed[];
    loading: boolean;
    loadingMore: boolean;
    nextSkip: number;
    totalCount?: number;
  }>({
    items: [],
    loading: true,
    loadingMore: false,
    nextSkip: 0,
  });
  const stateRef = useRef(state);
  const requestIdRef = useRef(0);
  const inFlightSkipRef = useRef<number | null>(null);
  const apiUrl = connection.apiUrl;
  const systemName = connection.systemName;
  const key = JSON.stringify(query);
  const resetKey = `${apiUrl}\n${systemName ?? ""}\n${key}`;
  const requestKey = `${resetKey}\n${refreshToken}`;
  const resetKeyRef = useRef<string | undefined>(undefined);
  const loadedRequestKeyRef = useRef<string | undefined>(undefined);
  const boundedTake = Math.min(maxQueryTake, Math.max(minQueryTake, queryPageTake));

  useEffect(() => {
    stateRef.current = state;
  }, [state]);

  const fetchPage = useCallback(async (skip: number) => {
    const parsedQuery = JSON.parse(key) as IterationQueryFilters;
    const requestConnection = { apiUrl, systemName };

    const result = await workableQueryFetch<WorkComponentQueryResult>(requestConnection, "views/iterations", {
      method: "POST",
      body: JSON.stringify({
        components: [
          createWorkComponentRequest("iterationGrid", "iterationGrid", "detailed", {
            keyKind: parsedQuery.keyKind,
            keyType: parsedQuery.keyType,
            keyValue: parsedQuery.keyValue,
            statuses: parsedQuery.statuses,
            skip,
            take: boundedTake,
          }),
        ],
        scope: createOverviewComponentScope({
          category: parsedQuery.category,
          definitionName: parsedQuery.definitionName,
          includeSubcategories: true,
        }, { emptyValue: undefined, includeSubcategoriesForDefinition: true }),
      }),
    });
    const data = getWorkComponentData<WorkerIterationQueryResult>(result, "iterationGrid");
    if (!data) {
      throw new Error(getWorkComponentErrors(result)[0] ?? "Iteration grid failed to load.");
    }

    return data;
  }, [apiUrl, boundedTake, key, systemName]);

  const loadPage = useCallback(async (skip: number, append: boolean, requestId: number) => {
    if (!enabled) {
      return;
    }

    setState((current) => ({
      ...current,
      error: undefined,
      loading: !append,
      loadingMore: append,
    }));

    try {
      const data = await fetchPage(skip);

      if (requestIdRef.current !== requestId) {
        return;
      }

      setState((current) => {
        const items = append
          ? appendUniqueIterations(current.items, data.iterations)
          : data.iterations;

        return {
          items,
          loading: false,
          loadingMore: false,
          nextSkip: Math.max(current.nextSkip, data.skip + data.iterations.length),
          totalCount: data.totalCount,
        };
      });
      if (inFlightSkipRef.current === skip) {
        inFlightSkipRef.current = null;
      }
    } catch (error) {
      if (requestIdRef.current !== requestId) {
        return;
      }
      if (inFlightSkipRef.current === skip) {
        inFlightSkipRef.current = null;
      }

      const detail = error instanceof Error ? error.message : "Request failed.";
      const nextError = `Iteration query failed. ${detail}`;
      setState((current) =>
        current.error === nextError && !current.loading && !current.loadingMore
          ? current
          : {
              ...current,
              error: nextError,
              loading: false,
              loadingMore: false,
            }
      );
    }
  }, [enabled, fetchPage]);

  const refreshLoadedWindow = useCallback(() => {
    const current = stateRef.current;
    if (!enabled || current.loading || current.loadingMore) {
      return;
    }

    const requestId = requestIdRef.current + 1;
    requestIdRef.current = requestId;
    inFlightSkipRef.current = null;
    const targetCount = Math.max(
      boundedTake,
      current.nextSkip,
      current.items.length
    );

    setState((currentState) => ({
      ...currentState,
      error: undefined,
      loading: true,
      loadingMore: false,
    }));

    void (async () => {
      try {
        let refreshedIterations: WorkViewIterationGridDetailed[] = [];
        let nextSkip = 0;
        let totalCount: number | undefined;

        while (nextSkip < targetCount) {
          const data = await fetchPage(nextSkip);
          if (requestIdRef.current !== requestId) {
            return;
          }

          refreshedIterations = appendUniqueIterations(refreshedIterations, data.iterations);
          totalCount = data.totalCount;

          const pageNextSkip = data.skip + data.iterations.length;
          if (
            data.iterations.length === 0 ||
            pageNextSkip <= nextSkip ||
            (totalCount !== undefined && pageNextSkip >= totalCount)
          ) {
            nextSkip = pageNextSkip;
            break;
          }

          nextSkip = pageNextSkip;
        }

        if (requestIdRef.current !== requestId) {
          return;
        }

        setState({
          items: refreshedIterations,
          loading: false,
          loadingMore: false,
          nextSkip,
          totalCount,
        });
      } catch (error) {
        if (requestIdRef.current !== requestId) {
          return;
        }

        const detail = error instanceof Error ? error.message : "Request failed.";
        const nextError = `Iteration query failed. ${detail}`;
        setState((currentState) => ({
          ...currentState,
          error: nextError,
          loading: false,
          loadingMore: false,
        }));
      }
    })();
  }, [boundedTake, enabled, fetchPage]);

  useEffect(() => {
    if (!enabled) {
      requestIdRef.current += 1;
      inFlightSkipRef.current = null;
      return;
    }

    const requestId = requestIdRef.current + 1;
    requestIdRef.current = requestId;
    inFlightSkipRef.current = null;
    const shouldResetQuery = resetKeyRef.current !== resetKey;
    resetKeyRef.current = resetKey;
    if (
      !shouldResetQuery &&
      loadedRequestKeyRef.current === requestKey &&
      stateRef.current.items.length > 0
    ) {
      return;
    }

    queueMicrotask(() => {
      if (requestIdRef.current !== requestId) {
        return;
      }

      setState((current) => ({
        ...current,
        error: undefined,
        loading: true,
        loadingMore: false,
        nextSkip: 0,
      }));
      loadedRequestKeyRef.current = requestKey;
      void loadPage(0, false, requestId);
    });
  }, [enabled, loadPage, requestKey, resetKey]);

  const loadMore = useCallback(() => {
    if (!enabled) {
      return;
    }

    const current = stateRef.current;
    if (
      current.loading ||
      current.loadingMore ||
      inFlightSkipRef.current === current.nextSkip ||
      (current.totalCount !== undefined && current.nextSkip >= current.totalCount)
    ) {
      return;
    }

    inFlightSkipRef.current = current.nextSkip;
    void loadPage(current.nextSkip, true, requestIdRef.current);
  }, [enabled, loadPage]);

  return {
    error: state.error,
    hasMore: state.totalCount === undefined || state.nextSkip < state.totalCount,
    items: state.items,
    loading: state.loading,
    loadingMore: state.loadingMore,
    loadMore,
    refreshLoadedWindow,
    totalCount: state.totalCount,
  };
}

export function appendUniqueWorkers(
  current: WorkViewWorkerGridDetailed[],
  next: WorkViewWorkerGridDetailed[]
) {
  const items = [...current];
  const indexes = new Map(current.map((worker, index) => [worker.id.value, index]));

  for (const worker of next) {
    const existingIndex = indexes.get(worker.id.value);
    if (existingIndex === undefined) {
      indexes.set(worker.id.value, items.length);
      items.push(worker);
      continue;
    }

    if (isNewerWorkerRow(items[existingIndex], worker)) {
      items[existingIndex] = worker;
    }
  }

  return items;
}

export function appendUniqueIterations(
  current: WorkViewIterationGridDetailed[],
  next: WorkViewIterationGridDetailed[]
) {
  const items = [...current];
  const indexes = new Map(
    current.map((iteration, index) => [getIterationRowKey(iteration), index])
  );

  for (const iteration of next) {
    const key = getIterationRowKey(iteration);
    const existingIndex = indexes.get(key);
    if (existingIndex === undefined) {
      indexes.set(key, items.length);
      items.push(iteration);
      continue;
    }

    if (isNewerIterationRow(items[existingIndex], iteration)) {
      items[existingIndex] = iteration;
    }
  }

  return items;
}

export function getIterationRowKey(iteration: WorkViewIterationGridDetailed) {
  return `${iteration.workerId.value}:${iteration.sequence}`;
}

export function isNewerWorkerRow(
  current: WorkViewWorkerGridDetailed,
  next: WorkViewWorkerGridDetailed
) {
  return next.revision > current.revision ||
    Date.parse(next.updatedAt) > Date.parse(current.updatedAt);
}

export function isNewerIterationRow(
  current: WorkViewIterationGridDetailed,
  next: WorkViewIterationGridDetailed
) {
  return Date.parse(next.completedAt) > Date.parse(current.completedAt);
}
