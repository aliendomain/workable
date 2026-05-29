"use client";

import { useVirtualizer } from "@tanstack/react-virtual";
import {
  Ban,
  Clock3,
  Loader2,
  MoreHorizontal,
  Pause,
  Play,
  SquareArrowOutUpRight,
  Trash2,
} from "lucide-react";
import type { MutableRefObject, ReactNode } from "react";
import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { ConsolePageLayout } from "@/components/features/console/console-primitives";
import { PanelAggregateFrame } from "@/components/features/console/panel-aggregate-frame";
import {
  PanelScrollViewport,
  PanelShell,
  type PanelFilterControl,
} from "@/components/features/console/panel-shell";
import type { PanelVisibilityOption } from "@/components/features/console/panel-visibility-settings";
import type { OverviewScope } from "@/components/features/console/types";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import {
  DropdownMenu,
  DropdownMenuContent,
  DropdownMenuItem,
  DropdownMenuTrigger,
} from "@/components/ui/dropdown-menu";
import {
  Table,
  TableBody,
  TableCell,
  TableRow,
} from "@/components/ui/table";
import { ErrorPanel } from "@/components/workable/console/feedback-panel";
import {
  formatRelativeTime,
  useLiveRelativeTimeNow,
} from "@/components/workable/console/live-relative-time";
import {
  stateTone,
  WorkableApiError,
  workableFetch,
  type WorkAction,
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
  refreshLoadedWindow?: () => void;
  totalCount?: number;
};

type DurationDisplay = {
  isWarning: boolean;
  text: string;
};

type WorkerActionResult = {
  messages?: Array<{ text?: string }>;
  status?: string;
};

type WorkerRowHighlight = {
  fallbackIndex: number;
  workerId: string | null;
};

type ActiveWorkerRowHighlight = WorkerRowHighlight & {
  resetKey: string;
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
const workersPanelOptions: PanelVisibilityOption<"workers">[] = [
  {
    id: "workers",
    label: "Workers",
    description: "Worker query results with filtering, refresh, and row actions.",
  },
];
const iterationsPanelOptions: PanelVisibilityOption<"iterations">[] = [
  {
    id: "iterations",
    label: "Iterations",
    description: "Iteration query results with filtering, refresh, and worker navigation.",
  },
];

export function WorkersView({
  categoryFilter,
  connection,
  definitionFilter,
  filterControl,
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
  filterControl?: PanelFilterControl;
  isLoadingTarget: boolean;
  isVisible: boolean;
  keyTypeFilter: string;
  onOpenWorker: (workerId: string) => void;
  onReady: () => void;
  refreshToken: number;
  stateFilter: WorkerState[];
}) {
  const [actionHighlight, setActionHighlight] = useState<ActiveWorkerRowHighlight | null>(null);
  const [selectedWorkerRowId, setSelectedWorkerRowId] = useState<string | null>(null);
  const [selectedWorkerResetKey, setSelectedWorkerResetKey] = useState<string | null>(null);
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
  const [manualRefreshToken, setManualRefreshToken] = useState(0);
  const queryKey = JSON.stringify(query);
  const selectionScopeKey = `${connection.apiUrl}\n${connection.systemName ?? ""}\n${queryKey}`;
  const scrollResetKey = `${connection.apiUrl}\n${connection.systemName ?? ""}\n${queryKey}`;
  const workerScrollTopRef = useRef(0);
  useEffect(() => {
    workerScrollTopRef.current = 0;
  }, [scrollResetKey]);
  const workers = useInfiniteWorkerQuery(
    connection,
    query,
    refreshToken + manualRefreshToken,
    isLoadingTarget
  );
  const [gridShape, setGridShape] = useState<WorkComponentShape>(
    queryGridShapeCapabilities.defaultShape
  );
  const [actionError, setActionError] = useState<string>();
  const [hiddenWorkerIds, setHiddenWorkerIds] = useState<ReadonlySet<string>>(() => new Set());
  const [hiddenPanelIds, setHiddenPanelIds] = useState<ReadonlySet<"workers">>(() => new Set());
  const [pendingActionWorkerId, setPendingActionWorkerId] = useState<string | null>(null);
  const refreshWorkers = useCallback((options?: { resetScroll?: boolean }) => {
    setActionError(undefined);
    setActionHighlight(null);
    if (options?.resetScroll === false) {
      workers.refreshLoadedWindow?.();
      return;
    }

    setManualRefreshToken((value) => value + 1);
  }, [workers]);
  const hideWorker = useCallback((workerId: string) => {
    setHiddenWorkerIds((current) => {
      if (current.has(workerId)) {
        return current;
      }

      return new Set(current).add(workerId);
    });
  }, []);
  const visibleWorkers = useMemo(
    () => hiddenWorkerIds.size === 0
      ? workers.items
      : workers.items.filter((worker) => !hiddenWorkerIds.has(worker.id.value)),
    [hiddenWorkerIds, workers.items]
  );
  const executeWorkerAction = useCallback(async (
    worker: WorkViewWorkerGridDetailed,
    action: WorkAction,
    options?: { watch?: boolean }
  ) => {
    setActionError(undefined);
    if (action !== "Purge") {
      setActionHighlight(null);
    }
    setPendingActionWorkerId(worker.id.value);
    const nextHighlight = action === "Purge"
      ? getNextVisibleWorkerHighlight(visibleWorkers, worker.id.value)
      : null;
    const nextActionHighlight = nextHighlight
      ? { ...nextHighlight, resetKey: scrollResetKey }
      : null;

    try {
      const result = await workableFetch<WorkerActionResult>(
        connection,
        `workers/${worker.id.value}/actions/${action.toLowerCase()}`,
        {
          method: "POST",
          body: JSON.stringify({ revision: worker.revision }),
        }
      );
      if (result.status && result.status !== "Accepted") {
        const detail = result.messages?.map((message) => message.text).filter(Boolean).join(" ");
        setActionError(detail || `${action} returned ${result.status}.`);
        return;
      }
      if (action === "Purge") {
        setActionHighlight(nextActionHighlight);
        hideWorker(worker.id.value);
      }
      if (options?.watch) {
        setSelectedWorkerRowId(worker.id.value);
        setSelectedWorkerResetKey(selectionScopeKey);
        onOpenWorker(worker.id.value);
      }
      if (action !== "Purge") {
        refreshWorkers({ resetScroll: false });
      }
    } catch (error) {
      if (action === "Purge" && isWorkerNotFoundError(error)) {
        setActionHighlight(nextActionHighlight);
        hideWorker(worker.id.value);
        return;
      }

      setActionError(
        error instanceof Error ? error.message : `Unable to ${action.toLowerCase()} worker.`
      );
    } finally {
      setPendingActionWorkerId(null);
    }
  }, [connection, hideWorker, onOpenWorker, refreshWorkers, scrollResetKey, selectionScopeKey, visibleWorkers]);
  const openWorkerRow = useCallback((worker: WorkViewWorkerGridDetailed) => {
    setActionHighlight(null);
    setSelectedWorkerRowId(worker.id.value);
    setSelectedWorkerResetKey(selectionScopeKey);
    onOpenWorker(worker.id.value);
  }, [onOpenWorker, selectionScopeKey]);
  const selectWorkerRow = useCallback((worker: WorkViewWorkerGridDetailed) => {
    setActionHighlight(null);
    setSelectedWorkerRowId(worker.id.value);
    setSelectedWorkerResetKey(selectionScopeKey);
  }, [selectionScopeKey]);
  const isReady = !workers.loading;
  useEffect(() => {
    if (isLoadingTarget && isReady) {
      onReady();
    }
  }, [isLoadingTarget, isReady, onReady]);

  const setWorkersPanelVisible = useCallback((panelId: "workers", visible: boolean) => {
    setHiddenPanelIds((current) => {
      const next = new Set(current);
      if (visible) {
        next.delete(panelId);
      } else {
        next.add(panelId);
      }
      return next;
    });
  }, []);

  const resetWorkersUiToDefaults = useCallback(() => {
    setHiddenPanelIds(new Set());
    setGridShape(queryGridShapeCapabilities.defaultShape);
  }, []);

  if (!isVisible) {
    return null;
  }

  const activeActionHighlight = actionHighlight?.resetKey === scrollResetKey
    ? actionHighlight
    : null;
  const activeSelectedWorkerRowId = selectedWorkerResetKey === selectionScopeKey
    ? selectedWorkerRowId
    : null;
  const workerHighlightId = activeActionHighlight
    ? activeActionHighlight.workerId
    : activeSelectedWorkerRowId;
  return (
    <ConsolePageLayout fill scrollMode="panel">
      <PanelAggregateFrame
        fill
        hiddenPanelIds={[...hiddenPanelIds]}
        onPanelVisibilityChange={setWorkersPanelVisible}
        onResetUi={resetWorkersUiToDefaults}
        padding="tightTop"
        panelOptions={workersPanelOptions}
        scrollMode="panel"
        settingsButtonLabel="Workers panel settings"
        settingsDescription="Checked panels are shown on the workers page."
        settingsTitle="Workers panels"
      >
        <ErrorPanel errors={[workers.error, actionError]} />
        {!hiddenPanelIds.has("workers") ? (
          <QueryPanelShell
            filterControl={filterControl}
            leadingActions={<QueryResultTotal noun="worker" totalCount={workers.totalCount} />}
            onClose={() => setWorkersPanelVisible("workers", false)}
            onShapeChange={setGridShape}
            shape={gridShape}
            supportedShapes={queryGridShapeCapabilities.supportedShapes}
            title="Workers"
          >
            <VirtualWorkerTable
              hasMore={workers.hasMore}
              highlightedWorkerId={workerHighlightId}
              highlightedWorkerIndex={activeActionHighlight?.fallbackIndex}
              loading={workers.loading}
              loadingMore={workers.loadingMore}
              loadMore={workers.loadMore}
              onAction={executeWorkerAction}
              onActionMenuOpen={selectWorkerRow}
              onSelect={openWorkerRow}
              onScrollPositionChange={(scrollTop) => {
                workerScrollTopRef.current = scrollTop;
              }}
              onView={openWorkerRow}
              pendingActionWorkerId={pendingActionWorkerId}
              scrollMemory={workerScrollTopRef}
              scrollResetKey={scrollResetKey}
              shape={gridShape}
              totalCount={workers.totalCount}
              workers={visibleWorkers}
            />
          </QueryPanelShell>
        ) : null}
      </PanelAggregateFrame>
    </ConsolePageLayout>
  );
}

export function IterationsView({
  categoryFilter,
  connection,
  definitionFilter,
  filterControl,
  isLoadingTarget,
  isVisible,
  keyTypeFilter,
  onOpenIteration,
  onReady,
  refreshToken,
  statusFilter,
}: {
  categoryFilter: string;
  connection: WorkableConnection;
  definitionFilter: string;
  filterControl?: PanelFilterControl;
  isLoadingTarget: boolean;
  isVisible: boolean;
  keyTypeFilter: string;
  onOpenIteration: (workerId: string, sequence: number) => void;
  onReady: () => void;
  refreshToken: number;
  statusFilter: WorkCompletionStatus[];
}) {
  const [selectedIterationRowKey, setSelectedIterationRowKey] = useState<string | null>(null);
  const [selectedIterationResetKey, setSelectedIterationResetKey] = useState<string | null>(null);
  const query = useMemo(
    () => ({
      category: normalizeCategoryFilter(categoryFilter) || undefined,
      definitionName: definitionFilter.trim() || undefined,
      keyType: keyTypeFilter.trim() || undefined,
      statuses: statusFilter.length === 0 ? undefined : statusFilter,
    }),
    [categoryFilter, definitionFilter, keyTypeFilter, statusFilter]
  );
  const queryKey = JSON.stringify(query);
  const selectionScopeKey = `${connection.apiUrl}\n${connection.systemName ?? ""}\n${queryKey}`;
  const scrollResetKey = `${connection.apiUrl}\n${connection.systemName ?? ""}\n${queryKey}`;
  const iterationScrollTopRef = useRef(0);
  useEffect(() => {
    iterationScrollTopRef.current = 0;
  }, [scrollResetKey]);
  const iterations = useInfiniteIterationQuery(
    connection,
    query,
    refreshToken,
    isLoadingTarget
  );
  const [gridShape, setGridShape] = useState<WorkComponentShape>(
    queryGridShapeCapabilities.defaultShape
  );
  const [hiddenPanelIds, setHiddenPanelIds] = useState<ReadonlySet<"iterations">>(() => new Set());
  const openIterationRow = useCallback((iteration: WorkViewIterationGridDetailed) => {
    if (!iteration.isFinal) {
      return;
    }

    setSelectedIterationRowKey(getIterationRowKey(iteration));
    setSelectedIterationResetKey(selectionScopeKey);
    onOpenIteration(iteration.workerId.value, iteration.sequence);
  }, [onOpenIteration, selectionScopeKey]);
  const isReady = !iterations.loading;
  useEffect(() => {
    if (isLoadingTarget && isReady) {
      onReady();
    }
  }, [isLoadingTarget, isReady, onReady]);

  const setIterationsPanelVisible = useCallback((panelId: "iterations", visible: boolean) => {
    setHiddenPanelIds((current) => {
      const next = new Set(current);
      if (visible) {
        next.delete(panelId);
      } else {
        next.add(panelId);
      }
      return next;
    });
  }, []);

  const resetIterationsUiToDefaults = useCallback(() => {
    setHiddenPanelIds(new Set());
    setGridShape(queryGridShapeCapabilities.defaultShape);
  }, []);

  if (!isVisible) {
    return null;
  }

  const activeSelectedIterationRowKey = selectedIterationResetKey === selectionScopeKey
    ? selectedIterationRowKey
    : null;
  return (
    <ConsolePageLayout fill scrollMode="panel">
      <PanelAggregateFrame
        fill
        hiddenPanelIds={[...hiddenPanelIds]}
        onPanelVisibilityChange={setIterationsPanelVisible}
        onResetUi={resetIterationsUiToDefaults}
        padding="tightTop"
        panelOptions={iterationsPanelOptions}
        scrollMode="panel"
        settingsButtonLabel="Iterations panel settings"
        settingsDescription="Checked panels are shown on the iterations page."
        settingsTitle="Iterations panels"
      >
        <ErrorPanel errors={[iterations.error]} />
        {!hiddenPanelIds.has("iterations") ? (
          <QueryPanelShell
            filterControl={filterControl}
            leadingActions={<QueryResultTotal noun="iteration" totalCount={iterations.totalCount} />}
            onClose={() => setIterationsPanelVisible("iterations", false)}
            onShapeChange={setGridShape}
            shape={gridShape}
            supportedShapes={queryGridShapeCapabilities.supportedShapes}
            title="Iterations"
          >
            <VirtualIterationTable
              hasMore={iterations.hasMore}
              highlightedIterationKey={activeSelectedIterationRowKey}
              highlightedWorkerId={null}
              iterations={iterations.items}
              loading={iterations.loading}
              loadingMore={iterations.loadingMore}
              loadMore={iterations.loadMore}
              onScrollPositionChange={(scrollTop) => {
                iterationScrollTopRef.current = scrollTop;
              }}
              onSelect={openIterationRow}
              scrollMemory={iterationScrollTopRef}
              scrollResetKey={scrollResetKey}
              shape={gridShape}
              totalCount={iterations.totalCount}
            />
          </QueryPanelShell>
        ) : null}
      </PanelAggregateFrame>
    </ConsolePageLayout>
  );
}

function getIterationRowKey(iteration: WorkViewIterationGridDetailed) {
  return `${iteration.workerId.value}:${iteration.sequence}`;
}

function QueryPanelShell({
  actions,
  children,
  filterControl,
  leadingActions,
  onClose,
  onShapeChange,
  shape,
  supportedShapes,
  title,
}: {
  actions?: ReactNode;
  children: ReactNode;
  filterControl?: PanelFilterControl;
  leadingActions?: ReactNode;
  onClose?: () => void;
  onShapeChange?: (shape: WorkComponentShape) => void;
  shape?: WorkComponentShape;
  supportedShapes?: readonly WorkComponentShape[];
  title: string;
}) {
  return (
    <PanelShell
      actions={actions}
      className="flex min-h-0 flex-1 flex-col overflow-hidden"
      contentClassName="mt-4 flex min-h-0 flex-1 flex-col overflow-hidden"
      filterControl={filterControl}
      leadingActions={leadingActions}
      onClose={onClose}
      onViewStateChange={onShapeChange}
      supportedViewStates={supportedShapes}
      title={title}
      viewState={shape}
    >
      <div className="flex min-h-0 flex-1 flex-col overflow-hidden">
        {children}
      </div>
    </PanelShell>
  );
}

function QueryTableStatus({
  label,
}: {
  label: string;
}) {
  return (
    <div className="flex min-h-0 flex-1 items-center justify-center rounded-lg border border-dashed p-8 text-center text-muted-foreground text-sm">
      <span>{label}</span>
    </div>
  );
}

function QueryTablePlaceholder() {
  return <div className="flex min-h-0 flex-1 rounded-lg border border-dashed" />;
}

function isWorkerNotFoundError(error: unknown) {
  if (!(error instanceof WorkableApiError) || error.status !== 404) {
    return false;
  }

  if (!isObjectWithMessages(error.body)) {
    return false;
  }

  return error.body.messages.some((message) => message.code === "workable.worker.not_found");
}

function isObjectWithMessages(value: unknown): value is { messages: Array<{ code?: string }> } {
  return typeof value === "object" &&
    value !== null &&
    "messages" in value &&
    Array.isArray((value as { messages?: unknown }).messages);
}

function getNextVisibleWorkerHighlight(
  workers: WorkViewWorkerGridDetailed[],
  workerId: string
): WorkerRowHighlight | null {
  const index = workers.findIndex((worker) => worker.id.value === workerId);
  if (index < 0) {
    return null;
  }

  const fallbackIndex = Math.min(index, Math.max(0, workers.length - 2));
  return {
    fallbackIndex,
    workerId: null,
  };
}

function VirtualWorkerTable({
  hasMore,
  highlightedWorkerId,
  highlightedWorkerIndex,
  loading,
  loadingMore,
  loadMore,
  onAction,
  onActionMenuOpen,
  onScrollPositionChange,
  onSelect,
  onView,
  pendingActionWorkerId,
  scrollMemory,
  scrollResetKey,
  shape,
  totalCount,
  workers,
}: {
  hasMore: boolean;
  highlightedWorkerId?: string | null;
  highlightedWorkerIndex?: number;
  loading: boolean;
  loadingMore: boolean;
  loadMore: () => void;
  onAction: (
    worker: WorkViewWorkerGridDetailed,
    action: WorkAction,
    options?: { watch?: boolean }
  ) => Promise<void>;
  onActionMenuOpen: (worker: WorkViewWorkerGridDetailed) => void;
  onScrollPositionChange: (scrollTop: number) => void;
  onSelect: (worker: WorkViewWorkerGridDetailed) => void;
  onView: (worker: WorkViewWorkerGridDetailed) => void;
  pendingActionWorkerId: string | null;
  scrollMemory: MutableRefObject<number>;
  scrollResetKey: string;
  shape: WorkComponentShape;
  totalCount?: number;
  workers: WorkViewWorkerGridDetailed[];
}) {
  const detailed = shape === "detailed";
  const scrollRef = useRef<HTMLDivElement>(null);
  const lastScrollResetKeyRef = useRef(scrollResetKey);
  const relativeNow = useLiveRelativeTimeNow();
  // eslint-disable-next-line react-hooks/incompatible-library -- TanStack Virtual owns scroll measurement state.
  const rowVirtualizer = useVirtualizer({
    count: workers.length,
    estimateSize: () => 64,
    getScrollElement: () => scrollRef.current,
    getItemKey: (index) => workers[index]?.id.value ?? index,
    overscan: 10,
  });
  const virtualItems = rowVirtualizer.getVirtualItems();
  const hasHighlightedWorker = highlightedWorkerId
    ? workers.some((worker) => worker.id.value === highlightedWorkerId)
    : false;

  useEffect(() => {
    if (scrollMemory.current > 0) {
      scrollRef.current?.scrollTo({ top: scrollMemory.current });
    }
  }, [scrollMemory]);

  useEffect(() => {
    if (
      loading &&
      !loadingMore &&
      lastScrollResetKeyRef.current !== scrollResetKey
    ) {
      lastScrollResetKeyRef.current = scrollResetKey;
      onScrollPositionChange(0);
      scrollRef.current?.scrollTo({ top: 0 });
    }
  }, [loading, loadingMore, onScrollPositionChange, scrollResetKey]);

  if (loading && workers.length === 0) {
    if (workers.length === 0 && totalCount === 0) {
      return <QueryTableStatus label="No workers matched the current query." />;
    }

    return <QueryTablePlaceholder />;
  }

  if (workers.length === 0) {
    return <QueryTableStatus label="No workers matched the current query." />;
  }

  return (
    <div className="flex min-h-0 flex-1 flex-col overflow-hidden rounded-lg border">
      <div className="grid bg-card shadow-[0_1px_0_var(--border)]">
        <div className="flex min-h-12">
          <div className="flex h-12 flex-[2_2_22rem] items-center px-3 font-medium text-sm">Definition</div>
          <div className="flex h-12 w-32 items-center px-3 font-medium text-sm">State</div>
          {detailed && <div className="flex h-12 w-72 items-center px-3 font-medium text-sm">Subject id</div>}
          {detailed && <div className="flex h-12 flex-[2_2_20rem] items-center px-3 font-medium text-sm">Identifiers</div>}
          <div className="flex h-12 w-36 items-center px-3 font-medium text-sm">Updated</div>
          <div className="flex h-12 w-28 items-center px-3 font-medium text-sm">Duration</div>
          <div className="flex h-12 w-12 items-center px-3" />
        </div>
      </div>
      <PanelScrollViewport
        className="workable-grid-scrollbar"
        hasMore={hasMore}
        loadedCount={workers.length}
        loading={loading}
        loadingMore={loadingMore}
        noun="worker"
        onLoadMore={loadMore}
        onScroll={(event) => {
          onScrollPositionChange(event.currentTarget.scrollTop);
        }}
        viewportRef={scrollRef}
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
              const isHighlighted = hasHighlightedWorker
                ? worker.id.value === highlightedWorkerId
                : virtualRow.index === highlightedWorkerIndex;

              return (
                <TableRow
                  className={`absolute flex h-16 w-full cursor-pointer overflow-hidden ${
                    isHighlighted
                      ? "bg-sky-500/10 ring-1 ring-inset ring-sky-500/40"
                      : ""
                  }`}
                  data-index={virtualRow.index}
                  key={virtualRow.key}
                  onClick={(event) => {
                    const target = event.target;
                    if (
                      target instanceof Element &&
                      target.closest("[data-worker-row-action]")
                    ) {
                      return;
                    }

                    onSelect(worker);
                  }}
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
                    {formatRelativeTime(worker.updatedAt, relativeNow)}
                  </TableCell>
                  <TableCell className="w-28 overflow-hidden">
                    <DurationValue
                      className="font-mono text-xs"
                      duration={formatWorkerDuration(worker)}
                    />
                  </TableCell>
                  <TableCell className="flex w-12 items-center justify-center overflow-hidden" data-worker-row-action>
                    <WorkerActionMenu
                      disabled={pendingActionWorkerId === worker.id.value}
                      onAction={(action) => onAction(worker, action)}
                      onOpen={() => onActionMenuOpen(worker)}
                      onStartAndWatch={() => onAction(worker, "Start", { watch: true })}
                      onView={() => onView(worker)}
                      worker={worker}
                    />
                  </TableCell>
                </TableRow>
              );
            })}
          </TableBody>
        </Table>
      </PanelScrollViewport>
    </div>
  );
}

function WorkerActionMenu({
  disabled,
  onAction,
  onOpen,
  onStartAndWatch,
  onView,
  worker,
}: {
  disabled: boolean;
  onAction: (action: WorkAction) => Promise<void> | void;
  onOpen: () => void;
  onStartAndWatch: () => Promise<void> | void;
  onView: () => void;
  worker: WorkViewWorkerGridDetailed;
}) {
  const actions = getWorkerActions(worker.state);

  return (
    <DropdownMenu>
      <DropdownMenuTrigger asChild>
        <Button
          aria-label={`Open actions for ${worker.definitionName}`}
          className="size-7 text-muted-foreground"
          data-worker-row-action
          disabled={disabled}
          onClick={(event) => event.stopPropagation()}
          onPointerDown={(event) => {
            event.stopPropagation();
            onOpen();
          }}
          size="icon-sm"
          type="button"
          variant="ghost"
        >
          {disabled ? (
            <Loader2 className="size-4 animate-spin" />
          ) : (
            <MoreHorizontal className="size-4" />
          )}
        </Button>
      </DropdownMenuTrigger>
      <DropdownMenuContent
        align="end"
        onClick={(event) => event.stopPropagation()}
        onPointerDown={(event) => event.stopPropagation()}
      >
        <DropdownMenuItem
          data-worker-row-action
          onClick={(event) => event.stopPropagation()}
          onPointerDown={(event) => event.stopPropagation()}
          onSelect={(event) => {
            event.stopPropagation();
            onView();
          }}
        >
          <SquareArrowOutUpRight className="size-4" />
          View
        </DropdownMenuItem>
        {isStartableWorker(worker.state) && (
          <DropdownMenuItem
            data-worker-row-action
            onClick={(event) => event.stopPropagation()}
            onPointerDown={(event) => event.stopPropagation()}
            onSelect={(event) => {
              event.stopPropagation();
              void onStartAndWatch();
            }}
          >
            <Play className="size-4" />
            Start & View
          </DropdownMenuItem>
        )}
        {actions.map((action) => {
          const Icon = workerActionIcons[action];
          return (
            <DropdownMenuItem
              data-worker-row-action
              key={action}
              onClick={(event) => event.stopPropagation()}
              onPointerDown={(event) => event.stopPropagation()}
              onSelect={(event) => {
                event.stopPropagation();
                void onAction(action);
              }}
            >
              <Icon className="size-4" />
              {action}
            </DropdownMenuItem>
          );
        })}
      </DropdownMenuContent>
    </DropdownMenu>
  );
}

const workerActionIcons = {
  Cancel: Ban,
  Pause,
  Purge: Trash2,
  Push: Clock3,
  Start: Play,
} satisfies Record<WorkAction, typeof Play>;

function getWorkerActions(state: WorkerState): WorkAction[] {
  switch (state) {
    case "Queued":
    case "Paused":
    case "Failed":
      return ["Start", "Cancel"];
    case "Running":
      return ["Pause", "Cancel"];
    case "Waiting":
    case "Retrying":
      return ["Pause", "Push", "Cancel"];
    case "Interrupted":
      return ["Cancel"];
    case "Canceled":
    case "Completed":
      return ["Purge"];
    case "Interrupting":
    case "Canceling":
    case "Pausing":
      return [];
  }
}

function isStartableWorker(state: WorkerState) {
  return state === "Queued" || state === "Paused" || state === "Failed";
}

function VirtualIterationTable({
  hasMore,
  highlightedIterationKey,
  highlightedWorkerId,
  iterations,
  loading,
  loadingMore,
  loadMore,
  onScrollPositionChange,
  onSelect,
  scrollMemory,
  scrollResetKey,
  shape,
  totalCount,
}: {
  hasMore: boolean;
  highlightedIterationKey?: string | null;
  highlightedWorkerId?: string | null;
  iterations: WorkViewIterationGridDetailed[];
  loading: boolean;
  loadingMore: boolean;
  loadMore: () => void;
  onScrollPositionChange: (scrollTop: number) => void;
  onSelect: (iteration: WorkViewIterationGridDetailed) => void;
  scrollMemory: MutableRefObject<number>;
  scrollResetKey: string;
  shape: WorkComponentShape;
  totalCount?: number;
}) {
  const detailed = shape === "detailed";
  const scrollRef = useRef<HTMLDivElement>(null);
  const lastScrollResetKeyRef = useRef(scrollResetKey);
  const relativeNow = useLiveRelativeTimeNow();
  // eslint-disable-next-line react-hooks/incompatible-library -- TanStack Virtual owns scroll measurement state.
  const rowVirtualizer = useVirtualizer({
    count: iterations.length,
    estimateSize: () => 64,
    getScrollElement: () => scrollRef.current,
    getItemKey: (index) => {
      const iteration = iterations[index];
      return iteration ? getIterationRowKey(iteration) : index;
    },
    overscan: 10,
  });
  const virtualItems = rowVirtualizer.getVirtualItems();

  useEffect(() => {
    if (scrollMemory.current > 0) {
      scrollRef.current?.scrollTo({ top: scrollMemory.current });
    }
  }, [scrollMemory]);

  useEffect(() => {
    if (
      loading &&
      !loadingMore &&
      lastScrollResetKeyRef.current !== scrollResetKey
    ) {
      lastScrollResetKeyRef.current = scrollResetKey;
      onScrollPositionChange(0);
      scrollRef.current?.scrollTo({ top: 0 });
    }
  }, [loading, loadingMore, onScrollPositionChange, scrollResetKey]);

  if (loading && iterations.length === 0) {
    if (iterations.length === 0 && totalCount === 0) {
      return <QueryTableStatus label="No iterations matched the current query." />;
    }

    return <QueryTablePlaceholder />;
  }

  if (iterations.length === 0) {
    return <QueryTableStatus label="No iterations matched the current query." />;
  }

  return (
    <div className="flex min-h-0 flex-1 flex-col overflow-hidden rounded-lg border">
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
      <PanelScrollViewport
        className="workable-grid-scrollbar"
        hasMore={hasMore}
        loadedCount={iterations.length}
        loading={loading}
        loadingMore={loadingMore}
        noun="iteration"
        onLoadMore={loadMore}
        onScroll={(event) => {
          onScrollPositionChange(event.currentTarget.scrollTop);
        }}
        viewportRef={scrollRef}
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
              const iterationKey = getIterationRowKey(iteration);
              const isOpenable = iteration.isFinal;
              const isHighlighted = highlightedIterationKey
                ? iterationKey === highlightedIterationKey
                : iteration.workerId.value === highlightedWorkerId;

              return (
                <TableRow
                  className={`absolute flex h-16 w-full overflow-hidden ${
                    isOpenable ? "cursor-pointer" : "cursor-default"
                  } ${
                    isHighlighted
                      ? "bg-sky-500/10 ring-1 ring-inset ring-sky-500/40"
                      : ""
                  }`}
                  data-index={virtualRow.index}
                  key={virtualRow.key}
                  onClick={() => {
                    if (isOpenable) {
                      onSelect(iteration);
                    }
                  }}
                  style={{
                    transform: `translateY(${virtualRow.start}px)`,
                  }}
                  title={isOpenable
                    ? `Open iteration #${iteration.sequence}`
                    : "Iteration detail is only available after the iteration reaches a final state."}
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
                    {formatRelativeTime(iteration.completedAt, relativeNow)}
                  </TableCell>
                  <TableCell className="w-28 overflow-hidden">
                    <DurationValue duration={formatExecutionDuration(iteration.executionDuration)} />
                  </TableCell>
                </TableRow>
              );
            })}
          </TableBody>
        </Table>
      </PanelScrollViewport>
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
    <div className="flex h-7 shrink-0 items-center text-muted-foreground text-xs tabular-nums">
      {totalCount.toLocaleString()} {noun}{totalCount === 1 ? "" : "s"}
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
  const resetKey = `${apiUrl}\n${systemName ?? ""}\n${key}`;
  const requestKey = `${resetKey}\n${refreshToken}`;
  const resetKeyRef = useRef<string | undefined>(undefined);
  const loadedRequestKeyRef = useRef<string | undefined>(undefined);
  const boundedTake = Math.min(maxQueryTake, Math.max(minQueryTake, queryPageTake));

  useEffect(() => {
    stateRef.current = state;
  }, [state]);

  const fetchPage = useCallback(async (skip: number) => {
    const parsedQuery = JSON.parse(key) as {
      category?: string;
      definitionName?: string;
      includeSubcategories?: boolean;
      keyType?: string;
      states?: WorkerState[];
    };
    const requestConnection = { apiUrl, systemName };

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
  const resetKey = `${apiUrl}\n${systemName ?? ""}\n${key}`;
  const requestKey = `${resetKey}\n${refreshToken}`;
  const resetKeyRef = useRef<string | undefined>(undefined);
  const loadedRequestKeyRef = useRef<string | undefined>(undefined);
  const boundedTake = Math.min(maxQueryTake, Math.max(minQueryTake, queryPageTake));

  useEffect(() => {
    stateRef.current = state;
  }, [state]);

  const fetchPage = useCallback(async (skip: number) => {
    const parsedQuery = JSON.parse(key) as {
      category?: string;
      definitionName?: string;
      keyType?: string;
      statuses?: WorkCompletionStatus[];
    };
    const requestConnection = { apiUrl, systemName };

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

function appendUniqueWorkers(
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

function appendUniqueIterations(
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

function isNewerWorkerRow(
  current: WorkViewWorkerGridDetailed,
  next: WorkViewWorkerGridDetailed
) {
  return next.revision > current.revision ||
    Date.parse(next.updatedAt) > Date.parse(current.updatedAt);
}

function isNewerIterationRow(
  current: WorkViewIterationGridDetailed,
  next: WorkViewIterationGridDetailed
) {
  return Date.parse(next.completedAt) > Date.parse(current.completedAt);
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
    case "Interrupted":
      return "border-amber-500/40 bg-amber-500/10 text-amber-300";
    default:
      return "border-muted-foreground/30 text-muted-foreground";
  }
}
