"use client";

import {
  Activity,
  Boxes,
  CheckCircle2,
  CircleAlert,
  Hourglass,
  Info,
  Rows4,
} from "lucide-react";
import type { ReactNode } from "react";
import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import {
  ConsolePageLayout,
} from "@/components/features/console/console-primitives";
import { PanelAggregateFrame } from "@/components/features/console/panel-aggregate-frame";
import {
  useConsolePageRealtimeView,
  useRegisterConsolePageRealtimeView,
  type ConsolePageRealtimeViewDescriptor,
} from "@/components/features/console/page-realtime-view";
import {
  createRealtimePayloadMessage,
  type RealtimePayloadMessage,
} from "@/components/features/console/realtime-payload";
import {
  useRegisterConsoleHeaderCapabilities,
  type ConsoleHeaderCapabilities,
} from "@/components/features/console/header-capabilities";
import {
  type OverviewPanelId,
  overviewPanelOptions,
  overviewPanelShapeCapabilities,
  type OverviewPanelShapeMap,
} from "@/components/features/console/overview-panels";
import { PanelShell } from "@/components/features/console/panel-shell";
import {
  type ConsoleRealtimeEventMessage,
  type ConsoleRealtimeViewLoadable,
  useConsoleRealtimeView,
  type ConsoleRealtimeEventLoadable,
  useConsoleRealtimeEventStream,
} from "@/components/features/console/realtime";
import type { Loadable, OverviewScope } from "@/components/features/console/types";
import { Badge } from "@/components/ui/badge";
import { Card, CardContent, CardDescription, CardHeader } from "@/components/ui/card";
import { Skeleton } from "@/components/ui/skeleton";
import { Tooltip, TooltipContent, TooltipTrigger } from "@/components/ui/tooltip";
import { createOverviewComponentScope } from "@/components/workable/console/catalog-path";
import {
  createWorkComponentRequest,
  getWorkComponentData,
  getWorkComponentErrors,
} from "@/components/workable/console/component-results";
import {
  formatQueueAge,
} from "@/components/workable/console/console-format";
import { ErrorPanel } from "@/components/workable/console/feedback-panel";
import {
  CompactIterationStrip,
  IterationStatusStrip,
  OverviewIterationList,
  TopKeyTypePanel,
  type WorkOverviewIterationsComponent,
} from "@/components/workable/console/overview-iterations";
import {
  ThroughputChartPanel,
  compactThroughputWindow,
  throughputWindows,
  type ThroughputMode,
  type ThroughputSeriesId,
} from "@/components/workable/console/overview-throughput";
import {
  OverviewWorkerList,
  type WorkerActionTarget,
} from "@/components/workable/console/overview-workers";
import { StatusCountPill } from "@/components/workable/console/status-count-pill";
import {
  stateTone,
  workableFetch,
  workableQueryFetch,
  type WorkAction,
  type WorkCompletionStatus,
  type WorkComponentQueryResult,
  type WorkComponentRequest,
  type WorkComponentShape,
  type WorkOverviewFailedWorker,
  type WorkOverviewIteration,
  type WorkOverviewThroughputComponent,
  type WorkSystemFailedWorkersOverview,
  type WorkSystemOverview,
  type WorkSystemAccessSummary,
  type WorkableConnection,
  type WorkableRealtimeEvent,
  type WorkableRealtimeEventCriteria,
  type WorkerState,
} from "@/lib/workable";
import {
  semanticTextToneClass,
} from "@/lib/ui/state-tones";

export {
  formatFailedWorkerDuration,
  getWorkerRowActions,
  isDetailedWorkerOverviewItem,
  toFailedWorkerActionTarget,
} from "@/components/workable/console/overview-workers";

export {
  formatIterationCount,
} from "@/components/workable/console/overview-iterations";

export {
  chartPoint,
  chartY,
  createAreaPath,
  createEmptyThroughputBucket,
  createExecutionPressureMetric,
  createLinePath,
  createThroughputMetrics,
  createThroughputSeries,
  createTimeAxisTicks,
  createYAxisTicks,
  formatChartTimeAxisLabel,
  formatCompactRate,
  formatMilliseconds,
  formatRate,
  formatThroughputAxisValue,
  formatThroughputWindowLabel,
  getNiceChartMax,
  getThroughputBuckets,
  isThroughputSeriesId,
  isZeroOnlySeries,
  parseChartTimestamp,
  pluralize,
  type ThroughputMetric,
  type ThroughputMode,
  type ThroughputSeries,
  type ThroughputSeriesId,
} from "@/components/workable/console/overview-throughput";

const jsonByteEncoder = new TextEncoder();
const noop = () => undefined;
type WorkOverviewSystemComponent = Pick<WorkSystemOverview, "systemName" | "systemState">;
type WorkOverviewWorkersComponent = Pick<
  WorkSystemOverview,
  | "activeWorkerCount"
  | "definitionCount"
  | "failedWorkerCount"
  | "finalWorkerCount"
  | "oldestQueuedAt"
  | "workerCountByState"
>;
export type RealtimeEventMessage = ConsoleRealtimeEventMessage;

export type RealtimeEventLoadable = ConsoleRealtimeEventLoadable<RealtimeEventMessage>;
export type RealtimeViewLoadable<T> = ConsoleRealtimeViewLoadable<T, RealtimePayloadMessage>;
const overviewWorkerStates: WorkerState[] = [
  "Queued",
  "Running",
  "Waiting",
  "Retrying",
  "Paused",
  "Interrupted",
  "Failed",
  "Canceled",
  "Completed",
];
const activeWorkerStates: WorkerState[] = ["Queued", "Running", "Waiting", "Retrying", "Paused"];
const failedWorkerStates: WorkerState[] = ["Failed"];
const finalWorkerStates: WorkerState[] = ["Canceled", "Completed"];
const clickableTileClass = "transition-colors hover:border-primary/70 hover:bg-accent/50";
const subtleClickableTileClass = "transition-colors hover:border-primary/60 hover:bg-accent/40";
export const overviewResumeRefreshThresholdMs = 30_000;

export function getOverviewPanelShape(
  shapes: OverviewPanelShapeMap,
  panelId: OverviewPanelId
) {
  const shape = shapes[panelId];
  return overviewPanelShapeCapabilities[panelId].supportedShapes.includes(shape)
    ? shape
    : overviewPanelShapeCapabilities[panelId].defaultShape;
}

export function shouldRefreshFailedWorkersAfterAction(realtime: {
  connectionState: string;
  enabled: boolean;
}) {
  return !realtime.enabled || realtime.connectionState !== "connected";
}

export function shouldRefreshOverviewAfterPageResume(hiddenDurationMs: number) {
  return hiddenDurationMs >= overviewResumeRefreshThresholdMs;
}

export function resolveOverviewData<T>(
  snapshotData: T | undefined,
  realtimeData: T | undefined,
  preferSnapshotAfterRecovery: boolean
) {
  if (preferSnapshotAfterRecovery && snapshotData !== undefined) {
    return snapshotData;
  }

  return realtimeData ?? snapshotData;
}

export function OverviewView({
  access,
  connection,
  hiddenPanelIds,
  hiddenThroughputSeries,
  isVisible,
  onConnectionError,
  onOpenCatalog,
  onOpenIterations,
  onOpenKeyType,
  onReady,
  onOpenWorker,
  onPanelShapeChange,
  onPanelVisibilityChange,
  onResetUi,
  onActiveRealtimeConnectionCountChange,
  onRealtimePayloadOpenChange,
  onStateLoaded,
  onThroughputSeriesToggle,
  onViewIterationsByStatus,
  onViewWorkersByState,
  overviewScope,
  panelShapes,
  realtimePayloadCaptureEnabled,
  realtimePayloadMaxMessages,
  realtimePayloadOpen,
  refreshToken,
  renderControls,
}: {
  access?: WorkSystemAccessSummary;
  connection: WorkableConnection;
  hiddenPanelIds: OverviewPanelId[];
  hiddenThroughputSeries: ThroughputSeriesId[];
  isVisible: boolean;
  onConnectionError: () => void;
  onOpenCatalog: () => void;
  onOpenIterations: () => void;
  onOpenKeyType: (keyType: string) => void;
  onReady: () => void;
  onOpenWorker: (workerId: string) => void;
  onPanelShapeChange: (panelId: OverviewPanelId, shape: WorkComponentShape) => void;
  onPanelVisibilityChange: (panelId: OverviewPanelId, visible: boolean) => void;
  onResetUi: () => void;
  onActiveRealtimeConnectionCountChange?: (count: number) => void;
  onRealtimePayloadOpenChange?: (open: boolean) => void;
  onStateLoaded: (state: string) => void;
  onThroughputSeriesToggle: (seriesId: ThroughputSeriesId) => void;
  onViewIterationsByStatus: (statuses: WorkCompletionStatus[]) => void;
  onViewWorkersByState: (states: WorkerState[]) => void;
  overviewScope: OverviewScope | null;
  panelShapes: OverviewPanelShapeMap;
  realtimePayloadCaptureEnabled?: boolean;
  realtimePayloadMaxMessages?: number;
  realtimePayloadOpen?: boolean;
  refreshToken: number;
  renderControls: (state: {
    loading: boolean;
    refreshing: boolean;
  }) => ReactNode;
}) {
  const [actionError, setActionError] = useState<string>();
  const [actionWorkerId, setActionWorkerId] = useState<string | null>(null);
  const [failedWorkersSlice, setFailedWorkersSlice] = useState<{
    data: WorkSystemFailedWorkersOverview;
    key: string;
  } | null>(null);
  const [throughputMode, setThroughputMode] = useState<ThroughputMode>("completion");
  const [throughputWindowSeconds, setThroughputWindowSeconds] = useState(60);
  const [overviewRecoveryVersion, setOverviewRecoveryVersion] = useState(0);
  const [realtimeRecoveryVersion, setRealtimeRecoveryVersion] = useState(0);
  const [preferSnapshotAfterRecovery, setPreferSnapshotAfterRecovery] = useState(false);
  const hiddenStartedAtRef = useRef<number | null>(null);
  const recoveryRealtimeBaselineRef = useRef<WorkComponentQueryResult | undefined>(undefined);
  const realtimeOverviewDataRef = useRef<WorkComponentQueryResult | undefined>(undefined);
  const payloadOpen = realtimePayloadOpen ?? false;
  const payloadCaptureEnabled = realtimePayloadCaptureEnabled ?? true;
  const payloadMaxMessages = realtimePayloadMaxMessages ?? 100;
  const setPayloadOpen = onRealtimePayloadOpenChange ?? noop;
  const lacksReadableWorkAccess =
    access !== undefined &&
    !access.canReadAllWork &&
    access.readableDefinitionCount === 0;
  const canUseRealtimeOverview = lacksReadableWorkAccess
    ? true
    : (access?.canReadAllWork ?? true) || (access?.readableDefinitionCount ?? 0) > 0;
  const canOperateWork =
    access === undefined ||
    access.canOperateAllWork ||
    access.operableDefinitionCount > 0;
  const throughputWindow =
    throughputWindows.find((window) => window.seconds === throughputWindowSeconds) ??
    throughputWindows[0];
  const isPanelVisible = useCallback(
    (panelId: OverviewPanelId) => !hiddenPanelIds.includes(panelId),
    [hiddenPanelIds]
  );
  const panelShape = useCallback(
    (panelId: OverviewPanelId) => getOverviewPanelShape(panelShapes, panelId),
    [panelShapes]
  );
  const shouldFetchPanel = useCallback(
    (panelId: OverviewPanelId) =>
      isVisible && isPanelVisible(panelId),
    [isPanelVisible, isVisible]
  );
  const workersShape = panelShape("workers");
  const failedWorkersShape = panelShape("failedWorkers");
  const throughputShape = panelShape("throughput");
  const requestedThroughputWindow = throughputShape === "compact"
    ? compactThroughputWindow
    : throughputWindow;
  const iterationsShape = panelShape("iterations");
  const failedIterationsShape = panelShape("failedIterations");
  const completedIterationsShape = panelShape("completedIterations");
  const overviewComponents = useMemo(() => {
    const components: WorkComponentRequest[] = [
      createWorkComponentRequest("system", "system", "detailed"),
    ];

    if (shouldFetchPanel("workers")) {
      components.push(createWorkComponentRequest("workers", "workers", workersShape));
    }
    if (shouldFetchPanel("failedWorkers")) {
      components.push(createWorkComponentRequest("failedWorkers", "failedWorkers", failedWorkersShape));
    }
    if (shouldFetchPanel("iterations")) {
      components.push(createWorkComponentRequest("iterations", "iterations", iterationsShape));
    }
    if (shouldFetchPanel("failedIterations")) {
      components.push(createWorkComponentRequest("failedIterations", "failedIterations", failedIterationsShape));
    }
    if (shouldFetchPanel("completedIterations")) {
      components.push(createWorkComponentRequest("completedIterations", "completedIterations", completedIterationsShape));
    }
    if (shouldFetchPanel("throughput")) {
      components.push(createWorkComponentRequest(
        "throughput",
        "throughput",
        throughputShape,
        {
          bucketSeconds: requestedThroughputWindow.bucketSeconds,
          windowSeconds: requestedThroughputWindow.seconds,
        }
      ));
    }

    return components;
  }, [
    completedIterationsShape,
    failedIterationsShape,
    failedWorkersShape,
    iterationsShape,
    requestedThroughputWindow.bucketSeconds,
    requestedThroughputWindow.seconds,
    shouldFetchPanel,
    throughputShape,
    workersShape,
  ]);
  const overviewRequest = useMemo(
    () => ({
      components: overviewComponents,
      scope: createOverviewComponentScope(overviewScope),
    }),
    [overviewComponents, overviewScope]
  );
  const systemOnlyOverviewRequest = useMemo(
    () => ({
      components: [createWorkComponentRequest("system", "system", "detailed")],
      scope: createOverviewComponentScope(overviewScope),
    }),
    [overviewScope]
  );
  const effectiveOverviewRequest = lacksReadableWorkAccess
    ? systemOnlyOverviewRequest
    : overviewRequest;
  const failedWorkersRefreshRequest = useMemo(
    () => ({
      components: [
        createWorkComponentRequest("workers", "workers", workersShape),
        createWorkComponentRequest("failedWorkers", "failedWorkers", failedWorkersShape),
      ],
      scope: createOverviewComponentScope(overviewScope),
    }),
    [failedWorkersShape, overviewScope, workersShape]
  );
  const effectiveRefreshToken = `${refreshToken}:${overviewRecoveryVersion}`;
  const failedWorkersKey = `${connection.apiUrl}:${connection.systemName ?? ""}:${JSON.stringify(failedWorkersRefreshRequest)}:${effectiveRefreshToken}`;
  const overview = useWorkablePostResource<WorkComponentQueryResult>(
    connection,
    isVisible ? "views/overview" : null,
    effectiveOverviewRequest,
    effectiveRefreshToken,
    {
      resetKey: `${connection.apiUrl}\n${connection.systemName ?? ""}`,
      retainDataOnRequestChange: true,
    }
  );
  const realtimeOverviewDescriptor = useMemo<ConsolePageRealtimeViewDescriptor>(
    () => ({
      body: effectiveOverviewRequest,
      captureEnabled: payloadCaptureEnabled && payloadOpen,
      connection,
      connectionInstanceKey: `overview-recovery:${realtimeRecoveryVersion}`,
      enabled: isVisible && canUseRealtimeOverview && Boolean(connection.realtimeHubPath),
      maxMessages: payloadMaxMessages,
      subscription: "overview",
      viewName: "overview",
    }),
    [
      canUseRealtimeOverview,
      connection,
      effectiveOverviewRequest,
      isVisible,
      payloadCaptureEnabled,
      payloadMaxMessages,
      payloadOpen,
      realtimeRecoveryVersion,
    ]
  );
  useRegisterConsolePageRealtimeView({
    active: isVisible,
    descriptor: realtimeOverviewDescriptor,
    id: "overview",
  });
  const realtimeOverview = useConsolePageRealtimeView<WorkComponentQueryResult>("overview");
  useEffect(() => {
    realtimeOverviewDataRef.current = realtimeOverview.data;
  }, [realtimeOverview.data]);
  const recoverOverviewAfterResume = useCallback(() => {
    if (!isVisible) {
      return;
    }

    recoveryRealtimeBaselineRef.current = realtimeOverviewDataRef.current;
    setPreferSnapshotAfterRecovery(true);
    setOverviewRecoveryVersion((current) => current + 1);
    setRealtimeRecoveryVersion((current) => current + 1);
  }, [isVisible]);

  useEffect(() => {
    if (!preferSnapshotAfterRecovery) {
      return;
    }

    if (!realtimeOverview.enabled) {
      setPreferSnapshotAfterRecovery(false);
      recoveryRealtimeBaselineRef.current = realtimeOverview.data;
      return;
    }

    if (realtimeOverview.data !== recoveryRealtimeBaselineRef.current) {
      setPreferSnapshotAfterRecovery(false);
      recoveryRealtimeBaselineRef.current = realtimeOverview.data;
    }
  }, [preferSnapshotAfterRecovery, realtimeOverview.data, realtimeOverview.enabled]);

  useEffect(() => {
    if (!isVisible || typeof document === "undefined" || typeof window === "undefined") {
      hiddenStartedAtRef.current = null;
      return;
    }

    const now = () => Date.now();
    if (document.visibilityState === "hidden") {
      hiddenStartedAtRef.current = now();
    } else {
      hiddenStartedAtRef.current = null;
    }

    const handleVisibilityChange = () => {
      if (document.visibilityState === "hidden") {
        hiddenStartedAtRef.current = now();
        return;
      }

      const hiddenStartedAt = hiddenStartedAtRef.current;
      hiddenStartedAtRef.current = null;
      if (hiddenStartedAt === null) {
        return;
      }

      if (shouldRefreshOverviewAfterPageResume(now() - hiddenStartedAt)) {
        recoverOverviewAfterResume();
      }
    };

    const handleOnline = () => {
      if (document.visibilityState === "visible") {
        recoverOverviewAfterResume();
      }
    };

    document.addEventListener("visibilitychange", handleVisibilityChange);
    window.addEventListener("online", handleOnline);
    return () => {
      document.removeEventListener("visibilitychange", handleVisibilityChange);
      window.removeEventListener("online", handleOnline);
    };
  }, [isVisible, recoverOverviewAfterResume]);

  const shouldUseFailedWorkersActionRefresh = shouldRefreshFailedWorkersAfterAction(realtimeOverview);
  const overviewData = resolveOverviewData(
    overview.data,
    realtimeOverview.data,
    preferSnapshotAfterRecovery
  );
  const togglePayloadOpen = useCallback(() => {
    setPayloadOpen(!payloadOpen);
  }, [payloadOpen, setPayloadOpen]);
  const overviewControls = renderControls({
    loading: overview.loading,
    refreshing: !!overview.refreshing || !!realtimeOverview.refreshing,
  });
  const headerCapabilities = useMemo<ConsoleHeaderCapabilities>(
    () => ({
      realtime: {
        connectionState: realtimeOverview.connectionState,
        enabled: realtimeOverview.enabled,
        menuItems: [
          {
            active: payloadOpen,
            icon: <Rows4 className="size-4" />,
            id: "overview-realtime-payloads",
            label: "Realtime payloads",
            onSelect: togglePayloadOpen,
          },
        ],
      },
      refresh: {
        disabled: overview.loading || (realtimeOverview.enabled && realtimeOverview.connectionState === "connected"),
        refreshing: Boolean(overview.refreshing) || Boolean(realtimeOverview.refreshing),
      },
    }),
    [
      overview.loading,
      overview.refreshing,
      payloadOpen,
      realtimeOverview.connectionState,
      realtimeOverview.enabled,
      realtimeOverview.refreshing,
      togglePayloadOpen,
    ]
  );
  const isReady = !overview.loading;
  const systemComponent = getWorkComponentData<WorkOverviewSystemComponent>(
    overviewData,
    "system"
  );
  const workersComponent = getWorkComponentData<WorkOverviewWorkersComponent>(
    overviewData,
    "workers"
  );
  const failedWorkersComponent = getWorkComponentData<WorkOverviewFailedWorker[]>(
    overviewData,
    "failedWorkers"
  );
  const iterationsComponent = getWorkComponentData<WorkOverviewIterationsComponent>(
    overviewData,
    "iterations"
  );
  const failedIterationsComponent = getWorkComponentData<WorkOverviewIteration[]>(
    overviewData,
    "failedIterations"
  );
  const completedIterationsComponent = getWorkComponentData<WorkOverviewIteration[]>(
    overviewData,
    "completedIterations"
  );
  const throughputComponent = getWorkComponentData<WorkOverviewThroughputComponent>(
    overviewData,
    "throughput"
  );
  const throughputData = throughputComponent?.throughput;
  const activeFailedWorkersSlice = shouldUseFailedWorkersActionRefresh &&
    failedWorkersSlice?.key === failedWorkersKey
    ? failedWorkersSlice.data
    : undefined;
  const activeWorkerCount = activeFailedWorkersSlice?.activeWorkerCount ??
    throughputComponent?.activeWorkerCount ??
    workersComponent?.activeWorkerCount ??
    0;
  const finalWorkerCount = activeFailedWorkersSlice?.finalWorkerCount ??
    workersComponent?.finalWorkerCount ??
    0;
  const failedWorkerCount = activeFailedWorkersSlice?.failedWorkerCount ??
    workersComponent?.failedWorkerCount ??
    0;
  const workerCountByState = activeFailedWorkersSlice?.workerCountByState ??
    workersComponent?.workerCountByState ??
    {};
  const oldestQueuedAt = activeFailedWorkersSlice?.oldestQueuedAt ??
    workersComponent?.oldestQueuedAt;
  const oldestQueuedAge = formatQueueAge(oldestQueuedAt);
  const failedWorkers = activeFailedWorkersSlice?.failedWorkers ??
    failedWorkersComponent ??
    [];
  const componentErrors = getWorkComponentErrors(overviewData);
  const showFailedIterations = isPanelVisible("failedIterations");
  const showCompletedIterations = isPanelVisible("completedIterations");

  const executeWorkerAction = async (worker: WorkerActionTarget, action: WorkAction) => {
    setActionError(undefined);
    setActionWorkerId(worker.id.value);
    try {
      await workableFetch<{ status: string; messages?: { text: string }[] }>(
        connection,
        `workers/${worker.id.value}/actions/${action.toLowerCase()}`,
        {
          method: "POST",
          body: JSON.stringify({ revision: worker.revision }),
        }
      );
    } catch (error) {
      setActionError(
        error instanceof Error ? error.message : `Unable to ${action.toLowerCase()} worker.`
      );
      setActionWorkerId(null);
      return;
    }

    if (!shouldUseFailedWorkersActionRefresh) {
      setActionWorkerId(null);
      return;
    }

    try {
      const failedWorkersOverview = await workableQueryFetch<WorkComponentQueryResult>(
        connection,
        "views/overview",
        {
          method: "POST",
          body: JSON.stringify(failedWorkersRefreshRequest),
        }
      );
      const refreshedWorkers = getWorkComponentData<WorkOverviewWorkersComponent>(
        failedWorkersOverview,
        "workers"
      );
      const refreshedFailedWorkers = getWorkComponentData<WorkOverviewFailedWorker[]>(
        failedWorkersOverview,
        "failedWorkers"
      );
      setFailedWorkersSlice({
        data: {
          activeWorkerCount: refreshedWorkers?.activeWorkerCount ?? activeWorkerCount,
          failedWorkerCount: refreshedWorkers?.failedWorkerCount ?? failedWorkerCount,
          finalWorkerCount: refreshedWorkers?.finalWorkerCount ?? finalWorkerCount,
          failedWorkers: refreshedFailedWorkers ?? failedWorkers,
          oldestQueuedAt: refreshedWorkers?.oldestQueuedAt ?? oldestQueuedAt,
          workerCountByState: refreshedWorkers?.workerCountByState ?? workerCountByState,
        },
        key: failedWorkersKey,
      });
    } catch (error) {
      const detail = error instanceof Error ? error.message : "Request failed.";
      setActionError(
        `Failed workers refresh failed. ${detail}`
      );
    } finally {
      setActionWorkerId(null);
    }
  };

  useEffect(() => {
    if (isReady) {
      onReady();
    }
  }, [isReady, onReady]);

  useEffect(() => {
    if (!overview.error && systemComponent?.systemState) {
      onStateLoaded(systemComponent.systemState);
    }
  }, [overview.error, systemComponent?.systemState, onStateLoaded]);

  useEffect(() => {
    if (overview.error) {
      onConnectionError();
    }
  }, [overview.error, onConnectionError]);
  useRegisterConsoleHeaderCapabilities({
    active: isVisible,
    capabilities: headerCapabilities,
    id: "overview",
  });
  useEffect(() => {
    onActiveRealtimeConnectionCountChange?.(
      realtimeOverview.enabled && realtimeOverview.connectionState !== "disabled" ? 1 : 0
    );
  }, [
    onActiveRealtimeConnectionCountChange,
    realtimeOverview.connectionState,
    realtimeOverview.enabled,
  ]);

  return (
    <ConsolePageLayout>
      <PanelAggregateFrame
        controls={overviewControls}
        hiddenPanelIds={hiddenPanelIds}
        onPanelVisibilityChange={onPanelVisibilityChange}
        onResetUi={onResetUi}
        padding="tightTop"
        panelOptions={overviewPanelOptions}
        settingsButtonLabel="Overview panel settings"
        settingsDescription="Checked panels are shown on the overview screen."
        settingsTitle="Overview panels"
      >
        <ErrorPanel
          errors={[
            overview.error,
            realtimeOverview.error,
            actionError,
            ...componentErrors,
          ]}
        />
        {lacksReadableWorkAccess && (
          <Card>
            <CardHeader>
              <div className="space-y-1">
                <h2 className="font-semibold leading-none tracking-tight">No work access</h2>
                <p className="text-muted-foreground text-sm">
                  You can connect to this system, but you do not have permission to read work.
                  Work overview panels are hidden, but system state can still update live.
                </p>
              </div>
            </CardHeader>
          </Card>
        )}
        {!lacksReadableWorkAccess && (
          <>
      {isPanelVisible("workers") && (
        <PanelShell
          actions={workersShape === "compact" ? (
            <CompactWorkerStrip
              activeWorkerCount={activeWorkerCount}
              failedWorkerCount={failedWorkerCount}
              loading={overview.loading}
              oldestQueuedText={oldestQueuedAge.text}
              oldestQueuedWarning={oldestQueuedAge.isWarning}
              onOpenActive={() => onViewWorkersByState(activeWorkerStates)}
              onOpenFailed={() => onViewWorkersByState(failedWorkerStates)}
              onOpenQueued={() => onViewWorkersByState(["Queued"])}
            />
          ) : undefined}
          className={workersShape === "compact" ? "w-full xl:w-[calc(50%_-_0.5rem)]" : undefined}
          contentClassName={workersShape === "compact" ? "hidden" : undefined}
          description={undefined}
          onClose={() => onPanelVisibilityChange("workers", false)}
          onViewStateChange={(shape) => onPanelShapeChange("workers", shape)}
          supportedViewStates={overviewPanelShapeCapabilities.workers.supportedShapes}
          viewState={workersShape}
          title={
            <>
              Workers
              {workersShape !== "compact" && (
                <Tooltip delayDuration={500} disableHoverableContent>
                  <TooltipTrigger asChild>
                    <button
                      aria-label="Workers: Workers grouped by current state, with summary links for active, final, failed, and catalog counts."
                      className="group inline-flex size-5 items-center justify-center rounded-sm text-muted-foreground transition-colors hover:text-foreground focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring focus-visible:ring-offset-2 focus-visible:ring-offset-background"
                      type="button"
                    >
                      <Info className="size-3.5 shrink-0" />
                    </button>
                  </TooltipTrigger>
                  <TooltipContent className="max-w-64 whitespace-normal text-left" side="top" sideOffset={6}>
                    Workers grouped by current state, with summary links for active, final, failed, and catalog counts.
                  </TooltipContent>
                </Tooltip>
              )}
            </>
          }
        >
          {workersShape !== "compact" && (
            <>
              <WorkerStateStrip
                counts={workerCountByState}
                loading={overview.loading}
                onSelectState={(state) => onViewWorkersByState([state])}
              />
              <div className="grid gap-3 md:grid-cols-2 xl:grid-cols-5">
                <MetricCard
                  compact
                  description="Workers that are not completed, canceled, or failed."
                  icon={Activity}
                  label="Active workers"
                  loading={overview.loading}
                  onClick={() => onViewWorkersByState(activeWorkerStates)}
                  value={activeWorkerCount}
                />
                <MetricCard
                  compact
                  description="Definitions currently associated with active or queued workers."
                  icon={Boxes}
                  label="Catalog"
                  loading={overview.loading}
                  onClick={onOpenCatalog}
                  value={workersComponent?.definitionCount ?? 0}
                />
                <MetricCard
                  compact
                  description="How long the oldest currently queued worker has been waiting."
                  icon={Hourglass}
                  label="Oldest queued"
                  loading={overview.loading}
                  onClick={() => onViewWorkersByState(["Queued"])}
                  tone={oldestQueuedAge.isWarning ? semanticTextToneClass("warning", "strong") : undefined}
                  value={oldestQueuedAge.text}
                />
                <MetricCard
                  compact
                  description="Workers in a final state: canceled or completed."
                  icon={CheckCircle2}
                  label="Final workers"
                  loading={overview.loading}
                  onClick={() => onViewWorkersByState(finalWorkerStates)}
                  value={finalWorkerCount}
                />
                <MetricCard
                  compact
                  description="Workers currently in the failed state."
                  icon={CircleAlert}
                  label="Failed workers"
                  loading={overview.loading}
                  onClick={() => onViewWorkersByState(failedWorkerStates)}
                  tone={semanticTextToneClass("danger", "strong")}
                  value={failedWorkerCount}
                />
              </div>
            </>
          )}
        </PanelShell>
      )}
      {isPanelVisible("failedWorkers") && (
        <OverviewWorkerList
          emptyText="No failed workers."
          loading={overview.loading && failedWorkers.length === 0}
          panelClassName={failedWorkersShape === "standard" ? "w-full xl:w-[calc(50%_-_0.5rem)]" : undefined}
          onClose={() => onPanelVisibilityChange("failedWorkers", false)}
          onShapeChange={(shape) => onPanelShapeChange("failedWorkers", shape)}
          onWorkerAction={executeWorkerAction}
          onOpenWorker={onOpenWorker}
          onViewState={() => onViewWorkersByState(failedWorkerStates)}
          pendingActionWorkerId={actionWorkerId}
          shape={failedWorkersShape}
          showActions={canOperateWork}
          state="Failed"
          supportedShapes={overviewPanelShapeCapabilities.failedWorkers.supportedShapes}
          title="Recent Failed Workers"
          workers={failedWorkers}
        />
      )}
      {isPanelVisible("throughput") && (
        <ThroughputChartPanel
          hiddenSeries={hiddenThroughputSeries}
          loading={overview.loading && !throughputData}
          mode={throughputMode}
          onClose={() => onPanelVisibilityChange("throughput", false)}
          onModeChange={setThroughputMode}
          onShapeChange={(shape) => onPanelShapeChange("throughput", shape)}
          onSeriesToggle={onThroughputSeriesToggle}
          onWindowChange={setThroughputWindowSeconds}
          shape={throughputShape}
          supportedShapes={overviewPanelShapeCapabilities.throughput.supportedShapes}
          throughput={throughputData}
          windowSeconds={requestedThroughputWindow.seconds}
        />
      )}
      {isPanelVisible("iterations") && (
        <PanelShell
          actions={iterationsShape === "compact" ? (
            <CompactIterationStrip
              statuses={["Executing", "Completed", "Failed"]}
              counts={iterationsComponent?.iterationCountByStatus ?? {}}
              loading={overview.loading}
              onSelectStatus={(status) => onViewIterationsByStatus([status])}
            />
          ) : undefined}
          className={iterationsShape === "compact" ? "w-full xl:w-[calc(50%_-_0.5rem)]" : undefined}
          contentClassName={iterationsShape === "compact" ? "hidden" : undefined}
          description={undefined}
          onClose={() => onPanelVisibilityChange("iterations", false)}
          onViewStateChange={(shape) => onPanelShapeChange("iterations", shape)}
          supportedViewStates={overviewPanelShapeCapabilities.iterations.supportedShapes}
          viewState={iterationsShape}
          title={
            <>
              Iterations
              <Tooltip delayDuration={500} disableHoverableContent>
                <TooltipTrigger asChild>
                  <button
                    aria-label="Iterations: Worker iterations grouped by status and common relationship type."
                    className="group inline-flex size-5 items-center justify-center rounded-sm text-muted-foreground transition-colors hover:text-foreground focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring focus-visible:ring-offset-2 focus-visible:ring-offset-background"
                    type="button"
                  >
                    <Info className="size-3.5 shrink-0" />
                  </button>
                </TooltipTrigger>
                <TooltipContent className="max-w-64 whitespace-normal text-left" side="top" sideOffset={6}>
                  Worker iterations grouped by status, with common relationship types for quick filtering.
                </TooltipContent>
              </Tooltip>
            </>
          }
        >
          {iterationsShape !== "compact" && (
            <IterationStatusStrip
              counts={iterationsComponent?.iterationCountByStatus ?? {}}
              loading={overview.loading}
              onSelectStatus={(status) => onViewIterationsByStatus([status])}
            />
          )}
          {iterationsShape === "standard" && (
            <TopKeyTypePanel
              keys={iterationsComponent?.commonKeyTypes ?? []}
              loading={overview.loading}
              onShowMore={onOpenIterations}
              onSelectKeyType={onOpenKeyType}
            />
          )}
        </PanelShell>
      )}
      <div className="grid gap-4 xl:grid-cols-2">
        {showFailedIterations && (
          <OverviewIterationList
            emptyText="No failed iterations."
            loading={overview.loading}
            onClose={() => onPanelVisibilityChange("failedIterations", false)}
            onShapeChange={(shape) => onPanelShapeChange("failedIterations", shape)}
            onOpenWorker={onOpenWorker}
            onViewState={() => onViewIterationsByStatus(["Failed"])}
            panelClassName={failedIterationsShape === "detailed" ? "xl:col-span-2" : undefined}
            shape={failedIterationsShape}
            status="Failed"
            supportedShapes={overviewPanelShapeCapabilities.failedIterations.supportedShapes}
            title="Recent Failed Iterations"
            iterations={failedIterationsComponent ?? []}
          />
        )}
        {showCompletedIterations && (
          <OverviewIterationList
            emptyText="No completed iterations."
            loading={overview.loading}
            onClose={() => onPanelVisibilityChange("completedIterations", false)}
            onShapeChange={(shape) => onPanelShapeChange("completedIterations", shape)}
            onOpenWorker={onOpenWorker}
            onViewState={() => onViewIterationsByStatus(["Completed"])}
            panelClassName={completedIterationsShape === "detailed" ? "xl:col-span-2" : undefined}
            shape={completedIterationsShape}
            status="Completed"
            supportedShapes={overviewPanelShapeCapabilities.completedIterations.supportedShapes}
            title="Recent Completed Iterations"
            iterations={completedIterationsComponent ?? []}
          />
        )}
      </div>
        </>
      )}
      </PanelAggregateFrame>
    </ConsolePageLayout>
  );
}

function CompactWorkerStrip({
  activeWorkerCount,
  failedWorkerCount,
  loading,
  oldestQueuedText,
  oldestQueuedWarning,
  onOpenActive,
  onOpenFailed,
  onOpenQueued,
}: {
  activeWorkerCount: number;
  failedWorkerCount: number;
  loading: boolean;
  oldestQueuedText: string;
  oldestQueuedWarning: boolean;
  onOpenActive: () => void;
  onOpenFailed: () => void;
  onOpenQueued: () => void;
}) {
  if (loading) {
    return (
      <div className="workable-grid-scrollbar flex h-8 items-center gap-2 overflow-x-auto">
        <Skeleton className="h-7 w-32 rounded-full" />
        <Skeleton className="h-7 w-30 rounded-full" />
        <Skeleton className="h-7 w-30 rounded-full" />
      </div>
    );
  }

  return (
    <div className="workable-grid-scrollbar flex min-h-8 items-center gap-2 overflow-x-auto">
      <CompactWorkerStripItem
        label="Oldest queued"
        onClick={onOpenQueued}
        value={oldestQueuedText}
        valueClassName={oldestQueuedWarning ? semanticTextToneClass("warning", "strong") : undefined}
      />
      <CompactWorkerStripItem
        label="Active workers"
        onClick={onOpenActive}
        value={activeWorkerCount}
      />
      <CompactWorkerStripItem
        label="Failed workers"
        onClick={onOpenFailed}
        value={failedWorkerCount}
        valueClassName={semanticTextToneClass("danger", "strong")}
      />
    </div>
  );
}

function CompactWorkerStripItem({
  label,
  onClick,
  value,
  valueClassName,
}: {
  label: string;
  onClick: () => void;
  value: number | string;
  valueClassName?: string;
}) {
  return (
    <button
      aria-label={`Open ${label.toLowerCase()}`}
      className="inline-flex h-8 shrink-0 cursor-pointer items-center gap-2 rounded-full border border-foreground/10 bg-muted/25 px-3 text-left transition-colors hover:border-primary/60 hover:bg-accent/50 hover:text-primary focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring focus-visible:ring-offset-2 focus-visible:ring-offset-background"
      onClick={onClick}
      type="button"
    >
      <span className="whitespace-nowrap text-muted-foreground text-xs">{label}</span>
      <span className={`whitespace-nowrap font-mono font-medium text-sm leading-none ${valueClassName ?? ""}`}>
        {value}
      </span>
    </button>
  );
}

function WorkerStateStrip({
  counts,
  loading,
  onSelectState,
}: {
  counts: Partial<Record<WorkerState, number>>;
  loading: boolean;
  onSelectState: (state: WorkerState) => void;
}) {
  if (loading) {
    return (
      <div className="workable-grid-scrollbar flex gap-2 overflow-x-auto pb-1">
        {overviewWorkerStates.map((state) => (
          <Skeleton className="h-8 min-w-28 flex-1 rounded-full" key={state} />
        ))}
      </div>
    );
  }

  return (
    <div className="workable-grid-scrollbar flex gap-2 overflow-x-auto pb-1">
      {overviewWorkerStates.map((state) => (
        <StatusCountPill
          ariaLabel={`Open workers filtered by ${state}`}
          badgeClassName={stateTone(state)}
          className={`min-w-28 flex-1 justify-center text-center ${subtleClickableTileClass}`}
          key={state}
          onClick={() => onSelectState(state)}
          label={state}
          value={counts[state] ?? 0}
        />
      ))}
    </div>
  );
}

function MetricCard({
  compact,
  description,
  icon: Icon,
  label,
  loading,
  onClick,
  tone,
  value,
}: {
  compact?: boolean;
  description: string;
  icon: typeof Activity;
  label: string;
  loading: boolean;
  onClick?: () => void;
  tone?: string;
  value: number | string;
}) {
  const content = (
    <>
      <CardHeader className={compact ? "pb-0" : "pb-2"}>
        <CardDescription
          className={
            onClick
              ? "inline-flex w-full items-center justify-center gap-1.5 text-center text-primary"
              : "inline-flex w-full items-center justify-center gap-1.5 text-center"
          }
        >
          <Tooltip delayDuration={500} disableHoverableContent>
            <TooltipTrigger asChild>
              <span className="inline-flex min-w-0 items-center justify-center gap-1.5">
                <Icon className="size-4 shrink-0 text-muted-foreground" />
                <span className="truncate">{label}</span>
              </span>
            </TooltipTrigger>
            <TooltipContent className="max-w-64 whitespace-normal text-left" side="top" sideOffset={6}>
              {description}
            </TooltipContent>
          </Tooltip>
        </CardDescription>
      </CardHeader>
      <CardContent className={compact ? "flex justify-center pt-0" : "flex justify-center"}>
        {loading ? (
          <Skeleton className={compact ? "h-6 w-14" : "h-9 w-24"} />
        ) : (
          <div className={`text-center font-mono leading-none ${compact ? "text-xl" : "text-3xl"} ${tone ?? ""}`}>{value}</div>
        )}
      </CardContent>
    </>
  );

  return (
    <Card
      className={`${compact ? "gap-2 py-3" : ""} ${onClick ? clickableTileClass : ""}`}
      size={compact ? "sm" : "default"}
    >
      {onClick ? (
        <button
          aria-label={`Open ${label.toLowerCase()}`}
          className="block w-full cursor-pointer text-left focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring focus-visible:ring-offset-2 focus-visible:ring-offset-background"
          onClick={onClick}
          type="button"
        >
          {content}
        </button>
      ) : (
        content
      )}
    </Card>
  );
}

export function useWorkableRealtimeEvents(
  connection: WorkableConnection | null,
  criteria: WorkableRealtimeEventCriteria,
  enabled: boolean,
  captureEnabled: boolean,
  maxMessages: number
): RealtimeEventLoadable {
  const criteriaKey = JSON.stringify(criteria);
  return useConsoleRealtimeEventStream({
    captureEnabled,
    connection,
    createBatchMessage: (batch, nextMessageId) =>
      createRealtimeEventMessage(
        batch.events,
        `batch:${nextMessageId}`,
        Date.now(),
        batch.sentAt
      ),
    createSingleMessage: (workEvent, nextMessageId) =>
      createRealtimeEventMessage(
        [workEvent],
        `events:${nextMessageId}`,
        Date.now()
      ),
    debugLabel: "events",
    enabled,
    maxMessages,
    subscriptionErrorMessage: "Realtime event subscription failed.",
    watchArgument: criteria,
    watchArgumentKey: criteriaKey,
    watchMethod: "WatchEvents",
    watchStoppedMessage: "Realtime event connection closed.",
  });
}

export function useWorkableRealtimeView<T>(
  connection: WorkableConnection | null,
  viewName: string,
  body: unknown,
  enabled: boolean,
  captureEnabled: boolean,
  maxMessages: number,
  subscription?: string
): RealtimeViewLoadable<T> {
  const subscriptionName = subscription ?? viewName;

  return useConsoleRealtimeView<T, RealtimePayloadMessage>({
    body,
    captureEnabled,
    connection,
    createMessage: (result, nextMessageId) => {
      const payloadJson = JSON.stringify(result);
      return createRealtimePayloadMessage(
        result,
        payloadJson,
        `${subscriptionName}:${nextMessageId}`,
        viewName,
        subscriptionName,
        connection
      );
    },
    enabled,
    maxMessages,
    subscription: subscriptionName,
    viewName,
  });
}

export function createRealtimeEventMessage(
  events: WorkableRealtimeEvent[],
  id: string,
  receivedAt: number,
  sentAt?: string
): RealtimeEventMessage {
  const isBatch = events.length > 1;
  const value = !isBatch
    ? events[0]
    : {
        sentAt: sentAt ?? new Date(receivedAt).toISOString(),
        events,
      };
  const payloadSize = measureJsonBytes(value);

  return {
    batchId: isBatch ? id : undefined,
    batchSize: isBatch ? events.length : undefined,
    bytes: payloadSize.bytes,
    bytesEstimated: payloadSize.estimated,
    events,
    eventTypes: [...new Set(events.map((workEvent) => workEvent.eventType))],
    id,
    receivedAt,
    sentAt,
    value,
  };
}

export function measureJsonBytes(value: unknown, budget = 250_000) {
  const seen = new WeakSet<object>();
  const state = {
    bytes: 0,
    estimated: false,
  };

  const add = (text: string) => {
    if (state.bytes >= budget) {
      state.estimated = true;
      return;
    }

    state.bytes += jsonByteEncoder.encode(text).length;
    if (state.bytes >= budget) {
      state.bytes = budget;
      state.estimated = true;
    }
  };

  const visit = (current: unknown) => {
    if (state.bytes >= budget) {
      state.estimated = true;
      return;
    }

    if (current === null) {
      add("null");
      return;
    }

    if (Array.isArray(current)) {
      add("[");
      for (let index = 0; index < current.length; index++) {
        if (index > 0) {
          add(",");
        }
        visit(current[index]);
        if (state.bytes >= budget) {
          state.estimated = true;
          return;
        }
      }
      add("]");
      return;
    }

    if (typeof current === "object") {
      if (seen.has(current)) {
        add("\"[Circular]\"");
        state.estimated = true;
        return;
      }

      seen.add(current);
      add("{");
      Object.entries(current as Record<string, unknown>).forEach(([key, item], index) => {
        if (index > 0) {
          add(",");
        }
        add(JSON.stringify(key));
        add(":");
        visit(item);
      });
      add("}");
      return;
    }

    add(JSON.stringify(current));
  };

  visit(value);
  return state;
}


function useWorkablePostResource<T>(
  connection: WorkableConnection,
  path: string | null,
  body: unknown,
  refreshToken: number | string,
  options?: {
    resetKey?: string | number | null;
    retainDataOnRequestChange?: boolean;
  }
): Loadable<T> {
  const [state, setState] = useState<Loadable<T>>({ loading: !!path });
  const apiUrl = connection.apiUrl;
  const systemName = connection.systemName;
  const bodyKey = JSON.stringify(body);
  const requestKey = `${apiUrl}\n${systemName ?? ""}\n${path ?? ""}\n${bodyKey}`;
  const previousRequestKey = useRef<string | null>(null);
  const retainDataOnRequestChange = options?.retainDataOnRequestChange === true;
  const resetKey = options?.resetKey ?? null;
  const lastResetKeyRef = useRef<string | number | null>(resetKey);

  useEffect(() => {
    if (!path) {
      lastResetKeyRef.current = resetKey;
      previousRequestKey.current = requestKey;
      queueMicrotask(() =>
        setState((current) =>
          !current.data && !current.error && !current.loading && !current.refreshing
            ? current
            : { loading: false }
        )
      );
      return;
    }

    let canceled = false;
    const requestChanged = previousRequestKey.current !== requestKey;
    const resetChanged = lastResetKeyRef.current !== resetKey;
    lastResetKeyRef.current = resetKey;
    previousRequestKey.current = requestKey;
    queueMicrotask(() => {
      if (!canceled) {
        setState((current) => {
          const retainCurrentData =
            !resetChanged &&
            requestChanged &&
            retainDataOnRequestChange &&
            current.data !== undefined;

          return {
            ...(requestChanged && !retainCurrentData ? {} : current),
            error: undefined,
            loading: retainCurrentData ? false : requestChanged || current.data === undefined,
            refreshing: retainCurrentData || (!requestChanged && current.data !== undefined),
          };
        });
      }
    });

    const requestConnection = { apiUrl, systemName };
    workableQueryFetch<T>(requestConnection, path, {
      method: "POST",
      body: bodyKey,
    })
      .then((data) => {
        if (!canceled) {
          setState({ data, loading: false, refreshing: false });
        }
      })
      .catch((error) => {
        if (!canceled) {
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
  }, [apiUrl, bodyKey, path, refreshToken, requestKey, resetKey, retainDataOnRequestChange, systemName]);

  return state;
}
