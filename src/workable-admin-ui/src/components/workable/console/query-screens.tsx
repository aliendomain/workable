"use client";

import { useVirtualizer } from "@tanstack/react-virtual";
import { MoreHorizontal } from "lucide-react";
import type { ReactNode } from "react";
import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import {
  DropdownMenu,
  DropdownMenuContent,
  DropdownMenuItem,
  DropdownMenuTrigger,
} from "@/components/ui/dropdown-menu";
import { Skeleton } from "@/components/ui/skeleton";
import {
  Table,
  TableBody,
  TableCell,
  TableRow,
} from "@/components/ui/table";
import {
  Tooltip,
  TooltipContent,
  TooltipTrigger,
} from "@/components/ui/tooltip";
import {
  stateTone,
  workableFetch,
  type WorkCompletionStatus,
  type WorkComponentQueryResult,
  type WorkComponentRequest,
  type WorkComponentResult,
  type WorkComponentShape,
  type WorkTypedValue,
  type WorkViewIterationGridDetailed,
  type WorkViewWorkerGridDetailed,
  type WorkableConnection,
  type WorkerIterationQueryResult,
  type WorkerQueryResult,
  type WorkerState,
} from "@/lib/workable";

type InfiniteLoadable<TItem> = {
  error?: string;
  hasMore: boolean;
  items: TItem[];
  loading: boolean;
  loadingMore: boolean;
  loadMore: () => void;
  totalCount?: number;
};

type OverviewScope = {
  category?: string;
  definitionName?: string;
  includeSubcategories?: boolean;
};

type DurationDisplay = {
  isWarning: boolean;
  text: string;
};

const queryGridShapeCapabilities = {
  defaultShape: "detailed",
  supportedShapes: ["detailed"],
} as const satisfies {
  defaultShape: WorkComponentShape;
  supportedShapes: WorkComponentShape[];
};
const queryPageTake = 50;
const maxQueryTake = 50;
const minQueryTake = 1;

export function WorkersView({
  categoryFilter,
  connection,
  definitionFilter,
  filterControls,
  isLoadingTarget,
  isVisible,
  keyTypeFilter,
  onOpenWorker,
  onReady,
  refreshToken,
  stateFilter,
}: {
  categoryFilter: string;
  connection: WorkableConnection;
  definitionFilter: string;
  filterControls: ReactNode;
  isLoadingTarget: boolean;
  isVisible: boolean;
  keyTypeFilter: string;
  onOpenWorker: (workerId: string) => void;
  onReady: () => void;
  refreshToken: number;
  stateFilter: WorkerState[];
}) {
  const query = useMemo(
    () => ({
      category: normalizeCategoryFilter(categoryFilter) || undefined,
      definitionName: definitionFilter.trim() || undefined,
      includeSubcategories: true,
      keyType: keyTypeFilter.trim() || undefined,
      states: stateFilter.length === 0 ? undefined : stateFilter,
    }),
    [categoryFilter, definitionFilter, keyTypeFilter, stateFilter]
  );
  const workers = useInfiniteWorkerQuery(connection, query, refreshToken, isLoadingTarget);
  const [gridShape, setGridShape] = useState<WorkComponentShape>(
    queryGridShapeCapabilities.defaultShape
  );
  const isReady = !workers.loading;
  useEffect(() => {
    if (isLoadingTarget && isReady) {
      onReady();
    }
  }, [isLoadingTarget, isReady, onReady]);

  if (!isVisible) {
    return null;
  }

  return (
    <div className="space-y-6">
      <ErrorPanel errors={[workers.error]} />
      {filterControls}
      <QueryPanelShell
        actions={<QueryResultTotal noun="worker" totalCount={workers.totalCount} />}
        contentClassName="mt-3"
        onShapeChange={setGridShape}
        shape={gridShape}
        supportedShapes={queryGridShapeCapabilities.supportedShapes}
        title="Workers"
      >
        <VirtualWorkerTable
          hasMore={workers.hasMore}
          loading={workers.loading}
          loadingMore={workers.loadingMore}
          loadMore={workers.loadMore}
          onSelect={(worker) => onOpenWorker(worker.id.value)}
          shape={gridShape}
          workers={workers.items}
        />
      </QueryPanelShell>
    </div>
  );
}

export function IterationsView({
  categoryFilter,
  connection,
  definitionFilter,
  filterControls,
  isLoadingTarget,
  isVisible,
  keyTypeFilter,
  onOpenWorker,
  onReady,
  refreshToken,
  statusFilter,
}: {
  categoryFilter: string;
  connection: WorkableConnection;
  definitionFilter: string;
  filterControls: ReactNode;
  isLoadingTarget: boolean;
  isVisible: boolean;
  keyTypeFilter: string;
  onOpenWorker: (workerId: string) => void;
  onReady: () => void;
  refreshToken: number;
  statusFilter: WorkCompletionStatus[];
}) {
  const query = useMemo(
    () => ({
      category: normalizeCategoryFilter(categoryFilter) || undefined,
      definitionName: definitionFilter.trim() || undefined,
      keyType: keyTypeFilter.trim() || undefined,
      statuses: statusFilter.length === 0 ? undefined : statusFilter,
    }),
    [categoryFilter, definitionFilter, keyTypeFilter, statusFilter]
  );
  const iterations = useInfiniteIterationQuery(connection, query, refreshToken, isLoadingTarget);
  const [gridShape, setGridShape] = useState<WorkComponentShape>(
    queryGridShapeCapabilities.defaultShape
  );
  const isReady = !iterations.loading;
  useEffect(() => {
    if (isLoadingTarget && isReady) {
      onReady();
    }
  }, [isLoadingTarget, isReady, onReady]);

  if (!isVisible) {
    return null;
  }

  return (
    <div className="space-y-6">
      <ErrorPanel errors={[iterations.error]} />
      {filterControls}
      <QueryPanelShell
        actions={<QueryResultTotal noun="iteration" totalCount={iterations.totalCount} />}
        contentClassName="mt-3"
        onShapeChange={setGridShape}
        shape={gridShape}
        supportedShapes={queryGridShapeCapabilities.supportedShapes}
        title="Iterations"
      >
        <VirtualIterationTable
          hasMore={iterations.hasMore}
          iterations={iterations.items}
          loading={iterations.loading}
          loadingMore={iterations.loadingMore}
          loadMore={iterations.loadMore}
          onSelect={(iteration) => onOpenWorker(iteration.workerId.value)}
          shape={gridShape}
        />
      </QueryPanelShell>
    </div>
  );
}

function QueryPanelShell({
  actions,
  children,
  contentClassName,
  onShapeChange,
  shape,
  supportedShapes,
  title,
}: {
  actions?: ReactNode;
  children: ReactNode;
  contentClassName?: string;
  onShapeChange?: (shape: WorkComponentShape) => void;
  shape?: WorkComponentShape;
  supportedShapes?: readonly WorkComponentShape[];
  title: string;
}) {
  return (
    <section className="rounded-lg border bg-card p-4 shadow-sm">
      <div className="flex min-w-0 items-start justify-between gap-3">
        <div className="min-w-0">
          <h2 className="truncate font-semibold text-base">{title}</h2>
        </div>
        <div className="flex shrink-0 items-center gap-2">
          {actions}
          {shape && onShapeChange && supportedShapes ? (
            <PanelMenu
              onShapeChange={onShapeChange}
              shape={shape}
              supportedShapes={supportedShapes}
            />
          ) : null}
        </div>
      </div>
      <div className={contentClassName}>{children}</div>
    </section>
  );
}

function PanelMenu({
  onShapeChange,
  shape,
  supportedShapes,
}: {
  onShapeChange: (shape: WorkComponentShape) => void;
  shape: WorkComponentShape;
  supportedShapes: readonly WorkComponentShape[];
}) {
  return (
    <DropdownMenu>
      <Tooltip delayDuration={500} disableHoverableContent>
        <TooltipTrigger asChild>
          <DropdownMenuTrigger asChild>
            <Button aria-label="Panel options" size="icon-sm" variant="ghost">
              <MoreHorizontal className="size-4" />
            </Button>
          </DropdownMenuTrigger>
        </TooltipTrigger>
        <TooltipContent side="bottom" sideOffset={6}>
          Panel options
        </TooltipContent>
      </Tooltip>
      <DropdownMenuContent align="end">
        {supportedShapes.map((supportedShape) => (
          <DropdownMenuItem
            disabled={supportedShape === shape}
            key={supportedShape}
            onClick={() => onShapeChange(supportedShape)}
          >
            {supportedShape}
          </DropdownMenuItem>
        ))}
      </DropdownMenuContent>
    </DropdownMenu>
  );
}

function VirtualWorkerTable({
  hasMore,
  loading,
  loadingMore,
  loadMore,
  onSelect,
  shape,
  workers,
}: {
  hasMore: boolean;
  loading: boolean;
  loadingMore: boolean;
  loadMore: () => void;
  onSelect: (worker: WorkViewWorkerGridDetailed) => void;
  shape: WorkComponentShape;
  workers: WorkViewWorkerGridDetailed[];
}) {
  const detailed = shape === "detailed";
  const scrollRef = useRef<HTMLDivElement>(null);
  // eslint-disable-next-line react-hooks/incompatible-library -- TanStack Virtual owns scroll measurement state.
  const rowVirtualizer = useVirtualizer({
    count: workers.length,
    estimateSize: () => 64,
    getScrollElement: () => scrollRef.current,
    getItemKey: (index) => workers[index]?.id.value ?? index,
    overscan: 10,
  });
  const virtualItems = rowVirtualizer.getVirtualItems();
  const lastVirtualIndex = virtualItems.at(-1)?.index;

  useEffect(() => {
    if (lastVirtualIndex !== undefined && lastVirtualIndex >= workers.length - 8 && hasMore) {
      loadMore();
    }
  }, [hasMore, lastVirtualIndex, loadMore, workers.length]);

  if (loading && workers.length === 0) {
    return <StackedSkeleton count={8} />;
  }

  if (workers.length === 0) {
    return (
      <div className="rounded-lg border border-dashed p-8 text-center text-muted-foreground text-sm">
        No workers matched the current query.
      </div>
    );
  }

  return (
    <div className="overflow-hidden rounded-lg border">
      <div className="grid bg-card shadow-[0_1px_0_var(--border)]">
        <div className="flex min-h-12">
          <div className="flex h-12 flex-[2_2_22rem] items-center px-3 font-medium text-sm">Definition</div>
          <div className="flex h-12 w-32 items-center px-3 font-medium text-sm">State</div>
          {detailed && <div className="flex h-12 w-72 items-center px-3 font-medium text-sm">Subject id</div>}
          {detailed && <div className="flex h-12 flex-[2_2_20rem] items-center px-3 font-medium text-sm">Identifiers</div>}
          <div className="flex h-12 w-36 items-center px-3 font-medium text-sm">Updated</div>
          <div className="flex h-12 w-28 items-center px-3 font-medium text-sm">Duration</div>
        </div>
      </div>
      <div
        className="workable-grid-scrollbar max-h-[calc(100vh-17rem)] overflow-auto"
        ref={scrollRef}
      >
        <Table className="grid">
          <TableBody
            className="relative grid"
            style={{ height: `${rowVirtualizer.getTotalSize()}px` }}
          >
            {virtualItems.map((virtualRow) => {
              const worker = workers[virtualRow.index];
              if (!worker) {
                return null;
              }

              return (
                <TableRow
                  className="absolute flex h-16 w-full cursor-pointer overflow-hidden"
                  data-index={virtualRow.index}
                  key={virtualRow.key}
                  onClick={() => onSelect(worker)}
                  style={{
                    transform: `translateY(${virtualRow.start}px)`,
                  }}
                >
                  <TableCell className="min-w-0 flex-[2_2_22rem] overflow-hidden">
                    <div className="font-mono text-xs">{worker.definitionName}</div>
                    <div
                      className="truncate font-mono text-muted-foreground text-xs"
                      title={worker.id.value}
                    >
                      {worker.id.value}
                    </div>
                  </TableCell>
                  <TableCell className="w-32 overflow-hidden">
                    <Badge className={stateTone(worker.state)} variant="outline">
                      {worker.state}
                    </Badge>
                  </TableCell>
                  {detailed && (
                    <TableCell className="w-72 overflow-hidden font-mono text-muted-foreground text-xs">
                      <TypedValueSummary values={worker.subjectId ? [worker.subjectId] : []} />
                    </TableCell>
                  )}
                  {detailed && (
                    <TableCell className="min-w-0 flex-[2_2_20rem] overflow-hidden font-mono text-muted-foreground text-xs">
                      <IdentifierSummary identifiers={worker.identifiers} />
                    </TableCell>
                  )}
                  <TableCell className="w-36 overflow-hidden text-muted-foreground text-xs">
                    {formatRelativeTime(worker.updatedAt)}
                  </TableCell>
                  <TableCell className="w-28 overflow-hidden">
                    <DurationValue
                      className="font-mono text-xs"
                      duration={formatWorkerDuration(worker)}
                    />
                  </TableCell>
                </TableRow>
              );
            })}
          </TableBody>
        </Table>
        <InfiniteGridFooter
          hasMore={hasMore}
          loadingMore={loadingMore}
          loadedCount={workers.length}
        />
      </div>
    </div>
  );
}

function VirtualIterationTable({
  hasMore,
  iterations,
  loading,
  loadingMore,
  loadMore,
  onSelect,
  shape,
}: {
  hasMore: boolean;
  iterations: WorkViewIterationGridDetailed[];
  loading: boolean;
  loadingMore: boolean;
  loadMore: () => void;
  onSelect: (iteration: WorkViewIterationGridDetailed) => void;
  shape: WorkComponentShape;
}) {
  const detailed = shape === "detailed";
  const scrollRef = useRef<HTMLDivElement>(null);
  // eslint-disable-next-line react-hooks/incompatible-library -- TanStack Virtual owns scroll measurement state.
  const rowVirtualizer = useVirtualizer({
    count: iterations.length,
    estimateSize: () => 64,
    getScrollElement: () => scrollRef.current,
    getItemKey: (index) => {
      const iteration = iterations[index];
      return iteration ? `${iteration.workerId.value}:${iteration.sequence}` : index;
    },
    overscan: 10,
  });
  const virtualItems = rowVirtualizer.getVirtualItems();
  const lastVirtualIndex = virtualItems.at(-1)?.index;

  useEffect(() => {
    if (lastVirtualIndex !== undefined && lastVirtualIndex >= iterations.length - 8 && hasMore) {
      loadMore();
    }
  }, [hasMore, iterations.length, lastVirtualIndex, loadMore]);

  if (loading && iterations.length === 0) {
    return <StackedSkeleton count={8} />;
  }

  if (iterations.length === 0) {
    return (
      <div className="rounded-lg border border-dashed p-8 text-center text-muted-foreground text-sm">
        No iterations matched the current query.
      </div>
    );
  }

  return (
    <div className="overflow-hidden rounded-lg border">
      <div className="grid bg-card shadow-[0_1px_0_var(--border)]">
        <div className="flex min-h-12">
          <div className="flex h-12 flex-[2_2_22rem] items-center px-3 font-medium text-sm">Definition</div>
          <div className="flex h-12 w-24 items-center px-3 font-medium text-sm">Status</div>
          <div className="flex h-12 w-32 items-center px-3 font-medium text-sm">Worker state</div>
          {detailed && <div className="flex h-12 w-72 items-center px-3 font-medium text-sm">Subject id</div>}
          {detailed && <div className="flex h-12 flex-[2_2_20rem] items-center px-3 font-medium text-sm">Identifiers</div>}
          <div className="flex h-12 w-36 items-center px-3 font-medium text-sm">Completed</div>
          <div className="flex h-12 w-28 items-center px-3 font-medium text-sm">Duration</div>
        </div>
      </div>
      <div
        className="workable-grid-scrollbar max-h-[calc(100vh-17rem)] overflow-auto"
        ref={scrollRef}
      >
        <Table className="grid">
          <TableBody
            className="relative grid"
            style={{ height: `${rowVirtualizer.getTotalSize()}px` }}
          >
            {virtualItems.map((virtualRow) => {
              const iteration = iterations[virtualRow.index];
              if (!iteration) {
                return null;
              }

              return (
                <TableRow
                  className="absolute flex h-16 w-full cursor-pointer overflow-hidden"
                  data-index={virtualRow.index}
                  key={virtualRow.key}
                  onClick={() => onSelect(iteration)}
                  style={{
                    transform: `translateY(${virtualRow.start}px)`,
                  }}
                >
                  <TableCell className="min-w-0 flex-[2_2_22rem] overflow-hidden">
                    <div className="font-mono text-xs">{iteration.definitionName}</div>
                    <div className="font-mono text-muted-foreground text-xs">
                      #{iteration.sequence} / {iteration.workerId.value}
                    </div>
                  </TableCell>
                  <TableCell className="w-24 overflow-hidden">
                    <Badge className={completionTone(iteration.status)} variant="outline">
                      {iteration.status}
                    </Badge>
                  </TableCell>
                  <TableCell className="w-32 overflow-hidden">
                    <Badge className={stateTone(iteration.workerState)} variant="outline">
                      {iteration.workerState}
                    </Badge>
                  </TableCell>
                  {detailed && (
                    <TableCell className="w-72 overflow-hidden font-mono text-muted-foreground text-xs">
                      <TypedValueSummary values={iteration.subjectId ? [iteration.subjectId] : []} />
                    </TableCell>
                  )}
                  {detailed && (
                    <TableCell className="min-w-0 flex-[2_2_20rem] overflow-hidden font-mono text-muted-foreground text-xs">
                      <IdentifierSummary identifiers={iteration.identifiers} />
                    </TableCell>
                  )}
                  <TableCell className="w-36 overflow-hidden text-muted-foreground text-xs">
                    {formatRelativeTime(iteration.completedAt)}
                  </TableCell>
                  <TableCell className="w-28 overflow-hidden">
                    <DurationValue duration={formatExecutionDuration(iteration.executionDuration)} />
                  </TableCell>
                </TableRow>
              );
            })}
          </TableBody>
        </Table>
        <InfiniteGridFooter
          hasMore={hasMore}
          loadingMore={loadingMore}
          loadedCount={iterations.length}
        />
      </div>
    </div>
  );
}

function QueryResultTotal({
  noun,
  totalCount,
}: {
  noun: string;
  totalCount?: number;
}) {
  if (totalCount === undefined) {
    return null;
  }

  return (
    <div className="shrink-0 text-muted-foreground text-xs tabular-nums">
      {totalCount.toLocaleString()} {noun}{totalCount === 1 ? "" : "s"}
    </div>
  );
}

function InfiniteGridFooter({
  hasMore,
  loadedCount,
  loadingMore,
}: {
  hasMore: boolean;
  loadedCount: number;
  loadingMore: boolean;
}) {
  return (
    <div className="flex h-12 items-center justify-center border-t text-muted-foreground text-xs">
      {loadingMore ? (
        <span>Loading more...</span>
      ) : hasMore ? (
        <span>Scroll to load more</span>
      ) : (
        <span>Showing {loadedCount.toLocaleString()}</span>
      )}
    </div>
  );
}

export function IdentifierSummary({ identifiers }: { identifiers?: WorkTypedValue[] | null }) {
  return <TypedValueSummary values={identifiers ?? []} />;
}

export function TypedValueSummary({ values }: { values: WorkTypedValue[] }) {
  if (values.length === 0) {
    return <span>-</span>;
  }

  return (
    <div className="grid max-w-full grid-cols-[max-content_minmax(0,1fr)] gap-x-3 gap-y-1 overflow-hidden">
      {values.slice(0, 3).map((value, index) => (
        <div className="contents" key={`${value.type}:${value.value}:${index}`}>
          <span className="truncate text-foreground">{value.type}</span>
          <span className="truncate" title={value.value}>
            {value.value}
          </span>
        </div>
      ))}
      {values.length > 3 && (
        <div className="contents">
          <span className="text-foreground">more</span>
          <span>{values.length - 3}</span>
        </div>
      )}
    </div>
  );
}

function DurationValue({
  className = "font-mono text-xs",
  duration,
}: {
  className?: string;
  duration: DurationDisplay;
}) {
  return (
    <span className={`${className} ${duration.isWarning ? "text-amber-300" : "text-muted-foreground"}`}>
      {duration.text}
    </span>
  );
}

function ErrorPanel({ errors }: { errors: Array<string | undefined> }) {
  const visibleErrors = errors.filter((error): error is string => Boolean(error));
  if (visibleErrors.length === 0) {
    return null;
  }

  return (
    <div className="space-y-2">
      {visibleErrors.map((error) => (
        <div
          className="rounded-lg border border-red-500/30 bg-red-500/10 px-3 py-2 text-red-100 text-sm"
          key={error}
        >
          {error}
        </div>
      ))}
    </div>
  );
}

function StackedSkeleton({ count }: { count: number }) {
  return (
    <div className="space-y-2">
      {Array.from({ length: count }, (_, index) => (
        <Skeleton className="h-12 w-full rounded-md" key={index} />
      ))}
    </div>
  );
}

function useInfiniteWorkerQuery(
  connection: WorkableConnection,
  query: {
    category?: string;
    definitionName?: string;
    includeSubcategories?: boolean;
    keyType?: string;
    states?: WorkerState[];
  },
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
  const boundedTake = Math.min(maxQueryTake, Math.max(minQueryTake, queryPageTake));

  useEffect(() => {
    stateRef.current = state;
  }, [state]);

  const loadPage = useCallback(async (skip: number, append: boolean, requestId: number) => {
    if (!enabled) {
      return;
    }

    const parsedQuery = JSON.parse(key) as {
      category?: string;
      definitionName?: string;
      includeSubcategories?: boolean;
      keyType?: string;
      states?: WorkerState[];
    };
    const requestConnection = { apiUrl, systemName };

    setState((current) => ({
      ...current,
      error: undefined,
      loading: !append && current.items.length === 0,
      loadingMore: append,
    }));

    try {
      const result = await workableFetch<WorkComponentQueryResult>(requestConnection, "views/workers", {
        method: "POST",
        body: JSON.stringify({
          components: [
            overviewComponent("workerGrid", "workerGrid", "detailed", {
              keyType: parsedQuery.keyType,
              states: parsedQuery.states,
              skip,
              take: boundedTake,
            }),
          ],
          scope: createOverviewComponentScope({
            category: parsedQuery.category,
            definitionName: parsedQuery.definitionName,
            includeSubcategories: parsedQuery.includeSubcategories,
          }),
        }),
      });
      const data = getWorkComponentData<WorkerQueryResult>(result, "workerGrid");
      if (!data) {
        throw new Error(getWorkComponentErrors(result)[0] ?? "Worker grid failed to load.");
      }

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
  }, [apiUrl, boundedTake, enabled, key, systemName]);

  useEffect(() => {
    if (!enabled) {
      requestIdRef.current += 1;
      inFlightSkipRef.current = null;
      return;
    }

    const requestId = requestIdRef.current + 1;
    requestIdRef.current = requestId;
    inFlightSkipRef.current = null;
    queueMicrotask(() => {
      if (requestIdRef.current !== requestId) {
        return;
      }

      setState({
        items: [],
        loading: true,
        loadingMore: false,
        nextSkip: 0,
      });
      void loadPage(0, false, requestId);
    });
  }, [enabled, loadPage, refreshToken]);

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
    totalCount: state.totalCount,
  };
}

function useInfiniteIterationQuery(
  connection: WorkableConnection,
  query: {
    category?: string;
    definitionName?: string;
    keyType?: string;
    statuses?: WorkCompletionStatus[];
  },
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
  const boundedTake = Math.min(maxQueryTake, Math.max(minQueryTake, queryPageTake));

  useEffect(() => {
    stateRef.current = state;
  }, [state]);

  const loadPage = useCallback(async (skip: number, append: boolean, requestId: number) => {
    if (!enabled) {
      return;
    }

    const parsedQuery = JSON.parse(key) as {
      category?: string;
      definitionName?: string;
      keyType?: string;
      statuses?: WorkCompletionStatus[];
    };
    const requestConnection = { apiUrl, systemName };

    setState((current) => ({
      ...current,
      error: undefined,
      loading: !append && current.items.length === 0,
      loadingMore: append,
    }));

    try {
      const result = await workableFetch<WorkComponentQueryResult>(requestConnection, "views/iterations", {
        method: "POST",
        body: JSON.stringify({
          components: [
            overviewComponent("iterationGrid", "iterationGrid", "detailed", {
              keyType: parsedQuery.keyType,
              statuses: parsedQuery.statuses,
              skip,
              take: boundedTake,
            }),
          ],
          scope: createOverviewComponentScope({
            category: parsedQuery.category,
            definitionName: parsedQuery.definitionName,
            includeSubcategories: true,
          }),
        }),
      });
      const data = getWorkComponentData<WorkerIterationQueryResult>(result, "iterationGrid");
      if (!data) {
        throw new Error(getWorkComponentErrors(result)[0] ?? "Iteration grid failed to load.");
      }

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
  }, [apiUrl, boundedTake, enabled, key, systemName]);

  useEffect(() => {
    if (!enabled) {
      requestIdRef.current += 1;
      inFlightSkipRef.current = null;
      return;
    }

    const requestId = requestIdRef.current + 1;
    requestIdRef.current = requestId;
    inFlightSkipRef.current = null;
    queueMicrotask(() => {
      if (requestIdRef.current !== requestId) {
        return;
      }

      setState({
        items: [],
        loading: true,
        loadingMore: false,
        nextSkip: 0,
      });
      void loadPage(0, false, requestId);
    });
  }, [enabled, loadPage, refreshToken]);

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
    totalCount: state.totalCount,
  };
}

function appendUniqueWorkers(
  current: WorkViewWorkerGridDetailed[],
  next: WorkViewWorkerGridDetailed[]
) {
  const seen = new Set(current.map((worker) => worker.id.value));
  return [
    ...current,
    ...next.filter((worker) => {
      if (seen.has(worker.id.value)) {
        return false;
      }

      seen.add(worker.id.value);
      return true;
    }),
  ];
}

function appendUniqueIterations(
  current: WorkViewIterationGridDetailed[],
  next: WorkViewIterationGridDetailed[]
) {
  const seen = new Set(
    current.map((iteration) => `${iteration.workerId.value}:${iteration.sequence}`)
  );
  return [
    ...current,
    ...next.filter((iteration) => {
      const key = `${iteration.workerId.value}:${iteration.sequence}`;
      if (seen.has(key)) {
        return false;
      }

      seen.add(key);
      return true;
    }),
  ];
}

function overviewComponent(
  id: string,
  type = id,
  shape?: WorkComponentShape,
  options?: Record<string, unknown>
): WorkComponentRequest {
  return {
    id,
    type,
    ...(shape ? { shape } : {}),
    ...(options ? { options } : {}),
  };
}

function createOverviewComponentScope(scope: OverviewScope | null) {
  const normalizedScope = normalizeOverviewScope(scope);
  if (!normalizedScope) {
    return undefined;
  }

  return {
    category: normalizedScope.category,
    definitionName: normalizedScope.definitionName,
    includeSubcategories: normalizedScope.definitionName
      ? scope?.includeSubcategories ?? true
      : normalizedScope.includeSubcategories,
  };
}

function normalizeOverviewScope(scope: OverviewScope | null | undefined): OverviewScope | null {
  if (!scope) {
    return null;
  }

  const category = normalizeScopeText(scope.category);
  const definitionName = normalizeScopeText(scope.definitionName);
  if (!category && !definitionName) {
    return null;
  }

  return {
    ...(category ? { category } : {}),
    ...(definitionName ? { definitionName } : {}),
    includeSubcategories: definitionName
      ? scope.includeSubcategories ?? true
      : scope.includeSubcategories ?? true,
  };
}

function normalizeScopeText(value: unknown) {
  return typeof value === "string" ? value.trim() : "";
}

function normalizeCategoryFilter(path: unknown) {
  return splitCatalogPath(path).join(":");
}

function splitCatalogPath(path: unknown) {
  if (typeof path !== "string") {
    return [];
  }

  return path
    .split(":")
    .map((segment) => segment.trim())
    .filter(Boolean);
}

function getWorkComponentData<T>(
  result: WorkComponentQueryResult | undefined,
  id: string
): T | undefined {
  const component = result?.components?.[id] as WorkComponentResult<T> | undefined;
  return component?.status === "ok" ? component.data : undefined;
}

function getWorkComponentErrors(result: WorkComponentQueryResult | undefined) {
  return Object.values(result?.components ?? {})
    .map((component) => component.error)
    .filter((error): error is string => !!error);
}

function formatWorkerDuration(worker: WorkViewWorkerGridDetailed): DurationDisplay {
  return worker.totalExecutionDuration
    ? formatExecutionDuration(worker.totalExecutionDuration)
    : { isWarning: false, text: "-" };
}

function formatExecutionDuration(value?: string | null): DurationDisplay {
  const seconds = parseDurationSeconds(value);
  if (seconds === null) {
    return { isWarning: false, text: "-" };
  }

  return formatDurationSeconds(seconds);
}

function formatRelativeTime(value?: string | null) {
  if (!value) {
    return "-";
  }

  const timestamp = Date.parse(value);
  if (!Number.isFinite(timestamp)) {
    return "-";
  }

  const elapsedSeconds = Math.max(0, (Date.now() - timestamp) / 1000);
  if (elapsedSeconds < 5) {
    return "just now";
  }

  const formatter = new Intl.RelativeTimeFormat(undefined, { numeric: "always" });
  if (elapsedSeconds < 60) {
    return formatter.format(-Math.floor(elapsedSeconds), "second");
  }
  if (elapsedSeconds < 60 * 60) {
    return formatter.format(-Math.floor(elapsedSeconds / 60), "minute");
  }
  if (elapsedSeconds < 24 * 60 * 60) {
    return formatter.format(-Math.floor(elapsedSeconds / (60 * 60)), "hour");
  }

  return formatter.format(-Math.floor(elapsedSeconds / (24 * 60 * 60)), "day");
}

function formatDurationSeconds(seconds: number): DurationDisplay {
  if (seconds < 0.005) {
    return { isWarning: false, text: "~0s" };
  }
  if (seconds < 60) {
    return { isWarning: false, text: `${seconds.toFixed(2)}s` };
  }

  return { isWarning: true, text: `${(seconds / 60).toFixed(2)}m` };
}

function parseDurationSeconds(value?: string | null) {
  if (!value) {
    return null;
  }

  const parts = value.split(":");
  if (parts.length !== 3) {
    return null;
  }

  const [daysPart, hoursPart] = parts[0].includes(".")
    ? parts[0].split(".")
    : ["0", parts[0]];
  const days = Number(daysPart);
  const hours = Number(hoursPart);
  const minutes = Number(parts[1]);
  const seconds = Number(parts[2]);
  if (
    !Number.isFinite(days) ||
    !Number.isFinite(hours) ||
    !Number.isFinite(minutes) ||
    !Number.isFinite(seconds)
  ) {
    return null;
  }

  return (days * 24 * 60 * 60) + (hours * 60 * 60) + (minutes * 60) + seconds;
}

function completionTone(status: WorkCompletionStatus) {
  switch (status) {
    case "Executing":
      return "border-emerald-500/40 bg-emerald-500/10 text-emerald-300";
    case "Completed":
      return "border-emerald-500/40 bg-emerald-500/10 text-emerald-300";
    case "Failed":
    case "Canceled":
      return "bg-red-500/15 text-red-300 border-red-500/30";
    case "Paused":
      return "border-amber-500/40 bg-amber-500/10 text-amber-300";
    default:
      return "border-muted-foreground/30 text-muted-foreground";
  }
}
