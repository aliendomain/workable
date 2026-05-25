"use client";

import {
  HubConnection,
  HubConnectionBuilder,
  HubConnectionState,
  LogLevel,
} from "@microsoft/signalr";
import {
  Activity,
  ArrowDown,
  ArrowUp,
  Ban,
  Boxes,
  CheckCircle2,
  ChevronRight,
  CircleAlert,
  FileJson,
  Equal,
  Hourglass,
  Info,
  Loader2,
  MoreHorizontal,
  Play,
  Rows2,
  Rows3,
  Rows4,
  X,
} from "lucide-react";
import type { PointerEvent, ReactNode } from "react";
import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { createPortal } from "react-dom";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Card, CardContent, CardDescription, CardHeader } from "@/components/ui/card";
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
  TableHead,
  TableHeader,
  TableRow,
} from "@/components/ui/table";
import { Tabs, TabsContent, TabsList, TabsTrigger } from "@/components/ui/tabs";
import { Tooltip, TooltipContent, TooltipTrigger } from "@/components/ui/tooltip";
import { ErrorPanel } from "@/components/workable/console/feedback-panel";
import {
  formatRelativeTime,
  useLiveRelativeTimeNow,
} from "@/components/workable/console/live-relative-time";
import { IdentifierSummary, TypedValueSummary } from "@/components/workable/console/query-screens";
import {
  createWorkableRealtimeUrl,
  getWorkableRealtimeAccessToken,
  stateTone,
  workableFetch,
  type WorkAction,
  type WorkCompletionStatus,
  type WorkComponentQueryResult,
  type WorkComponentRequest,
  type WorkComponentResult,
  type WorkComponentShape,
  type WorkOverviewFailedWorker,
  type WorkOverviewFailedWorkerDetailed,
  type WorkOverviewIteration,
  type WorkIterationKeyTypeFacet,
  type WorkOverviewThroughputComponent,
  type WorkSystemFailedWorkersOverview,
  type WorkSystemOverview,
  type WorkSystemThroughput,
  type WorkThroughputBucket,
  type WorkThroughputLiveSummary,
  type WorkSystemAccessSummary,
  type WorkableConnection,
  type WorkableRealtimeEvent,
  type WorkableRealtimeEventBatch,
  type WorkableRealtimeEventCriteria,
  type WorkerOverviewItem,
  type WorkerState,
} from "@/lib/workable";

type ThroughputMode = "completion" | "execution";
const throughputSeriesIds = ["started", "completed", "failed", "canceled"] as const;
const jsonByteEncoder = new TextEncoder();
type ThroughputSeriesId = "started" | "completed" | "failed" | "canceled";
type ThroughputMetric = {
  description: string;
  icon?: typeof ArrowUp;
  iconClass?: string;
  id: string;
  label: string;
  pulseClass?: string;
  value: string;
  valueClass?: string;
  widthClass?: string;
};
type ThroughputSeries = {
  color: string;
  gradientId: string;
  id: string;
  label: string;
  legendClass: string;
  strokeDasharray?: string;
  strokeWidth?: string;
  values: number[];
};
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
type WorkerActionTarget = Pick<WorkerOverviewItem, "definitionName" | "id" | "revision" | "state">;
type WorkOverviewIterationsComponent = {
  commonKeyTypes?: WorkIterationKeyTypeFacet[];
  iterationCountByStatus: Partial<Record<WorkCompletionStatus, number>>;
};
type OverviewScope = {
  category?: string;
  definitionName?: string;
  includeSubcategories?: boolean;
};
type Loadable<T> = {
  data?: T;
  error?: string;
  loading: boolean;
  refreshing?: boolean;
};
export type RealtimePayloadPanelState = {
  captureEnabled: boolean;
  connectionState: string;
  enabled: boolean;
  externalMessages: RealtimePayloadMessage[];
  hubUrl?: string | null;
  maxMessages: number;
  messages: RealtimePayloadMessage[];
  onCaptureEnabledChange: (enabled: boolean) => void;
  onClearExternalMessages: () => void;
  onClearMessages: () => void;
  onMaxMessagesChange: (maxMessages: number) => void;
  onOpenChange: (open: boolean) => void;
  open: boolean;
};
export type RealtimePayloadMessage = {
  bytes: number;
  components: Array<{ id: string; shape?: string; status?: string }>;
  id: string;
  receivedAt: number;
  subscription: string;
  value: unknown;
  viewName: string;
};

export type RealtimeEventMessage = {
  batchId?: string;
  batchSize?: number;
  bytes: number;
  bytesEstimated?: boolean;
  events: WorkableRealtimeEvent[];
  eventTypes: string[];
  id: string;
  receivedAt: number;
  sentAt?: string;
  value: WorkableRealtimeEvent | WorkableRealtimeEventBatch;
};

export type RealtimeEventLoadable = {
  clearMessages: () => void;
  connectionState: string;
  enabled: boolean;
  error?: string;
  hubUrl?: string | null;
  loading?: boolean;
  messages: RealtimeEventMessage[];
};
type RealtimePayloadComponentData = {
  data: unknown;
  id: string;
  shape?: WorkComponentShape;
  status?: string;
};
export const overviewPanelIds = [
  "workers",
  "failedWorkers",
  "throughput",
  "iterations",
  "failedIterations",
  "completedIterations",
] as const;
export type OverviewPanelId = (typeof overviewPanelIds)[number];
export type OverviewPanelShapeMap = Record<OverviewPanelId, WorkComponentShape>;
const overviewShapeOptions: Array<{
  icon: typeof Rows2;
  label: string;
  shape: WorkComponentShape;
}> = [
  { icon: Rows2, label: "Compact", shape: "compact" },
  { icon: Rows3, label: "Standard", shape: "standard" },
  { icon: Rows4, label: "Detailed", shape: "detailed" },
];
export const overviewPanelShapeCapabilities: Record<OverviewPanelId, {
  defaultShape: WorkComponentShape;
  supportedShapes: WorkComponentShape[];
}> = {
  workers: {
    defaultShape: "standard",
    supportedShapes: ["compact", "standard"],
  },
  failedWorkers: {
    defaultShape: "standard",
    supportedShapes: ["standard", "detailed"],
  },
  throughput: {
    defaultShape: "standard",
    supportedShapes: ["compact", "standard"],
  },
  iterations: {
    defaultShape: "standard",
    supportedShapes: ["compact", "standard"],
  },
  failedIterations: {
    defaultShape: "standard",
    supportedShapes: ["standard", "detailed"],
  },
  completedIterations: {
    defaultShape: "standard",
    supportedShapes: ["standard", "detailed"],
  },
};
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
const iterationStatuses: WorkCompletionStatus[] = ["Executing", "Completed", "Failed", "Interrupted", "Canceled", "Paused"];
const throughputWindows = [
  { bucketSeconds: 1, label: "60s", seconds: 60 },
  { bucketSeconds: 5, label: "5m", seconds: 5 * 60 },
  { bucketSeconds: 15, label: "15m", seconds: 15 * 60 },
  { bucketSeconds: 60, label: "1h", seconds: 60 * 60 },
];
const compactThroughputWindow = throughputWindows[0];
const clickableTileClass = "transition-colors hover:border-primary/70 hover:bg-accent/50";
const subtleClickableTileClass = "transition-colors hover:border-primary/60 hover:bg-accent/40";

export function getOverviewPanelShape(
  shapes: OverviewPanelShapeMap,
  panelId: OverviewPanelId
) {
  const shape = shapes[panelId];
  return overviewPanelShapeCapabilities[panelId].supportedShapes.includes(shape)
    ? shape
    : overviewPanelShapeCapabilities[panelId].defaultShape;
}

function overviewComponent(
  id: string,
  type: string = id,
  shape: WorkComponentShape = "detailed",
  options?: unknown
): WorkComponentRequest {
  return options === undefined ? { id, shape, type } : { id, options, shape, type };
}
export function OverviewView({
  access,
  connection,
  externalRealtimeMessages,
  hiddenPanelIds,
  hiddenThroughputSeries,
  isVisible,
  onClearExternalRealtimeMessages,
  onConnectionError,
  onOpenCatalog,
  onOpenIterations,
  onOpenKeyType,
  onReady,
  onOpenWorker,
  onPanelShapeChange,
  onPanelVisibilityChange,
  onRealtimePayloadCaptureEnabledChange,
  onRealtimePayloadMaxMessagesChange,
  onRealtimePayloadOpenChange,
  onStateLoaded,
  onThroughputSeriesToggle,
  onViewIterationsByStatus,
  onViewWorkersByState,
  overviewScope,
  panelShapes,
  realtimeFeatures,
  realtimePayloadCaptureEnabled,
  realtimePayloadMaxMessages,
  realtimePayloadOpen,
  refreshToken,
  renderToolbar,
}: {
  access?: WorkSystemAccessSummary;
  connection: WorkableConnection;
  externalRealtimeMessages?: RealtimePayloadMessage[];
  hiddenPanelIds: OverviewPanelId[];
  hiddenThroughputSeries: ThroughputSeriesId[];
  isVisible: boolean;
  onClearExternalRealtimeMessages?: () => void;
  onConnectionError: () => void;
  onOpenCatalog: () => void;
  onOpenIterations: () => void;
  onOpenKeyType: (keyType: string) => void;
  onReady: () => void;
  onOpenWorker: (workerId: string) => void;
  onPanelShapeChange: (panelId: OverviewPanelId, shape: WorkComponentShape) => void;
  onPanelVisibilityChange: (panelId: OverviewPanelId, visible: boolean) => void;
  onRealtimePayloadCaptureEnabledChange?: (enabled: boolean) => void;
  onRealtimePayloadMaxMessagesChange?: (maxMessages: number) => void;
  onRealtimePayloadOpenChange?: (open: boolean) => void;
  onStateLoaded: (state: string) => void;
  onThroughputSeriesToggle: (seriesId: ThroughputSeriesId) => void;
  onViewIterationsByStatus: (statuses: WorkCompletionStatus[]) => void;
  onViewWorkersByState: (states: WorkerState[]) => void;
  overviewScope: OverviewScope | null;
  panelShapes: OverviewPanelShapeMap;
  realtimeFeatures?: string[] | null;
  realtimePayloadCaptureEnabled?: boolean;
  realtimePayloadMaxMessages?: number;
  realtimePayloadOpen?: boolean;
  refreshToken: number;
  renderToolbar: (state: {
    loading: boolean;
    realtimePayloadControl: ReactNode;
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
  const payloadOpen = realtimePayloadOpen ?? false;
  const payloadCaptureEnabled = realtimePayloadCaptureEnabled ?? true;
  const payloadMaxMessages = realtimePayloadMaxMessages ?? 100;
  const setPayloadOpen = onRealtimePayloadOpenChange ?? (() => undefined);
  const setPayloadCaptureEnabled = onRealtimePayloadCaptureEnabledChange ?? (() => undefined);
  const setPayloadMaxMessages = onRealtimePayloadMaxMessagesChange ?? (() => undefined);
  const lacksReadableWorkAccess =
    access !== undefined &&
    !access.canReadAllWork &&
    access.readableDefinitionCount === 0;
  const canUseRealtimeOverview = lacksReadableWorkAccess
    ? hasRealtimeFeature(realtimeFeatures, "system-view")
    : hasRealtimeFeature(realtimeFeatures, "work-views");
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
      overviewComponent("system"),
    ];

    if (shouldFetchPanel("workers")) {
      components.push(overviewComponent("workers", "workers", workersShape));
    }
    if (shouldFetchPanel("failedWorkers")) {
      components.push(overviewComponent("failedWorkers", "failedWorkers", failedWorkersShape));
    }
    if (shouldFetchPanel("iterations")) {
      components.push(overviewComponent("iterations", "iterations", iterationsShape));
    }
    if (shouldFetchPanel("failedIterations")) {
      components.push(overviewComponent("failedIterations", "failedIterations", failedIterationsShape));
    }
    if (shouldFetchPanel("completedIterations")) {
      components.push(overviewComponent("completedIterations", "completedIterations", completedIterationsShape));
    }
    if (shouldFetchPanel("throughput")) {
      components.push(overviewComponent(
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
      components: [overviewComponent("system")],
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
        overviewComponent("workers", "workers", workersShape),
        overviewComponent("failedWorkers", "failedWorkers", failedWorkersShape),
      ],
      scope: createOverviewComponentScope(overviewScope),
    }),
    [failedWorkersShape, overviewScope, workersShape]
  );
  const failedWorkersKey = `${connection.apiUrl}:${connection.systemName ?? ""}:${JSON.stringify(failedWorkersRefreshRequest)}:${refreshToken}`;
  const overview = useWorkablePostResource<WorkComponentQueryResult>(
    connection,
    isVisible ? "views/overview" : null,
    effectiveOverviewRequest,
    refreshToken
  );
  const realtimeOverview = useWorkableRealtimeView<WorkComponentQueryResult>(
    connection,
    "overview",
    effectiveOverviewRequest,
    isVisible && canUseRealtimeOverview && Boolean(connection.realtimeHubPath),
    payloadCaptureEnabled && payloadOpen,
    payloadMaxMessages,
    "overview"
  );
  const overviewData = realtimeOverview.data ?? overview.data;
  const realtimePayloadControl = (
    <Button
      className="h-9 w-full justify-start gap-2 text-muted-foreground"
      onClick={() => setPayloadOpen(true)}
      size="sm"
      variant="ghost"
    >
      <FileJson className="size-4" />
      Realtime payloads
      {realtimeOverview.enabled && (
        <span
          aria-hidden="true"
          className={`ml-auto size-2 rounded-full ${
            realtimeOverview.connectionState === "connected" ? "bg-emerald-400" : "bg-amber-400"
          }`}
        />
      )}
    </Button>
  );
  const realtimePayloadWindow = isVisible ? (
    <RealtimePayloadWindow
      captureEnabled={payloadCaptureEnabled}
      connectionState={realtimeOverview.connectionState}
      enabled={realtimeOverview.enabled}
      externalMessages={externalRealtimeMessages ?? []}
      hubUrl={realtimeOverview.hubUrl}
      maxMessages={payloadMaxMessages}
      messages={realtimeOverview.messages}
      onCaptureEnabledChange={setPayloadCaptureEnabled}
      onClearExternalMessages={onClearExternalRealtimeMessages ?? (() => undefined)}
      onClearMessages={realtimeOverview.clearMessages}
      onMaxMessagesChange={setPayloadMaxMessages}
      onOpenChange={setPayloadOpen}
      open={payloadOpen}
    />
  ) : null;
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
  const activeFailedWorkersSlice = failedWorkersSlice?.key === failedWorkersKey
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

    try {
      const failedWorkersOverview = await workableFetch<WorkComponentQueryResult>(
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

  return (
    <div className="space-y-4">
      <ErrorPanel
        errors={[
          overview.error,
          realtimeOverview.error,
          actionError,
          ...componentErrors,
        ]}
      />
      {realtimePayloadWindow}
      {renderToolbar({
        loading: overview.loading,
        realtimePayloadControl,
        refreshing: !!overview.refreshing || !!realtimeOverview.refreshing,
      })}
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
        <OverviewPanelShell
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
          onShapeChange={(shape) => onPanelShapeChange("workers", shape)}
          shape={workersShape}
          supportedShapes={overviewPanelShapeCapabilities.workers.supportedShapes}
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
                  tone={oldestQueuedAge.isWarning ? "text-amber-300" : undefined}
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
                  tone="text-red-300"
                  value={failedWorkerCount}
                />
              </div>
            </>
          )}
        </OverviewPanelShell>
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
        <OverviewPanelShell
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
          onShapeChange={(shape) => onPanelShapeChange("iterations", shape)}
          shape={iterationsShape}
          supportedShapes={overviewPanelShapeCapabilities.iterations.supportedShapes}
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
        </OverviewPanelShell>
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
    </div>
  );
}

function hasRealtimeFeature(features: string[] | null | undefined, feature: string) {
  return Array.isArray(features) && features.includes(feature);
}

function OverviewPanelShell({
  actions,
  centerActions = false,
  children,
  className,
  contentClassName,
  description,
  onClose,
  onShapeChange,
  shape,
  supportedShapes,
  title,
}: {
  actions?: ReactNode;
  centerActions?: boolean;
  children: ReactNode;
  className?: string;
  contentClassName?: string;
  description?: string;
  onClose?: () => void;
  onShapeChange?: (shape: WorkComponentShape) => void;
  shape?: WorkComponentShape;
  supportedShapes?: WorkComponentShape[];
  title: ReactNode;
}) {
  const hasPanelMenu = Boolean((shape && onShapeChange && supportedShapes) || onClose);

  return (
    <section className={`rounded-xl bg-card p-4 ring-1 ring-foreground/10 ${className ?? ""}`}>
      <div className={centerActions ? "grid grid-cols-[minmax(0,1fr)_auto_minmax(0,1fr)] items-center gap-3" : "flex items-center justify-between gap-3"}>
        <div className="flex min-w-0 items-center gap-2">
          <span className="min-w-0">
            <span className="flex min-w-0 flex-wrap items-center gap-2 font-semibold text-sm">
              {title}
            </span>
            {description && (
              <span className="mt-0.5 block text-muted-foreground text-xs">
                {description}
              </span>
            )}
          </span>
        </div>
        {centerActions ? (
          <>
            <div className="flex min-w-0 flex-wrap items-center justify-center gap-1.5">
              {actions}
            </div>
            <div className="flex min-w-0 items-center justify-end">
              {hasPanelMenu && (
                <OverviewPanelMenu
                  onClose={onClose}
                  onShapeChange={onShapeChange}
                  shape={shape}
                  supportedShapes={supportedShapes}
                />
              )}
            </div>
          </>
        ) : (
          <div className="flex shrink-0 flex-wrap items-center justify-end gap-1.5">
            {actions}
            {hasPanelMenu && (
              <OverviewPanelMenu
                onClose={onClose}
                onShapeChange={onShapeChange}
                shape={shape}
                supportedShapes={supportedShapes}
              />
            )}
          </div>
        )}
      </div>
      <div className={contentClassName ?? "mt-4 space-y-4"}>
        {children}
      </div>
    </section>
  );
}

function OverviewPanelMenu({
  onClose,
  onShapeChange,
  shape,
  supportedShapes,
}: {
  onClose?: () => void;
  onShapeChange?: (shape: WorkComponentShape) => void;
  shape?: WorkComponentShape;
  supportedShapes?: WorkComponentShape[];
}) {
  const canChangeShape = Boolean(shape && onShapeChange && supportedShapes);

  return (
    <DropdownMenu>
      <Tooltip delayDuration={500} disableHoverableContent>
        <TooltipTrigger asChild>
          <DropdownMenuTrigger asChild>
            <Button
              aria-label="Open panel options"
              className="size-7 text-muted-foreground"
              size="icon-sm"
              variant="ghost"
            >
              <MoreHorizontal className="size-4" />
            </Button>
          </DropdownMenuTrigger>
        </TooltipTrigger>
        <TooltipContent side="top" sideOffset={6}>
          Panel options
        </TooltipContent>
      </Tooltip>
      <DropdownMenuContent align="end" className="w-44">
        {canChangeShape && overviewShapeOptions.map((option) => {
          const Icon = option.icon;
          const supported = supportedShapes?.includes(option.shape) ?? false;
          const active = shape === option.shape;

          return (
            <DropdownMenuItem
              className={active ? "bg-accent/60" : undefined}
              disabled={!supported}
              key={option.shape}
              onSelect={() => {
                if (supported) {
                  onShapeChange?.(option.shape);
                }
              }}
            >
              <Icon className="size-4" />
              <span>{option.label}</span>
              {!supported && (
                <span className="ml-auto text-muted-foreground text-[11px]">
                  Unavailable
                </span>
              )}
            </DropdownMenuItem>
          );
        })}
        {onClose && (
          <DropdownMenuItem onSelect={onClose}>
            <X className="size-4" />
            Hide panel
          </DropdownMenuItem>
        )}
      </DropdownMenuContent>
    </DropdownMenu>
  );
}

function OverviewWorkerList({
  emptyText,
  loading,
  onClose,
  onShapeChange,
  onOpenWorker,
  onViewState,
  onWorkerAction,
  panelClassName,
  pendingActionWorkerId,
  shape,
  state,
  supportedShapes,
  title,
  workers,
}: {
  emptyText: string;
  loading: boolean;
  onClose: () => void;
  onShapeChange: (shape: WorkComponentShape) => void;
  onOpenWorker: (workerId: string) => void;
  onViewState: () => void;
  onWorkerAction: (worker: WorkerActionTarget, action: WorkAction) => Promise<void>;
  panelClassName?: string;
  pendingActionWorkerId: string | null;
  shape: WorkComponentShape;
  state: WorkerState;
  supportedShapes: WorkComponentShape[];
  title: string;
  workers: WorkOverviewFailedWorker[];
}) {
  const detailedWorkers = workers.filter(isDetailedWorkerOverviewItem);
  const [selectedWorkerId, setSelectedWorkerId] = useState<string | null>(null);

  return (
    <OverviewPanelShell
      actions={
        <button
          className="inline-flex cursor-pointer items-center gap-1 rounded-md border border-transparent px-2 py-1 text-muted-foreground text-sm transition-colors hover:border-primary/60 hover:bg-accent/40 hover:text-primary focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring focus-visible:ring-offset-2 focus-visible:ring-offset-background"
          onClick={onViewState}
          type="button"
        >
          View
          <ChevronRight className="size-4" />
        </button>
      }
      className={panelClassName}
      onClose={onClose}
      onShapeChange={onShapeChange}
      shape={shape}
      supportedShapes={supportedShapes}
      title={
        <>
          {title}
          <Badge className={`justify-center ${stateTone(state)}`} variant="outline">
            {state}
          </Badge>
        </>
      }
    >
      {shape === "detailed" ? (
        <WorkerTable
          emptyText={emptyText}
          hideState
          showIdentifiers
          showSubjectSummary
          loading={loading}
          onAction={onWorkerAction}
          onActionMenuOpen={(worker) => setSelectedWorkerId(worker.id.value)}
          onSelect={(worker) => onOpenWorker(worker.id.value)}
          pendingActionWorkerId={pendingActionWorkerId}
          selectedWorkerId={selectedWorkerId}
          workers={detailedWorkers}
        />
      ) : (
        <FailedWorkerTable
          emptyText={emptyText}
          loading={loading}
          onAction={onWorkerAction}
          onActionMenuOpen={(worker) => setSelectedWorkerId(worker.id.value)}
          onSelect={(worker) => onOpenWorker(worker.id.value)}
          pendingActionWorkerId={pendingActionWorkerId}
          selectedWorkerId={selectedWorkerId}
          workers={workers}
        />
      )}
    </OverviewPanelShell>
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
      <div className="flex h-8 items-center gap-2 overflow-x-auto">
        <Skeleton className="h-7 w-32 rounded-full" />
        <Skeleton className="h-7 w-30 rounded-full" />
        <Skeleton className="h-7 w-30 rounded-full" />
      </div>
    );
  }

  return (
    <div className="flex min-h-8 items-center gap-2 overflow-x-auto">
      <CompactWorkerStripItem
        label="Oldest queued"
        onClick={onOpenQueued}
        value={oldestQueuedText}
        valueClassName={oldestQueuedWarning ? "text-amber-300" : undefined}
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
        valueClassName="text-red-300"
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
      <div className="flex gap-2 overflow-x-auto pb-1">
        {overviewWorkerStates.map((state) => (
          <Skeleton className="h-8 min-w-28 flex-1 rounded-full" key={state} />
        ))}
      </div>
    );
  }

  return (
    <div className="flex gap-2 overflow-x-auto pb-1">
      {overviewWorkerStates.map((state) => (
        <button
          aria-label={`Open workers filtered by ${state}`}
          className={`inline-flex h-8 min-w-28 flex-1 cursor-pointer items-center justify-center gap-2 rounded-full border bg-muted/25 px-3 text-center ${subtleClickableTileClass} focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring focus-visible:ring-offset-2 focus-visible:ring-offset-background`}
          key={state}
          onClick={() => onSelectState(state)}
          type="button"
        >
          <Badge className={`justify-center ${stateTone(state)}`} variant="outline">
            {state}
          </Badge>
          <span className="font-mono text-sm leading-none">{counts[state] ?? 0}</span>
        </button>
      ))}
    </div>
  );
}

function IterationStatusStrip({
  counts,
  loading,
  onSelectStatus,
  statuses = iterationStatuses,
}: {
  counts: Partial<Record<WorkCompletionStatus, number>>;
  loading: boolean;
  onSelectStatus: (status: WorkCompletionStatus) => void;
  statuses?: WorkCompletionStatus[];
}) {
  if (loading) {
    return (
      <div className="flex gap-2 overflow-x-auto pb-1">
        {statuses.map((status) => (
          <Skeleton className="h-8 min-w-28 flex-1 rounded-full" key={status} />
        ))}
      </div>
    );
  }

  return (
    <div className="flex gap-2 overflow-x-auto pb-1">
      {statuses.map((status) => (
        <button
          aria-label={`Open iterations filtered by ${status}`}
          className={`inline-flex h-8 min-w-28 flex-1 cursor-pointer items-center justify-center gap-2 rounded-full border bg-muted/25 px-3 text-center ${subtleClickableTileClass} focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring focus-visible:ring-offset-2 focus-visible:ring-offset-background`}
          key={status}
          onClick={() => onSelectStatus(status)}
          type="button"
        >
          <Badge className={`justify-center ${completionTone(status)}`} variant="outline">
            {status}
          </Badge>
          <span className={`font-mono text-sm leading-none ${status === "Failed" ? "text-red-300" : ""}`}>
            {counts[status] ?? 0}
          </span>
        </button>
      ))}
    </div>
  );
}

function CompactIterationStrip({
  counts,
  loading,
  onSelectStatus,
  statuses,
}: {
  counts: Partial<Record<WorkCompletionStatus, number>>;
  loading: boolean;
  onSelectStatus: (status: WorkCompletionStatus) => void;
  statuses: WorkCompletionStatus[];
}) {
  if (loading) {
    return (
      <div className="flex h-8 items-center gap-2 overflow-x-auto">
        {statuses.map((status) => (
          <Skeleton className="h-7 w-30 rounded-full" key={status} />
        ))}
      </div>
    );
  }

  return (
    <div className="flex min-h-8 items-center gap-2 overflow-x-auto">
      {statuses.map((status) => (
        <CompactWorkerStripItem
          key={status}
          label={status}
          onClick={() => onSelectStatus(status)}
          value={counts[status] ?? 0}
          valueClassName={status === "Failed" ? "text-red-300" : undefined}
        />
      ))}
    </div>
  );
}

function TopKeyTypePanel({
  keys,
  loading,
  onShowMore,
  onSelectKeyType,
}: {
  keys: WorkIterationKeyTypeFacet[];
  loading: boolean;
  onShowMore: () => void;
  onSelectKeyType: (keyType: string) => void;
}) {
  const [visibleCount, setVisibleCount] = useState(keys.length);
  const measureRef = useRef<HTMLDivElement>(null);
  const visibleKeys = keys.slice(0, visibleCount);
  const hiddenKeys = keys.slice(visibleCount);

  useEffect(() => {
    if (loading) {
      return;
    }

    const measure = () => {
      const root = measureRef.current;
      if (!root) {
        return;
      }

      const width = root.clientWidth;
      const pillWidths = Array.from(
        root.querySelectorAll<HTMLElement>("[data-key-type-pill]")
      ).map((element) => element.offsetWidth);
      const moreWidth = root
        .querySelector<HTMLElement>("[data-key-type-more]")
        ?.offsetWidth ?? 0;
      const gap = 8;

      let used = 0;
      let nextVisibleCount = pillWidths.length;

      for (let index = 0; index < pillWidths.length; index += 1) {
        const remaining = pillWidths.length - index - 1;
        const itemWidth = pillWidths[index] + (index > 0 ? gap : 0);
        const reserveMoreWidth = remaining > 0
          ? moreWidth + (index >= 0 ? gap : 0)
          : 0;

        if (used + itemWidth + reserveMoreWidth > width) {
          nextVisibleCount = index;
          break;
        }

        used += itemWidth;
      }

      setVisibleCount(Math.max(0, nextVisibleCount));
    };

    measure();
    const observer = new ResizeObserver(measure);
    if (measureRef.current) {
      observer.observe(measureRef.current);
    }

    return () => observer.disconnect();
  }, [keys, loading]);

  if (!loading && keys.length === 0) {
    return null;
  }

  return (
    <section className="space-y-2">
      {loading ? (
        <div className="flex flex-wrap gap-2">
          {Array.from({ length: 6 }).map((_, index) => (
            <Skeleton className="h-8 w-32 shrink-0 rounded-full" key={index} />
          ))}
        </div>
      ) : (
        <div className="relative">
          <div
            aria-hidden="true"
            className="pointer-events-none invisible absolute inset-x-0 top-0 flex h-8 gap-2 overflow-hidden"
            ref={measureRef}
          >
            {keys.map((key) => (
              <span
                className="inline-flex h-8 shrink-0 items-center rounded-full border px-3 font-mono text-sm"
                data-key-type-pill
                key={key.type}
              >
                {key.type}
              </span>
            ))}
            <span className="inline-flex h-8 shrink-0 items-center rounded-full border px-3 text-sm" data-key-type-more>
              +{Math.max(1, keys.length)} more
            </span>
          </div>
          <div className="flex gap-2 overflow-hidden">
          {visibleKeys.map((key) => (
            <Tooltip delayDuration={500} disableHoverableContent key={key.type}>
              <TooltipTrigger asChild>
                <button
                  aria-label={`Open iterations for key type ${key.type}`}
                  className={`inline-flex h-8 shrink-0 cursor-pointer items-center rounded-full border bg-muted/25 px-3 text-left ${subtleClickableTileClass} focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring focus-visible:ring-offset-2 focus-visible:ring-offset-background`}
                  onClick={() => onSelectKeyType(key.type)}
                  type="button"
                >
                  <span className="truncate font-mono text-sm">{key.type}</span>
                </button>
              </TooltipTrigger>
              <TooltipContent
                className="max-w-56 whitespace-normal text-left"
                side="top"
                sideOffset={6}
              >
                <KeyTypeTooltipContent keyType={key} />
              </TooltipContent>
            </Tooltip>
          ))}
          {hiddenKeys.length > 0 && (
            <Tooltip delayDuration={500} disableHoverableContent>
              <TooltipTrigger asChild>
                <button
                  aria-label={`Open iterations to view ${hiddenKeys.length} more relationship types`}
                  className={`inline-flex h-8 shrink-0 cursor-pointer items-center rounded-full border bg-muted/25 px-3 text-sm ${subtleClickableTileClass} focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring focus-visible:ring-offset-2 focus-visible:ring-offset-background`}
                  onClick={onShowMore}
                  type="button"
                >
                  +{hiddenKeys.length} more
                </button>
              </TooltipTrigger>
              <TooltipContent
                className="max-w-64 whitespace-normal text-left"
                side="top"
                sideOffset={6}
              >
                <div className="space-y-1">
                  <div className="font-medium">More relationship types</div>
                  <div className="text-muted-foreground">
                    {hiddenKeys.map((key) => key.type).join(", ")}
                  </div>
                </div>
              </TooltipContent>
            </Tooltip>
          )}
          </div>
        </div>
      )}
    </section>
  );
}

function KeyTypeTooltipContent({ keyType }: { keyType: WorkIterationKeyTypeFacet }) {
  return (
    <div className="space-y-1">
      <div className="font-medium">Open iterations filtered to this key type.</div>
      <div>{formatIterationCount(keyType.iterationCount)} with this relationship type.</div>
      <div className="grid grid-cols-[auto_auto] gap-x-3 gap-y-0.5 text-muted-foreground">
        <span>Subjects</span>
        <span className="text-right font-mono">{keyType.iterationCountByKind.Subject ?? 0}</span>
        <span>Concurrency keys</span>
        <span className="text-right font-mono">{keyType.iterationCountByKind.ConcurrencyKey ?? 0}</span>
        <span>Identifiers</span>
        <span className="text-right font-mono">{keyType.iterationCountByKind.Identifier ?? 0}</span>
      </div>
    </div>
  );
}

function OverviewIterationList({
  emptyText,
  loading,
  onClose,
  onShapeChange,
  onOpenWorker,
  onViewState,
  panelClassName,
  shape,
  status,
  supportedShapes,
  title,
  iterations,
}: {
  emptyText: string;
  loading: boolean;
  onClose: () => void;
  onShapeChange: (shape: WorkComponentShape) => void;
  onOpenWorker: (workerId: string) => void;
  onViewState: () => void;
  panelClassName?: string;
  shape: WorkComponentShape;
  status: WorkCompletionStatus;
  supportedShapes: WorkComponentShape[];
  title: string;
  iterations: WorkOverviewIteration[];
}) {
  return (
    <OverviewPanelShell
      actions={
        <button
          className="inline-flex cursor-pointer items-center gap-1 rounded-md border border-transparent px-2 py-1 text-muted-foreground text-sm transition-colors hover:border-primary/60 hover:bg-accent/40 hover:text-primary focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring focus-visible:ring-offset-2 focus-visible:ring-offset-background"
          onClick={onViewState}
          type="button"
        >
          View
          <ChevronRight className="size-4" />
        </button>
      }
      className={panelClassName}
      onClose={onClose}
      onShapeChange={onShapeChange}
      shape={shape}
      supportedShapes={supportedShapes}
      title={
        <>
          {title}
          <Badge className={`justify-center ${completionTone(status)}`} variant="outline">
            {status}
          </Badge>
        </>
      }
    >
      {loading ? (
        <StackedSkeleton count={4} />
      ) : iterations.length === 0 ? (
        <div className="rounded-lg border border-dashed p-6 text-center text-muted-foreground text-sm">
          {emptyText}
        </div>
      ) : shape === "detailed" ? (
        <OverviewIterationTable
          iterations={iterations}
          timestampLabel={status}
          onOpenWorker={onOpenWorker}
        />
      ) : (
        <OverviewIterationTable
          compact
          iterations={iterations}
          timestampLabel={status}
          onOpenWorker={onOpenWorker}
        />
      )}
    </OverviewPanelShell>
  );
}

function OverviewIterationTable({
  compact = false,
  iterations,
  onOpenWorker,
  timestampLabel,
}: {
  compact?: boolean;
  iterations: WorkOverviewIteration[];
  onOpenWorker: (workerId: string) => void;
  timestampLabel: string;
}) {
  const relativeNow = useLiveRelativeTimeNow();

  return (
    <div className="overflow-hidden rounded-lg border">
      <Table>
        <TableHeader>
          <TableRow>
            <TableHead>Definition</TableHead>
            {!compact && <TableHead>Worker state</TableHead>}
            {!compact && <TableHead>Subject id</TableHead>}
            {!compact && <TableHead>Identifiers</TableHead>}
            <TableHead>{timestampLabel}</TableHead>
            <TableHead>Duration</TableHead>
          </TableRow>
        </TableHeader>
        <TableBody>
          {iterations.map((iteration) => (
            <TableRow
              className="cursor-pointer"
              key={`${iteration.workerId.value}:${iteration.sequence}`}
              onClick={() => onOpenWorker(iteration.workerId.value)}
            >
              <TableCell>
                <div className="font-mono text-xs">{iteration.definitionName}</div>
                {!compact && (
                  <div className="font-mono text-muted-foreground text-xs">
                    {iteration.workerId.value} / #{iteration.sequence}
                  </div>
                )}
              </TableCell>
              {!compact && (
                <TableCell>
                  {"workerState" in iteration ? (
                    <Badge className={stateTone(iteration.workerState)} variant="outline">
                      {iteration.workerState}
                    </Badge>
                  ) : (
                    <span className="text-muted-foreground">-</span>
                  )}
                </TableCell>
              )}
              {!compact && (
                <TableCell className="max-w-72 font-mono text-muted-foreground text-xs">
                  <TypedValueSummary values={"subjectId" in iteration && iteration.subjectId ? [iteration.subjectId] : []} />
                </TableCell>
              )}
              {!compact && (
                <TableCell className="max-w-72 font-mono text-muted-foreground text-xs">
                  <IdentifierSummary identifiers={"identifiers" in iteration ? iteration.identifiers : []} />
                </TableCell>
              )}
              <TableCell className="text-muted-foreground text-xs">
                {formatRelativeTime(iteration.completedAt, relativeNow)}
              </TableCell>
              <TableCell>
                <DurationValue
                  className="font-mono text-xs"
                  duration={formatExecutionDuration(iteration.executionDuration)}
                  muted
                />
              </TableCell>
            </TableRow>
          ))}
        </TableBody>
      </Table>
    </div>
  );
}

function DurationValue({
  className = "text-xs",
  duration,
  muted = false,
}: {
  className?: string;
  duration: DurationDisplay;
  muted?: boolean;
}) {
  const tone = duration.isWarning
    ? "text-amber-300"
    : muted
      ? "text-muted-foreground"
      : "";

  return (
    <span className={`${className} ${tone}`}>
      {duration.text}
    </span>
  );
}

function FailedWorkerTable({
  emptyText,
  loading,
  onAction,
  onActionMenuOpen,
  onSelect,
  pendingActionWorkerId,
  selectedWorkerId,
  workers,
}: {
  emptyText: string;
  loading: boolean;
  onAction: (worker: WorkerActionTarget, action: WorkAction) => Promise<void>;
  onActionMenuOpen: (worker: WorkOverviewFailedWorker) => void;
  onSelect: (worker: WorkOverviewFailedWorker) => void;
  pendingActionWorkerId: string | null;
  selectedWorkerId: string | null;
  workers: WorkOverviewFailedWorker[];
}) {
  const relativeNow = useLiveRelativeTimeNow();

  if (loading) {
    return <StackedSkeleton count={5} />;
  }

  if (workers.length === 0) {
    return (
      <div className="rounded-lg border border-dashed p-8 text-center text-muted-foreground text-sm">
        {emptyText}
      </div>
    );
  }

  return (
    <div className="overflow-hidden rounded-lg border">
      <Table>
        <TableHeader>
          <TableRow>
            <TableHead>Definition</TableHead>
            <TableHead>Updated</TableHead>
            <TableHead>Duration</TableHead>
            <TableHead className="w-12" />
          </TableRow>
        </TableHeader>
        <TableBody>
          {workers.map((worker) => {
            const isSelected = selectedWorkerId === worker.id.value;

            return (
              <TableRow
                className={`cursor-pointer ${
                  isSelected ? "bg-sky-500/10 ring-1 ring-inset ring-sky-500/40" : ""
                }`}
                key={worker.id.value}
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
              >
                <TableCell>
                  <div className="font-mono text-xs">{worker.definitionName}</div>
                </TableCell>
                <TableCell className="text-muted-foreground text-xs">
                  {formatRelativeTime(worker.updatedAt, relativeNow)}
                </TableCell>
                <TableCell>
                  <DurationValue
                    className="font-mono text-xs"
                    duration={formatFailedWorkerDuration(worker)}
                    muted
                  />
                </TableCell>
                <TableCell data-worker-row-action>
                  <WorkerRowActionMenu
                    disabled={pendingActionWorkerId === worker.id.value}
                    onAction={(action) => onAction(toFailedWorkerActionTarget(worker), action)}
                    onOpen={() => onActionMenuOpen(worker)}
                    worker={toFailedWorkerActionTarget(worker)}
                  />
                </TableCell>
              </TableRow>
            );
          })}
        </TableBody>
      </Table>
    </div>
  );
}

function WorkerTable({
  emptyText,
  hideState = false,
  loading,
  onAction,
  onActionMenuOpen,
  onSelect,
  pendingActionWorkerId,
  selectedWorkerId,
  showIdentifiers = false,
  showSubjectSummary = false,
  workers,
}: {
  emptyText: string;
  hideState?: boolean;
  loading: boolean;
  onAction: (worker: WorkerActionTarget, action: WorkAction) => Promise<void>;
  onActionMenuOpen: (worker: WorkOverviewFailedWorkerDetailed | WorkerOverviewItem) => void;
  onSelect: (worker: WorkOverviewFailedWorkerDetailed | WorkerOverviewItem) => void;
  pendingActionWorkerId: string | null;
  selectedWorkerId: string | null;
  showIdentifiers?: boolean;
  showSubjectSummary?: boolean;
  workers: Array<WorkOverviewFailedWorkerDetailed | WorkerOverviewItem>;
}) {
  const relativeNow = useLiveRelativeTimeNow();

  if (loading) {
    return <StackedSkeleton count={5} />;
  }

  if (workers.length === 0) {
    return (
      <div className="rounded-lg border border-dashed p-8 text-center text-muted-foreground text-sm">
        {emptyText}
      </div>
    );
  }

  return (
    <div className="overflow-hidden rounded-lg border">
      <Table>
        <TableHeader>
          <TableRow>
            <TableHead>Definition</TableHead>
            {!hideState && <TableHead>State</TableHead>}
            {showSubjectSummary && <TableHead>Subject id</TableHead>}
            {showIdentifiers && <TableHead>Identifiers</TableHead>}
            <TableHead>Updated</TableHead>
            <TableHead>Duration</TableHead>
            <TableHead className="w-12" />
          </TableRow>
        </TableHeader>
        <TableBody>
          {workers.map((worker) => {
            const isSelected = selectedWorkerId === worker.id.value;

            return (
              <TableRow
                className={`cursor-pointer ${
                  isSelected ? "bg-sky-500/10 ring-1 ring-inset ring-sky-500/40" : ""
                }`}
                key={worker.id.value}
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
              >
                <TableCell>
                  <div className="font-mono text-xs">{worker.definitionName}</div>
                  <div className="font-mono text-muted-foreground text-xs">{worker.id.value}</div>
                </TableCell>
                {!hideState && (
                  <TableCell>
                    <Badge className={stateTone(worker.state)} variant="outline">
                      {worker.state}
                    </Badge>
                  </TableCell>
                )}
                {showSubjectSummary && (
                  <TableCell className="max-w-72 font-mono text-muted-foreground text-xs">
                    <TypedValueSummary values={worker.subjectId ? [worker.subjectId] : []} />
                  </TableCell>
                )}
                {showIdentifiers && (
                  <TableCell className="max-w-72 font-mono text-muted-foreground text-xs">
                    <IdentifierSummary identifiers={worker.identifiers} />
                  </TableCell>
                )}
                <TableCell className="text-muted-foreground text-xs">
                  {formatRelativeTime(worker.updatedAt, relativeNow)}
                </TableCell>
                <TableCell>
                  <DurationValue
                    className="font-mono text-xs"
                    duration={formatFailedWorkerDuration(worker)}
                    muted
                  />
                </TableCell>
                <TableCell data-worker-row-action>
                  <WorkerRowActionMenu
                    disabled={pendingActionWorkerId === worker.id.value}
                    onAction={(action) => onAction(worker, action)}
                    onOpen={() => onActionMenuOpen(worker)}
                    worker={worker}
                  />
                </TableCell>
              </TableRow>
            );
          })}
        </TableBody>
      </Table>
    </div>
  );
}

function WorkerRowActionMenu({
  disabled,
  onAction,
  onOpen,
  worker,
}: {
  disabled: boolean;
  onAction: (action: WorkAction) => Promise<void> | void;
  onOpen: () => void;
  worker: WorkerActionTarget;
}) {
  const actions = getWorkerRowActions(worker);
  if (actions.length === 0) {
    return null;
  }

  return (
    <DropdownMenu>
      <DropdownMenuTrigger asChild>
        <button
          aria-label={`Open actions for ${worker.definitionName}`}
          className="flex size-7 cursor-pointer items-center justify-center rounded-md text-muted-foreground transition-colors hover:bg-accent hover:text-foreground focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring focus-visible:ring-offset-2 focus-visible:ring-offset-background disabled:cursor-wait disabled:opacity-60"
          data-worker-row-action
          disabled={disabled}
          onClick={(event) => event.stopPropagation()}
          onPointerDown={(event) => {
            event.stopPropagation();
            onOpen();
          }}
          type="button"
        >
          {disabled ? (
            <Loader2 className="size-4 animate-spin" />
          ) : (
            <MoreHorizontal className="size-4" />
          )}
        </button>
      </DropdownMenuTrigger>
      <DropdownMenuContent
        align="end"
        onClick={(event) => event.stopPropagation()}
        onPointerDown={(event) => event.stopPropagation()}
      >
        {actions.map((action) => (
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
            {action === "Start" ? (
              <Play className="size-4" />
            ) : (
              <Ban className="size-4" />
            )}
            {action}
          </DropdownMenuItem>
        ))}
      </DropdownMenuContent>
    </DropdownMenu>
  );
}

function getWorkerRowActions(worker: WorkerActionTarget): WorkAction[] {
  if (worker.state === "Failed" || worker.state === "Paused" || worker.state === "Queued") {
    return ["Start", "Cancel"];
  }

  if (worker.state === "Running" || worker.state === "Waiting" || worker.state === "Retrying") {
    return ["Cancel"];
  }

  return [];
}

function toFailedWorkerActionTarget(worker: WorkOverviewFailedWorker): WorkerActionTarget {
  return {
    definitionName: worker.definitionName,
    id: worker.id,
    revision: worker.revision,
    state: "Failed",
  };
}

function isDetailedWorkerOverviewItem(
  worker: WorkOverviewFailedWorker
): worker is WorkOverviewFailedWorkerDetailed | WorkerOverviewItem {
  return "subjectId" in worker || "identifiers" in worker;
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

function ThroughputChartPanel({
  hiddenSeries,
  loading,
  mode,
  onClose,
  onModeChange,
  onSeriesToggle,
  onShapeChange,
  onWindowChange,
  shape,
  supportedShapes,
  throughput,
  windowSeconds,
}: {
  hiddenSeries: ThroughputSeriesId[];
  loading: boolean;
  mode: ThroughputMode;
  onClose: () => void;
  onModeChange: (mode: ThroughputMode) => void;
  onSeriesToggle: (seriesId: ThroughputSeriesId) => void;
  onShapeChange: (shape: WorkComponentShape) => void;
  onWindowChange: (seconds: number) => void;
  shape: WorkComponentShape;
  supportedShapes: WorkComponentShape[];
  throughput?: WorkSystemThroughput;
  windowSeconds: number;
}) {
  const compact = shape === "compact";
  const chartLabel = mode === "execution" ? "Execution time" : "Throughput";
  const chartDescription = mode === "execution"
    ? "Execution timing for completed iterations, scoped to the current overview filter."
    : "Started, completed, failed, and canceled iteration rates, scoped to the current overview filter.";
  return (
    <OverviewPanelShell
      actions={compact ? (
        <CompactThroughputStrip
          loading={loading}
          throughput={throughput}
          windowSeconds={compactThroughputWindow.seconds}
        />
      ) : (
        <div className="flex flex-wrap items-center gap-2">
          <div className="flex rounded-lg bg-muted/40 p-0.5">
            {throughputWindows.map((window) => (
              <Button
                className="h-7 px-2 text-xs"
                key={window.seconds}
                onClick={() => onWindowChange(window.seconds)}
                size="sm"
                variant={windowSeconds === window.seconds ? "secondary" : "ghost"}
              >
                {window.label}
              </Button>
            ))}
          </div>
        </div>
      )}
      className={compact ? "w-full" : undefined}
      centerActions={compact}
      contentClassName={compact ? "hidden" : undefined}
      description={compact ? undefined : chartDescription}
      onClose={onClose}
      onShapeChange={onShapeChange}
      shape={shape}
      supportedShapes={supportedShapes}
      title={compact ? "Throughput & Execution" : chartLabel}
    >
      {!compact && (
      <Tabs value={mode} onValueChange={(value) => onModeChange(value as ThroughputMode)}>
        <TabsList className="h-8">
          <TabsTrigger className="text-xs" value="completion">Throughput</TabsTrigger>
          <TabsTrigger className="text-xs" value="execution">Execution</TabsTrigger>
        </TabsList>
        <TabsContent className="mt-3" value={mode}>
          {loading ? (
            <Skeleton className="h-52 w-full" />
          ) : (
            <ThroughputAreaChart
              hiddenSeries={hiddenSeries}
              key={mode}
              mode={mode}
              onSeriesToggle={onSeriesToggle}
              throughput={throughput}
              windowSeconds={windowSeconds}
            />
          )}
        </TabsContent>
      </Tabs>
      )}
    </OverviewPanelShell>
  );
}

function CompactThroughputStrip({
  loading,
  throughput,
  windowSeconds,
}: {
  loading: boolean;
  throughput?: WorkSystemThroughput;
  windowSeconds: number;
}) {
  const throughputMetrics = createThroughputMetrics(
    "completion",
    throughput,
    windowSeconds
  ).filter((metric) =>
    metric.id !== "window-average"
  );
  const executionMetrics = createThroughputMetrics(
    "execution",
    throughput,
    windowSeconds
  ).filter((metric) =>
    [
      "execution-average",
      "execution-p95",
      "execution-p99",
      "execution-slowest",
    ].includes(metric.id)
  );
  const metrics = [...throughputMetrics, ...executionMetrics];

  if (loading) {
    return (
      <div className="flex h-8 max-w-full flex-wrap items-center justify-center gap-2 overflow-hidden">
        {Array.from({ length: 10 }).map((_, index) => (
          <Skeleton className="h-7 w-20 shrink-0 rounded-full" key={index} />
        ))}
      </div>
    );
  }

  return (
    <div className="flex min-h-8 max-w-full flex-wrap items-center justify-center gap-1.5 overflow-hidden">
      {metrics.map((metric) => (
        <ThroughputMetricPill key={metric.id} metric={metric} />
      ))}
    </div>
  );
}

function ThroughputAreaChart({
  hiddenSeries,
  mode,
  onSeriesToggle,
  showChart = true,
  showLegend = true,
  throughput,
  windowSeconds,
}: {
  hiddenSeries: ThroughputSeriesId[];
  mode: ThroughputMode;
  onSeriesToggle: (seriesId: ThroughputSeriesId) => void;
  showChart?: boolean;
  showLegend?: boolean;
  throughput?: WorkSystemThroughput;
  windowSeconds: number;
}) {
  const buckets = getThroughputBuckets(throughput);
  const bucketSeconds = throughput?.bucketSeconds ??
    throughputWindows.find((window) => window.seconds === windowSeconds)?.bucketSeconds ??
    1;
  const allSeries = createThroughputSeries(mode, buckets, bucketSeconds);
  const hiddenSeriesSet = new Set(hiddenSeries);
  const visibleSeries = mode === "completion"
    ? allSeries.filter((item) => !isThroughputSeriesId(item.id) || !hiddenSeriesSet.has(item.id))
    : allSeries;
  const series = visibleSeries.length > 0 ? visibleSeries : allSeries;
  const maxValue = getNiceChartMax(Math.max(0, ...series.flatMap((item) => item.values)), mode);
  const yTicks = createYAxisTicks(maxValue);
  const xTicks = createTimeAxisTicks(throughput, buckets);
  const metrics = createThroughputMetrics(
    mode,
    throughput,
    windowSeconds
  );
  const lineSeries = mode === "completion" && series.length > 1
    ? [...series.slice(1), series[0]]
    : series;

  return (
    <div className="space-y-3">
      <div className={`flex flex-wrap items-center gap-3 ${showLegend ? "justify-between" : "justify-end"}`}>
        {showLegend && (
          <div className="flex flex-wrap items-center gap-3">
            {allSeries.map((item) => {
              const seriesId = isThroughputSeriesId(item.id) ? item.id : null;
              return (
                <ThroughputLegendItem
                  hidden={seriesId ? hiddenSeriesSet.has(seriesId) : false}
                  item={item}
                  key={item.id}
                  onToggle={
                    mode === "completion" && seriesId
                      ? () => onSeriesToggle(seriesId)
                      : undefined
                  }
                />
              );
            })}
          </div>
        )}
        <div className="flex flex-wrap items-center justify-end gap-1.5">
          {metrics.map((metric) => {
            const seriesId = isThroughputSeriesId(metric.id) ? metric.id : null;
            return (
              <ThroughputMetricPill
                hidden={seriesId ? hiddenSeriesSet.has(seriesId) : false}
                key={metric.id}
                metric={metric}
                onClick={
                  mode === "completion" && seriesId
                    ? () => onSeriesToggle(seriesId)
                    : undefined
                }
              />
            );
          })}
        </div>
      </div>
      {showChart && (
        <div>
          <div className="relative grid h-56 grid-cols-[3.25rem_1fr] overflow-hidden rounded-lg border bg-background/40">
            <div className="flex flex-col justify-between border-r border-border/70 px-2 py-3 text-right font-mono text-[10px] text-muted-foreground">
              {yTicks.map((tick) => (
                <span key={tick}>{formatThroughputAxisValue(mode, tick)}</span>
              ))}
            </div>
            <div className="relative min-w-0">
              <svg
                aria-label={mode === "execution" ? "Execution time chart" : "Throughput chart"}
                className="h-full w-full"
                preserveAspectRatio="none"
                role="img"
                viewBox="0 0 1000 220"
              >
                <defs>
                  {series.map((item) => (
                    <linearGradient id={item.gradientId} key={item.gradientId} x1="0" x2="0" y1="0" y2="1">
                      <stop offset="5%" stopColor={item.color} stopOpacity="0.42" />
                      <stop offset="95%" stopColor={item.color} stopOpacity="0.04" />
                    </linearGradient>
                  ))}
                </defs>
                {[0, 1, 2, 3].map((line) => (
                  <line
                    className="stroke-border"
                    key={line}
                    strokeDasharray={line === 3 ? undefined : "4 8"}
                    strokeWidth="1"
                    x1="0"
                    x2="1000"
                    y1={20 + line * 55}
                    y2={20 + line * 55}
                  />
                ))}
                {series.map((item) => (
                  <path d={createAreaPath(item.values, maxValue)} fill={`url(#${item.gradientId})`} key={`${item.label}-area`} />
                ))}
                {lineSeries.map((item) => (
                  <path
                    d={createLinePath(item.values, maxValue)}
                    fill="none"
                    key={`${item.label}-line`}
                    stroke={item.color}
                    strokeDasharray={item.strokeDasharray}
                    strokeLinecap="round"
                    strokeLinejoin="round"
                    strokeWidth={item.strokeWidth ?? "2.5"}
                    vectorEffect="non-scaling-stroke"
                  />
                ))}
              </svg>
            </div>
            {buckets.length === 0 && (
              <div className="absolute inset-0 grid place-items-center bg-background/70 text-muted-foreground text-sm">
                Waiting for throughput data.
              </div>
            )}
          </div>
          {xTicks.length > 0 && (
            <div className="ml-[3.25rem] mt-1 grid grid-cols-5 gap-2 px-1 font-mono text-[10px] text-foreground/75">
              {xTicks.map((tick, index) => (
                <span
                  className={
                    index === 0
                      ? "text-left"
                      : index === xTicks.length - 1
                        ? "text-right"
                        : "text-center"
                  }
                  key={`${tick.position}-${tick.label}`}
                >
                  {tick.label}
                </span>
              ))}
            </div>
          )}
        </div>
      )}
    </div>
  );
}

function ThroughputLegendItem({
  hidden,
  item,
  onToggle,
}: {
  hidden: boolean;
  item: ThroughputSeries;
  onToggle?: () => void;
}) {
  const content = (
    <>
      <span className={`size-2 rounded-full ${item.legendClass}`} />
      <span>{item.label}</span>
    </>
  );

  if (!onToggle) {
    return (
      <div className="flex items-center gap-1.5 text-muted-foreground text-xs">
        {content}
      </div>
    );
  }

  return (
    <button
      aria-pressed={!hidden}
      className={`flex cursor-pointer items-center gap-1.5 rounded-md border px-1.5 py-0.5 text-xs transition-colors focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring focus-visible:ring-offset-2 focus-visible:ring-offset-background ${
        hidden
          ? "border-transparent text-muted-foreground/45 hover:border-foreground/10 hover:bg-muted/20 hover:text-muted-foreground"
          : "border-foreground/10 bg-muted/20 text-foreground shadow-sm hover:border-primary/50 hover:bg-accent/50 hover:text-primary"
      }`}
      onClick={onToggle}
      type="button"
    >
      {content}
    </button>
  );
}

function ThroughputMetricPill({
  hidden = false,
  metric,
  onClick,
}: {
  hidden?: boolean;
  metric: ThroughputMetric;
  onClick?: () => void;
}) {
  const Icon = metric.icon;
  const content = (
    <>
      {metric.pulseClass && <span className={`size-2 rounded-full ${metric.pulseClass}`} />}
      {Icon && <Icon className={`size-3.5 ${metric.iconClass ?? "text-muted-foreground"}`} />}
      {metric.label && <span className="text-muted-foreground text-[11px]">{metric.label}</span>}
      <span className={`font-mono font-medium text-xs ${metric.valueClass ?? ""}`}>{metric.value}</span>
    </>
  );
  const className = `flex items-center justify-center gap-1.5 whitespace-nowrap rounded-full border px-2.5 py-1 shadow-sm transition-all ${
    onClick
      ? hidden
        ? "border-foreground/10 bg-background/40 opacity-50"
        : "border-primary/35 bg-accent/35 ring-1 ring-primary/20"
      : "border-foreground/10 bg-background/70"
  } ${metric.widthClass ?? "min-w-24"}`;

  return (
    <Tooltip delayDuration={500} disableHoverableContent>
      <TooltipTrigger asChild>
        {onClick ? (
          <button
            aria-pressed={!hidden}
            className={`${className} cursor-pointer hover:border-primary/70 hover:bg-accent/60 hover:ring-primary/35 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring focus-visible:ring-offset-2 focus-visible:ring-offset-background`}
            onClick={onClick}
            type="button"
          >
            {content}
          </button>
        ) : (
          <div className={className} tabIndex={0}>
            {content}
          </div>
        )}
      </TooltipTrigger>
      <TooltipContent className="max-w-64 whitespace-normal text-left" side="top" sideOffset={6}>
        {metric.description}
      </TooltipContent>
    </Tooltip>
  );
}

function getThroughputBuckets(throughput?: WorkSystemThroughput) {
  if (!throughput) {
    return [];
  }

  const buckets = throughput.buckets ?? [];
  const bucketSeconds = throughput.bucketSeconds;
  const toTime = parseChartTimestamp(throughput.to);
  if (!bucketSeconds || toTime === null) {
    return buckets;
  }

  const normalizedBucketSeconds = Math.max(1, bucketSeconds);
  const windowSeconds = Math.max(normalizedBucketSeconds, throughput.windowSeconds);
  const bucketCount = Math.max(1, Math.ceil(windowSeconds / normalizedBucketSeconds));
  const toSecond = Math.floor(toTime / 1000);
  const latestBucketSecond = toSecond - normalizedBucketSeconds + 1;
  const firstBucketSecond = latestBucketSecond - (bucketCount - 1) * normalizedBucketSeconds;
  const bucketsBySecond = new Map<number, WorkThroughputBucket>();

  for (const bucket of buckets) {
    const bucketTime = parseChartTimestamp(bucket.at);
    if (bucketTime === null) {
      continue;
    }

    bucketsBySecond.set(Math.floor(bucketTime / 1000), bucket);
  }

  return Array.from({ length: bucketCount }, (_, index) => {
    const bucketSecond = firstBucketSecond + index * normalizedBucketSeconds;
    return bucketsBySecond.get(bucketSecond) ?? createEmptyThroughputBucket(bucketSecond);
  });
}

function createEmptyThroughputBucket(second: number): WorkThroughputBucket {
  return {
    at: new Date(second * 1000).toISOString(),
    averageExecutionMilliseconds: 0,
    canceled: 0,
    completed: 0,
    failed: 0,
    started: 0,
  };
}

function createThroughputSeries(
  mode: ThroughputMode,
  buckets: WorkThroughputBucket[],
  bucketSeconds: number
): ThroughputSeries[] {
  const normalizedBucketSeconds = Math.max(1, bucketSeconds);
  if (mode === "execution") {
    return [
      {
        color: "#a78bfa",
        gradientId: "execution-throughput",
        id: "execution-average",
        label: "Avg successful execution ms",
        legendClass: "bg-violet-400",
        values: buckets.map((bucket) => Math.round(bucket.averageExecutionMilliseconds)),
      },
    ];
  }

  return [
      {
        color: "#38bdf8",
        gradientId: "started-throughput",
        id: "started",
        label: "Started",
        legendClass: "bg-sky-400",
        strokeDasharray: "6 5",
        strokeWidth: "3",
        values: buckets.map((bucket) => bucket.started / normalizedBucketSeconds),
      },
      {
        color: "#34d399",
        gradientId: "completed-throughput",
        id: "completed",
        label: "Completed",
        legendClass: "bg-emerald-400",
        values: buckets.map((bucket) => bucket.completed / normalizedBucketSeconds),
      },
      {
        color: "#f87171",
        gradientId: "failed-throughput",
        id: "failed",
        label: "Failed",
        legendClass: "bg-red-400",
        values: buckets.map((bucket) => bucket.failed / normalizedBucketSeconds),
      },
      {
        color: "#fbbf24",
        gradientId: "canceled-throughput",
        id: "canceled",
        label: "Canceled",
        legendClass: "bg-amber-400",
        values: buckets.map((bucket) => bucket.canceled / normalizedBucketSeconds),
      },
  ];
}

function createLinePath(values: number[], maxValue: number) {
  if (values.length === 0) {
    return "";
  }

  return values
    .map((value, index) => {
      const point = chartPoint(value, index, values.length, maxValue);
      return `${index === 0 ? "M" : "L"} ${point.x.toFixed(2)} ${point.y.toFixed(2)}`;
    })
    .join(" ");
}

function createAreaPath(values: number[], maxValue: number) {
  const line = createLinePath(values, maxValue);
  if (!line) {
    return "";
  }

  const last = chartPoint(values.at(-1) ?? 0, values.length - 1, values.length, maxValue);
  const first = chartPoint(values[0] ?? 0, 0, values.length, maxValue);
  return `${line} L ${last.x.toFixed(2)} 210 L ${first.x.toFixed(2)} 210 Z`;
}

function chartPoint(value: number, index: number, count: number, maxValue: number) {
  const x = count <= 1 ? 0 : (index / (count - 1)) * 1000;
  const y = 20 + (1 - value / maxValue) * 170;
  return { x, y };
}

function createThroughputMetrics(
  mode: ThroughputMode,
  chartThroughput: WorkSystemThroughput | undefined,
  chartWindowSeconds: number
): ThroughputMetric[] {
  const totalDescription = `Total settled iterations in the selected ${formatThroughputWindowLabel(chartWindowSeconds)} chart window. This includes completed, failed, and canceled iterations.`;
  if (!chartThroughput) {
    if (mode === "execution") {
      return [
        {
          description: `Exact average execution time across completed iterations in the selected ${formatThroughputWindowLabel(chartWindowSeconds)} chart window. Failed and canceled iterations are excluded.`,
          id: "execution-average",
          label: "Avg",
          pulseClass: "bg-violet-400",
          value: "-",
          widthClass: "min-w-20",
        },
        {
          description: `Approximate p95 execution time across completed iterations in the selected ${formatThroughputWindowLabel(chartWindowSeconds)} chart window, interpolated from fast-work-optimized backend histogram buckets. Failed and canceled iterations are excluded.`,
          id: "execution-p95",
          label: "P95",
          value: "-",
          widthClass: "min-w-20",
        },
        {
          description: `Approximate p99 execution time across completed iterations in the selected ${formatThroughputWindowLabel(chartWindowSeconds)} chart window, interpolated from fast-work-optimized backend histogram buckets. Failed and canceled iterations are excluded.`,
          id: "execution-p99",
          label: "P99",
          value: "-",
          widthClass: "min-w-20",
        },
        {
          description: `Exact slowest execution time across completed iterations in the selected ${formatThroughputWindowLabel(chartWindowSeconds)} chart window. Failed and canceled iterations are excluded.`,
          id: "execution-slowest",
          label: "Slow",
          value: "-",
          widthClass: "min-w-20",
        },
        {
          description: `Exact count of completed iterations with execution timing in the selected ${formatThroughputWindowLabel(chartWindowSeconds)} chart window.`,
          id: "execution-count",
          label: "Count",
          value: "-",
          widthClass: "min-w-20",
        },
      ];
    }

    return [
      {
        description: "Started iterations per second over the last 60 seconds.",
        id: "started",
        label: "",
        pulseClass: "bg-sky-400",
        value: "-",
        valueClass: "text-sky-300",
        widthClass: "min-w-16",
      },
      {
        description: "Completed iterations per second over the last 60 seconds.",
        id: "completed",
        label: "",
        pulseClass: "bg-emerald-400",
        value: "-",
        valueClass: "text-emerald-300",
        widthClass: "min-w-16",
      },
      {
        description: "Failed iterations per second over the last 60 seconds.",
        id: "failed",
        label: "",
        pulseClass: "bg-red-400",
        value: "-",
        valueClass: "text-red-300",
        widthClass: "min-w-16",
      },
      {
        description: "Canceled iterations per second over the last 60 seconds.",
        id: "canceled",
        label: "",
        pulseClass: "bg-amber-400",
        value: "-",
        valueClass: "text-amber-300",
        widthClass: "min-w-16",
      },
      {
        description: "Live execution pressure over the last 60 seconds: started iterations per second minus completed, failed, and canceled iterations per second.",
        icon: Equal,
        iconClass: "text-muted-foreground",
        id: "execution-pressure",
        label: "",
        value: "-",
        valueClass: "text-muted-foreground",
        widthClass: "w-24 shrink-0",
      },
      {
        description: totalDescription,
        id: "total",
        label: "Total",
        value: "-",
        widthClass: "min-w-20",
      },
      {
        description: `Average execution time across completed iterations in the selected ${formatThroughputWindowLabel(chartWindowSeconds)} chart window. Failed and canceled iterations are excluded.`,
        id: "window-average",
        label: "Avg",
        value: "-",
        widthClass: "min-w-20",
      },
    ];
  }

  if (mode === "execution") {
    const executionSummary = chartThroughput.executionSummary;
    return [
      {
        description: `Exact average execution time across ${executionSummary.executionCount} completed ${pluralize("iteration", executionSummary.executionCount)} in the selected ${formatThroughputWindowLabel(chartWindowSeconds)} chart window. Failed and canceled iterations are excluded.`,
        id: "execution-average",
        label: "Avg",
        pulseClass: "bg-violet-400 shadow-[0_0_14px_rgba(167,139,250,0.75)]",
        value: formatMilliseconds(executionSummary.averageExecutionMilliseconds),
        widthClass: "min-w-20",
      },
      {
        description: `Approximate p95 execution time across completed iterations in the selected ${formatThroughputWindowLabel(chartWindowSeconds)} chart window, interpolated from fast-work-optimized backend histogram buckets. Failed and canceled iterations are excluded.`,
        id: "execution-p95",
        label: "P95",
        value: formatMilliseconds(executionSummary.p95ExecutionMilliseconds),
        widthClass: "min-w-20",
      },
      {
        description: `Approximate p99 execution time across completed iterations in the selected ${formatThroughputWindowLabel(chartWindowSeconds)} chart window, interpolated from fast-work-optimized backend histogram buckets. Failed and canceled iterations are excluded.`,
        id: "execution-p99",
        label: "P99",
        value: formatMilliseconds(executionSummary.p99ExecutionMilliseconds),
        widthClass: "min-w-20",
      },
      {
        description: `Exact slowest execution time across completed iterations in the selected ${formatThroughputWindowLabel(chartWindowSeconds)} chart window. Failed and canceled iterations are excluded.`,
        id: "execution-slowest",
        label: "Slow",
        value: formatMilliseconds(executionSummary.slowestExecutionMilliseconds),
        widthClass: "min-w-20",
      },
      {
        description: `Exact count of completed iterations with execution timing in the selected ${formatThroughputWindowLabel(chartWindowSeconds)} chart window.`,
        id: "execution-count",
        label: "Count",
        value: String(executionSummary.executionCount),
        widthClass: "min-w-20",
      },
    ];
  }

  const liveSummary = chartThroughput.liveSummary;
  const executionSummary = chartThroughput.executionSummary;
  const latestStartedRate = liveSummary.startedPerSecond;
  const latestCompletedRate = liveSummary.completedPerSecond;
  const latestFailedRate = liveSummary.failedPerSecond;
  const latestCanceledRate = liveSummary.canceledPerSecond;
  const settledTotal = chartThroughput.settledCount;
  const executionPressureMetric = createExecutionPressureMetric(liveSummary);
  return [
    {
      description: "Started iterations per second over the last 60 seconds.",
      id: "started",
      label: "",
      pulseClass: "bg-sky-400 shadow-[0_0_14px_rgba(56,189,248,0.75)]",
      value: `${formatRate(latestStartedRate)}/s`,
      valueClass: "text-sky-300",
      widthClass: "min-w-16",
    },
    {
      description: "Completed iterations per second over the last 60 seconds.",
      id: "completed",
      label: "",
      pulseClass: "bg-emerald-400 shadow-[0_0_14px_rgba(52,211,153,0.75)]",
      value: `${formatRate(latestCompletedRate)}/s`,
      valueClass: "text-emerald-300",
      widthClass: "min-w-16",
    },
    {
      description: "Failed iterations per second over the last 60 seconds.",
      id: "failed",
      label: "",
      pulseClass: "bg-red-400 shadow-[0_0_14px_rgba(248,113,113,0.7)]",
      value: `${formatRate(latestFailedRate)}/s`,
      valueClass: "text-red-300",
      widthClass: "min-w-16",
    },
    {
      description: "Canceled iterations per second over the last 60 seconds.",
      id: "canceled",
      label: "",
      pulseClass: "bg-amber-400 shadow-[0_0_14px_rgba(251,191,36,0.7)]",
      value: `${formatRate(latestCanceledRate)}/s`,
      valueClass: "text-amber-300",
      widthClass: "min-w-16",
    },
    executionPressureMetric,
    {
      description: totalDescription,
      id: "total",
      label: "Total",
      value: String(settledTotal),
      widthClass: "min-w-20",
    },
    {
      description: `Exact average execution time across ${executionSummary.executionCount} completed ${pluralize("iteration", executionSummary.executionCount)} in the selected ${formatThroughputWindowLabel(chartWindowSeconds)} chart window. Failed and canceled iterations are excluded.`,
      id: "window-average",
      label: "Avg",
      value: formatMilliseconds(executionSummary.averageExecutionMilliseconds),
      widthClass: "min-w-20",
    },
  ];
}

function createExecutionPressureMetric(summary: WorkThroughputLiveSummary): ThroughputMetric {
  const deltaPerSecond = summary.inFlightDeltaPerSecond;
  if (deltaPerSecond > 0) {
    return {
      description: `Live execution pressure is increasing. Over the last ${summary.rateWindowSeconds} seconds, iterations started ${formatRate(deltaPerSecond)} per second faster than they settled.`,
      icon: ArrowUp,
      iconClass: "text-red-300",
      id: "execution-pressure",
      label: "",
      value: `+${formatRate(deltaPerSecond)}/s`,
      valueClass: "text-red-300",
      widthClass: "w-24 shrink-0",
    };
  }

  if (deltaPerSecond < 0) {
    const absoluteDeltaPerSecond = Math.abs(deltaPerSecond);
    return {
      description: `Live execution pressure is decreasing. Over the last ${summary.rateWindowSeconds} seconds, iterations settled ${formatRate(absoluteDeltaPerSecond)} per second faster than they started.`,
      icon: ArrowDown,
      iconClass: "text-emerald-300",
      id: "execution-pressure",
      label: "",
      value: `-${formatRate(absoluteDeltaPerSecond)}/s`,
      valueClass: "text-emerald-300",
      widthClass: "w-24 shrink-0",
    };
  }

  return {
    description: `Live execution pressure is balanced. Over the last ${summary.rateWindowSeconds} seconds, starts and settled outcomes matched.`,
    icon: Equal,
    iconClass: "text-muted-foreground",
    id: "execution-pressure",
    label: "",
    value: "0/s",
    valueClass: "text-muted-foreground",
    widthClass: "w-24 shrink-0",
  };
}

function formatThroughputWindowLabel(seconds: number) {
  if (seconds === 60) {
    return "60-second";
  }
  if (seconds === 3600) {
    return "1-hour";
  }
  if (seconds % 3600 === 0) {
    return `${seconds / 3600}-hour`;
  }
  if (seconds % 60 === 0) {
    return `${seconds / 60}-minute`;
  }

  return `${seconds}-second`;
}

function getNiceChartMax(value: number, mode: ThroughputMode) {
  if (value <= 0) {
    return mode === "execution" ? 100 : 1;
  }

  const exponent = Math.floor(Math.log10(value));
  const magnitude = 10 ** exponent;
  const normalized = value / magnitude;
  const nice = normalized <= 1
    ? 1
    : normalized <= 2
      ? 2
      : normalized <= 5
        ? 5
        : 10;
  return nice * magnitude;
}

function createYAxisTicks(maxValue: number) {
  return [maxValue, maxValue * 2 / 3, maxValue / 3, 0];
}

function formatThroughputAxisValue(mode: ThroughputMode, value: number) {
  if (mode === "execution") {
    return formatMilliseconds(value);
  }

  return `${formatRate(value)}/s`;
}

function createTimeAxisTicks(throughput: WorkSystemThroughput | undefined, buckets: WorkThroughputBucket[]) {
  if (!throughput || buckets.length === 0 || !throughput.bucketSeconds) {
    return [];
  }

  const firstBucketTime = parseChartTimestamp(buckets[0].at);
  const latestBucketTime = parseChartTimestamp(buckets.at(-1)?.at ?? throughput.to);
  const toTime = parseChartTimestamp(throughput.to);
  const latest = latestBucketTime ?? toTime;
  const from = firstBucketTime ?? (
    latest === null ? null : latest - Math.max(1, buckets.length - 1) * throughput.bucketSeconds * 1000
  );
  if (from === null || latest === null || !Number.isFinite(from) || !Number.isFinite(latest)) {
    return [];
  }

  const windowSeconds = Math.max(1, Math.round((latest - from) / 1000) + throughput.bucketSeconds);
  return [0, 0.25, 0.5, 0.75, 1].map((position) => {
    const timestamp = from + (latest - from) * position;
    return {
      label: formatChartTimeAxisLabel(timestamp, windowSeconds),
      position,
    };
  });
}

function parseChartTimestamp(value: string | undefined) {
  if (!value) {
    return null;
  }

  const timestamp = Date.parse(value);
  return Number.isFinite(timestamp) ? timestamp : null;
}

function formatChartTimeAxisLabel(timestamp: number, windowSeconds: number) {
  const options: Intl.DateTimeFormatOptions =
    windowSeconds >= 3600
      ? { hour: "numeric", minute: "2-digit" }
      : { hour: "numeric", minute: "2-digit", second: "2-digit" };
  return new Intl.DateTimeFormat(undefined, options).format(new Date(timestamp));
}

function formatRate(value: number) {
  if (value >= 100) {
    return value.toFixed(0);
  }
  if (value >= 10) {
    return value.toFixed(1);
  }
  if (value >= 1) {
    return value.toFixed(2);
  }
  return value.toFixed(2);
}

function formatMilliseconds(value: number) {
  if (value >= 1000) {
    return `${(value / 1000).toFixed(value >= 60_000 ? 0 : 1)}s`;
  }

  return `${Math.round(value)}ms`;
}

function pluralize(word: string, count: number) {
  return count === 1 ? word : `${word}s`;
}

function isThroughputSeriesId(value: unknown): value is ThroughputSeriesId {
  return typeof value === "string" && throughputSeriesIds.includes(value as ThroughputSeriesId);
}

function getWorkComponentData<T>(
  result: WorkComponentQueryResult | undefined,
  id: string
): T | undefined {
  const component = result?.components[id] as WorkComponentResult<T> | undefined;
  return component?.status?.toLowerCase() === "ok" ? component.data : undefined;
}

function getWorkComponentErrors(result: WorkComponentQueryResult | undefined) {
  return Object.entries(result?.components ?? {})
    .filter(([, component]) => component.status?.toLowerCase() !== "ok")
    .map(([id, component]) => component.error ?? `${id} failed to load.`);
}

function StackedSkeleton({ count }: { count: number }) {
  return (
    <div className="space-y-3">
      {Array.from({ length: count }).map((_, index) => (
        <Skeleton className="h-10 w-full" key={index} />
      ))}
    </div>
  );
}

function createOverviewComponentScope(scope: OverviewScope | null) {
  const normalizedScope = normalizeOverviewScope(scope);
  const category = normalizeCategoryFilter(normalizedScope?.category ?? "");
  const definitionName = normalizedScope?.definitionName ?? "";
  if (!category && !definitionName) {
    return null;
  }

  return {
    category: category || undefined,
    definitionName: definitionName || undefined,
    includeSubcategories: category && !definitionName
      ? scope?.includeSubcategories ?? true
      : undefined,
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
    category: category || undefined,
    definitionName: definitionName || undefined,
    includeSubcategories: category && !definitionName
      ? scope.includeSubcategories ?? true
      : undefined,
  };
}

function normalizeScopeText(value: unknown) {
  return typeof value === "string" ? value.trim() : "";
}

function splitCatalogPath(path: unknown) {
  const value = normalizeScopeText(path);
  return value
    ? value
        .split(":")
        .map((segment) => segment.trim())
        .filter(Boolean)
    : [];
}

function normalizeCategoryFilter(path: unknown) {
  return splitCatalogPath(path).join(":");
}

function formatIterationCount(count: number) {
  return `${count} ${count === 1 ? "iteration" : "iterations"}`;
}

type DurationDisplay = {
  isWarning: boolean;
  text: string;
};

function formatExecutionDuration(value?: string | null): DurationDisplay {
  const seconds = parseDurationSeconds(value);
  if (seconds === null) {
    return { isWarning: false, text: "-" };
  }

  return formatDurationSeconds(seconds);
}


function formatFailedWorkerDuration(worker: WorkOverviewFailedWorker): DurationDisplay {
  return worker.totalExecutionDuration
    ? formatExecutionDuration(worker.totalExecutionDuration)
    : { isWarning: false, text: "-" };
}

function formatQueueAge(value?: string | null): DurationDisplay {
  if (!value) {
    return { isWarning: false, text: "-" };
  }

  const timestamp = Date.parse(value);
  if (!Number.isFinite(timestamp)) {
    return { isWarning: false, text: "-" };
  }

  return formatDurationSeconds(Math.max(0, (Date.now() - timestamp) / 1000));
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

export function RealtimePayloadWindow({
  captureEnabled,
  connectionState,
  enabled,
  externalMessages,
  hubUrl,
  maxMessages,
  messages,
  onCaptureEnabledChange,
  onClearExternalMessages,
  onClearMessages,
  onMaxMessagesChange,
  onOpenChange,
  open,
}: RealtimePayloadPanelState) {
  const [position, setPosition] = useState({ x: 0, y: 0 });
  const [windowSize, setWindowSize] = useState<"compact" | "large">("large");
  const [messagesCollapsed, setMessagesCollapsed] = useState(false);
  const [jsonView, setJsonView] = useState<"payload" | "componentData">("payload");
  const [jsonCollapsedToComponents, setJsonCollapsedToComponents] = useState(false);
  const [subscriptionFilter, setSubscriptionFilter] = useState("all");
  const [selectedComponentId, setSelectedComponentId] = useState<string | null>(null);
  const [selectedMessageId, setSelectedMessageId] = useState<string | null>(null);
  const dragRef = useRef<{
    originX: number;
    originY: number;
    startX: number;
    startY: number;
  } | null>(null);
  const panelRef = useRef<HTMLDivElement | null>(null);
  const wasOpenRef = useRef(false);
  const allMessages = useMemo(
    () => [...messages, ...externalMessages]
      .sort((left, right) => right.receivedAt - left.receivedAt)
      .slice(0, maxMessages),
    [externalMessages, maxMessages, messages]
  );
  const subscriptionOptions = useMemo(
    () => Array.from(new Set(allMessages.map((message) => message.subscription))).sort(),
    [allMessages]
  );
  const activeSubscriptionFilter =
    subscriptionFilter !== "all" && subscriptionOptions.includes(subscriptionFilter)
      ? subscriptionFilter
      : "all";
  const filteredMessages = activeSubscriptionFilter === "all"
    ? allMessages
    : allMessages.filter((message) => message.subscription === activeSubscriptionFilter);
  const selectedMessage =
    filteredMessages.find((message) => message.id === selectedMessageId) ?? filteredMessages[0];
  const returnedComponents = useMemo(
    () => getRealtimePayloadComponentData(selectedMessage?.value),
    [selectedMessage]
  );
  const selectedComponent =
    returnedComponents.find((component) => component.id === selectedComponentId) ??
    returnedComponents[0];
  const isCompactWindow = windowSize === "compact";
  const receivedAtText = selectedMessage
    ? formatPayloadTime(selectedMessage.receivedAt)
    : "No messages captured";
  const payloadSizeText = selectedMessage?.bytes === undefined
    ? "-"
    : `${selectedMessage.bytes.toLocaleString()} bytes`;

  useEffect(() => {
    if (open && !wasOpenRef.current) {
      setPosition(getCenteredRealtimePayloadPosition(windowSize));
    }
    wasOpenRef.current = open;
  }, [open, windowSize]);

  const toggleWindowSize = () => {
    const nextSize = isCompactWindow ? "large" : "compact";

    setWindowSize(nextSize);
    setPosition(getCenteredRealtimePayloadPosition(nextSize));
  };

  const showComponentDataView = () => {
    setJsonView("componentData");
    setSelectedComponentId((current) =>
      current && returnedComponents.some((component) => component.id === current)
        ? current
        : returnedComponents[0]?.id ?? null
    );
  };

  const startDrag = (event: PointerEvent<HTMLDivElement>) => {
    event.currentTarget.setPointerCapture(event.pointerId);
    dragRef.current = {
      originX: position.x,
      originY: position.y,
      startX: event.clientX,
      startY: event.clientY,
    };
  };

  const drag = (event: PointerEvent<HTMLDivElement>) => {
    if (!dragRef.current) {
      return;
    }

    const nextX = dragRef.current.originX + event.clientX - dragRef.current.startX;
    const nextY = dragRef.current.originY + event.clientY - dragRef.current.startY;
    const viewportWidth = window.visualViewport?.width ?? window.innerWidth;
    const viewportHeight = window.visualViewport?.height ?? window.innerHeight;
    const panelWidth = panelRef.current?.offsetWidth ?? 0;
    const panelHeight = panelRef.current?.offsetHeight ?? 0;

    setPosition({
      x: clampFloatingWindowPosition(nextX, viewportWidth, panelWidth),
      y: clampFloatingWindowPosition(nextY, viewportHeight, panelHeight),
    });
  };

  const stopDrag = (event: PointerEvent<HTMLDivElement>) => {
    dragRef.current = null;
    event.currentTarget.releasePointerCapture(event.pointerId);
  };

  return (
    <>
      {open && typeof document !== "undefined"
        ? createPortal(
          <div
            className={`fixed z-50 grid resize grid-rows-[auto_minmax(0,1fr)] overflow-hidden rounded-lg border bg-popover text-sm text-popover-foreground shadow-2xl ring-1 ring-foreground/10 ${
              isCompactWindow ? "min-h-[28rem] min-w-[42rem]" : "min-h-[32rem] min-w-[48rem]"
            }`}
            ref={panelRef}
            style={{
              height: isCompactWindow ? "min(82vh, 32rem)" : "min(88vh, 56rem)",
              left: position.x,
              top: position.y,
              width: isCompactWindow ? "min(96vw, 48rem)" : "min(96vw, 96rem)",
            }}
          >
            <div
              className="flex cursor-move items-center justify-between gap-3 border-b px-4 py-3 select-none"
              onPointerDown={startDrag}
              onPointerMove={drag}
              onPointerUp={stopDrag}
            >
              <div className="min-w-0">
                <div className="font-medium text-base">Realtime payloads</div>
                <div className="truncate text-muted-foreground text-xs">
                  {enabled ? connectionState : "disabled"} - {messages.length}/{maxMessages} messages - {payloadSizeText}
                </div>
              </div>
              <div className="flex shrink-0 items-center gap-1">
                <Button
                  aria-label={isCompactWindow ? "Expand realtime payloads" : "Compact realtime payloads"}
                  className="cursor-pointer"
                  onClick={toggleWindowSize}
                  onPointerDown={(event) => event.stopPropagation()}
                  size="icon-sm"
                  variant="ghost"
                >
                  {isCompactWindow ? <Rows4 className="size-4" /> : <Rows2 className="size-4" />}
                </Button>
                <Button
                  aria-label="Close realtime payloads"
                  className="cursor-pointer"
                  onClick={() => onOpenChange(false)}
                  onPointerDown={(event) => event.stopPropagation()}
                  size="icon-sm"
                  variant="ghost"
                >
                  <X className="size-4" />
                </Button>
              </div>
            </div>
            <div className="grid min-h-0 grid-rows-[auto_minmax(0,1fr)] gap-3 overflow-hidden p-3">
              <div className="grid gap-2 rounded-md border px-3 py-2 lg:grid-cols-[minmax(0,1fr)_auto]">
                <div className="flex min-w-0 flex-wrap items-center gap-x-4 gap-y-1 text-xs">
                  <PayloadInlineMetric label="Received" value={receivedAtText} />
                  <PayloadInlineMetric label="Size" value={payloadSizeText} />
                  <PayloadInlineMetric label="Messages" value={`${filteredMessages.length}/${allMessages.length}/${maxMessages}`} />
                  <PayloadInlineMetric label="Subscription" value={selectedMessage?.subscription ?? "-"} />
                  <PayloadInlineMetric label="Hub" value={hubUrl ?? "-"} wide />
                </div>
                <div className="flex flex-wrap items-center gap-3">
                  <Button
                    className="h-8 px-2 text-xs"
                    disabled={allMessages.length === 0}
                    onClick={() => {
                      setSelectedMessageId(null);
                      setSelectedComponentId(null);
                      onClearMessages();
                      onClearExternalMessages();
                    }}
                    size="sm"
                    variant="ghost"
                  >
                    Clear
                  </Button>
                  <label className="flex items-center gap-2 text-muted-foreground">
                    <span>Show</span>
                    <select
                      className="h-8 max-w-44 rounded-md border bg-background px-2 text-foreground"
                      onChange={(event) => setSubscriptionFilter(event.currentTarget.value)}
                      value={activeSubscriptionFilter}
                    >
                      <option value="all">All subscriptions</option>
                      {subscriptionOptions.map((subscription) => (
                        <option key={subscription} value={subscription}>
                          {subscription}
                        </option>
                      ))}
                    </select>
                  </label>
                  <label className="flex items-center gap-2 text-muted-foreground">
                    <input
                      checked={captureEnabled}
                      className="size-4 accent-primary"
                      onChange={(event) => onCaptureEnabledChange(event.currentTarget.checked)}
                      type="checkbox"
                    />
                    <span>Capture incoming messages</span>
                  </label>
                  <label className="flex items-center gap-2">
                    <span className="text-muted-foreground">Max</span>
                    <input
                      className="h-8 w-20 rounded-md border bg-background px-2 font-mono text-foreground"
                      max={1000}
                      min={1}
                      onChange={(event) =>
                        onMaxMessagesChange(normalizeRealtimeMaxMessages(event.currentTarget.value))
                      }
                      type="number"
                      value={maxMessages}
                    />
                  </label>
                </div>
              </div>
              <div
                className={`grid min-h-0 gap-3 ${
                  messagesCollapsed
                    ? "md:grid-cols-[2.75rem_minmax(0,1fr)]"
                    : "md:grid-cols-[22rem_minmax(0,1fr)]"
                }`}
              >
                <div className="grid min-h-0 grid-rows-[auto_minmax(0,1fr)] overflow-hidden rounded-md border">
                  <div className="flex items-center justify-between gap-2 border-b px-2 py-1.5">
                    {!messagesCollapsed && (
                      <div className="font-medium text-muted-foreground text-xs">Messages</div>
                    )}
                    <Button
                      aria-label={messagesCollapsed ? "Show messages" : "Collapse messages"}
                      className="ml-auto"
                      onClick={() => setMessagesCollapsed((current) => !current)}
                      size="icon-sm"
                      variant="ghost"
                    >
                      <ChevronRight
                        className={`size-4 transition-transform ${
                          messagesCollapsed ? "" : "rotate-180"
                        }`}
                      />
                    </Button>
                  </div>
                  {messagesCollapsed ? (
                    <div className="flex min-h-0 items-start justify-center overflow-hidden py-2">
                      <div className="font-mono text-muted-foreground text-xs [writing-mode:vertical-rl]">
                        {filteredMessages.length}
                      </div>
                    </div>
                  ) : (
                    <div className="min-h-0 overflow-auto p-2">
                      {allMessages.length === 0 ? (
                        <div className="p-3 text-muted-foreground text-sm">
                          Waiting for realtime payloads.
                        </div>
                      ) : filteredMessages.length === 0 ? (
                        <div className="p-3 text-muted-foreground text-sm">
                          No payloads match this subscription.
                        </div>
                      ) : (
                        <div className="space-y-1">
                          {filteredMessages.map((message) => (
                            <button
                              className={`grid w-full gap-1 rounded-md px-2 py-2 text-left text-xs transition-colors ${
                                message.id === selectedMessage?.id
                                  ? "bg-accent text-accent-foreground"
                                  : "hover:bg-accent/50"
                              }`}
                              key={message.id}
                              onClick={() => setSelectedMessageId(message.id)}
                              type="button"
                            >
                              <span className="flex items-center justify-between gap-2">
                                <span className="min-w-0 truncate font-mono">
                                  {formatPayloadTime(message.receivedAt)}
                                </span>
                                <span className="font-mono text-muted-foreground">
                                  {message.bytes.toLocaleString()}b
                                </span>
                              </span>
                              <span className="truncate font-mono text-muted-foreground">
                                {message.subscription}
                              </span>
                              <span className="truncate text-muted-foreground">
                                {message.components.map((component) => component.id).join(", ")}
                              </span>
                            </button>
                          ))}
                        </div>
                      )}
                    </div>
                  )}
                </div>
                <div
                  className={`grid min-h-0 overflow-hidden rounded-md border ${
                    jsonView === "componentData"
                      ? "grid-rows-[auto_auto_minmax(0,1fr)]"
                      : "grid-rows-[auto_minmax(0,1fr)]"
                  }`}
                >
                  <div className="flex items-center justify-between gap-2 border-b px-3 py-2">
                    <div className="flex min-w-0 items-center gap-2">
                      <div className="font-medium text-muted-foreground text-xs">
                        JSON
                      </div>
                      <div className="flex rounded-md border bg-muted/30 p-0.5">
                        <Button
                          className={`h-6 px-2 text-xs ${
                            jsonView === "payload" ? "bg-accent text-accent-foreground" : ""
                          }`}
                          onClick={() => setJsonView("payload")}
                          size="sm"
                          variant="ghost"
                        >
                          Payload
                        </Button>
                        <Button
                          className={`h-6 px-2 text-xs ${
                            jsonView === "componentData" ? "bg-accent text-accent-foreground" : ""
                          }`}
                          disabled={returnedComponents.length === 0}
                          onClick={showComponentDataView}
                          size="sm"
                          variant="ghost"
                        >
                          Data
                        </Button>
                      </div>
                    </div>
                    {jsonView === "payload" ? (
                      <Button
                        className="h-7 px-2 text-xs"
                        onClick={() => setJsonCollapsedToComponents((current) => !current)}
                        size="sm"
                        variant="ghost"
                      >
                        {jsonCollapsedToComponents ? "Expand JSON" : "Component level"}
                      </Button>
                    ) : (
                      <div className="min-w-0 truncate font-mono text-muted-foreground text-xs">
                        {selectedComponent
                          ? `${selectedComponent.id}:${selectedComponent.shape ?? "?"}:${selectedComponent.status ?? "?"}`
                          : "No component data"}
                      </div>
                    )}
                  </div>
                  {jsonView === "componentData" && (
                    <div className="flex min-w-0 gap-1 overflow-x-auto border-b px-3 py-2">
                      {returnedComponents.length === 0 ? (
                        <span className="text-muted-foreground text-xs">
                          No returned components.
                        </span>
                      ) : (
                        returnedComponents.map((component) => (
                          <button
                            className={`shrink-0 rounded-md border px-2 py-1 font-mono text-xs transition-colors ${
                              component.id === selectedComponent?.id
                                ? "bg-accent text-accent-foreground"
                                : "text-muted-foreground hover:bg-accent/50"
                            }`}
                            key={component.id}
                            onClick={() => setSelectedComponentId(component.id)}
                            type="button"
                          >
                            {component.id}
                          </button>
                        ))
                      )}
                    </div>
                  )}
                  <pre className="min-h-0 overflow-auto whitespace-pre-wrap break-words bg-muted/30 p-3 font-mono text-xs leading-relaxed">
                    {jsonView === "componentData" ? (
                      selectedComponent ? (
                        <JsonValue
                          key={`${selectedMessage?.id ?? "none"}:${selectedComponent.id}:data`}
                          value={selectedComponent.data}
                        />
                      ) : (
                        "Select a returned component."
                      )
                    ) : selectedMessage ? (
                      <JsonValue
                        collapseToComponentLevel={jsonCollapsedToComponents}
                        key={`${selectedMessage.id}:${jsonCollapsedToComponents ? "components" : "full"}`}
                        value={selectedMessage.value}
                      />
                    ) : (
                      "Waiting for the first realtime payload."
                    )}
                  </pre>
                </div>
              </div>
            </div>
          </div>,
          document.body
        )
        : null}
    </>
  );
}

export function JsonValue({
  collapseToComponentLevel = false,
  indent = 0,
  maxExpandedArrayItems,
  value,
}: {
  collapseToComponentLevel?: boolean;
  indent?: number;
  maxExpandedArrayItems?: number;
  value: unknown;
}) {
  if (value === null) {
    return <span className="text-muted-foreground">null</span>;
  }

  if (Array.isArray(value)) {
    return (
      <JsonArrayValue
        collapseToComponentLevel={collapseToComponentLevel}
        indent={indent}
        maxExpandedArrayItems={maxExpandedArrayItems}
        value={value}
      />
    );
  }

  if (typeof value === "object") {
    return (
      <JsonObjectValue
        collapseToComponentLevel={collapseToComponentLevel}
        indent={indent}
        maxExpandedArrayItems={maxExpandedArrayItems}
        value={value as Record<string, unknown>}
      />
    );
  }

  if (typeof value === "string") {
    return <span className="text-emerald-300">{JSON.stringify(value)}</span>;
  }

  if (typeof value === "number") {
    return <span className="text-amber-300">{value}</span>;
  }

  if (typeof value === "boolean") {
    return <span className="text-purple-300">{String(value)}</span>;
  }

  if (typeof value === "undefined") {
    return <span className="text-muted-foreground">undefined</span>;
  }

  return <span>{JSON.stringify(value)}</span>;
}

function JsonArrayValue({
  collapseToComponentLevel,
  indent,
  maxExpandedArrayItems,
  value,
}: {
  collapseToComponentLevel: boolean;
  indent: number;
  maxExpandedArrayItems?: number;
  value: unknown[];
}) {
  const [manualExpanded, setManualExpanded] = useState<boolean | null>(null);
  const isCollapsedToComponent = collapseToComponentLevel && indent >= 2;
  const isExpanded = manualExpanded ?? !isCollapsedToComponent;
  const expandedItemLimit = maxExpandedArrayItems && maxExpandedArrayItems > 0
    ? maxExpandedArrayItems
    : value.length;
  const visibleItems = value.length > expandedItemLimit
    ? value.slice(0, expandedItemLimit)
    : value;
  const hiddenItemCount = value.length - visibleItems.length;

  if (value.length === 0) {
    return <span>[]</span>;
  }

  if (!isExpanded) {
    return (
      <JsonCollapseButton
        closer="]"
        count={`${value.length} items`}
        expanded={false}
        opener="["
        onToggle={() => setManualExpanded(true)}
      />
    );
  }

  return (
    <>
      <JsonCollapseButton
        expanded={isExpanded}
        onToggle={() => setManualExpanded(false)}
        opener="["
      />
      {visibleItems.map((item, index) => (
        <span key={index}>
          {"\n"}
          {jsonIndent(indent + 1)}
          <JsonValue
            collapseToComponentLevel={collapseToComponentLevel}
            indent={indent + 1}
            maxExpandedArrayItems={maxExpandedArrayItems}
            value={item}
          />
          {index < value.length - 1 ? <span>,</span> : null}
        </span>
      ))}
      {hiddenItemCount > 0 && (
        <span>
          {"\n"}
          {jsonIndent(indent + 1)}
          <span className="text-muted-foreground">
            ... {hiddenItemCount.toLocaleString()} more item{hiddenItemCount === 1 ? "" : "s"}
          </span>
        </span>
      )}
      {"\n"}
      {jsonIndent(indent)}
      <span>]</span>
    </>
  );
}

function JsonObjectValue({
  collapseToComponentLevel,
  indent,
  maxExpandedArrayItems,
  value,
}: {
  collapseToComponentLevel: boolean;
  indent: number;
  maxExpandedArrayItems?: number;
  value: Record<string, unknown>;
}) {
  const [manualExpanded, setManualExpanded] = useState<boolean | null>(null);
  const entries = Object.entries(value);
  const isCollapsedToComponent = collapseToComponentLevel && indent >= 2;
  const isExpanded = manualExpanded ?? !isCollapsedToComponent;

  if (entries.length === 0) {
    return <span>{"{}"}</span>;
  }

  if (!isExpanded) {
    return (
      <JsonCollapseButton
        closer="}"
        count={`${entries.length} keys`}
        expanded={false}
        opener="{"
        onToggle={() => setManualExpanded(true)}
      />
    );
  }

  return (
    <>
      <JsonCollapseButton
        expanded={isExpanded}
        onToggle={() => setManualExpanded(false)}
        opener="{"
      />
      {entries.map(([key, item], index) => (
        <span key={key}>
          {"\n"}
          {jsonIndent(indent + 1)}
          <span className="text-sky-300">{JSON.stringify(key)}</span>
          <span>: </span>
          <JsonValue
            collapseToComponentLevel={collapseToComponentLevel}
            indent={indent + 1}
            maxExpandedArrayItems={maxExpandedArrayItems}
            value={item}
          />
          {index < entries.length - 1 ? <span>,</span> : null}
        </span>
      ))}
      {"\n"}
      {jsonIndent(indent)}
      <span>{"}"}</span>
    </>
  );
}

function JsonCollapseButton({
  closer,
  count,
  expanded,
  onToggle,
  opener,
}: {
  closer?: string;
  count?: string;
  expanded: boolean;
  onToggle: () => void;
  opener: string;
}) {
  return (
    <button
      className="inline-flex items-center gap-1 rounded px-0.5 text-left hover:bg-accent"
      onClick={onToggle}
      type="button"
    >
      <ChevronRight className={`size-3 transition-transform ${expanded ? "rotate-90" : ""}`} />
      <span>{opener}</span>
      {count ? <span className="text-muted-foreground">{count}</span> : null}
      {closer ? <span>{closer}</span> : null}
    </button>
  );
}

function jsonIndent(level: number) {
  return <span>{Array.from({ length: level }).map(() => "  ").join("")}</span>;
}

function formatPayloadTime(value: number) {
  return new Intl.DateTimeFormat(undefined, {
    hour: "numeric",
    minute: "numeric",
    second: "numeric",
  }).format(new Date(value));
}

function getCenteredRealtimePayloadPosition(size: "compact" | "large") {
  const viewportWidth = window.visualViewport?.width ?? window.innerWidth;
  const viewportHeight = window.visualViewport?.height ?? window.innerHeight;
  const panelWidth = size === "compact"
    ? Math.min(viewportWidth * 0.96, 768)
    : Math.min(viewportWidth * 0.96, 1536);
  const panelHeight = size === "compact"
    ? Math.min(viewportHeight * 0.82, 512)
    : Math.min(viewportHeight * 0.88, 896);

  return {
    x: Math.max(8, Math.round((viewportWidth - panelWidth) / 2)),
    y: Math.max(8, Math.round((viewportHeight - panelHeight) / 2)),
  };
}

function PayloadInlineMetric({
  label,
  value,
  wide = false,
}: {
  label: string;
  value: string;
  wide?: boolean;
}) {
  return (
    <div className={`flex min-w-0 items-center gap-1 ${wide ? "max-w-[32rem]" : ""}`}>
      <span className="text-muted-foreground">{label}</span>
      <span className="min-w-0 truncate font-mono text-foreground">{value}</span>
    </div>
  );
}

function normalizeRealtimeMaxMessages(value: string) {
  const parsed = Number.parseInt(value, 10);
  if (!Number.isFinite(parsed)) {
    return 100;
  }

  return Math.min(1000, Math.max(1, parsed));
}

function clampFloatingWindowPosition(value: number, viewport: number, size: number) {
  const visibleGrip = 40;
  const min = size > 0 ? Math.min(8, visibleGrip - size) : 8;
  const max = Math.max(8, viewport - visibleGrip);

  return Math.min(Math.max(min, value), max);
}

function getRealtimeErrorMessage(error: unknown, fallback: string) {
  return error instanceof Error && error.message ? error.message : fallback;
}

function isExpectedRealtimeDisconnect(error: unknown) {
  const message = getRealtimeErrorMessage(error, "").toLowerCase();
  return (
    message.includes("failed to fetch") ||
    message.includes("failed to complete negotiation") ||
    message.includes("failed to start the connection") ||
    message.includes("websocket closed with status code: 1006")
  );
}

export type RealtimeViewLoadable<T> = Loadable<T> & {
  clearMessages: () => void;
  connectionState: string;
  enabled: boolean;
  hubUrl?: string | null;
  messages: RealtimePayloadMessage[];
};

export function useWorkableRealtimeView<T>(
  connection: WorkableConnection | null,
  viewName: string,
  body: unknown,
  enabled: boolean,
  captureEnabled: boolean,
  maxMessages: number,
  subscription = viewName
): RealtimeViewLoadable<T> {
  const [state, setState] = useState<RealtimeViewLoadable<T>>({
    clearMessages: () => undefined,
    connectionState: enabled ? "connecting" : "disabled",
    enabled,
    hubUrl: connection ? createWorkableRealtimeUrl(connection) : null,
    loading: false,
    messages: [],
  });
  const hubConnectionRef = useRef<HubConnection | null>(null);
  const hasConnection = connection !== null;
  const apiUrl = connection?.apiUrl ?? "";
  const hubUrl = connection ? createWorkableRealtimeUrl(connection) : null;
  const systemName = connection?.systemName;
  const bodyKey = JSON.stringify(body);
  const bodyKeyRef = useRef(bodyKey);
  const captureEnabledRef = useRef(captureEnabled);
  const maxMessagesRef = useRef(maxMessages);
  const messageIdRef = useRef(0);
  const systemNameRef = useRef(systemName);

  useEffect(() => {
    bodyKeyRef.current = bodyKey;
    captureEnabledRef.current = captureEnabled;
    maxMessagesRef.current = maxMessages;
    systemNameRef.current = systemName;
  }, [bodyKey, captureEnabled, maxMessages, systemName]);

  const clearMessages = useCallback(() => {
    setState((current) =>
      current.messages.length === 0 ? current : { ...current, messages: [] }
    );
  }, []);

  useEffect(() => {
    queueMicrotask(() => {
      setState((current) =>
        current.messages.length > maxMessages
          ? { ...current, messages: current.messages.slice(0, maxMessages) }
          : current
      );
    });
  }, [maxMessages]);

  useEffect(() => {
    if (!hasConnection || !enabled || !hubUrl) {
      queueMicrotask(() =>
        setState((current) =>
          current.connectionState === "disabled" &&
          current.enabled === enabled &&
          current.hubUrl === hubUrl &&
          !current.loading &&
          !current.refreshing
            ? current
            : {
                ...current,
                connectionState: "disabled",
                enabled,
                hubUrl,
                loading: false,
                refreshing: false,
              }
        )
      );
      return;
    }

    let canceled = false;
    let retryTimer: ReturnType<typeof setTimeout> | null = null;
    queueMicrotask(() => {
      if (!canceled) {
        setState((current) => ({
          ...current,
          connectionState: "connecting",
          enabled,
          hubUrl,
        }));
      }
    });
    const hubConnection = new HubConnectionBuilder()
      .withUrl(hubUrl, {
        accessTokenFactory: () => getWorkableRealtimeAccessToken(apiUrl),
        withCredentials: true,
      })
      .withAutomaticReconnect()
      .configureLogging(LogLevel.None)
      .build();

    hubConnectionRef.current = hubConnection;
    const subscribe = () =>
      hubConnection.invoke(
        "WatchView",
        viewName,
        JSON.parse(bodyKeyRef.current),
        systemNameRef.current ?? null
      );
    const scheduleRestart = (error: unknown, delayMs = 1000) => {
      if (canceled || retryTimer) {
        return;
      }

      retryTimer = setTimeout(() => {
        retryTimer = null;
        if (!canceled && hubConnection.state === HubConnectionState.Disconnected) {
          startConnection();
        }
      }, delayMs);
      setState((current) => ({
        ...current,
        connectionState: "disconnected",
        error: error && !isExpectedRealtimeDisconnect(error)
          ? getRealtimeErrorMessage(error, "Realtime view connection closed.")
          : undefined,
        loading: false,
        refreshing: false,
      }));
    };
    const startConnection = () => {
      if (canceled || hubConnection.state !== HubConnectionState.Disconnected) {
        return;
      }

      queueMicrotask(() => {
        if (!canceled) {
          setState((current) => ({
            ...current,
            connectionState: current.data === undefined ? "connecting" : "reconnecting",
            loading: current.data === undefined,
            refreshing: current.data !== undefined,
          }));
        }
      });
      void hubConnection
        .start()
        .then(() => subscribe())
        .then(() => {
          if (!canceled) {
            setState((current) => ({
              ...current,
              connectionState: "connected",
              error: undefined,
              loading: false,
              refreshing: false,
            }));
          }
        })
        .catch((error) => {
          if (!canceled) {
            scheduleRestart(error, isExpectedRealtimeDisconnect(error) ? 1000 : 3000);
          }
        });
    };
    hubConnection.on("workable.view", (result: T) => {
      if (!canceled) {
        const payloadJson = JSON.stringify(result);
        const message = createRealtimePayloadMessage(
          result,
          payloadJson,
          `${subscription}:${++messageIdRef.current}`,
          viewName,
          subscription
        );
        setState((current) => ({
          ...current,
          connectionState: "connected",
          data: result,
          enabled,
          hubUrl,
          loading: false,
          messages: captureEnabledRef.current
            ? [message, ...current.messages].slice(0, maxMessagesRef.current)
            : current.messages,
          refreshing: false,
        }));
      }
    });
    hubConnection.onreconnecting(() => {
      if (!canceled) {
        setState((current) => ({
          ...current,
          connectionState: "reconnecting",
          refreshing: current.data !== undefined,
        }));
      }
    });
    hubConnection.onreconnected(() => {
      if (!canceled) {
        setState((current) => ({
          ...current,
          connectionState: "connected",
        }));
      }
      void subscribe().catch((error) => {
        if (!canceled && !isExpectedRealtimeDisconnect(error)) {
          setState((current) => ({
            ...current,
            connectionState: "error",
            error: getRealtimeErrorMessage(error, "Realtime view subscription failed."),
            loading: false,
            refreshing: false,
          }));
        }
      });
    });
    hubConnection.onclose((error) => {
      if (!canceled) {
        scheduleRestart(error, isExpectedRealtimeDisconnect(error) ? 1000 : 3000);
      }
    });

    startConnection();

    return () => {
      canceled = true;
      if (retryTimer) {
        clearTimeout(retryTimer);
      }
      hubConnectionRef.current = null;
      void hubConnection.stop().catch(() => undefined);
    };
  }, [apiUrl, enabled, hasConnection, hubUrl, subscription, systemName, viewName]);

  useEffect(() => {
    const hubConnection = hubConnectionRef.current;
    if (!enabled || !hubConnection || hubConnection.state !== HubConnectionState.Connected) {
      return;
    }

    let canceled = false;
    queueMicrotask(() => {
      if (!canceled) {
        setState((current) => ({
          ...current,
          connectionState: hubConnection.state.toLowerCase(),
          error: undefined,
          loading: current.data === undefined,
          refreshing: current.data !== undefined,
        }));
      }
    });

    hubConnection
      .invoke("WatchView", viewName, JSON.parse(bodyKey), systemName ?? null)
      .catch((error) => {
        if (!canceled) {
          setState((current) => ({
            ...current,
            data: current.data,
            connectionState: "error",
            error: getRealtimeErrorMessage(error, "Realtime view subscription failed."),
            loading: false,
            refreshing: false,
          }));
        }
      });

    return () => {
      canceled = true;
      if (hubConnection.state === HubConnectionState.Connected) {
        void hubConnection
          .invoke("UnwatchView", viewName, systemName ?? null)
          .catch(() => undefined);
      }
    };
  }, [bodyKey, enabled, systemName, viewName]);

  return { ...state, clearMessages };
}

export function useWorkableRealtimeEvents(
  connection: WorkableConnection | null,
  criteria: WorkableRealtimeEventCriteria,
  enabled: boolean,
  maxMessages: number
): RealtimeEventLoadable {
  const [state, setState] = useState<RealtimeEventLoadable>({
    clearMessages: () => undefined,
    connectionState: enabled ? "connecting" : "disabled",
    enabled,
    hubUrl: connection ? createWorkableRealtimeUrl(connection) : null,
    loading: false,
    messages: [],
  });
  const hubConnectionRef = useRef<HubConnection | null>(null);
  const hasConnection = connection !== null;
  const apiUrl = connection?.apiUrl ?? "";
  const hubUrl = connection ? createWorkableRealtimeUrl(connection) : null;
  const systemName = connection?.systemName;
  const criteriaKey = JSON.stringify(criteria);
  const criteriaKeyRef = useRef(criteriaKey);
  const maxMessagesRef = useRef(maxMessages);
  const messageIdRef = useRef(0);
  const systemNameRef = useRef(systemName);

  useEffect(() => {
    criteriaKeyRef.current = criteriaKey;
    maxMessagesRef.current = maxMessages;
    systemNameRef.current = systemName;
  }, [criteriaKey, maxMessages, systemName]);

  const clearMessages = useCallback(() => {
    setState((current) =>
      current.messages.length === 0 ? current : { ...current, messages: [] }
    );
  }, []);

  useEffect(() => {
    queueMicrotask(() => {
      setState((current) =>
        current.messages.length > maxMessages
          ? { ...current, messages: current.messages.slice(0, maxMessages) }
          : current
      );
    });
  }, [maxMessages]);

  useEffect(() => {
    if (!hasConnection || !enabled || !hubUrl) {
      queueMicrotask(() =>
        setState((current) =>
          current.connectionState === "disabled" &&
          current.enabled === enabled &&
          current.hubUrl === hubUrl &&
          !current.loading
            ? current
            : {
                ...current,
                connectionState: "disabled",
                enabled,
                hubUrl,
                loading: false,
              }
        )
      );
      return;
    }

    let canceled = false;
    let retryTimer: ReturnType<typeof setTimeout> | null = null;
    queueMicrotask(() => {
      if (!canceled) {
        setState((current) => ({
          ...current,
          connectionState: "connecting",
          enabled,
          hubUrl,
          loading: current.messages.length === 0,
        }));
      }
    });
    const hubConnection = new HubConnectionBuilder()
      .withUrl(hubUrl, {
        accessTokenFactory: () => getWorkableRealtimeAccessToken(apiUrl),
        withCredentials: true,
      })
      .withAutomaticReconnect()
      .configureLogging(LogLevel.None)
      .build();

    hubConnectionRef.current = hubConnection;
    const subscribe = () =>
      hubConnection.invoke(
        "WatchEvents",
        JSON.parse(criteriaKeyRef.current),
        systemNameRef.current ?? null
      );
    const scheduleRestart = (error: unknown, delayMs = 1000) => {
      if (canceled || retryTimer) {
        return;
      }

      retryTimer = setTimeout(() => {
        retryTimer = null;
        if (!canceled && hubConnection.state === HubConnectionState.Disconnected) {
          startConnection();
        }
      }, delayMs);
      setState((current) => ({
        ...current,
        connectionState: "disconnected",
        error: error && !isExpectedRealtimeDisconnect(error)
          ? getRealtimeErrorMessage(error, "Realtime event connection closed.")
          : undefined,
        loading: false,
      }));
    };
    const startConnection = () => {
      if (canceled || hubConnection.state !== HubConnectionState.Disconnected) {
        return;
      }

      queueMicrotask(() => {
        if (!canceled) {
          setState((current) => ({
            ...current,
            connectionState: current.messages.length === 0 ? "connecting" : "reconnecting",
            loading: current.messages.length === 0,
          }));
        }
      });
      void hubConnection
        .start()
        .then(() => subscribe())
        .then(() => {
          if (!canceled) {
            setState((current) => ({
              ...current,
              connectionState: "connected",
              error: undefined,
              loading: false,
            }));
          }
        })
        .catch((error) => {
          if (!canceled) {
            scheduleRestart(error, isExpectedRealtimeDisconnect(error) ? 1000 : 3000);
          }
        });
    };
    hubConnection.on("workable.event", (workEvent: WorkableRealtimeEvent) => {
      if (!canceled) {
        const message = createRealtimeEventMessage(
          [workEvent],
          `events:${++messageIdRef.current}`,
          Date.now()
        );
        setState((current) => ({
          ...current,
          connectionState: "connected",
          enabled,
          hubUrl,
          loading: false,
          messages: [message, ...current.messages].slice(0, maxMessagesRef.current),
        }));
      }
    });
    hubConnection.on("workable.events", (batch: WorkableRealtimeEventBatch) => {
      if (!canceled) {
        const batchId = `batch:${++messageIdRef.current}`;
        const message = createRealtimeEventMessage(
          batch.events,
          batchId,
          Date.now(),
          batch.sentAt
        );
        setState((current) => ({
          ...current,
          connectionState: "connected",
          enabled,
          hubUrl,
          loading: false,
          messages: [message, ...current.messages].slice(0, maxMessagesRef.current),
        }));
      }
    });
    hubConnection.onreconnecting(() => {
      if (!canceled) {
        setState((current) => ({
          ...current,
          connectionState: "reconnecting",
        }));
      }
    });
    hubConnection.onreconnected(() => {
      if (!canceled) {
        setState((current) => ({
          ...current,
          connectionState: "connected",
        }));
      }
      void hubConnection.invoke(
        "WatchEvents",
        JSON.parse(criteriaKeyRef.current),
        systemNameRef.current ?? null
      ).catch((error) => {
        if (!canceled && !isExpectedRealtimeDisconnect(error)) {
          setState((current) => ({
            ...current,
            connectionState: "error",
            error: getRealtimeErrorMessage(error, "Realtime event subscription failed."),
            loading: false,
          }));
        }
      });
    });
    hubConnection.onclose((error) => {
      if (!canceled) {
        scheduleRestart(error, isExpectedRealtimeDisconnect(error) ? 1000 : 3000);
      }
    });

    startConnection();

    return () => {
      canceled = true;
      if (retryTimer) {
        clearTimeout(retryTimer);
      }
      hubConnectionRef.current = null;
      void hubConnection.stop().catch(() => undefined);
    };
  }, [apiUrl, criteriaKey, enabled, hasConnection, hubUrl, systemName]);

  return { ...state, clearMessages };
}

export function useWorkableRealtimeWorkerEvents(
  connection: WorkableConnection | null,
  workerId: string,
  enabled: boolean,
  maxMessages: number
): RealtimeEventLoadable {
  const [state, setState] = useState<RealtimeEventLoadable>({
    clearMessages: () => undefined,
    connectionState: enabled ? "connecting" : "disabled",
    enabled,
    hubUrl: connection ? createWorkableRealtimeUrl(connection) : null,
    loading: false,
    messages: [],
  });
  const hubConnectionRef = useRef<HubConnection | null>(null);
  const hasConnection = connection !== null;
  const apiUrl = connection?.apiUrl ?? "";
  const hubUrl = connection ? createWorkableRealtimeUrl(connection) : null;
  const systemName = connection?.systemName;
  const workerIdRef = useRef(workerId);
  const maxMessagesRef = useRef(maxMessages);
  const messageIdRef = useRef(0);
  const systemNameRef = useRef(systemName);

  useEffect(() => {
    workerIdRef.current = workerId;
    maxMessagesRef.current = maxMessages;
    systemNameRef.current = systemName;
  }, [maxMessages, systemName, workerId]);

  const clearMessages = useCallback(() => {
    setState((current) =>
      current.messages.length === 0 ? current : { ...current, messages: [] }
    );
  }, []);

  useEffect(() => {
    queueMicrotask(() => {
      setState((current) =>
        current.messages.length > maxMessages
          ? { ...current, messages: current.messages.slice(0, maxMessages) }
          : current
      );
    });
  }, [maxMessages]);

  useEffect(() => {
    if (!hasConnection || !enabled || !hubUrl || !workerId.trim()) {
      queueMicrotask(() =>
        setState((current) =>
          current.connectionState === "disabled" &&
          current.enabled === enabled &&
          current.hubUrl === hubUrl &&
          !current.loading
            ? current
            : {
                ...current,
                connectionState: "disabled",
                enabled,
                hubUrl,
                loading: false,
              }
        )
      );
      return;
    }

    let canceled = false;
    let retryTimer: ReturnType<typeof setTimeout> | null = null;
    queueMicrotask(() => {
      if (!canceled) {
        setState((current) => ({
          ...current,
          connectionState: "connecting",
          enabled,
          hubUrl,
          loading: current.messages.length === 0,
        }));
      }
    });
    const hubConnection = new HubConnectionBuilder()
      .withUrl(hubUrl, {
        accessTokenFactory: () => getWorkableRealtimeAccessToken(apiUrl),
        withCredentials: true,
      })
      .withAutomaticReconnect()
      .configureLogging(LogLevel.None)
      .build();

    hubConnectionRef.current = hubConnection;
    const subscribe = () =>
      hubConnection.invoke(
        "WatchWorker",
        workerIdRef.current,
        systemNameRef.current ?? null
      );
    const scheduleRestart = (error: unknown, delayMs = 1000) => {
      if (canceled || retryTimer) {
        return;
      }

      retryTimer = setTimeout(() => {
        retryTimer = null;
        if (!canceled && hubConnection.state === HubConnectionState.Disconnected) {
          startConnection();
        }
      }, delayMs);
      setState((current) => ({
        ...current,
        connectionState: "disconnected",
        error: error && !isExpectedRealtimeDisconnect(error)
          ? getRealtimeErrorMessage(error, "Realtime worker connection closed.")
          : undefined,
        loading: false,
      }));
    };
    const startConnection = () => {
      if (canceled || hubConnection.state !== HubConnectionState.Disconnected) {
        return;
      }

      queueMicrotask(() => {
        if (!canceled) {
          setState((current) => ({
            ...current,
            connectionState: current.messages.length === 0 ? "connecting" : "reconnecting",
            loading: current.messages.length === 0,
          }));
        }
      });
      void hubConnection
        .start()
        .then(() => subscribe())
        .then(() => {
          if (!canceled) {
            setState((current) => ({
              ...current,
              connectionState: "connected",
              error: undefined,
              loading: false,
            }));
          }
        })
        .catch((error) => {
          if (!canceled) {
            scheduleRestart(error, isExpectedRealtimeDisconnect(error) ? 1000 : 3000);
          }
        });
    };
    hubConnection.on("workable.event", (workEvent: WorkableRealtimeEvent) => {
      if (!canceled) {
        const message = createRealtimeEventMessage(
          [workEvent],
          `worker-events:${++messageIdRef.current}`,
          Date.now()
        );
        setState((current) => ({
          ...current,
          connectionState: "connected",
          enabled,
          hubUrl,
          loading: false,
          messages: [message, ...current.messages].slice(0, maxMessagesRef.current),
        }));
      }
    });
    hubConnection.on("workable.events", (batch: WorkableRealtimeEventBatch) => {
      if (!canceled) {
        const batchId = `worker-batch:${++messageIdRef.current}`;
        const message = createRealtimeEventMessage(
          batch.events,
          batchId,
          Date.now(),
          batch.sentAt
        );
        setState((current) => ({
          ...current,
          connectionState: "connected",
          enabled,
          hubUrl,
          loading: false,
          messages: [message, ...current.messages].slice(0, maxMessagesRef.current),
        }));
      }
    });
    hubConnection.onreconnecting(() => {
      if (!canceled) {
        setState((current) => ({
          ...current,
          connectionState: "reconnecting",
        }));
      }
    });
    hubConnection.onreconnected(() => {
      if (!canceled) {
        setState((current) => ({
          ...current,
          connectionState: "connected",
        }));
      }
      void hubConnection.invoke(
        "WatchWorker",
        workerIdRef.current,
        systemNameRef.current ?? null
      ).catch((error) => {
        if (!canceled && !isExpectedRealtimeDisconnect(error)) {
          setState((current) => ({
            ...current,
            connectionState: "error",
            error: getRealtimeErrorMessage(error, "Realtime worker subscription failed."),
            loading: false,
          }));
        }
      });
    });
    hubConnection.onclose((error) => {
      if (!canceled) {
        scheduleRestart(error, isExpectedRealtimeDisconnect(error) ? 1000 : 3000);
      }
    });

    startConnection();

    return () => {
      canceled = true;
      if (retryTimer) {
        clearTimeout(retryTimer);
      }
      if (hubConnection.state === HubConnectionState.Connected) {
        void hubConnection
          .invoke("UnwatchWorker", workerIdRef.current, systemNameRef.current ?? null)
          .catch(() => undefined);
      }
      hubConnectionRef.current = null;
      void hubConnection.stop().catch(() => undefined);
    };
  }, [apiUrl, enabled, hasConnection, hubUrl, systemName, workerId]);

  return { ...state, clearMessages };
}

function createRealtimeEventMessage(
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

function measureJsonBytes(value: unknown, budget = 250_000) {
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

function createRealtimePayloadMessage<T>(
  result: T,
  payloadJson: string,
  id: string,
  viewName: string,
  subscription: string
): RealtimePayloadMessage {
  const maybeComponents =
    typeof result === "object" && result !== null && "components" in result
      ? (result as { components?: Record<string, WorkComponentResult> }).components
      : undefined;

  return {
    bytes: new TextEncoder().encode(payloadJson).length,
    components: Object.entries(maybeComponents ?? {}).map(([id, component]) => ({
      id,
      shape: component.shape,
      status: component.status,
    })),
    id,
    receivedAt: Date.now(),
    subscription,
    value: result,
    viewName,
  };
}

function getRealtimePayloadComponentData(value: unknown): RealtimePayloadComponentData[] {
  const components =
    typeof value === "object" && value !== null && "components" in value
      ? (value as { components?: Record<string, WorkComponentResult> }).components
      : undefined;

  return Object.entries(components ?? {}).map(([id, component]) => ({
    data: component.data,
    id,
    shape: component.shape,
    status: component.status,
  }));
}

function useWorkablePostResource<T>(
  connection: WorkableConnection,
  path: string | null,
  body: unknown,
  refreshToken: number
): Loadable<T> {
  const [state, setState] = useState<Loadable<T>>({ loading: !!path });
  const apiUrl = connection.apiUrl;
  const systemName = connection.systemName;
  const bodyKey = JSON.stringify(body);
  const requestKey = `${apiUrl}\n${systemName ?? ""}\n${path ?? ""}\n${bodyKey}`;
  const previousRequestKey = useRef<string | null>(null);

  useEffect(() => {
    if (!path) {
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
    previousRequestKey.current = requestKey;
    queueMicrotask(() => {
      if (!canceled) {
        setState((current) => ({
          ...(requestChanged ? {} : current),
          error: undefined,
          loading: requestChanged || current.data === undefined,
          refreshing: !requestChanged && current.data !== undefined,
        }));
      }
    });

    const requestConnection = { apiUrl, systemName };
    workableFetch<T>(requestConnection, path, {
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
  }, [apiUrl, bodyKey, path, refreshToken, requestKey, systemName]);

  return state;
}
