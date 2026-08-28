"use client";

import type { ReactNode } from "react";
import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { ConsolePageLayout } from "@/components/features/console/console-primitives";
import { PanelAggregateFrame } from "@/components/features/console/panel-aggregate-frame";
import {
  PanelShell,
  type PanelFilterControl,
} from "@/components/features/console/panel-shell";
import { usePanelVisibilityState } from "@/components/features/console/panel-visibility-state";
import type { PanelVisibilityOption } from "@/components/features/console/panel-visibility-settings";
import {
  normalizeCategoryFilter,
} from "@/components/workable/console/catalog-path";
import { ErrorPanel } from "@/components/workable/console/feedback-panel";
import {
  getIterationRowKey,
  useInfiniteIterationQuery,
  useInfiniteWorkerQuery,
} from "@/components/workable/console/query-data";
import {
  QueryResultTotal,
  VirtualIterationTable,
  VirtualWorkerTable,
  getNextVisibleWorkerHighlight,
  isWorkerNotFoundError,
} from "@/components/workable/console/query-tables";
import {
  workableFetch,
  type WorkAction,
  type WorkCompletionStatus,
  type WorkComponentShape,
  type WorkKeyKind,
  type WorkViewIterationGridDetailed,
  type WorkViewWorkerGridDetailed,
  type WorkableConnection,
  type WorkerState,
} from "@/lib/workable";

export {
  appendUniqueIterations,
  appendUniqueWorkers,
  getIterationRowKey,
  isNewerIterationRow,
  isNewerWorkerRow,
} from "@/components/workable/console/query-data";

export {
  DurationValue,
  IdentifierSummary,
  QueryResultTotal,
  QueryTablePlaceholder,
  QueryTableStatus,
  TypedValueSummary,
  formatWorkerDuration,
  getNextVisibleWorkerHighlight,
  getWorkerActions,
  isObjectWithMessages,
  isStartableWorker,
  isWorkerNotFoundError,
} from "@/components/workable/console/query-tables";

type WorkerActionResult = {
  messages?: Array<{ text?: string }>;
  status?: string;
};

type ActiveWorkerRowHighlight = {
  fallbackIndex: number;
  resetKey: string;
  workerId: string | null;
};

const queryGridShapeCapabilities = {
  defaultShape: "detailed",
  supportedShapes: ["detailed"],
} as const satisfies {
  defaultShape: WorkComponentShape;
  supportedShapes: WorkComponentShape[];
};
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
  keyKindFilter,
  keyTypeFilter,
  keyValueFilter,
  onOpenWorker,
  onReady,
  refreshToken,
  showActions = true,
  stateFilter,
}: {
  categoryFilter: string;
  connection: WorkableConnection;
  definitionFilter: string;
  filterControl?: PanelFilterControl;
  isLoadingTarget: boolean;
  isVisible: boolean;
  keyKindFilter: WorkKeyKind | "Any";
  keyTypeFilter: string;
  keyValueFilter: string;
  onOpenWorker: (workerId: string) => void;
  onReady: () => void;
  refreshToken: number;
  showActions?: boolean;
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
      keyKind: keyKindFilter === "Any" ? undefined : keyKindFilter,
      keyType: keyTypeFilter.trim() || undefined,
      keyValue: keyValueFilter.trim() || undefined,
      states: stateFilter.length === 0 ? undefined : stateFilter,
    }),
    [categoryFilter, definitionFilter, keyKindFilter, keyTypeFilter, keyValueFilter, stateFilter]
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
  const {
    hiddenPanelIdList,
    hiddenPanelIds,
    resetPanelVisibility: resetWorkersPanelVisibility,
    setPanelVisible: setWorkersPanelVisible,
  } = usePanelVisibilityState<"workers">();
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

  const resetWorkersUiToDefaults = useCallback(() => {
    resetWorkersPanelVisibility();
    setGridShape(queryGridShapeCapabilities.defaultShape);
  }, [resetWorkersPanelVisibility]);

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
        hiddenPanelIds={hiddenPanelIdList}
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
              showActions={showActions}
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
  keyKindFilter,
  keyTypeFilter,
  keyValueFilter,
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
  keyKindFilter: WorkKeyKind | "Any";
  keyTypeFilter: string;
  keyValueFilter: string;
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
      keyKind: keyKindFilter === "Any" ? undefined : keyKindFilter,
      keyType: keyTypeFilter.trim() || undefined,
      keyValue: keyValueFilter.trim() || undefined,
      statuses: statusFilter.length === 0 ? undefined : statusFilter,
    }),
    [categoryFilter, definitionFilter, keyKindFilter, keyTypeFilter, keyValueFilter, statusFilter]
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
  const {
    hiddenPanelIdList,
    hiddenPanelIds,
    resetPanelVisibility: resetIterationsPanelVisibility,
    setPanelVisible: setIterationsPanelVisible,
  } = usePanelVisibilityState<"iterations">();
  const openIterationRow = useCallback((iteration: WorkViewIterationGridDetailed) => {
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

  const resetIterationsUiToDefaults = useCallback(() => {
    resetIterationsPanelVisibility();
    setGridShape(queryGridShapeCapabilities.defaultShape);
  }, [resetIterationsPanelVisibility]);

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
        hiddenPanelIds={hiddenPanelIdList}
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

export function QueryPanelShell({
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
