"use client";

import {
  Activity,
  ArrowDownWideNarrow,
  ArrowUpNarrowWide,
  Ban,
  Braces,
  CheckCircle2,
  Clock3,
  Copy,
  Eye,
  Info,
  Loader2,
  Maximize2,
  Minimize2,
  Pause,
  Play,
  RefreshCw,
  Rows4,
  RotateCw,
  Search,
  Send,
  Trash2,
} from "lucide-react";
import type { Dispatch, SetStateAction } from "react";
import { Fragment, useCallback, useEffect, useLayoutEffect, useMemo, useRef, useState } from "react";
import { Badge } from "@/components/ui/badge";
import {
  AlertDialog,
  AlertDialogAction,
  AlertDialogCancel,
  AlertDialogContent,
  AlertDialogDescription,
  AlertDialogFooter,
  AlertDialogHeader,
  AlertDialogTitle,
} from "@/components/ui/alert-dialog";
import {
  Breadcrumb,
  BreadcrumbItem,
  BreadcrumbLink,
  BreadcrumbList,
  BreadcrumbPage,
  BreadcrumbSeparator,
} from "@/components/ui/breadcrumb";
import { Button } from "@/components/ui/button";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card";
import { Dialog, DialogContent, DialogDescription, DialogHeader, DialogTitle } from "@/components/ui/dialog";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { Skeleton } from "@/components/ui/skeleton";
import { Tabs, TabsContent, TabsList, TabsTrigger } from "@/components/ui/tabs";
import { Textarea } from "@/components/ui/textarea";
import { Tooltip, TooltipContent, TooltipTrigger } from "@/components/ui/tooltip";
import {
  consoleBreadcrumbCurrentClassName,
  consoleBreadcrumbDefinitionClassName,
  consoleBreadcrumbLinkClassName,
  consoleBreadcrumbRootItemClassName,
  consoleBreadcrumbTextClassName,
  ConsolePageLayout,
  consolePanelActionGapClassName,
} from "@/components/features/console/console-primitives";
import { PanelAggregateFrame } from "@/components/features/console/panel-aggregate-frame";
import {
  useRegisterConsoleHeaderCapabilities,
  type ConsoleHeaderCapabilities,
} from "@/components/features/console/header-capabilities";
import {
  useConsolePageRealtimeView,
  useRegisterConsolePageRealtimeView,
  type ConsolePageRealtimeViewDescriptor,
} from "@/components/features/console/page-realtime-view";
import { PanelShell } from "@/components/features/console/panel-shell";
import type { PanelVisibilityOption } from "@/components/features/console/panel-visibility-settings";
import { ToolbarIconButton } from "@/components/features/console/toolbar-icon-button";
import type { Loadable, OverviewScope } from "@/components/features/console/types";
import {
  SchemaForm,
  SchemaPathField,
  SchemaPresetButton,
  compactJson,
  createDefaultValue,
  parseJsonSchema,
} from "@/components/workable/schema-form";
import {
  ErrorBanner,
  ErrorPanel,
  FeedbackBanner,
  type FeedbackTone,
} from "@/components/workable/console/feedback-panel";
import {
  formatRelativeTime,
  useLiveRelativeTimeNow,
} from "@/components/workable/console/live-relative-time";
import {
  formatDateTime,
  workableFetch,
  type QueueRequestSchemaDescriptor,
  type QueueWorkRequest,
  type WorkAction,
  type WorkCompletionStatus,
  type WorkComponentQueryResult,
  type WorkComponentShape,
  type WorkConfiguration,
  type WorkData,
  type WorkDefinition,
  type WorkDefinitionReconfigurationOutcome,
  type WorkInfo,
  type WorkMessage,
  type WorkableRealtimeOrigin,
  type WorkableConnection,
  type WorkerActionHistoryEntry,
  type WorkerIterationSnapshot,
  type WorkerLogEntry,
  type WorkerSummary,
  type WorkerOptions,
  type WorkerState,
  type WorkerSnapshot,
} from "@/lib/workable";

type QueueConfigurationField = QueueRequestSchemaDescriptor["tabs"][number]["fields"][number];
type QueueConfigurationTab = QueueRequestSchemaDescriptor["tabs"][number];
type QueueConfigurationFieldSection = {
  id: string;
  label: string;
  description?: string;
  fields: QueueConfigurationField[];
};
type WorkerRetryTimelineState = {
  kind: "state";
  mode: "retry";
  nextRunAt?: string | null;
  retryAttempt?: number | null;
  stateChangedAt?: string | null;
  updatedAt: string;
};
type WorkerFailureDetails = {
  code?: string;
  declaredByWork?: boolean;
  exceptionType?: string;
  innerExceptions?: WorkerFailureException[];
  kind: "exception" | "failure";
  message: string;
  retryPending?: {
    nextRunAt?: string | null;
    retryAttempt?: number | null;
    stateChangedAt?: string | null;
    updatedAt: string;
  };
  stackTrace?: string;
  target?: string;
};
type WorkerFailureException = {
  exceptionType?: string;
  message: string;
  stackTrace?: string;
};
type StackFrameFilterKind = "application" | "library" | "work";
type StackTraceDisplayEntry =
  | {
      kind: "detail" | StackFrameFilterKind;
      line: string;
      type: "line";
    }
  | {
      counts: Record<StackFrameFilterKind, number>;
      total: number;
      type: "collapsed";
    };

const workerExecutionLogStreamLimit = 400;
const stackFrameFilterKinds: StackFrameFilterKind[] = ["application", "work", "library"];
const catalogPanelOptions: PanelVisibilityOption<"catalog">[] = [
  {
    id: "catalog",
    label: "Catalog",
    description: "Definitions in the selected catalog scope, with search and queue actions.",
  },
];
type WorkerDetailPanelId =
  | "workerControls"
  | "workerLogs"
  | "workerDuration"
  | "workerTimeline";

const workerPanelOptions: PanelVisibilityOption<WorkerDetailPanelId>[] = [
  {
    id: "workerControls",
    label: "Worker controls",
    description: "Current worker state, control actions, and input summary.",
  },
  {
    id: "workerLogs",
    label: "Logs",
    description: "Compact log summary or detailed retained worker log stream.",
  },
  {
    id: "workerDuration",
    label: "Recent iterations",
    description: "Recent completed or active iteration durations.",
  },
  {
    id: "workerTimeline",
    label: "Iteration timeline",
    description: "Detailed iteration timeline with filters and status events.",
  },
];

export function DefinitionsView({
  catalogScope,
  connection,
  onCatalogScopeChange,
  onOpenDefinition,
  onOpenWorker,
  onReady,
  refreshToken,
}: {
  catalogScope: OverviewScope | null;
  connection: WorkableConnection;
  onCatalogScopeChange: (scope: OverviewScope | null) => void;
  onOpenDefinition: (definitionId: string, definitionName?: string) => void;
  onOpenWorker: (workerId: string) => void;
  onReady: () => void;
  refreshToken: number;
}) {
  const definitions = useWorkableResource<WorkDefinition[]>(
    connection,
    "definitions",
    refreshToken
  );
  const [search, setSearch] = useState("");
  const [queueDefinition, setQueueDefinition] = useState<WorkDefinition | null>(null);
  const [gridShape, setGridShape] = useState<WorkComponentShape>("detailed");
  const [hiddenPanelIds, setHiddenPanelIds] = useState<ReadonlySet<"catalog">>(() => new Set());
  const autoOpenedDefinitionScope = useRef("");
  const isReady = !definitions.loading;

  useEffect(() => {
    if (isReady) {
      onReady();
    }
  }, [isReady, onReady]);

  const filtered = useMemo(() => {
    const query = search.trim().toLowerCase();
    return (definitions.data ?? [])
      .filter((definition) => definitionMatchesCatalogScope(definition, catalogScope))
      .filter((definition) =>
        !query ||
        [definition.name, definition.category, definition.description]
        .filter(Boolean)
        .some((value) => String(value).toLowerCase().includes(query))
      );
  }, [catalogScope, definitions.data, search]);

  useEffect(() => {
    if (!catalogScope?.definitionName) {
      autoOpenedDefinitionScope.current = "";
    }
    const autoOpenKey =
      catalogScope?.definitionName && filtered.length === 1
        ? `${catalogScope.definitionName}:${filtered[0].id.value}`
        : "";
    if (
      !definitions.loading &&
      catalogScope?.definitionName &&
      filtered.length === 1 &&
      autoOpenedDefinitionScope.current !== autoOpenKey
    ) {
      autoOpenedDefinitionScope.current = autoOpenKey;
      onOpenDefinition(filtered[0].id.value, filtered[0].name);
    }
  }, [catalogScope?.definitionName, definitions.loading, filtered, onOpenDefinition]);

  const setCatalogPanelVisible = useCallback((panelId: "catalog", visible: boolean) => {
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

  const resetCatalogUiToDefaults = useCallback(() => {
    setHiddenPanelIds(new Set());
    setGridShape("detailed");
  }, []);

  const isCatalogPanelVisible = !hiddenPanelIds.has("catalog");

  return (
    <ConsolePageLayout>
      <ErrorPanel errors={[definitions.error]} />
      <PanelAggregateFrame
        hiddenPanelIds={[...hiddenPanelIds]}
        onPanelVisibilityChange={setCatalogPanelVisible}
        onResetUi={resetCatalogUiToDefaults}
        padding="tightTop"
        panelOptions={catalogPanelOptions}
        settingsButtonLabel="Catalog panel settings"
        settingsDescription="Checked panels are shown on the catalog page."
        settingsTitle="Catalog panels"
      >
        {isCatalogPanelVisible ? (
          <PanelShell
            onClose={() => setCatalogPanelVisible("catalog", false)}
            onViewStateChange={setGridShape}
            supportedViewStates={["detailed"]}
            title="Catalog"
            viewState={gridShape}
          >
            <div className="space-y-4">
              <div className="gap-4 md:flex md:items-center md:justify-between">
                <div className="min-w-0 flex-1">
                  <ScopeTrail
                    onClear={() => onCatalogScopeChange(null)}
                    onSelectCategory={(category) => onCatalogScopeChange({
                      category,
                      includeSubcategories: true,
                    })}
                    scope={catalogScope}
                  />
                </div>
                <div className="relative mt-4 w-full md:mt-0 md:w-80">
                  <Search className="absolute left-3 top-2.5 size-4 text-muted-foreground" />
                  <Input
                    className="pl-9"
                    onChange={(event) => setSearch(event.target.value)}
                    placeholder="Search catalog"
                    value={search}
                  />
                </div>
              </div>
              {definitions.loading ? (
                <StackedSkeleton count={8} />
              ) : (
                <div className="grid gap-3 md:grid-cols-2">
                  {filtered.map((definition) => (
                    <div
                      className="rounded-lg border bg-card p-4"
                      key={definition.id.value}
                    >
                      <div className="flex items-start justify-between gap-4">
                        <div className="min-w-0">
                          <div className="truncate font-mono text-sm">{definition.name}</div>
                          <div className="mt-1 text-muted-foreground text-sm">
                            {definition.description ?? "No description"}
                          </div>
                        </div>
                        <Badge variant="secondary">{definition.category ?? "Uncategorized"}</Badge>
                      </div>
                      <div className="mt-4 flex justify-end gap-2">
                        <Button
                          onClick={() => onOpenDefinition(definition.id.value, definition.name)}
                          size="sm"
                          variant="outline"
                        >
                          <Info className="size-4" />
                          Definition
                        </Button>
                        <Button onClick={() => setQueueDefinition(definition)} size="sm">
                          <Send className="size-4" />
                          Queue
                        </Button>
                      </div>
                    </div>
                  ))}
                </div>
              )}
            </div>
          </PanelShell>
        ) : null}
      </PanelAggregateFrame>
      <QueueDialog
        connection={connection}
        definition={queueDefinition}
        onQueuedWorker={onOpenWorker}
        onOpenChange={(open) => !open && setQueueDefinition(null)}
      />
    </ConsolePageLayout>
  );
}

export function DefinitionView({
  connection,
  definitionId,
  onDefinitionResolved,
  onOpenWorker,
  onReady,
  refreshToken,
}: {
  connection: WorkableConnection;
  definitionId: string;
  onDefinitionResolved: (definitionName: string | null) => void;
  onOpenWorker: (workerId: string) => void;
  onReady: () => void;
  refreshToken: number;
}) {
  const info = useWorkableResource<WorkInfo>(
    connection,
    `definitions/${definitionId}/info`,
    refreshToken
  );
  const [queueDefinition, setQueueDefinition] = useState<WorkDefinition | null>(null);
  const [definitionRequest, setDefinitionRequest] = useState<QueueWorkRequest>(() =>
    createDefaultQueueRequest(null)
  );
  const [queueSchemaDescriptor, setQueueSchemaDescriptor] =
    useState<QueueRequestSchemaDescriptor | null>(null);
  const [saveError, setSaveError] = useState<string>();
  const [saveStatus, setSaveStatus] = useState<string>();
  const [isSaving, setIsSaving] = useState(false);
  const [updatedDefinition, setUpdatedDefinition] = useState<WorkDefinition | null>(null);
  const definition = updatedDefinition ?? info.data?.definition;
  const isReady = !info.loading;
  const queueRequestSchema = useMemo(
    () => parseJsonSchema(queueSchemaDescriptor?.schema?.jsonSchema),
    [queueSchemaDescriptor?.schema?.jsonSchema]
  );
  const definitionConfigurationDescriptor = useMemo(
    () => createDefinitionConfigurationDescriptor(queueSchemaDescriptor),
    [queueSchemaDescriptor]
  );

  useEffect(() => {
    if (isReady) {
      onReady();
    }
  }, [isReady, onReady]);

  useEffect(() => {
    if (!definition) {
      return;
    }

    const nextRequest = createDefaultQueueRequest(definition);
    queueMicrotask(() => {
      setDefinitionRequest(nextRequest);
      setSaveError(undefined);
      setSaveStatus(undefined);
    });
  }, [definition]);

  useEffect(() => {
    if (updatedDefinition?.id.value === definitionId) {
      onDefinitionResolved(updatedDefinition.name);
      return;
    }

    if (info.data?.definition?.id.value === definitionId) {
      onDefinitionResolved(info.data.definition.name);
      return;
    }

    if (!info.loading && !info.refreshing) {
      onDefinitionResolved(null);
    }
  }, [
    definitionId,
    info.data?.definition,
    info.loading,
    info.refreshing,
    onDefinitionResolved,
    updatedDefinition,
  ]);

  useEffect(() => {
    queueMicrotask(() => setUpdatedDefinition(null));
  }, [definitionId, info.data?.definition]);

  useEffect(() => {
    let canceled = false;

    workableFetch<QueueRequestSchemaDescriptor>(connection, "queue-request/schema")
      .then((descriptor) => {
        if (!canceled) {
          setQueueSchemaDescriptor(descriptor);
        }
      })
      .catch(() => {
        if (!canceled) {
          setQueueSchemaDescriptor(null);
        }
      });

    return () => {
      canceled = true;
    };
  }, [connection]);

  const saveConfiguration = async () => {
    if (!definition) {
      return;
    }

    setIsSaving(true);
    setSaveError(undefined);
    setSaveStatus(undefined);

    try {
      const configuration = definitionRequest.options?.configuration
        ? stripInvocationConfiguration(definitionRequest.options.configuration)
        : stripInvocationConfiguration(cloneConfiguration(defaultWorkConfiguration));
      const defaultOptions: WorkerOptions = {
        ...definition.defaultOptions,
        profilingEnabled: definitionRequest.options?.profilingEnabled ?? false,
        configuration: definition.defaultOptions?.configuration ?? null,
      };
      const result = await workableFetch<WorkDefinitionReconfigurationOutcome>(
        connection,
        `definitions/${definition.id.value}/reconfigure`,
        {
          method: "POST",
          body: JSON.stringify({
            revision: definition.revision,
            changes: {
              configuration,
              defaultOptions,
            },
          }),
        }
      );

      if (result.definition) {
        setUpdatedDefinition(result.definition);
      }
      setSaveStatus(`Definition configuration ${result.status.toLowerCase()}.`);
    } catch (caught) {
      setSaveError(caught instanceof Error ? caught.message : "Configuration update failed.");
    } finally {
      setIsSaving(false);
    }
  };

  return (
    <ConsolePageLayout reserveToolbar>
      <ErrorPanel errors={[info.error, saveError]} />
      {info.loading ? (
        <StackedSkeleton count={6} />
      ) : !definition ? (
        <div className="rounded-lg border border-dashed p-8 text-center text-muted-foreground text-sm">
          Definition not found.
        </div>
      ) : (
        <>
          {saveStatus && (
            <FeedbackBanner
              key={saveStatus}
              message={saveStatus}
              title="Configuration saved"
              tone="success"
            />
          )}
          <Card>
            <CardHeader className="gap-4 md:flex-row md:items-start md:justify-between">
              <div className="min-w-0">
                <CardTitle className="truncate font-mono text-base">
                  {definition.name}
                </CardTitle>
                <CardDescription className="mt-1">
                  {definition.description ?? "No description"}
                </CardDescription>
                <div className="mt-3 flex flex-wrap gap-2">
                  <Badge variant="secondary">{definition.category ?? "Uncategorized"}</Badge>
                  <Badge variant="outline">Revision {definition.revision}</Badge>
                  <Badge variant="outline">{info.data?.status ?? "Unknown"}</Badge>
                </div>
              </div>
              <Button onClick={() => setQueueDefinition(definition)}>
                <Send className="size-4" />
                Queue
              </Button>
            </CardHeader>
            <CardContent>
              <div className="grid gap-3 md:grid-cols-4">
                <MetadataItem label="Total workers" value={String(info.data?.workers.total ?? 0)} />
                <MetadataItem label="Active" value={String(info.data?.workers.active ?? 0)} />
                <MetadataItem label="Failed" value={String(info.data?.workers.failed ?? 0)} />
                <MetadataItem
                  label="Last activity"
                  value={formatDateTime(info.data?.workers.lastActivityAt)}
                />
              </div>
            </CardContent>
          </Card>
          <Card>
            <CardHeader className="gap-3 md:flex-row md:items-center md:justify-between">
              <div>
                <CardTitle className="text-base">Configuration</CardTitle>
                <CardDescription>
                  Changes apply to future workers queued from this definition.
                </CardDescription>
              </div>
              <Button disabled={isSaving} onClick={() => void saveConfiguration()} size="sm">
                {isSaving ? (
                  <Loader2 className="size-4 animate-spin" />
                ) : (
                  <CheckCircle2 className="size-4" />
                )}
                Save
              </Button>
            </CardHeader>
            <CardContent>
              <div className="space-y-6">
                <QueueConfigurationTabs
                  descriptor={definitionConfigurationDescriptor}
                  onRequestChange={setDefinitionRequest}
                  request={definitionRequest}
                  schema={queueRequestSchema}
                />
                <div className="grid gap-4 lg:grid-cols-2">
                  <SnapshotBlock
                    label="Input schema"
                    value={parseSchemaJsonValue(definition.inputSchema?.jsonSchema)}
                  />
                  <SnapshotBlock
                    label="Output schema"
                    value={parseSchemaJsonValue(definition.outputSchema?.jsonSchema)}
                  />
                  <SnapshotBlock label="Metadata" value={definition.metadata} />
                </div>
              </div>
            </CardContent>
          </Card>
          <QueueDialog
            connection={connection}
            definition={queueDefinition}
            onQueuedWorker={onOpenWorker}
            onOpenChange={(open) => !open && setQueueDefinition(null)}
          />
        </>
      )}
    </ConsolePageLayout>
  );
}

export function WorkerConsoleView({
  connection,
  onActiveRealtimeConnectionCountChange,
  onNavigateBack,
  onOpenWorker,
  onRealtimePayloadOpenChange,
  refreshToken,
  realtimePayloadCaptureEnabled,
  realtimePayloadMaxMessages,
  realtimePayloadOpen,
  workerId,
}: {
  connection: WorkableConnection;
  onActiveRealtimeConnectionCountChange: (count: number) => void;
  onNavigateBack: () => void;
  onOpenWorker: (workerId: string) => void;
  onRealtimePayloadOpenChange: (open: boolean) => void;
  refreshToken: number;
  realtimePayloadCaptureEnabled: boolean;
  realtimePayloadMaxMessages: number;
  realtimePayloadOpen: boolean;
  workerId: string;
}) {
  const [actionFeedback, setActionFeedback] = useState<{
    message: string;
    tone: FeedbackTone;
  }>();
  const [pendingAction, setPendingAction] = useState<WorkAction | null>(null);
  const [actionRefreshToken, setActionRefreshToken] = useState(0);
  const [realtimeRefreshToken, setRealtimeRefreshToken] = useState(0);
  const [executionLogState, setExecutionLogState] = useState<{
    entries: WorkerLogEntry[];
    workerId: string;
  }>({
    entries: [],
    workerId,
  });
  const [copyQueueDialog, setCopyQueueDialog] = useState<{
    definition: WorkDefinition;
    formValue: unknown;
    request: QueueWorkRequest;
  } | null>(null);
  const [openingCopyQueue, setOpeningCopyQueue] = useState(false);
  const [hiddenPanelIds, setHiddenPanelIds] = useState<ReadonlySet<WorkerDetailPanelId>>(() => new Set());
  const [workerControlsPanelViewState, setWorkerControlsPanelViewState] = useState<WorkComponentShape>("compact");
  const [workerLogsPanelViewState, setWorkerLogsPanelViewState] = useState<WorkComponentShape>("compact");
  const [workerDurationPanelViewState, setWorkerDurationPanelViewState] = useState<WorkComponentShape>("standard");
  const [workerTimelinePanelViewState, setWorkerTimelinePanelViewState] = useState<WorkComponentShape>("detailed");
  const initializedWorkerPanelsRef = useRef<string | null>(null);
  const realtimeEnabled = Boolean(connection.realtimeHubPath);
  const workerViewRequest = useMemo(
    () => ({
      components: [
        {
          id: "worker",
          options: { workerId },
          shape: "detailed",
          type: "workerDetail",
        },
        {
          id: "currentIteration",
          options: { workerId },
          shape: "detailed",
          type: "workerCurrentIteration",
        },
      ],
    }),
    [workerId]
  );
  const realtimeWorkerDescriptor = useMemo<ConsolePageRealtimeViewDescriptor>(
    () => ({
      body: workerViewRequest,
      captureEnabled: realtimePayloadCaptureEnabled && realtimePayloadOpen,
      connection,
      enabled: realtimeEnabled && workerId.trim().length > 0,
      maxMessages: realtimePayloadMaxMessages,
      subscription: `worker:${workerId}`,
      viewName: "worker",
    }),
    [
      connection,
      realtimeEnabled,
      realtimePayloadCaptureEnabled,
      realtimePayloadMaxMessages,
      realtimePayloadOpen,
      workerId,
      workerViewRequest,
    ]
  );
  useRegisterConsolePageRealtimeView({
    active: true,
    descriptor: realtimeWorkerDescriptor,
    id: "worker-console",
  });
  const realtimeWorker = useConsolePageRealtimeView<WorkComponentQueryResult>("worker-console");
  const snapshot = useWorkableResource<WorkerSnapshot>(
    connection,
    `workers/${workerId}`,
    refreshToken + actionRefreshToken + realtimeRefreshToken
  );
  const realtimeWorkerData = realtimeWorker.data;
  const worker = getWorkComponentData<WorkerSnapshot>(realtimeWorkerData, "worker") ?? snapshot.data;
  const liveCurrentIteration = getWorkComponentData<WorkerIterationSnapshot>(
    realtimeWorkerData,
    "currentIteration"
  );
  const currentIterationSequence = worker?.currentIterationSequence ?? null;
  const currentIterationSnapshot = useWorkableResource<WorkerIterationSnapshot>(
    connection,
    currentIterationSequence !== null
      ? `workers/${workerId}/iterations/${currentIterationSequence}`
      : null,
    refreshToken + actionRefreshToken + realtimeRefreshToken,
    { retainDataOnNull: true, resetKey: workerId }
  );
  const relativeNow = useLiveRelativeTimeNow();
  const retainedIterationSequences = useMemo(
    () => new Set((worker?.iterations ?? []).map((iteration) => iteration.sequence)),
    [worker?.iterations]
  );
  const currentIterationData = liveCurrentIteration ?? currentIterationSnapshot.data ?? null;
  const currentIteration = currentIterationSequence !== null
    ? currentIterationData
    : null;
  const timelineTransitionIteration = currentIterationSequence === null &&
      currentIterationData &&
      !retainedIterationSequences.has(currentIterationData.sequence)
    ? currentIterationData
    : null;
  const activeIteration = currentIteration ?? getActiveIteration(worker?.iterations);
  const timelineIteration = activeIteration ?? timelineTransitionIteration;
  const latestIteration = getLatestIteration(worker?.iterations);
  const primaryIteration = activeIteration ?? latestIteration ?? null;
  const hasActiveIteration = activeIteration !== null;
  const executionLogs = useMemo(
    () => executionLogState.workerId === workerId
      ? executionLogState.entries
      : [],
    [
      executionLogState.entries,
      executionLogState.workerId,
      workerId,
    ]
  );
  const defaultsToTimeline = worker ? shouldDefaultToTimeline(worker) : false;
  const retryTimelineState = useMemo<WorkerRetryTimelineState | null>(
    () => {
      if (worker?.state === "Retrying") {
        return {
          kind: "state" as const,
          mode: "retry" as const,
          nextRunAt: worker.nextRunAt ?? null,
          retryAttempt: worker.retryAttempt ?? null,
          stateChangedAt: worker.stateChangedAt,
          updatedAt: worker.updatedAt,
        };
      }

      return null;
    },
    [worker]
  );
  const liveTimelineStatusItem = useMemo(
    () => worker
      ? createWorkerTimelineLiveStatusItem(worker, timelineIteration, relativeNow)
      : null,
    [relativeNow, timelineIteration, worker]
  );
  const [historicalTimelineStatusState, setHistoricalTimelineStatusState] = useState<{
    items: WorkerTimelineItem[];
    workerId: string;
  }>({
    items: [],
    workerId,
  });
  const previousLiveTimelineStatusItemRef = useRef<WorkerTimelineItem | null>(null);
  const historicalTimelineStatusItems = useMemo(
    () => historicalTimelineStatusState.workerId === workerId
      ? historicalTimelineStatusState.items
      : [],
    [historicalTimelineStatusState, workerId]
  );
  const timelineItems = useMemo(
    () => worker
      ? createWorkerTimelineItems(
        worker,
        timelineIteration,
        relativeNow,
        historicalTimelineStatusItems,
        retryTimelineState,
        liveTimelineStatusItem
      )
      : [],
    [
      historicalTimelineStatusItems,
      liveTimelineStatusItem,
      relativeNow,
      retryTimelineState,
      timelineIteration,
      worker,
    ]
  );
  const retainedLogEntries = useMemo(
    () => mergeWorkerLogEntries(
      primaryIteration?.logs ?? [],
      currentIteration?.logs ?? []
    ),
    [currentIteration?.logs, primaryIteration?.logs]
  );
  const timelineIterations = useMemo(
    () => getTimelineIterations(worker?.iterations, timelineIteration),
    [timelineIteration, worker?.iterations]
  );
  const terminalFailure = worker?.state === "Failed"
    ? getWorkerFailureDetails(worker, latestIteration)
    : null;
  const availableActions = worker
    ? getAvailableWorkerActions(worker.state)
    : emptyAvailableWorkerActions;
  const toggleRealtimePayloadOpen = useCallback(() => {
    onRealtimePayloadOpenChange(!realtimePayloadOpen);
  }, [onRealtimePayloadOpenChange, realtimePayloadOpen]);
  const refreshWorkerSnapshot = useCallback(() => {
    setRealtimeRefreshToken((value) => value + 1);
  }, []);
  const headerCapabilities = useMemo<ConsoleHeaderCapabilities>(
    () => ({
      realtime: {
        connectionState: realtimeWorker.connectionState,
        enabled: realtimeWorker.enabled,
        menuItems: [
          {
            active: realtimePayloadOpen,
            icon: <Rows4 className="size-4" />,
            id: "worker-realtime-payloads",
            label: "Realtime payloads",
            onSelect: toggleRealtimePayloadOpen,
          },
        ],
      },
      refresh: {
        disabled: snapshot.loading || snapshot.refreshing === true ||
          (realtimeWorker.enabled && realtimeWorker.connectionState === "connected"),
        onRefresh: refreshWorkerSnapshot,
        refreshing: snapshot.refreshing === true || realtimeWorker.refreshing === true,
        title: "Refresh worker logs and snapshot",
      },
    }),
    [
      realtimePayloadOpen,
      realtimeWorker.connectionState,
      realtimeWorker.enabled,
      realtimeWorker.refreshing,
      refreshWorkerSnapshot,
      snapshot.loading,
      snapshot.refreshing,
      toggleRealtimePayloadOpen,
    ]
  );
  const openCopyQueueDialog = async () => {
    if (!worker) {
      return;
    }

    setOpeningCopyQueue(true);
    try {
      const info = await workableFetch<WorkInfo>(
        connection,
        `definitions/${worker.definitionId.value}/info`
      );
      setCopyQueueDialog({
        definition: info.definition,
        formValue: cloneJsonValue(parseSchemaJsonValue(worker.input?.json)),
        request: createCopiedWorkerQueueRequest(worker),
      });
    } catch (caught) {
      setActionFeedback({
        message: caught instanceof Error ? caught.message : "Could not load queue settings.",
        tone: "error",
      });
    } finally {
      setOpeningCopyQueue(false);
    }
  };

  useEffect(() => {
    previousLiveTimelineStatusItemRef.current = null;
  }, [workerId]);

  useLayoutEffect(() => {
    const previous = previousLiveTimelineStatusItemRef.current;
    if (previous && previous.id !== liveTimelineStatusItem?.id && shouldPersistLiveTimelineItem(previous)) {
      const frozen = freezeWorkerTimelineItem(previous, relativeNow);
      setHistoricalTimelineStatusState((current) => ({
        items: upsertTimelineStatusHistoryItem(
          current.workerId === workerId ? current.items : [],
          frozen
        ),
        workerId,
      }));
    }

    previousLiveTimelineStatusItemRef.current = liveTimelineStatusItem;
  }, [liveTimelineStatusItem, relativeNow, workerId]);

  useEffect(() => {
    if (!retainedLogEntries.length) {
      return;
    }

    queueMicrotask(() => {
      setExecutionLogState((current) => ({
        entries: mergeWorkerLogEntries(
          current.workerId === workerId
            ? current.entries
            : [],
          retainedLogEntries,
          workerExecutionLogStreamLimit
        ),
        workerId,
      }));
    });
  }, [retainedLogEntries, workerId]);

  useEffect(() => {
    if (actionFeedback?.tone !== "success") {
      return;
    }

    const timer = setTimeout(() => {
      setActionFeedback((current) =>
        current?.message === actionFeedback.message && current.tone === "success"
          ? undefined
          : current
      );
    }, 2000);

    return () => clearTimeout(timer);
  }, [actionFeedback]);
  useEffect(() => {
    onActiveRealtimeConnectionCountChange(
      realtimeWorker.enabled && realtimeWorker.connectionState !== "disabled" ? 1 : 0
    );

    return () => onActiveRealtimeConnectionCountChange(0);
  }, [
    onActiveRealtimeConnectionCountChange,
    realtimeWorker.connectionState,
    realtimeWorker.enabled,
  ]);
  useRegisterConsoleHeaderCapabilities({
    active: true,
    capabilities: headerCapabilities,
    id: "worker-console",
  });

  const executeAction = async (action: WorkAction) => {
    const current = worker;
    if (!current) {
      return;
    }

    setPendingAction(action);
    try {
      const result = await workableFetch<{ status: string; messages?: { text: string }[] }>(
        connection,
        `workers/${current.id.value}/actions/${action.toLowerCase()}`,
        {
          method: "POST",
          body: JSON.stringify({ revision: current.revision }),
        }
      );
      const message = result.messages?.map((message) => message.text).filter(Boolean).join(" ") ||
        `${action} returned ${result.status}.`;
      if (action === "Purge" && result.status === "Accepted") {
        onNavigateBack();
        return;
      }
      setActionFeedback({
        message,
        tone: result.status === "Accepted" ? "success" : "warning",
      });
      setActionRefreshToken((value) => value + 1);
    } catch (error) {
      setActionFeedback({
        message: error instanceof Error ? error.message : `Unable to ${action.toLowerCase()} worker.`,
        tone: "warning",
      });
    } finally {
      setPendingAction(null);
    }
  };

  const setWorkerPanelVisible = useCallback((panelId: WorkerDetailPanelId, visible: boolean) => {
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

  useEffect(() => {
    if (!worker || initializedWorkerPanelsRef.current === workerId) {
      return;
    }

    setHiddenPanelIds(createDefaultWorkerHiddenPanels());
    setWorkerControlsPanelViewState("compact");
    setWorkerLogsPanelViewState(defaultsToTimeline ? "compact" : "detailed");
    setWorkerDurationPanelViewState("standard");
    setWorkerTimelinePanelViewState(defaultsToTimeline ? "detailed" : "compact");
    initializedWorkerPanelsRef.current = workerId;
  }, [defaultsToTimeline, worker, workerId]);

  const resetWorkerUiToDefaults = useCallback(() => {
    setHiddenPanelIds(createDefaultWorkerHiddenPanels());
    setWorkerControlsPanelViewState("compact");
    setWorkerLogsPanelViewState(defaultsToTimeline ? "compact" : "detailed");
    setWorkerDurationPanelViewState("standard");
    setWorkerTimelinePanelViewState(defaultsToTimeline ? "detailed" : "compact");
  }, [defaultsToTimeline]);

  return (
    <ConsolePageLayout>
      <PanelAggregateFrame
        hiddenPanelIds={[...hiddenPanelIds]}
        onPanelVisibilityChange={setWorkerPanelVisible}
        onResetUi={resetWorkerUiToDefaults}
        padding="tightTop"
        panelOptions={workerPanelOptions}
        settingsButtonLabel="Worker panel settings"
        settingsDescription="Checked panels are shown on the worker details page."
        settingsTitle="Worker panels"
      >
        {snapshot.loading && <StackedSkeleton count={8} />}
        {snapshot.error && !worker && (
          <ErrorBanner key={snapshot.error} message={snapshot.error} title="Unable to load worker" />
        )}
        {worker && (
          <div className="relative flex min-h-0 flex-1 flex-col gap-6">
            {actionFeedback?.tone === "success" && (
              <div className="pointer-events-none absolute bottom-4 right-4 z-10 w-full max-w-md">
                <div className="pointer-events-auto">
                  <FeedbackBanner
                    key={actionFeedback.message}
                    message={actionFeedback.message}
                    onDismiss={() => setActionFeedback(undefined)}
                    title="Action result"
                    tone={actionFeedback.tone}
                  />
                </div>
              </div>
            )}
            {!hiddenPanelIds.has("workerControls") ? (
              <PanelShell
                leadingActions={(
                  <div className={`flex flex-wrap items-center ${consolePanelActionGapClassName}`}>
                    <WorkerActionButton
                      action="Start"
                      disabled={pendingAction !== null || !availableActions.Start}
                      icon={Play}
                      onAction={executeAction}
                    />
                    <WorkerActionButton
                      action="Pause"
                      disabled={pendingAction !== null || !availableActions.Pause}
                      icon={Pause}
                      onAction={executeAction}
                    />
                    <WorkerActionButton
                      action="Cancel"
                      cancellationMayStopExecution={worker.state !== "Paused" && worker.state !== "Failed"}
                      disabled={pendingAction !== null || !availableActions.Cancel}
                      icon={Ban}
                      onAction={executeAction}
                    />
                    <WorkerActionButton
                      action="Push"
                      disabled={pendingAction !== null || !availableActions.Push}
                      icon={Clock3}
                      onAction={executeAction}
                      tooltip="Request the next scheduled run immediately."
                    />
                    <Tooltip delayDuration={250}>
                      <TooltipTrigger asChild>
                        <Button
                          className={workerActionToneClassName("Start", pendingAction !== null || openingCopyQueue || !worker)}
                          disabled={pendingAction !== null || openingCopyQueue || !worker}
                          onClick={() => void openCopyQueueDialog()}
                          size="sm"
                          variant="outline"
                        >
                          {openingCopyQueue ? <Loader2 className="size-4 animate-spin" /> : <Copy className="size-4" />}
                          New
                        </Button>
                      </TooltipTrigger>
                      <TooltipContent side="top" sideOffset={6}>
                        Queue a new worker using this worker&apos;s current input and runtime settings.
                      </TooltipContent>
                    </Tooltip>
                    <WorkerActionButton
                      action="Purge"
                      disabled={pendingAction !== null || !availableActions.Purge}
                      icon={Trash2}
                      onAction={executeAction}
                      tooltip="Remove this completed or canceled worker from retained history."
                    />
                  </div>
                )}
                contentClassName={workerControlsPanelViewState === "compact" ? "hidden" : "space-y-4"}
                onClose={() => setWorkerPanelVisible("workerControls", false)}
                onViewStateChange={setWorkerControlsPanelViewState}
                supportedViewStates={["compact", "standard"]}
                title={<WorkerStatusBadge now={relativeNow} worker={worker} />}
                viewState={workerControlsPanelViewState}
              >
                <div className="grid gap-3 md:grid-cols-2 xl:grid-cols-4">
                  <MetadataItem label="Created on" value={formatDateTime(worker.createdAt)} />
                  <MetadataItem label="Created by" value={getWorkerCreatedByLabel(worker)} />
                  <MetadataItem label="Type" value={worker.input?.clrType ?? worker.input?.contentType ?? "Unknown"} />
                  <MetadataItem label="Definition" value={worker.definitionName} />
                </div>
                <WorkDataCard data={worker.input} label="Input" />
              </PanelShell>
            ) : null}
            {terminalFailure ? (
              <WorkerFailureBanner
                key={`${worker.id.value}:${worker.stateSequence}:failed`}
                details={terminalFailure}
                now={relativeNow}
              />
            ) : null}
            {snapshot.error && (
              <ErrorBanner key={snapshot.error} message={snapshot.error} title="Unable to load worker" />
            )}
            {actionFeedback && actionFeedback.tone !== "success" && (
              <FeedbackBanner
                key={actionFeedback.message}
                message={actionFeedback.message}
                onDismiss={() => setActionFeedback(undefined)}
                title="Action result"
                tone={actionFeedback.tone}
              />
            )}
            {!hiddenPanelIds.has("workerDuration") ? (
              <PanelShell
                onClose={() => setWorkerPanelVisible("workerDuration", false)}
                onViewStateChange={setWorkerDurationPanelViewState}
                supportedViewStates={["standard"]}
                title="Recent Iterations"
                viewState={workerDurationPanelViewState}
              >
                <IterationDurationGraph iterations={timelineIterations} now={relativeNow} />
                {timelineIterations.length <= 1 ? (
                  <EmptyListState message="At least two iteration points are needed to draw the duration chart." />
                ) : null}
              </PanelShell>
            ) : null}
            {!hiddenPanelIds.has("workerLogs") ? (
              <WorkerLogPanel
                connectionError={realtimeWorker.error}
                entries={executionLogs}
                hasActiveIteration={hasActiveIteration}
                onClose={() => setWorkerPanelVisible("workerLogs", false)}
                onViewStateChange={setWorkerLogsPanelViewState}
                viewState={workerLogsPanelViewState}
              />
            ) : null}

            {!hiddenPanelIds.has("workerTimeline") ? (
              <WorkerTimelinePanel
                items={timelineItems}
                now={relativeNow}
                onClose={() => setWorkerPanelVisible("workerTimeline", false)}
                onViewStateChange={setWorkerTimelinePanelViewState}
                viewState={workerTimelinePanelViewState}
              />
            ) : null}
          </div>
        )}
      </PanelAggregateFrame>
      <QueueDialog
        connection={connection}
        definition={copyQueueDialog?.definition ?? null}
        initialFormValue={copyQueueDialog?.formValue}
        initialRequest={copyQueueDialog?.request}
        onOpenChange={(open) => !open && setCopyQueueDialog(null)}
        onQueuedWorker={onOpenWorker}
      />
    </ConsolePageLayout>
  );
}

export function QueueDialog({
  connection,
  definition,
  initialFormValue,
  initialRequest,
  onQueuedWorker,
  onOpenChange,
}: {
  connection: WorkableConnection;
  definition: WorkDefinition | null;
  initialFormValue?: unknown;
  initialRequest?: QueueWorkRequest | null;
  onQueuedWorker: (workerId: string) => void;
  onOpenChange: (open: boolean) => void;
}) {
  const inputSchema = useMemo(
    () => parseJsonSchema(definition?.inputSchema?.jsonSchema),
    [definition?.inputSchema?.jsonSchema]
  );
  const [activeTab, setActiveTab] = useState<"input" | "config" | "manual">("input");
  const [formValue, setFormValue] = useState<unknown>(undefined);
  const [manualRequestJson, setManualRequestJson] = useState("{}");
  const [queueRequest, setQueueRequest] = useState<QueueWorkRequest>(() =>
    createDefaultQueueRequest(null)
  );
  const [queueSchemaDescriptor, setQueueSchemaDescriptor] =
    useState<QueueRequestSchemaDescriptor | null>(null);
  const [isQueueing, setIsQueueing] = useState(false);
  const [status, setStatus] = useState<string | undefined>();
  const [error, setError] = useState<string | undefined>();
  const queueRequestSchema = useMemo(
    () => parseJsonSchema(queueSchemaDescriptor?.schema?.jsonSchema),
    [queueSchemaDescriptor?.schema?.jsonSchema]
  );

  useEffect(() => {
    if (!definition) {
      queueMicrotask(() => setQueueSchemaDescriptor(null));
      return;
    }

    let canceled = false;

    workableFetch<QueueRequestSchemaDescriptor>(connection, "queue-request/schema")
      .then((descriptor) => {
        if (!canceled) {
          setQueueSchemaDescriptor(descriptor);
        }
      })
      .catch(() => {
        if (!canceled) {
          setQueueSchemaDescriptor(null);
        }
      });

    return () => {
      canceled = true;
    };
  }, [connection, definition]);

  useEffect(() => {
    const nextValue = initialFormValue === undefined
      ? createDefaultValue(inputSchema)
      : cloneJsonValue(initialFormValue);
    const nextRequest = createQueueDialogRequest(definition, initialRequest);
    queueMicrotask(() => {
      setActiveTab(inputSchema ? "input" : "manual");
      setFormValue(nextValue);
      setManualRequestJson(compactJson({
        ...nextRequest,
        input: nextValue,
      }));
      setQueueRequest(nextRequest);
      setIsQueueing(false);
      setStatus(undefined);
      setError(undefined);
    });
  }, [definition, initialFormValue, initialRequest, inputSchema]);

  const updateFormValue = (nextValue: unknown) => {
    setFormValue(nextValue);
  };

  const resetQueueConfiguration = () => {
    setQueueRequest((current) => ({
      ...current,
      options: createEffectiveConfigurationOptions(definition),
    }));
  };

  const createComposedRequest = () => {
    const request: QueueWorkRequest = {
      ...queueRequest,
      input: formValue,
    };

    return sanitizeQueueWorkRequest(request);
  };

  const queue = async (postQueue: "open-worker" | "stay-on-screen") => {
    if (!definition) {
      return;
    }

    setError(undefined);
    setStatus(undefined);
    setIsQueueing(true);

    try {
      const request =
        activeTab === "manual"
          ? sanitizeQueueWorkRequest(
              parseOptionalObjectJson<QueueWorkRequest>(manualRequestJson, "Manual request") ?? {}
            )
          : createComposedRequest();
      const completionMode = request.completion ?? "ReturnAfterAccepted";

      const result = await workableFetch<{ queueOutcome?: { status: string }; workerId?: { value: string } }>(
        connection,
        `work/${definition.name}`,
        {
          method: "POST",
          body: JSON.stringify(request),
        }
      );

      if (postQueue === "open-worker" && result.workerId?.value) {
        onOpenChange(false);
        onQueuedWorker(result.workerId.value);
        return;
      }

      if (postQueue === "stay-on-screen") {
        onOpenChange(false);
        return;
      }

      setStatus(
        result.workerId
          ? completionMode === "WaitForCompletion"
            ? `Worker ${result.workerId.value} completed.`
            : `Queued worker ${result.workerId.value}`
          : `Queue result: ${result.queueOutcome?.status ?? "received"}`
      );
    } catch (caught) {
      setError(caught instanceof Error ? caught.message : "Queue request failed.");
    } finally {
      setIsQueueing(false);
    }
  };
  const isWaitingForCompletion = isQueueing && (
    activeTab === "manual"
      ? manualRequestJson.includes("WaitForCompletion")
      : queueRequest.completion === "WaitForCompletion"
  );

  return (
    <Dialog onOpenChange={onOpenChange} open={!!definition}>
      <DialogContent
        className="flex h-[calc(100vh-2rem)] max-h-[calc(100vh-2rem)] flex-col overflow-hidden p-0 sm:h-[88vh] sm:max-h-[88vh] sm:max-w-5xl"
        onInteractOutside={(event) => event.preventDefault()}
      >
        <DialogHeader className="shrink-0">
          <DialogTitle className="flex min-w-0 flex-wrap items-baseline gap-x-2 gap-y-1 px-4 pt-4">
            <span>Configure input, behavior, and runtime options for</span>
            <span
              className="min-w-0 truncate font-mono text-sm font-semibold text-sky-300"
              title={definition?.name}
            >
              {definition?.name ?? "worker"}
            </span>
          </DialogTitle>
          <DialogDescription className="sr-only">
            Queue a worker by setting request input, queue behavior, and optional runtime configuration overrides.
          </DialogDescription>
        </DialogHeader>
        <div className="flex min-h-0 flex-1 flex-col gap-4 px-4">
          {error && (
            <ErrorBanner key={error} message={error} title="Queue failed" />
          )}
          {status && (
            <FeedbackBanner
              key={status}
              message={status}
              title="Queue accepted"
              tone="success"
            />
          )}
          {isWaitingForCompletion && (
            <FeedbackBanner
              message="The worker is executing. This dialog will update when the HTTP request returns."
              title="Waiting for completion"
              tone="info"
            />
          )}
          <Tabs
            onValueChange={(value) => {
              if (value === "manual") {
                try {
                  setManualRequestJson(compactJson(createComposedRequest()));
                } catch {
                  setManualRequestJson(compactJson({
                    completion: queueRequest.completion,
                    input: formValue,
                  }));
                }
              }
              setActiveTab(value as "input" | "config" | "manual");
            }}
            className="flex min-h-0 flex-1 flex-col"
            value={activeTab}
          >
            <div className="shrink-0 flex flex-wrap items-center justify-between gap-3 border-b pb-3">
              <TabsList className="grid w-full grid-cols-3 sm:w-[30rem]">
                <TabsTrigger disabled={!inputSchema} value="input">
                  Input
                </TabsTrigger>
                <TabsTrigger value="config">Config</TabsTrigger>
                <TabsTrigger value="manual">Manual JSON</TabsTrigger>
              </TabsList>
              {activeTab === "input" && (
                <SchemaPresetButton schema={inputSchema} onApply={updateFormValue} />
              )}
              {activeTab === "config" && (
                <Button onClick={resetQueueConfiguration} size="sm" type="button" variant="outline">
                  <RefreshCw className="size-4" />
                  Use definition defaults
                </Button>
              )}
            </div>
            <TabsContent className="mt-4 min-h-0 flex-1 overflow-y-auto pr-2" value="input">
              <SchemaForm
                onChange={updateFormValue}
                schema={inputSchema}
                value={formValue}
              />
            </TabsContent>
            <TabsContent className="mt-4 min-h-0 flex-1 overflow-y-auto pr-2" value="config">
              <QueueConfigurationTabs
                descriptor={queueSchemaDescriptor}
                onRequestChange={setQueueRequest}
                request={queueRequest}
                schema={queueRequestSchema}
              />
            </TabsContent>
            <TabsContent className="mt-4 min-h-0 flex-1 overflow-y-auto pr-2" value="manual">
              <JsonTextEditor
                label="Request JSON"
                onChange={setManualRequestJson}
                value={manualRequestJson}
              />
            </TabsContent>
          </Tabs>
          <div className="-mx-4 shrink-0 flex items-center justify-between gap-3 border-t bg-muted/30 px-4 py-3">
            <div className="min-w-0 text-sm">
              <span className="text-muted-foreground">Queue a worker for </span>
              <span className="font-mono font-semibold text-sky-300">
                {definition?.name ?? "definition"}
              </span>
            </div>
            <div className="flex shrink-0 items-center gap-2">
              <Button
                disabled={isQueueing}
                onClick={() => void queue("stay-on-screen")}
                variant="outline"
              >
                {isQueueing ? (
                  <Loader2 className="size-4 animate-spin" />
                ) : (
                  <Send className="size-4" />
                )}
                {isWaitingForCompletion ? "Waiting" : "Queue"}
              </Button>
              <Button
                disabled={isQueueing}
                onClick={() => void queue("open-worker")}
              >
                {isQueueing ? (
                  <Loader2 className="size-4 animate-spin" />
                ) : (
                  <Eye className="size-4" />
                )}
                {isWaitingForCompletion ? "Waiting" : "Watch"}
              </Button>
            </div>
          </div>
        </div>
      </DialogContent>
    </Dialog>
  );
}

function QueueConfigurationTabs({
  descriptor,
  onRequestChange,
  request,
  schema,
}: {
  descriptor: QueueRequestSchemaDescriptor | null;
  onRequestChange: Dispatch<SetStateAction<QueueWorkRequest>>;
  request: QueueWorkRequest;
  schema: ReturnType<typeof parseJsonSchema>;
}) {
  if (!descriptor || !schema) {
    return (
      <div className="rounded-lg border border-dashed p-6 text-muted-foreground text-sm">
        Queue configuration schema is not available from this Workable host.
      </div>
    );
  }

  const firstTab = descriptor.tabs[0]?.id ?? "queue";

  return (
    <Tabs className="flex min-h-full flex-col" defaultValue={firstTab}>
      <TabsList className="shrink-0 flex h-auto w-full flex-wrap justify-start">
        {descriptor.tabs.map((tab) => (
          <TabsTrigger key={tab.id} value={tab.id}>
            {tab.label}
          </TabsTrigger>
        ))}
      </TabsList>

      {descriptor.tabs.map((tab) => (
        <TabsContent className="mt-4 min-h-0 flex-1 space-y-4" key={tab.id} value={tab.id}>
          <ConfigTabHeader description={tab.description} title={tab.label} />
          <ConfigFieldSections
            onRequestChange={onRequestChange}
            request={request}
            schema={schema}
            tab={tab}
          />
        </TabsContent>
      ))}
    </Tabs>
  );
}

function ConfigFieldSections({
  onRequestChange,
  request,
  schema,
  tab,
}: {
  onRequestChange: Dispatch<SetStateAction<QueueWorkRequest>>;
  request: QueueWorkRequest;
  schema: ReturnType<typeof parseJsonSchema>;
  tab: QueueConfigurationTab;
}) {
  const sections = createConfigurationFieldSections(tab);

  if (sections.length === 1 && sections[0]?.id === "root") {
    return (
      <div className="grid max-w-5xl gap-4 md:grid-cols-2">
        {sections[0].fields.map((field) => (
          <QueueConfigurationPathField
            field={field}
            key={`${tab.id}:${field.path}`}
            onRequestChange={onRequestChange}
            request={request}
            schema={schema}
            tabId={tab.id}
          />
        ))}
      </div>
    );
  }

  return (
    <div className="grid max-w-5xl gap-4">
      {sections.map((section) => (
        <section className="rounded-xl border bg-muted/10 p-4" key={section.id}>
          <div className="mb-3 max-w-2xl space-y-1">
            <h4 className="font-medium text-sm">{section.label}</h4>
            {section.description ? (
              <p className="text-muted-foreground text-xs">{section.description}</p>
            ) : null}
          </div>
          <div className="grid gap-4 md:grid-cols-2">
            {section.fields.map((field) => (
              <QueueConfigurationPathField
                field={field}
                key={`${tab.id}:${field.path}`}
                onRequestChange={onRequestChange}
                request={request}
                schema={schema}
                tabId={tab.id}
              />
            ))}
          </div>
        </section>
      ))}
    </div>
  );
}

function QueueConfigurationPathField({
  field,
  onRequestChange,
  request,
  schema,
  tabId,
}: {
  field: QueueConfigurationField;
  onRequestChange: Dispatch<SetStateAction<QueueWorkRequest>>;
  request: QueueWorkRequest;
  schema: ReturnType<typeof parseJsonSchema>;
  tabId: string;
}) {
  const constraint = getQueueConfigurationFieldConstraint(request, field.path);
  if (constraint) {
    return (
      <LockedConfigurationField
        description={field.description}
        label={field.label}
        reason={constraint.reason}
        value={constraint.value}
      />
    );
  }

  return (
    <SchemaPathField
      description={field.description}
      key={`${tabId}:${field.path}`}
      label={field.label}
      onChange={(next) => onRequestChange(applyQueueConfigurationRules(next as QueueWorkRequest))}
      path={field.path}
      schema={schema}
      value={request}
    />
  );
}

function LockedConfigurationField({
  description,
  label,
  reason,
  value,
}: {
  description?: string;
  label: string;
  reason: string;
  value: string | boolean;
}) {
  return (
    <div className="w-full max-w-md space-y-2">
      <div className="space-y-1">
        <Label className="flex items-center gap-1.5">
          {label}
          <Info className="size-3.5 text-muted-foreground" />
        </Label>
        {description ? (
          <p className="text-muted-foreground text-xs">{description}</p>
        ) : null}
      </div>
      <div className="rounded-lg border bg-muted/30 px-3 py-2 font-mono text-sm">
        {String(value)}
      </div>
      <p className="text-amber-200 text-xs">{reason}</p>
    </div>
  );
}

function createConfigurationFieldSections(tab: QueueConfigurationTab): QueueConfigurationFieldSection[] {
  const tabBasePath = findTabBasePath(tab);
  const sections = new Map<string, QueueConfigurationFieldSection>();

  for (const field of tab.fields) {
    const sectionId = getFieldSectionId(field.path, tabBasePath);
    const existing = sections.get(sectionId);
    if (existing) {
      existing.fields.push(field);
      continue;
    }

    sections.set(sectionId, {
      id: sectionId,
      label: labelForFieldSection(sectionId, tab),
      description: descriptionForFieldSection(sectionId, tab),
      fields: [field],
    });
  }

  return Array.from(sections.values());
}

function findTabBasePath(tab: QueueConfigurationTab) {
  const configurationPrefix = `options.configuration.${tab.id}`;
  if (tab.fields.some((field) => field.path === configurationPrefix || field.path.startsWith(`${configurationPrefix}.`))) {
    return configurationPrefix;
  }

  return "";
}

function getFieldSectionId(path: string, tabBasePath: string) {
  if (tabBasePath && (path === tabBasePath || path.startsWith(`${tabBasePath}.`))) {
    const remaining = path === tabBasePath
      ? []
      : path.slice(tabBasePath.length + 1).split(".");
    return remaining.length > 1 ? `${tabBasePath}.${remaining[0]}` : "root";
  }

  const segments = path.split(".").filter(Boolean);
  return segments.length > 1 ? segments[0] ?? "root" : "root";
}

function labelForFieldSection(sectionId: string, tab: QueueConfigurationTab) {
  if (sectionId === "root") {
    return `${tab.label} settings`;
  }

  const segment = sectionId.split(".").at(-1) ?? sectionId;
  return fieldSectionLabels[segment] ?? humanizePathSegment(segment);
}

function descriptionForFieldSection(sectionId: string, tab: QueueConfigurationTab) {
  if (sectionId === "root") {
    return rootFieldSectionDescriptions[tab.id];
  }

  const segment = sectionId.split(".").at(-1) ?? sectionId;
  return fieldSectionDescriptions[segment];
}

function humanizePathSegment(value: string) {
  return value
    .replace(/([a-z0-9])([A-Z])/g, "$1 $2")
    .replace(/[_-]+/g, " ")
    .replace(/\b\w/g, (character) => character.toUpperCase());
}

const fieldSectionLabels: Record<string, string> = {
  concurrencyKey: "Concurrency key",
  idempotency: "Idempotency",
  durability: "Durability",
  subjectId: "Queue subject",
  transientRetry: "Transient retry",
};

const fieldSectionDescriptions: Record<string, string> = {
  concurrency: "Capacity rules that decide how many workers may occupy the same group and whether extra work waits or is rejected.",
  concurrencyKey: "Optional queue identity used when concurrency scope is configured per key.",
  durability: "Persistence-backed queueing and completion settings.",
  idempotency: "Duplicate-subject protection for this work definition.",
  subjectId: "Optional queue-time subject you supply when starting the worker. Idempotency, querying, and subject-scoped concurrency can use it.",
};

const rootFieldSectionDescriptions: Record<string, string> = {
  coordination: "Turn coordination on and choose where Workable keeps the state used by duplicate protection, capacity limits, and durable queueing.",
  queue: "How the queue request returns and which worker-level options are applied.",
};

function createDefinitionConfigurationDescriptor(
  descriptor: QueueRequestSchemaDescriptor | null
): QueueRequestSchemaDescriptor | null {
  if (!descriptor) {
    return null;
  }

  const tabs = descriptor.tabs
    .map((tab) => ({
      ...tab,
      fields: tab.fields.filter((field) => field.path.startsWith("options.")),
    }))
    .filter((tab) => tab.fields.length > 0);

  return {
    ...descriptor,
    tabs,
  };
}

function ConfigTabHeader({
  description,
  title,
}: {
  description: string;
  title: string;
}) {
  return (
    <div className="max-w-3xl space-y-1">
      <h3 className="font-medium text-sm">{title}</h3>
      <p className="text-muted-foreground text-sm">{description}</p>
    </div>
  );
}

function ScopeTrail({
  onClear,
  onSelectCategory,
  onSelectDefinition,
  scope,
}: {
  onClear: () => void;
  onSelectCategory: (category: string) => void;
  onSelectDefinition?: (definitionName: string, category: string) => void;
  scope: OverviewScope | null;
}) {
  const categorySegments = splitCatalogPath(scope?.category ?? "");
  const categoryCrumbs = categorySegments.map((segment, index) => ({
    label: segment,
    path: categorySegments.slice(0, index + 1).join(":"),
  }));
  const hasDefinition = !!scope?.definitionName;
  const activeCategoryPath = scope?.category ?? "";

  return (
    <div className={`min-w-0 overflow-x-auto ${consoleBreadcrumbTextClassName}`}>
      <Breadcrumb>
        <BreadcrumbList className={`flex-nowrap whitespace-nowrap ${consoleBreadcrumbTextClassName}`}>
          <BreadcrumbItem className="shrink-0">
            {scope ? (
              <BreadcrumbLink asChild className={consoleBreadcrumbRootItemClassName}>
                <button className="inline-flex items-center" onClick={onClear} type="button">
                  All categories
                </button>
              </BreadcrumbLink>
            ) : (
              <BreadcrumbPage className={consoleBreadcrumbRootItemClassName}>All categories</BreadcrumbPage>
            )}
          </BreadcrumbItem>
          {categoryCrumbs.map((crumb, index) => {
            const isCurrentCategory = !hasDefinition && index === categoryCrumbs.length - 1;

            return (
              <Fragment key={crumb.path}>
                <BreadcrumbSeparator className="shrink-0" />
                <BreadcrumbItem className="min-w-0 shrink-0">
                  {isCurrentCategory ? (
                    <BreadcrumbPage className={consoleBreadcrumbCurrentClassName}>
                      {crumb.label}
                    </BreadcrumbPage>
                  ) : (
                    <BreadcrumbLink asChild className={consoleBreadcrumbLinkClassName}>
                      <button
                        onClick={() => onSelectCategory(crumb.path)}
                        type="button"
                      >
                        {crumb.label}
                      </button>
                    </BreadcrumbLink>
                  )}
                </BreadcrumbItem>
              </Fragment>
            );
          })}
          {hasDefinition && (
            <>
              <BreadcrumbSeparator className="shrink-0" />
              <BreadcrumbItem className="min-w-0 shrink-0">
                {onSelectDefinition && scope?.definitionName ? (
                  <BreadcrumbLink asChild className={consoleBreadcrumbDefinitionClassName}>
                    <button
                      onClick={() => onSelectDefinition(
                        scope.definitionName ?? "",
                        activeCategoryPath
                      )}
                      type="button"
                    >
                      {scope.definitionName}
                    </button>
                  </BreadcrumbLink>
                ) : (
                  <BreadcrumbPage className={`${consoleBreadcrumbDefinitionClassName} text-foreground`}>
                    {scope?.definitionName}
                  </BreadcrumbPage>
                )}
              </BreadcrumbItem>
            </>
          )}
        </BreadcrumbList>
      </Breadcrumb>
    </div>
  );
}

function WorkerActionButton({
  action,
  cancellationMayStopExecution,
  disabled,
  icon: Icon,
  onAction,
  tooltip,
}: {
  action: WorkAction;
  cancellationMayStopExecution?: boolean;
  disabled?: boolean;
  icon: typeof Play;
  onAction: (action: WorkAction) => Promise<void>;
  tooltip?: string;
}) {
  const [confirmOpen, setConfirmOpen] = useState(false);
  const toneClassName = workerActionToneClassName(action, disabled === true);

  if (action === "Cancel" || action === "Pause") {
    const isCancel = action === "Cancel";

    return (
      <AlertDialog onOpenChange={setConfirmOpen} open={confirmOpen}>
        <Button
          className={toneClassName}
          disabled={disabled}
          onClick={() => setConfirmOpen(true)}
          size="sm"
          variant="outline"
        >
          <Icon className="size-4" />
          {action}
        </Button>
        <AlertDialogContent>
          <AlertDialogHeader>
            <AlertDialogTitle>{isCancel ? "Cancel worker?" : "Pause worker?"}</AlertDialogTitle>
            <AlertDialogDescription>
              {isCancel
                ? (
                  <>
                    This will request cancellation for the current worker.
                    {cancellationMayStopExecution
                      ? " Any in-flight execution may stop as soon as the work observes the cancellation."
                      : ""}
                    {" "}Cancellation is final and cannot be undone.
                  </>
                )
                : "This will request that the current worker pause. Any in-flight execution may stop when the work observes the pause request, and it can be resumed later."}
            </AlertDialogDescription>
          </AlertDialogHeader>
          <AlertDialogFooter>
            <AlertDialogCancel>{isCancel ? "Keep running" : "Keep executing"}</AlertDialogCancel>
            <AlertDialogAction
              variant="default"
              className={isCancel
                ? "bg-red-600 text-white hover:bg-red-700 focus-visible:ring-red-500"
                : "!bg-amber-400 !text-amber-950 hover:!bg-amber-500 focus-visible:ring-amber-500"}
              onClick={() => {
                setConfirmOpen(false);
                void onAction(action);
              }}
            >
              {isCancel ? "Cancel worker" : "Pause worker"}
            </AlertDialogAction>
          </AlertDialogFooter>
        </AlertDialogContent>
      </AlertDialog>
    );
  }

  const button = (
    <Button
      className={toneClassName}
      disabled={disabled}
      onClick={() => void onAction(action)}
      size="sm"
      variant="outline"
    >
      <Icon className="size-4" />
      {action}
    </Button>
  );

  if (!tooltip) {
    return button;
  }

  return (
    <Tooltip delayDuration={250}>
      <TooltipTrigger asChild>
        {button}
      </TooltipTrigger>
      <TooltipContent side="top" sideOffset={6}>
        {tooltip}
      </TooltipContent>
    </Tooltip>
  );
}

function WorkerStatusBadge({
  now,
  worker,
}: {
  now: number;
  worker: WorkerSnapshot;
}) {
  const timing = formatWorkerStatusTiming(worker, now);
  const tone = workerStatusTextTone(worker.state);
  const showTiming = Boolean(timing);

  return (
    <div className={`inline-flex min-w-[6rem] flex-col items-center justify-center gap-0.5 text-[11px] leading-none ${tone}`}>
      <span className="inline-flex items-center justify-center font-medium leading-none">{worker.state}</span>
      {showTiming ? (
        <span className="inline-flex items-center justify-center tabular-nums leading-none">{timing}</span>
      ) : null}
    </div>
  );
}

function workerStatusTextTone(state: WorkerState) {
  switch (state) {
    case "Queued":
    case "Running":
    case "Waiting":
      return "text-sky-300";
    case "Retrying":
    case "Paused":
    case "Interrupting":
    case "Interrupted":
      return "text-amber-300";
    case "Failed":
      return "text-red-300";
    case "Completed":
      return "text-emerald-300";
    case "Canceled":
      return "text-foreground/80";
    default:
      return "text-muted-foreground";
  }
}

function workerActionToneClassName(_action: WorkAction, disabled: boolean) {
  if (disabled) {
    return "";
  }

  return "border-border bg-muted/20 text-foreground hover:bg-muted/35";
}

function SnapshotBlock({ label, value }: { label: string; value: unknown }) {
  return (
    <div className="space-y-2">
      <div className="flex items-center gap-2 text-sm">
        <Braces className="size-4 text-muted-foreground" />
        <span className="font-medium">{label}</span>
      </div>
      <pre className="max-h-64 overflow-auto rounded-lg border bg-muted/30 p-3 font-mono text-xs leading-relaxed">
        <JsonValue value={value ?? null} />
      </pre>
    </div>
  );
}

function JsonTextEditor({
  label,
  onChange,
  value,
}: {
  label: string;
  onChange: (value: string) => void;
  value: string;
}) {
  const parsed = parseJsonText(value);

  const format = () => {
    if (!parsed.ok) {
      return;
    }

    onChange(JSON.stringify(parsed.value, null, 2));
  };

  return (
    <div className="grid h-full min-h-0 gap-3 lg:grid-cols-2">
      <div className="grid min-h-0 gap-2">
        <div className="flex items-center justify-between gap-2">
          <Label>{label}</Label>
          <Button disabled={!parsed.ok} onClick={format} size="xs" variant="outline">
            <Braces className="size-3" />
            Format
          </Button>
        </div>
        <Textarea
          className="h-[calc(54vh-2.25rem)] min-h-0 resize-none overflow-y-auto font-mono text-xs"
          onChange={(event) => onChange(event.target.value)}
          spellCheck={false}
          value={value}
        />
      </div>
      <div className="grid min-h-0 gap-2">
        <Label>Preview</Label>
        <pre className="h-[calc(54vh-2.25rem)] overflow-auto rounded-lg border bg-muted/30 p-3 font-mono text-xs leading-relaxed">
          {parsed.ok ? (
            <JsonValue value={parsed.value} />
          ) : (
            <span className="text-red-300">{parsed.error}</span>
          )}
        </pre>
      </div>
    </div>
  );
}

function JsonValue({ indent = 0, value }: { indent?: number; value: unknown }) {
  if (value === null || value === undefined) {
    return <span className="text-muted-foreground">null</span>;
  }

  if (typeof value === "string") {
    return <span className="text-emerald-300">{JSON.stringify(value)}</span>;
  }

  if (typeof value === "number") {
    return <span className="text-amber-300">{String(value)}</span>;
  }

  if (typeof value === "boolean") {
    return <span className="text-sky-300">{String(value)}</span>;
  }

  if (Array.isArray(value)) {
    if (value.length === 0) {
      return <span>[]</span>;
    }

    return (
      <>
        <span>[</span>
        {"\n"}
        {value.map((item, index) => (
          <Fragment key={index}>
            {jsonIndent(indent + 1)}
            <JsonValue indent={indent + 1} value={item} />
            {index < value.length - 1 && <span>,</span>}
            {"\n"}
          </Fragment>
        ))}
        {jsonIndent(indent)}
        <span>]</span>
      </>
    );
  }

  if (typeof value === "object") {
    const entries = Object.entries(value as Record<string, unknown>);
    if (entries.length === 0) {
      return <span>{"{}"}</span>;
    }

    return (
      <>
        <span>{"{"}</span>
        {"\n"}
        {entries.map(([key, item], index) => (
          <Fragment key={key}>
            {jsonIndent(indent + 1)}
            <span className="text-violet-300">{JSON.stringify(key)}</span>
            <span>: </span>
            <JsonValue indent={indent + 1} value={item} />
            {index < entries.length - 1 && <span>,</span>}
            {"\n"}
          </Fragment>
        ))}
        {jsonIndent(indent)}
        <span>{"}"}</span>
      </>
    );
  }

  return <span>{JSON.stringify(value)}</span>;
}

function jsonIndent(level: number) {
  return "  ".repeat(level);
}

function WorkDataCard({ data, label }: { data?: WorkData | null; label: string }) {
  const preview = parseSchemaJsonValue(data?.json);

  return (
    <Card size="sm">
      <CardHeader>
        <div className="flex min-w-0 flex-wrap items-center justify-between gap-2">
          <CardTitle>{label}</CardTitle>
          {data?.clrType ? (
            <div
              className="max-w-full truncate rounded-md border bg-muted/30 px-2 py-1 font-mono text-[11px] text-muted-foreground"
              title={data.clrType}
            >
              {data.clrType}
            </div>
          ) : null}
        </div>
      </CardHeader>
      <CardContent>
        <pre className="max-h-72 overflow-auto rounded-lg border bg-muted/30 p-3 font-mono text-xs leading-relaxed">
          <JsonValue value={preview ?? null} />
        </pre>
      </CardContent>
    </Card>
  );
}

type WorkerTimelineItem = {
  actorLabel?: string;
  at: string;
  badge: string;
  description: string;
  failureDetails?: WorkerFailureDetails | null;
  facts: Array<{ label: string; value: string }>;
  filterKind?: WorkerTimelineFilterKind;
  icon: typeof Clock3;
  id: string;
  kind: "action" | "iteration" | "queue" | "state";
  liveText?: WorkerTimelineLiveText;
  marker?: "current" | "latest";
  sortOrder: number;
  sourceLabel?: string;
  sourceTooltip?: string;
  stateMode?: "recurrence" | "retry";
  title: string;
  tone: "danger" | "info" | "neutral" | "success" | "warning";
};

type WorkerTimelineFilterKind = "failures" | "system" | "user";
type WorkerSortDirection = "asc" | "desc";

const workerTimelineExecutingIterationRowEnabled = false;

type WorkerTimelineLiveText =
  | {
    kind: "iteration";
    executionDuration?: string | null;
    sequence: number;
    startedAt?: string | null;
    status: WorkCompletionStatus;
  }
  | {
    kind: "state";
    mode: "recurrence" | "retry";
    nextRunAt?: string | null;
    retryAttempt?: number | null;
    stateChangedAt?: string | null;
    updatedAt: string;
  };

type WorkerTimelineRow =
  | {
    kind: "gap";
    id: string;
    liveSinceAt?: string;
    milliseconds: number;
  }
  | {
    kind: "item";
    item: WorkerTimelineItem;
  };

function WorkerTimelinePanel({
  items,
  now,
  onClose,
  onViewStateChange,
  viewState,
}: {
  items: WorkerTimelineItem[];
  now: number;
  onClose: () => void;
  onViewStateChange: (shape: WorkComponentShape) => void;
  viewState: WorkComponentShape;
}) {
  const [pausedItems, setPausedItems] = useState<WorkerTimelineItem[] | null>(null);
  const [sortDirection, setSortDirection] = useState<WorkerSortDirection>("desc");
  const [selectedFilters, setSelectedFilters] = useState<Set<WorkerTimelineFilterKind>>(
    () => new Set(workerTimelineFilterKinds)
  );
  const scrollRef = useRef<HTMLDivElement | null>(null);
  const rowRefs = useRef(new Map<string, HTMLDivElement>());
  const scrollAnchorRef = useRef<{ key: string; top: number } | null>(null);
  const sortedItems = useMemo(
    () => sortTimelineItems(items, sortDirection),
    [items, sortDirection]
  );
  const normalizedSelectedFilters = useMemo(
    () => new Set(
      [...selectedFilters].filter((filterKind): filterKind is WorkerTimelineFilterKind =>
        workerTimelineFilterKinds.includes(filterKind)
      )
    ),
    [selectedFilters]
  );
  const allFiltersSelected = normalizedSelectedFilters.size === workerTimelineFilterKinds.length;
  const filtersActive = !allFiltersSelected;
  const hideRecurrenceWaitItems = normalizedSelectedFilters.size === 1 && normalizedSelectedFilters.has("system");
  const currentFilteredItems = useMemo(
    () => allFiltersSelected
      ? sortedItems
      : sortedItems.filter((item) => shouldIncludeTimelineItemForFilters(item, normalizedSelectedFilters, hideRecurrenceWaitItems)),
    [allFiltersSelected, hideRecurrenceWaitItems, normalizedSelectedFilters, sortedItems]
  );
  const isPaused = pausedItems !== null;
  const visibleItems = useMemo(
    () => {
      const sourceItems = sortTimelineItems(pausedItems ?? items, sortDirection);
      return allFiltersSelected
        ? sourceItems
        : sourceItems.filter((item) => shouldIncludeTimelineItemForFilters(item, normalizedSelectedFilters, hideRecurrenceWaitItems));
    },
    [allFiltersSelected, hideRecurrenceWaitItems, items, normalizedSelectedFilters, pausedItems, sortDirection]
  );
  const visibleRows = useMemo(
    () => createTimelineRows(visibleItems),
    [visibleItems]
  );
  const pendingPausedCount = useMemo(() => {
    if (!pausedItems) {
      return 0;
    }

    const visibleIds = new Set(visibleItems.map((item) => item.id));
    return currentFilteredItems.reduce(
      (count, item) => visibleIds.has(item.id) ? count : count + 1,
      0
    );
  }, [currentFilteredItems, pausedItems, visibleItems]);
  const togglePause = () => {
    setPausedItems((current) => current ? null : items);
  };
  const setFilterSelected = (filterKind: WorkerTimelineFilterKind, selected: boolean) => {
    setSelectedFilters((current) => {
      const next = new Set(current);
      if (selected) {
        next.add(filterKind);
      } else {
        next.delete(filterKind);
      }

      return next;
    });
  };

  useLayoutEffect(() => {
    const container = scrollRef.current;
    if (!container) {
      return;
    }

    const previousAnchor = scrollAnchorRef.current;
    if (previousAnchor && container.scrollTop > 24) {
      const anchorElement = rowRefs.current.get(previousAnchor.key);
      if (anchorElement) {
        const containerRect = container.getBoundingClientRect();
        const nextTop = anchorElement.getBoundingClientRect().top - containerRect.top;
        const topDelta = nextTop - previousAnchor.top;
        if (topDelta !== 0) {
          container.scrollTop += topDelta;
        }
      }
    }

    scrollAnchorRef.current = captureTimelineScrollAnchor(visibleRows, container, rowRefs.current);
  }, [visibleRows]);

  return (
    <PanelShell
      contentClassName={viewState === "compact" ? "hidden" : "space-y-4"}
      actions={viewState === "detailed" ? (
        <WorkerTimelinePanelActions
          isPaused={isPaused}
          onTogglePause={togglePause}
          onToggleSortDirection={() => setSortDirection((current) => current === "desc" ? "asc" : "desc")}
          sortDirection={sortDirection}
        />
      ) : null}
      filterControl={viewState === "detailed"
        ? {
            activeCount: filtersActive ? normalizedSelectedFilters.size : 0,
            content: (
              <WorkerTimelineFilterContent
                onClearFilters={() => setSelectedFilters(new Set(workerTimelineFilterKinds))}
                onSetFilterSelected={setFilterSelected}
                selectedFilters={normalizedSelectedFilters}
              />
            ),
            label: "Filter timeline",
          }
        : undefined}
      onClose={onClose}
      onViewStateChange={onViewStateChange}
      supportedViewStates={["compact", "detailed"]}
      title="Iteration Timeline"
      viewState={viewState}
    >
      <section className="flex h-full min-h-0 flex-col rounded-xl border bg-muted/10 p-4">
        <div className="mb-4 flex flex-wrap items-center justify-end gap-2">
          {isPaused && pendingPausedCount > 0 ? (
            <Badge className="border-amber-500/30 bg-amber-500/10 text-amber-800 dark:text-amber-100" variant="outline">
              {pendingPausedCount} buffered
            </Badge>
          ) : null}
        </div>
        {visibleItems.length === 0 ? (
          <EmptyListState
            message={filtersActive
              ? "No retained timeline events match the current filters."
              : "No retained timeline events yet."}
          />
        ) : (
          <div
            className="min-h-0 flex-1 overflow-auto rounded-xl border bg-background/60 p-4"
            onScroll={(event) => {
              scrollAnchorRef.current = captureTimelineScrollAnchor(
                visibleRows,
                event.currentTarget,
                rowRefs.current
              );
            }}
            ref={scrollRef}
          >
            <div className="space-y-0">
              {visibleRows.map((row, index) => {
                if (row.kind === "gap") {
                  const gapLabel = row.liveSinceAt
                    ? formatMillisecondsCompact(Math.max(0, now - parseTimelineTimestamp(row.liveSinceAt)))
                  : formatMillisecondsCompact(row.milliseconds);
                return (
                  <div
                    className="grid grid-cols-[2.5rem_minmax(0,1fr)] gap-3 py-3"
                    key={row.id}
                    ref={(node) => {
                      if (node) {
                        rowRefs.current.set(row.id, node);
                      } else {
                        rowRefs.current.delete(row.id);
                      }
                    }}
                  >
                    <div className="relative flex justify-center">
                      <span className="absolute top-0 bottom-0 w-px bg-border/60" />
                    </div>
                    <div className="flex items-center">
                      <div className="inline-flex items-center gap-1.5 rounded-full border border-dashed border-border/70 bg-muted/20 px-3 py-1 font-mono text-[11px] text-muted-foreground">
                        <Clock3 className="size-3" />
                        <span>{gapLabel}</span>
                      </div>
                    </div>
                  </div>
                );
              }

              const { item } = row;
              const Icon = item.icon;
              const isLast = index === visibleRows.length - 1;
              const itemTitle = renderTimelineItemTitle(item, now);
              const itemDescription = renderTimelineItemDescription(item, now);

                return (
                  <div
                    className="grid grid-cols-[2.5rem_minmax(0,1fr)] gap-3"
                    key={item.id}
                    ref={(node) => {
                      if (node) {
                        rowRefs.current.set(item.id, node);
                      } else {
                        rowRefs.current.delete(item.id);
                      }
                    }}
                  >
                    <div className="relative flex justify-center">
                      {!isLast && (
                        <span className="absolute top-9 bottom-0 w-px bg-border/80" />
                      )}
                      <span className={`mt-1 flex size-8 items-center justify-center rounded-full border ${timelineIconTone(item)}`}>
                        <Icon className="size-4" />
                      </span>
                    </div>
                    <div className="pb-6 last:pb-0">
                      <div className="rounded-xl border bg-background/80 p-4 shadow-sm">
                        <div className="flex flex-wrap items-start justify-between gap-3">
                          <div className="min-w-0">
                            <div className="flex flex-wrap items-center gap-2">
                              <div className="font-medium">{itemTitle}</div>
                            </div>
                            {item.actorLabel ? (
                              <div className="mt-1 text-muted-foreground text-xs">
                                {item.actorLabel}
                              </div>
                          ) : null}
                        </div>
                        <div className="flex flex-wrap items-center justify-end gap-2">
                          {item.sourceLabel ? (
                            item.sourceTooltip ? (
                              <Tooltip delayDuration={250}>
                                <TooltipTrigger asChild>
                                  <Badge className="border-border bg-muted/40 text-muted-foreground" variant="outline">
                                    {item.sourceLabel}
                                  </Badge>
                                </TooltipTrigger>
                                <TooltipContent side="top" sideOffset={6}>
                                  {item.sourceTooltip}
                                </TooltipContent>
                              </Tooltip>
                            ) : (
                              <Badge className="border-border bg-muted/40 text-muted-foreground" variant="outline">
                                {item.sourceLabel}
                              </Badge>
                            )
                          ) : null}
                          <Badge className={`${timelineBadgeTone(item.tone)} inline-flex w-[12ch] justify-center px-2 text-center`} variant="outline">
                            {item.badge}
                          </Badge>
                          <div className="inline-flex w-[6ch] justify-end font-mono tabular-nums text-muted-foreground text-xs whitespace-nowrap">
                            {renderTimelineItemMeta(item, now)}
                          </div>
                          </div>
                        </div>
                      {item.kind === "queue" && itemDescription ? (
                        <p className="mt-3 text-sm leading-6 text-foreground/90">{itemDescription}</p>
                      ) : null}
                      {shouldRenderTimelineStateDescription(item, itemTitle, itemDescription) ? (
                        <p className="mt-3 text-sm leading-6 text-foreground/90">{itemDescription}</p>
                      ) : null}
                      {item.facts.length > 0 && (
                        <div className="mt-3 grid gap-2 sm:grid-cols-2 xl:grid-cols-3">
                          {item.facts.map((fact) => (
                            <InlineFact key={`${item.id}:${fact.label}`} label={fact.label} value={fact.value} />
                          ))}
                        </div>
                      )}
                      {item.failureDetails ? (
                        <div className="mt-3">
                          <WorkerFailureBanner details={item.failureDetails} now={now} />
                        </div>
                      ) : null}
                    </div>
                  </div>
                  </div>
                );
              })}
            </div>
          </div>
        )}
      </section>
    </PanelShell>
  );
}

function WorkerTimelineFilterContent({
  onClearFilters,
  onSetFilterSelected,
  selectedFilters,
}: {
  onClearFilters: () => void;
  onSetFilterSelected: (filterKind: WorkerTimelineFilterKind, selected: boolean) => void;
  selectedFilters: ReadonlySet<WorkerTimelineFilterKind>;
}) {
  return (
    <>
      <div className="flex items-center justify-between border-b px-3 py-2">
        <span className="font-medium text-sm">Timeline filters</span>
        <Button
          className="h-7 px-2 text-xs"
          onClick={onClearFilters}
          size="sm"
          variant="ghost"
        >
          All
        </Button>
      </div>
      <div className="space-y-1 p-2">
        {workerTimelineFilterKinds.map((filterKind) => {
          const selected = selectedFilters.has(filterKind);

          return (
            <label
              className="flex cursor-pointer items-center gap-3 rounded-md px-2 py-2 transition-colors hover:bg-accent/40"
              key={filterKind}
            >
              <input
                checked={selected}
                className="size-4 accent-primary"
                onChange={(event) => onSetFilterSelected(filterKind, event.currentTarget.checked)}
                type="checkbox"
              />
              <span className={`inline-flex rounded-full border px-2 py-0.5 font-mono text-[11px] ${workerTimelineFilterTone(filterKind)}`}>
                {workerTimelineFilterLabel(filterKind)}
              </span>
            </label>
          );
        })}
      </div>
    </>
  );
}

function WorkerTimelinePanelActions({
  isPaused,
  onTogglePause,
  onToggleSortDirection,
  sortDirection,
}: {
  isPaused: boolean;
  onTogglePause: () => void;
  onToggleSortDirection: () => void;
  sortDirection: WorkerSortDirection;
}) {
  return (
    <>
      <ToolbarIconButton
        label={isPaused ? "Resume timeline stream" : "Pause timeline stream"}
        onClick={onTogglePause}
        type="button"
        tooltip={isPaused ? "Resume the timeline stream" : "Pause the timeline stream from updating"}
      >
        {isPaused ? <Play className="size-3.5" /> : <Pause className="size-3.5" />}
      </ToolbarIconButton>
      <ToolbarIconButton
        label={sortDirection === "desc" ? "Show oldest timeline items first" : "Show newest timeline items first"}
        onClick={onToggleSortDirection}
        type="button"
        tooltip={sortDirection === "desc" ? "Show oldest timeline items first" : "Show newest timeline items first"}
      >
        {sortDirection === "desc"
          ? <ArrowDownWideNarrow className="size-3.5" />
          : <ArrowUpNarrowWide className="size-3.5" />}
      </ToolbarIconButton>
    </>
  );
}

function IterationDurationGraph({
  iterations,
  now,
}: {
  iterations: WorkerIterationSnapshot[];
  now: number;
}) {
  const scrollRef = useRef<HTMLDivElement | null>(null);
  const points = useMemo(
    () => iterations
      .map((iteration) => {
        const durationMs = getIterationDurationMilliseconds(iteration, now);
        if (durationMs === null) {
          return null;
        }

        return {
          at: iteration.completedAt ?? iteration.startedAt ?? iteration.occurredAt,
          durationMs,
          isExecuting: iteration.status === "Executing",
          sequence: iteration.sequence,
          status: iteration.status,
        };
      })
      .filter((point): point is NonNullable<typeof point> => point !== null),
    [iterations, now]
  );
  const maxDuration = useMemo(
    () => points.reduce((max, point) => Math.max(max, point.durationMs), 0),
    [points]
  );
  const minDuration = useMemo(
    () => points.reduce((min, point) => Math.min(min, point.durationMs), Number.POSITIVE_INFINITY),
    [points]
  );
  const lastPointKey = points[points.length - 1]?.sequence ?? null;
  const trackMinWidth = useMemo(
    () => Math.max(points.length * 14, 0),
    [points.length]
  );
  const graphLowerBound = useMemo(
    () => maxDuration > 0 && Number.isFinite(minDuration) && minDuration / maxDuration >= 0.6
      ? minDuration
      : 0,
    [maxDuration, minDuration]
  );
  const maxLabel = useMemo(
    () => formatMillisecondsCompact(maxDuration),
    [maxDuration]
  );
  const minLabel = useMemo(
    () => formatMillisecondsCompact(graphLowerBound),
    [graphLowerBound]
  );
  const oldestTimeLabel = useMemo(
    () => formatTimeOfDay(points[0]?.at),
    [points]
  );
  const newestTimeLabel = useMemo(
    () => formatTimeOfDay(points[points.length - 1]?.at),
    [points]
  );

  useLayoutEffect(() => {
    if (!scrollRef.current) {
      return;
    }

    scrollRef.current.scrollLeft = scrollRef.current.scrollWidth;
  }, [lastPointKey, points.length]);

  if (points.length <= 1) {
    return null;
  }

  return (
    <div className="mb-4 rounded-xl border bg-background/60 p-3">
      <div className="flex items-stretch gap-3">
        <div className="flex h-24 w-14 shrink-0 flex-col justify-between py-2 text-right font-mono text-[11px] text-muted-foreground">
          <span>{maxLabel}</span>
          <span>{minLabel}</span>
        </div>
        <div className="min-w-0 flex-1 overflow-x-auto pb-1" ref={scrollRef}>
          <div
            className="flex h-24 min-w-full items-end justify-start gap-1.5 rounded-lg border border-border/50 bg-muted/15 px-3 py-2"
            style={{ minWidth: `${trackMinWidth}px` }}
          >
            {points.map((point) => {
              const scaledRange = Math.max(1, maxDuration - graphLowerBound);
              const normalizedHeight = maxDuration > 0
                ? Math.max(0, (point.durationMs - graphLowerBound) / scaledRange)
                : 0;
              const height = maxDuration > 0
                ? Math.max(10, Math.round(normalizedHeight * 56))
                : 10;
              const label = `${formatMillisecondsCompact(point.durationMs)} (${formatIterationTimelineStatus(point.status)})`;

              return (
                <div
                  className="flex min-w-[6px] flex-1 basis-0 flex-col items-center justify-end"
                  key={`iteration-graph:${point.sequence}`}
                  title={`Iteration #${point.sequence} ${label}`}
                >
                  <div
                    className={`w-full rounded-t-sm ${
                      point.status === "Completed"
                        ? "bg-emerald-400/80"
                        : point.status === "Failed"
                          ? "bg-red-400/85"
                          : point.status === "Executing"
                            ? "bg-sky-400/90"
                            : point.status === "Paused"
                              ? "bg-amber-400/85"
                              : point.status === "Canceled"
                                ? "bg-slate-400/80"
                                : "bg-muted-foreground/70"
                    } ${point.isExecuting ? "animate-pulse" : ""}`}
                    style={{ height }}
                  />
                </div>
              );
            })}
          </div>
        </div>
      </div>
      <div className="mt-2 flex items-center gap-3">
        <div className="w-14 shrink-0" />
        <div className="flex min-w-0 flex-1 items-center justify-between font-mono text-[11px] text-muted-foreground">
          <span>{oldestTimeLabel}</span>
          <span>{newestTimeLabel}</span>
        </div>
      </div>
    </div>
  );
}

function WorkerLogPanel({
  connectionError,
  entries,
  hasActiveIteration,
  onClose,
  onViewStateChange,
  viewState,
}: {
  connectionError?: string;
  entries: WorkerLogEntry[];
  hasActiveIteration: boolean;
  onClose: () => void;
  onViewStateChange: (shape: WorkComponentShape) => void;
  viewState: WorkComponentShape;
}) {
  const [hiddenLevels, setHiddenLevels] = useState<Set<string>>(() => new Set());
  const [pausedEntries, setPausedEntries] = useState<WorkerLogEntry[] | null>(null);
  const [sortDirection, setSortDirection] = useState<WorkerSortDirection>("desc");
  const sortedEntries = useMemo(
    () => sortWorkerLogEntries(entries, sortDirection),
    [entries, sortDirection]
  );
  const availableLevels = useMemo(
    () => getOrderedLogLevels(sortedEntries),
    [sortedEntries]
  );
  const filteredEntries = useMemo(
    () => filterWorkerLogEntries(sortedEntries, hiddenLevels),
    [hiddenLevels, sortedEntries]
  );
  const isPaused = pausedEntries !== null;
  const visibleEntries = useMemo(
    () => filterWorkerLogEntries(sortWorkerLogEntries(pausedEntries ?? entries, sortDirection), hiddenLevels),
    [entries, hiddenLevels, pausedEntries, sortDirection]
  );
  const pendingPausedCount = useMemo(() => {
    if (!pausedEntries) {
      return 0;
    }

    const visibleKeys = new Set(visibleEntries.map(getWorkerLogEntryKey));
    return filteredEntries.reduce(
      (count, entry) => visibleKeys.has(getWorkerLogEntryKey(entry)) ? count : count + 1,
      0
    );
  }, [filteredEntries, pausedEntries, visibleEntries]);
  const clientEntryCount = sortedEntries.length;
  const filtersActive = hiddenLevels.size > 0;
  const selectedLevelCount = filtersActive
    ? availableLevels.filter((level) => !hiddenLevels.has(normalizeLogLevel(level))).length
    : 0;
  const summary = useMemo(
    () => summarizeWorkerLogEntries(entries),
    [entries]
  );
  const title = useMemo(
    () => (
      <WorkerLogPanelTitle
        onSelectLevel={(level) => {
          setHiddenLevels(createHiddenLogLevelsForFocus(availableLevels, level));
          onViewStateChange("detailed");
        }}
        summary={summary}
        viewState={viewState}
      />
    ),
    [availableLevels, onViewStateChange, summary, viewState]
  );

  const togglePause = () => {
    setPausedEntries((current) => current ? null : sortedEntries);
  };
  const setLevelVisible = (level: string, visible: boolean) => {
    const normalizedLevel = normalizeLogLevel(level);
    setHiddenLevels((current) => {
      const next = new Set(current);
      if (visible) {
        next.delete(normalizedLevel);
      } else {
        next.add(normalizedLevel);
      }

      return next;
    });
  };

  return (
    <PanelShell
      contentClassName={viewState === "compact"
        ? connectionError && hasActiveIteration
          ? "space-y-3"
          : "hidden"
        : "space-y-4"}
      filterControl={viewState === "detailed"
        ? {
            activeCount: selectedLevelCount,
            content: (
              <WorkerLogFilterContent
                availableLevels={availableLevels}
                hiddenLevels={hiddenLevels}
                onClearFilters={() => setHiddenLevels(new Set())}
                onSetLevelVisible={setLevelVisible}
              />
            ),
            label: "Filter log levels",
          }
        : undefined}
      actions={viewState === "detailed" ? (
        <WorkerLogPanelActions
          isPaused={isPaused}
          onTogglePause={togglePause}
          onToggleSortDirection={() => setSortDirection((current) => current === "desc" ? "asc" : "desc")}
          sortDirection={sortDirection}
        />
      ) : null}
      onClose={onClose}
      onViewStateChange={onViewStateChange}
      supportedViewStates={["compact", "detailed"]}
      title={title}
      viewState={viewState}
    >
      <WorkerLogStreamCard
        clientEntryCount={clientEntryCount}
        connectionError={connectionError}
        hasActiveIteration={hasActiveIteration}
        isPaused={isPaused}
        pendingPausedCount={pendingPausedCount}
        viewState={viewState}
        visibleEntries={visibleEntries}
      />
    </PanelShell>
  );
}

function WorkerLogPanelTitle({
  onSelectLevel,
  summary,
  viewState,
}: {
  onSelectLevel: (level: "Error" | "Information" | "Warning") => void;
  summary: {
    errors: number;
    information: number;
    total: number;
    warnings: number;
  };
  viewState: WorkComponentShape;
}) {
  return (
    <>
      <span>Logs</span>
      {viewState === "compact" ? (
        <>
          <LogSummaryPill
            count={summary.errors}
            label="Errors"
            onClick={() => onSelectLevel("Error")}
            tone={logLevelFilterTone("Error")}
          />
          <LogSummaryPill
            count={summary.warnings}
            label="Warnings"
            onClick={() => onSelectLevel("Warning")}
            tone={logLevelFilterTone("Warning")}
          />
          <LogSummaryPill
            count={summary.information}
            label="Info"
            onClick={() => onSelectLevel("Information")}
            tone={logLevelFilterTone("Information")}
          />
        </>
      ) : null}
    </>
  );
}

function LogSummaryPill({
  count,
  label,
  onClick,
  tone,
}: {
  count: number;
  label: string;
  onClick: () => void;
  tone: string;
}) {
  return (
    <button
      className={`inline-flex items-center gap-1.5 rounded-full border px-3 py-1 font-mono text-xs transition-colors ${tone}`}
      onClick={onClick}
      type="button"
    >
      <span>{label}</span>
      <span>{count}</span>
    </button>
  );
}

function WorkerLogFilterContent({
  availableLevels,
  hiddenLevels,
  onClearFilters,
  onSetLevelVisible,
}: {
  availableLevels: string[];
  hiddenLevels: ReadonlySet<string>;
  onClearFilters: () => void;
  onSetLevelVisible: (level: string, visible: boolean) => void;
}) {
  return (
    <>
      <div className="flex items-center justify-between border-b px-3 py-2">
        <span className="font-medium text-sm">Log levels</span>
        <Button
          className="h-7 px-2 text-xs"
          onClick={onClearFilters}
          size="sm"
          variant="ghost"
        >
          All
        </Button>
      </div>
      <div className="space-y-1 p-2">
        {availableLevels.map((level) => {
          const visible = !hiddenLevels.has(normalizeLogLevel(level));

          return (
            <label
              className="flex cursor-pointer items-center gap-3 rounded-md px-2 py-2 transition-colors hover:bg-accent/40"
              key={level}
            >
              <input
                checked={visible}
                className="size-4 accent-primary"
                onChange={(event) => onSetLevelVisible(level, event.currentTarget.checked)}
                type="checkbox"
              />
              <span className={`inline-flex rounded-full border px-2 py-0.5 font-mono text-[11px] ${logLevelFilterTone(level)}`}>
                {level}
              </span>
            </label>
          );
        })}
      </div>
    </>
  );
}

function WorkerLogPanelActions({
  isPaused,
  onTogglePause,
  onToggleSortDirection,
  sortDirection,
}: {
  isPaused: boolean;
  onTogglePause: () => void;
  onToggleSortDirection: () => void;
  sortDirection: WorkerSortDirection;
}) {
  return (
    <>
      <ToolbarIconButton
        label={isPaused ? "Resume log stream" : "Pause log stream"}
        onClick={onTogglePause}
        type="button"
        tooltip={isPaused ? "Resume the log stream" : "Pause the log stream from updating"}
      >
        {isPaused ? <Play className="size-3.5" /> : <Pause className="size-3.5" />}
      </ToolbarIconButton>
      <ToolbarIconButton
        label={sortDirection === "desc" ? "Show oldest log entries first" : "Show newest log entries first"}
        onClick={onToggleSortDirection}
        type="button"
        tooltip={sortDirection === "desc" ? "Show oldest log entries first" : "Show newest log entries first"}
      >
        {sortDirection === "desc"
          ? <ArrowDownWideNarrow className="size-3.5" />
          : <ArrowUpNarrowWide className="size-3.5" />}
      </ToolbarIconButton>
    </>
  );
}

function WorkerLogStreamCard({
  clientEntryCount,
  connectionError,
  hasActiveIteration,
  isPaused,
  pendingPausedCount,
  viewState,
  visibleEntries,
}: {
  clientEntryCount: number;
  connectionError?: string;
  hasActiveIteration: boolean;
  isPaused: boolean;
  pendingPausedCount: number;
  viewState: WorkComponentShape;
  visibleEntries: WorkerLogEntry[];
}) {
  const scrollRef = useRef<HTMLDivElement | null>(null);

  useEffect(() => {
    if (hasActiveIteration && !isPaused && scrollRef.current) {
      scrollRef.current.scrollTop = 0;
    }
  }, [hasActiveIteration, isPaused, visibleEntries.length]);

  if (viewState === "compact") {
    return (
      <section className="flex h-full min-h-0 flex-col rounded-xl border bg-muted/10 p-4">
        {connectionError && hasActiveIteration ? (
          <div className="mb-3 rounded-lg border border-amber-500/30 bg-amber-500/10 px-3 py-2 text-amber-900 text-sm dark:text-amber-100">
            {connectionError}
          </div>
        ) : null}
      </section>
    );
  }

  return (
    <section className="flex h-full min-h-0 flex-col rounded-xl border bg-muted/10 p-4">
      <div className="mb-3 flex flex-wrap items-center justify-end gap-2">
        <div className="flex flex-wrap items-center gap-2">
          <Badge className="border-slate-500/30 bg-slate-500/10 text-slate-700 dark:text-slate-200" variant="outline">
            {clientEntryCount}/{workerExecutionLogStreamLimit} logs
          </Badge>
          {isPaused && pendingPausedCount > 0 ? (
            <Badge className="border-amber-500/30 bg-amber-500/10 text-amber-800 dark:text-amber-100" variant="outline">
              {pendingPausedCount} buffered
            </Badge>
          ) : null}
        </div>
      </div>
      {connectionError && hasActiveIteration ? (
        <div className="mb-3 rounded-lg border border-amber-500/30 bg-amber-500/10 px-3 py-2 text-amber-900 text-sm dark:text-amber-100">
          {connectionError}
        </div>
      ) : null}
      <div
        className="min-h-0 flex-1 overflow-auto rounded-xl border border-slate-800 bg-slate-950 text-slate-100 shadow-inner"
        ref={scrollRef}
      >
        {visibleEntries.length === 0 ? (
          <div className="p-4">
            <EmptyListState
              message={hasActiveIteration
                ? "The active iteration has not emitted any log lines yet."
                : "No logs in this view."}
            />
          </div>
        ) : (
          <div className="font-mono text-xs leading-[1.15rem]">
            {visibleEntries.map((entry, index) => {
              const previousEntry = index > 0 ? visibleEntries[index - 1] : null;
              const showCategory = !previousEntry || previousEntry.category !== entry.category;

              return (
                <div
                  className="px-4 py-0"
                  key={getWorkerLogEntryKey(entry)}
                >
                  {showCategory ? (
                    <div className="text-slate-400">
                      {entry.category}
                    </div>
                  ) : null}
                  <div className="flex min-w-0 flex-wrap items-start gap-x-2 gap-y-0">
                    <span className="shrink-0 text-slate-500">
                      {formatWorkerLogTimestamp(entry.occurredAt)}
                    </span>
                    <span className={`shrink-0 ${consoleLogLevelTone(entry.level)}`}>
                      {entry.level.toUpperCase()}
                    </span>
                    {formatWorkerLogEventId(entry.eventId) ? (
                      <span className="shrink-0 text-slate-600">
                        [{formatWorkerLogEventId(entry.eventId)}]
                      </span>
                    ) : null}
                    <span className="min-w-0 flex-1 whitespace-pre-wrap break-words text-slate-100">
                      {entry.message}
                    </span>
                  </div>
                  {(entry.exceptionType || entry.exceptionMessage) ? (
                    <div className="pl-[7.5rem] leading-[1.15rem] text-amber-300/90">
                      <span className="text-amber-400">! </span>
                      {entry.exceptionType ?? "Exception"}
                      {entry.exceptionMessage ? `: ${entry.exceptionMessage}` : ""}
                    </div>
                  ) : null}
                </div>
              );
            })}
          </div>
        )}
      </div>
    </section>
  );
}

function WorkerFailureBanner({
  details,
  now,
}: {
  details: WorkerFailureDetails;
  now?: number;
}) {
  const [stackOpen, setStackOpen] = useState(false);
  const [stackMaximized, setStackMaximized] = useState(false);
  const [hiddenStackFrameKinds, setHiddenStackFrameKinds] = useState<StackFrameFilterKind[]>(["work", "library"]);
  const exceptionChain = useMemo(
    () => details.kind !== "exception"
      ? []
      : [
        {
          exceptionType: details.exceptionType,
          message: details.message,
          stackTrace: details.stackTrace,
        },
        ...(details.innerExceptions ?? []),
      ],
    [details.exceptionType, details.innerExceptions, details.kind, details.message, details.stackTrace]
  );
  const collapsedStackFrameCounts = useMemo(() => {
    const counts: Record<StackFrameFilterKind, number> = {
      application: 0,
      library: 0,
      work: 0,
    };

    exceptionChain.forEach((exceptionItem) => {
      getStackTraceLines(exceptionItem.stackTrace).forEach((line) => {
        const kind = classifyStackTraceLine(line);
        if (kind === "detail" || !hiddenStackFrameKinds.includes(kind)) {
          return;
        }

        counts[kind] += 1;
      });
    });

    return counts;
  }, [exceptionChain, hiddenStackFrameKinds]);
  const collapsedStackFrameTotal = collapsedStackFrameCounts.application +
    collapsedStackFrameCounts.work +
    collapsedStackFrameCounts.library;
  const toggleStackFrameKind = (kind: StackFrameFilterKind) => {
    setHiddenStackFrameKinds((current) => {
      const isHidden = current.includes(kind);
      if (isHidden) {
        return current.filter((entry) => entry !== kind);
      }

      const visibleCount = stackFrameFilterKinds.filter((entry) => !current.includes(entry)).length;
      if (visibleCount <= 1) {
        return current;
      }

      return [...current, kind];
    });
  };
  const expandCollapsedStackEntry = (entry: Extract<StackTraceDisplayEntry, { type: "collapsed" }>) => {
    setHiddenStackFrameKinds((current) =>
      current.filter((kind) => entry.counts[kind] <= 0)
    );
  };
  const retrySummary = details.retryPending
    ? formatPendingRetryText(details.retryPending, now)
    : null;

  return (
    <section className="rounded-xl border border-red-500/30 bg-red-500/10 p-4 text-red-50 shadow-lg">
      <div className="flex flex-wrap items-start justify-between gap-3">
        <div className="min-w-0 flex-1 space-y-2">
          <div className="flex flex-wrap items-center justify-between gap-3">
            <div className="flex min-w-0 flex-wrap items-center gap-2">
              <Ban className="size-4 text-red-300" />
              <div className="font-semibold text-red-100">Execution failed</div>
              {details.kind === "exception" ? (
                <Badge
                  className="border-red-400/40 bg-red-500/20 text-red-100"
                  variant="outline"
                >
                  Exception
                </Badge>
              ) : null}
              {details.kind === "failure" && details.declaredByWork ? (
                <Tooltip delayDuration={300}>
                  <TooltipTrigger asChild>
                    <Badge
                      className="border-amber-400/40 bg-amber-500/20 text-amber-100"
                      variant="outline"
                    >
                      Marked by work
                    </Badge>
                  </TooltipTrigger>
                  <TooltipContent className="max-w-72 whitespace-normal text-left" side="top" sideOffset={6}>
                    <div className="text-sm leading-5">
                      The executor called{" "}
                      <span className="font-mono">IWorkExecutionContext.Fail(...)</span>{" "}
                      to stop this execution with a structured failure message.
                    </div>
                  </TooltipContent>
                </Tooltip>
              ) : null}
            </div>
            {retrySummary ? (
              <div className="flex flex-1 items-center justify-center text-amber-100 text-sm">
                <span className="truncate">Retrying • {retrySummary}</span>
              </div>
            ) : null}
          </div>
          <p className="text-sm leading-6 text-red-100">
            {details.message}
          </p>
          {details.code || details.target ? (
            <div className="flex flex-wrap items-center gap-2 text-xs">
              {details.code ? (
                <span className="rounded-md border border-red-400/20 bg-red-500/10 px-2 py-1 text-red-200/90">
                  Code: <span className="font-mono text-red-100">{details.code}</span>
                </span>
              ) : null}
              {details.target ? (
                <span className="rounded-md border border-red-400/20 bg-red-500/10 px-2 py-1 text-red-200/90">
                  Target: <span className="font-mono text-red-100">{details.target}</span>
                </span>
              ) : null}
            </div>
          ) : null}
          {details.kind === "exception" && details.exceptionType ? (
            <div className="font-mono text-xs text-red-200/85">
              {details.exceptionType}
            </div>
          ) : null}
        </div>
        {details.kind === "exception" && exceptionChain.some((item) => getStackTraceLines(item.stackTrace).length > 0) ? (
          <Button
            className="h-8 shrink-0 border-red-400/30 bg-red-500/10 text-red-100 hover:bg-red-500/20 hover:text-white"
            onClick={() => setStackOpen(true)}
            size="sm"
            type="button"
            variant="outline"
          >
            <Braces className="size-3.5" />
            Open stack
          </Button>
        ) : null}
      </div>
      <Dialog onOpenChange={setStackOpen} open={stackOpen && exceptionChain.length > 0}>
        <DialogContent className={`${stackMaximized
          ? "h-[92vh] max-h-[92vh] w-[96vw] max-w-[96vw] xl:w-[92vw] xl:max-w-[92vw]"
          : "max-h-[88vh] sm:max-w-4xl xl:max-w-6xl"} overflow-hidden border-red-400/20 bg-slate-950 p-0 text-slate-50`}>
          <Button
            aria-label={stackMaximized ? "Restore stack viewer" : "Maximize stack viewer"}
            className="absolute right-10 top-2 z-10 cursor-pointer text-slate-300 hover:bg-slate-800 hover:text-white"
            onClick={() => setStackMaximized((current) => !current)}
            size="icon-sm"
            type="button"
            variant="ghost"
          >
            {stackMaximized ? <Minimize2 className="size-4" /> : <Maximize2 className="size-4" />}
          </Button>
          <DialogHeader className="sr-only">
            <DialogTitle>Execution stack</DialogTitle>
            <DialogDescription>
              Review exception and inner-exception frames with app, Workable, and library filters.
            </DialogDescription>
          </DialogHeader>
          <div className="space-y-4 p-6">
            <div className="flex flex-wrap items-center justify-between gap-3">
              <div className="flex flex-wrap items-center gap-2 text-[11px] uppercase tracking-[0.16em] text-slate-400">
                <span>Call stack</span>
                {stackFrameFilterKinds.map((kind) => {
                  const hidden = hiddenStackFrameKinds.includes(kind);
                  return (
                    <button
                      className={`rounded-full border px-2 py-0.5 transition ${stackFrameFilterTone(kind, hidden)}`}
                      key={kind}
                      onClick={() => toggleStackFrameKind(kind)}
                      title={`${hidden ? "Show" : "Hide"} ${stackFrameKindLabel(kind)} stack frames`}
                      type="button"
                    >
                      {stackFrameKindLabel(kind)}
                    </button>
                  );
                })}
                {collapsedStackFrameTotal > 0 ? (
                  <span className="text-[10px] tracking-[0.12em] text-slate-500">
                    {collapsedStackFrameTotal} collapsed
                    {collapsedStackFrameCounts.work > 0 ? `, ${collapsedStackFrameCounts.work} workable` : ""}
                    {collapsedStackFrameCounts.library > 0 ? `, ${collapsedStackFrameCounts.library} library` : ""}
                    {collapsedStackFrameCounts.application > 0 ? `, ${collapsedStackFrameCounts.application} app` : ""}
                  </span>
                ) : null}
              </div>
            </div>
            <div className={`${stackMaximized ? "max-h-[calc(92vh-10rem)]" : "max-h-[70vh]"} space-y-4 overflow-auto pr-2`}>
              {exceptionChain.map((exceptionItem, exceptionIndex) => {
                const stackLines = getStackTraceLines(exceptionItem.stackTrace);
                const stackDisplayEntries = createStackTraceDisplayEntries(stackLines, hiddenStackFrameKinds);
                const label = exceptionIndex === 0 ? "Top-level exception" : `Inner exception ${exceptionIndex}`;
                return (
                  <div className="space-y-2" key={`${exceptionItem.exceptionType ?? "exception"}:${exceptionIndex}`}>
                    <div className="flex flex-wrap items-center gap-2">
                      <Badge className="border-red-400/30 bg-red-500/10 text-red-100" variant="outline">
                        {label}
                      </Badge>
                      {exceptionItem.exceptionType ? (
                        <span className="font-mono text-sm text-red-100">{exceptionItem.exceptionType}</span>
                      ) : null}
                    </div>
                    <div className="rounded-md border border-red-400/20 bg-red-500/10 px-3 py-2 text-sm text-red-50">
                      {exceptionItem.message}
                    </div>
                    {stackDisplayEntries.length > 0 ? (
                      <div className="space-y-1 font-mono text-sm">
                        {stackDisplayEntries.map((entry, index) =>
                          entry.type === "line" ? (
                            <div
                              className={`rounded-md border px-3 py-2 ${stackTraceLineTone(entry.kind)}`}
                              key={`${exceptionIndex}:${entry.line}:${index}`}
                            >
                              {entry.line}
                            </div>
                          ) : (
                            <button
                              className="w-full rounded-md border border-dashed border-slate-700/80 bg-slate-900/80 px-3 py-2 text-left text-xs uppercase tracking-[0.14em] text-slate-400 transition hover:border-slate-500/80 hover:bg-slate-800/90 hover:text-slate-200"
                              key={`${exceptionIndex}:collapsed:${index}`}
                              onClick={() => expandCollapsedStackEntry(entry)}
                              title="Show the hidden stack frames in this collapsed section"
                              type="button"
                            >
                              {formatCollapsedStackEntry(entry)}
                            </button>
                          )
                        )}
                      </div>
                    ) : (
                      <div className="rounded-md border border-slate-700 bg-slate-900 px-3 py-2 font-mono text-sm text-slate-400">
                        No stack trace available.
                      </div>
                    )}
                  </div>
                );
              })}
            </div>
          </div>
        </DialogContent>
      </Dialog>
    </section>
  );
}

function InlineFact({ label, value }: { label: string; value: string }) {
  return (
    <div className="rounded-md border border-dashed bg-background/70 px-3 py-2">
      <div className="text-muted-foreground text-[11px] uppercase tracking-[0.16em]">{label}</div>
      <div className="mt-1 font-mono text-xs">{value}</div>
    </div>
  );
}

function EmptyListState({ message }: { message: string }) {
  return (
    <div className="rounded-lg border border-dashed p-4 text-muted-foreground text-sm">
      {message}
    </div>
  );
}

function createWorkerTimelineItems(
  worker: WorkerSnapshot,
  activeIteration: WorkerIterationSnapshot | null,
  now: number,
  historicalStatusItems: WorkerTimelineItem[],
  retryTimelineState: WorkerRetryTimelineState | null,
  liveStatusItem: WorkerTimelineItem | null
): WorkerTimelineItem[] {
  const liveExecutingIterationSequence = liveStatusItem?.liveText?.kind === "iteration" &&
      liveStatusItem.liveText.status === "Executing"
    ? liveStatusItem.liveText.sequence
    : null;
  const iterations = getTimelineIterations(worker.iterations, activeIteration);
  const latestRetryingFailedIterationSequence = retryTimelineState
    ? getLatestRetryingFailedIterationSequence(iterations)
    : null;
  const iterationItems = iterations
    .flatMap((iteration) =>
      createIterationTimelineItems(
        iteration,
        now,
        workerTimelineExecutingIterationRowEnabled
      )
    )
    .filter((item) => item.id !== `iteration:${liveExecutingIterationSequence}`)
    .map((item) =>
      latestRetryingFailedIterationSequence !== null &&
          item.kind === "iteration" &&
          item.id === `iteration:${latestRetryingFailedIterationSequence}` &&
          item.failureDetails
        ? {
          ...item,
          failureDetails: {
            ...item.failureDetails,
            retryPending: {
              nextRunAt: retryTimelineState?.nextRunAt ?? null,
              retryAttempt: retryTimelineState?.retryAttempt ?? null,
              stateChangedAt: retryTimelineState?.stateChangedAt ?? null,
              updatedAt: retryTimelineState?.updatedAt ?? worker.updatedAt,
            },
          },
        }
        : item
    );
  const items: WorkerTimelineItem[] = [
    createQueuedTimelineItem(worker),
    ...historicalStatusItems.filter((item) => item.badge !== "Retrying"),
    ...(liveStatusItem ? [liveStatusItem] : []),
    ...createActionTimelineItems(worker, worker.actionHistory, true),
    ...iterationItems,
  ];

  return items.sort(compareTimelineItems);
}

function createWorkerTimelineLiveStatusItem(
  worker: WorkerSnapshot,
  activeIteration: WorkerIterationSnapshot | null,
  now: number
) {
  if (activeIteration?.status === "Executing") {
    return createExecutingIterationTimelineItem(activeIteration, now);
  }

  const currentStateItem = createWorkerStateTimelineItems(worker, now)[0];
  if (currentStateItem?.liveText?.kind === "state" && currentStateItem.liveText.mode === "retry") {
    return null;
  }

  if (currentStateItem) {
    return currentStateItem;
  }

  return null;
}

function getTimelineIterations(
  iterations: WorkerIterationSnapshot[] | null | undefined,
  activeIteration: WorkerIterationSnapshot | null
) {
  const merged = new Map<number, WorkerIterationSnapshot>();
  for (const iteration of getChronologicalIterations(iterations)) {
    merged.set(
      iteration.sequence,
      mergeTimelineIteration(merged.get(iteration.sequence), iteration)
    );
  }

  if (activeIteration) {
    merged.set(
      activeIteration.sequence,
      mergeTimelineIteration(merged.get(activeIteration.sequence), activeIteration)
    );
  }

  return [...merged.values()]
    .sort((left, right) => {
      if (left.sequence !== right.sequence) {
        return left.sequence - right.sequence;
      }

      return Date.parse(left.occurredAt) - Date.parse(right.occurredAt);
    });
}

function mergeTimelineIteration(
  existing: WorkerIterationSnapshot | undefined,
  candidate: WorkerIterationSnapshot
): WorkerIterationSnapshot {
  if (!existing) {
    return candidate;
  }

  const preferred = preferTimelineIteration(existing, candidate);
  const secondary = preferred === existing ? candidate : existing;

  return {
    ...secondary,
    ...preferred,
    completedAt: preferred.completedAt ?? secondary.completedAt,
    executionDuration: preferred.executionDuration ?? secondary.executionDuration,
    logs: mergeTimelineIterationLogs(preferred.logs, secondary.logs),
    messages: mergeTimelineIterationMessages(preferred.messages, secondary.messages),
    occurredAt: preferred.occurredAt || secondary.occurredAt,
    output: preferred.output ?? secondary.output,
    startedAt: preferred.startedAt ?? secondary.startedAt,
    status: preferred.status,
  };
}

function preferTimelineIteration(left: WorkerIterationSnapshot, right: WorkerIterationSnapshot) {
  const leftTerminal = left.status !== "Executing";
  const rightTerminal = right.status !== "Executing";
  if (leftTerminal !== rightTerminal) {
    return rightTerminal ? right : left;
  }

  const leftScore = scoreTimelineIterationCompleteness(left);
  const rightScore = scoreTimelineIterationCompleteness(right);
  if (leftScore !== rightScore) {
    return rightScore > leftScore ? right : left;
  }

  return parseTimelineTimestamp(right.completedAt ?? right.occurredAt) >=
      parseTimelineTimestamp(left.completedAt ?? left.occurredAt)
    ? right
    : left;
}

function scoreTimelineIterationCompleteness(iteration: WorkerIterationSnapshot) {
  let score = 0;
  if (iteration.completedAt) {
    score += 8;
  }
  if (iteration.executionDuration) {
    score += 4;
  }
  if ((iteration.messages?.length ?? 0) > 0) {
    score += 2;
  }
  if ((iteration.logs?.length ?? 0) > 0) {
    score += 2;
  }
  if (iteration.output !== undefined) {
    score += 1;
  }

  return score;
}

function mergeTimelineIterationMessages(
  preferred?: WorkMessage[] | null,
  secondary?: WorkMessage[] | null
): WorkMessage[] | undefined {
  if ((preferred?.length ?? 0) >= (secondary?.length ?? 0)) {
    return preferred ?? secondary ?? undefined;
  }

  return secondary ?? preferred ?? undefined;
}

function mergeTimelineIterationLogs(
  preferred?: WorkerLogEntry[] | null,
  secondary?: WorkerLogEntry[] | null
): WorkerLogEntry[] | undefined {
  if ((preferred?.length ?? 0) >= (secondary?.length ?? 0)) {
    return preferred ?? secondary ?? undefined;
  }

  return secondary ?? preferred ?? undefined;
}

function createQueuedTimelineItem(worker: WorkerSnapshot): WorkerTimelineItem {
  return {
    at: worker.createdAt,
    badge: "Queued",
    description: "",
    facts: [],
    filterKind: "system",
    icon: Send,
    id: `queue:${worker.id.value}`,
    kind: "queue",
    sortOrder: 0,
    title: "Worker queued",
    tone: "info",
  };
}

function createWorkerStateTimelineItems(worker: WorkerSnapshot, now: number): WorkerTimelineItem[] {
  const stateAt = worker.stateChangedAt ?? worker.updatedAt;

  switch (worker.state) {
    case "Canceled":
      if (getLatestRetainedIteration(worker)?.status === "Canceled") {
        return [];
      }

      return [{
        at: stateAt,
        badge: "Canceled",
        description: "",
        facts: [],
        filterKind: "system",
        icon: Ban,
        id: `state:canceled:${worker.stateSequence}`,
        kind: "state",
        sortOrder: 4,
        title: "Worker canceled",
        tone: "neutral",
      }];
    case "Paused":
      if (getLatestRetainedIteration(worker)?.status === "Paused") {
        return [];
      }

      return [{
        at: stateAt,
        badge: "Paused",
        description: "",
        facts: [],
        filterKind: "system",
        icon: Pause,
        id: `state:paused:${worker.stateSequence}`,
        kind: "state",
        sortOrder: 4,
        title: "Worker paused",
        tone: "warning",
      }];
    case "Retrying":
      {
        const description = describeWaitingTimelineItem(worker, now, "retry");
      return [{
        at: stateAt,
        badge: "Retrying",
        description,
        facts: [],
        filterKind: "system",
        icon: RotateCw,
        id: `state:retrying:${worker.stateSequence}`,
        kind: "state",
        liveText: {
          kind: "state",
          mode: "retry",
          nextRunAt: worker.nextRunAt ?? null,
          retryAttempt: worker.retryAttempt ?? null,
          stateChangedAt: worker.stateChangedAt,
          updatedAt: worker.updatedAt,
        },
        sortOrder: 4,
        stateMode: "retry",
        title: description,
        tone: "warning",
      }];
      }
    case "Waiting":
      {
        const description = describeWaitingTimelineItem(worker, now, "recurrence");
      return [{
        at: stateAt,
        badge: "Waiting",
        description,
        facts: [],
        filterKind: "system",
        icon: Clock3,
        id: `state:waiting:${worker.stateSequence}`,
        kind: "state",
        liveText: {
          kind: "state",
          mode: "recurrence",
          nextRunAt: worker.nextRunAt ?? null,
          stateChangedAt: worker.stateChangedAt,
          updatedAt: worker.updatedAt,
        },
        sortOrder: 4,
        stateMode: "recurrence",
        title: description,
        tone: "info",
      }];
      }
    default:
      return [];
  }
}

function getLatestRetainedIteration(worker: WorkerSnapshot) {
  return [...(worker.iterations ?? [])]
    .sort((left, right) => right.sequence - left.sequence)[0];
}

function getLatestRetryingFailedIterationSequence(iterations: WorkerIterationSnapshot[]) {
  return [...iterations]
    .filter((iteration) => iteration.status === "Failed")
    .sort((left, right) => {
      const timestampDifference = parseTimelineTimestamp(right.completedAt ?? right.occurredAt) -
        parseTimelineTimestamp(left.completedAt ?? left.occurredAt);
      if (timestampDifference !== 0) {
        return timestampDifference;
      }

      return right.sequence - left.sequence;
    })[0]?.sequence ?? null;
}

function createActionTimelineItems(
  worker: WorkerSnapshot,
  actionHistory?: WorkerActionHistoryEntry[] | null,
  includeResultingStateItems = true
): WorkerTimelineItem[] {
  return getChronologicalActionHistory(actionHistory)
    .flatMap((entry) => {
      const items: WorkerTimelineItem[] = [createActionTimelineItem(entry)];
      const resultingStateItem = includeResultingStateItems
        ? createActionResultStateTimelineItem(worker, entry)
        : null;
      if (resultingStateItem) {
        items.push(resultingStateItem);
      }

      return items;
    });
}

function createActionTimelineItem(entry: WorkerActionHistoryEntry): WorkerTimelineItem {
  const title = `${describeActionTimelineSubject(entry)} requested`;
  const statusPhrase = formatActionTimelineStatus(entry.status);
  const hasActor = hasTimelineActionActor(entry.origin);

  return {
    actorLabel: formatActionTimelineActorLabel(entry.origin),
    at: entry.occurredAt,
    badge: entry.status,
    description: `The request was ${statusPhrase}.`,
    facts: [],
    filterKind: hasTimelineActionActor(entry.origin) ? "user" : "system",
    icon: actionTimelineIcon(entry),
    id: `action:${entry.kind}:${entry.action ?? "none"}:${entry.occurredAt}:${entry.stateSequence}`,
    kind: "action",
    sortOrder: 1,
    sourceLabel: hasActor ? undefined : formatActionTimelineSourceLabel(entry.origin?.channel),
    sourceTooltip: hasActor ? undefined : formatActionTimelineSourceTooltip(entry.origin?.channel),
    title,
    tone: actionTimelineTone(entry),
  };
}

function createActionResultStateTimelineItem(
  worker: WorkerSnapshot,
  entry: WorkerActionHistoryEntry
): WorkerTimelineItem | null {
  if ((entry.status ?? "").trim() !== "Accepted") {
    return null;
  }

  if (worker.stateSequence === entry.stateSequence && worker.state === entry.state) {
    return null;
  }

  switch (entry.state) {
    case "Paused":
      return {
        at: entry.occurredAt,
        badge: "Paused",
        description: "",
        facts: [],
        filterKind: "system",
        icon: Pause,
        id: `action-state:paused:${entry.occurredAt}:${entry.stateSequence}`,
        kind: "state",
        sortOrder: 2,
        title: "Worker paused",
        tone: "warning",
      };
    case "Canceled":
      return {
        at: entry.occurredAt,
        badge: "Canceled",
        description: "",
        facts: [],
        filterKind: "system",
        icon: Ban,
        id: `action-state:canceled:${entry.occurredAt}:${entry.stateSequence}`,
        kind: "state",
        sortOrder: 2,
        title: "Worker canceled",
        tone: "neutral",
      };
    default:
      return null;
  }
}

function createIterationTimelineItems(
  iteration: WorkerIterationSnapshot,
  now: number,
  includeExecutingRow: boolean
): WorkerTimelineItem[] {
  const items: WorkerTimelineItem[] = [];
  const failureDetails = getIterationFailureDetails(iteration);
  const settledDuration = formatDurationLabel(iteration.executionDuration);

  if (iteration.status === "Executing") {
    if (!includeExecutingRow) {
      return items;
    }

    items.push(createExecutingIterationTimelineItem(iteration, now));
    return items;
  }

  items.push({
    at: iteration.completedAt ?? iteration.occurredAt,
    badge: iteration.status,
    description: describeIterationOutcome(iteration, now),
    failureDetails,
    facts: [],
    filterKind: iteration.status === "Failed" ? "failures" : "system",
    icon: iterationTimelineIcon(iteration.status),
    id: `iteration:${iteration.sequence}`,
    kind: "iteration",
    sortOrder: 3,
    title: `Iteration #${iteration.sequence} ${formatIterationTimelineStatus(iteration.status)} after ${settledDuration}`,
    tone: iterationTimelineTone(iteration.status),
  });

  return items;
}

function createExecutingIterationTimelineItem(
  iteration: WorkerIterationSnapshot,
  now: number
): WorkerTimelineItem {
  return {
    at: iteration.startedAt ?? iteration.occurredAt,
    badge: "Executing",
    description: describeIterationOutcome(iteration, now),
    failureDetails: null,
    facts: [],
    filterKind: "system",
    icon: Activity,
    id: `iteration:${iteration.sequence}`,
    kind: "iteration",
    liveText: {
      kind: "iteration",
      sequence: iteration.sequence,
      startedAt: iteration.startedAt,
      status: iteration.status,
    },
    sortOrder: 2,
    title: `Iteration #${iteration.sequence} has been executing for ${formatElapsedSince(iteration.startedAt, now)}`,
    tone: "info",
  };
}

function freezeWorkerTimelineItem(item: WorkerTimelineItem, now: number): WorkerTimelineItem {
  if (item.liveText?.kind === "state") {
    return {
      ...item,
      badge: item.liveText.mode === "recurrence" ? "Completed" : item.badge,
      description: "",
      id: `history|${item.id}|${item.at}`,
      liveText: undefined,
      title: describeHistoricalStateTimelineItem(item.liveText, now),
    };
  }

  return {
    ...item,
    description: renderTimelineItemDescription(item, now),
    id: `history|${item.id}|${item.at}`,
    liveText: undefined,
    title: renderTimelineItemTitle(item, now),
  };
}

function upsertTimelineStatusHistoryItem(
  items: WorkerTimelineItem[],
  nextItem: WorkerTimelineItem
) {
  const nextItemBaseId = nextItem.id.startsWith("history|")
    ? nextItem.id.split("|")[1] ?? nextItem.id
    : nextItem.id;
  const nextItems = items.filter((item) => {
    const itemBaseId = item.id.startsWith("history|")
      ? item.id.split("|")[1] ?? item.id
      : item.id;
    return itemBaseId !== nextItemBaseId;
  });
  nextItems.push(nextItem);
  return nextItems;
}

function shouldPersistLiveTimelineItem(item: WorkerTimelineItem) {
  return item.liveText?.kind === "state" && item.liveText.mode === "recurrence";
}

function describeIterationOutcome(iteration: WorkerIterationSnapshot, now: number) {
  const duration = iteration.status === "Executing"
    ? formatElapsedSince(iteration.startedAt, now)
    : formatDurationLabel(iteration.executionDuration);

  switch (iteration.status) {
    case "Completed":
      return `Iteration #${iteration.sequence} finished successfully after ${duration}.`;
    case "Failed":
      return `Iteration #${iteration.sequence} ended in failure after ${duration}.`;
    case "Canceled":
      return `Iteration #${iteration.sequence} was canceled after ${duration}.`;
    case "Interrupted":
      return `Iteration #${iteration.sequence} was interrupted after ${duration}.`;
    case "Paused":
      return `Iteration #${iteration.sequence} paused after ${duration}.`;
    case "Executing":
      return `Iteration #${iteration.sequence} has been executing for ${duration}.`;
    default:
      return `Iteration #${iteration.sequence} changed to ${iteration.status.toLowerCase()} after ${duration}.`;
  }
}

function describeWaitingTimelineItem(
  worker: Pick<WorkerSummary, "nextRunAt" | "retryAttempt" | "stateChangedAt" | "updatedAt">,
  now: number,
  mode: "recurrence" | "retry"
) {
  const retryLabel = `Retry #${worker.retryAttempt ?? "?"}`;

  if (worker.nextRunAt) {
    return mode === "retry"
      ? `${retryLabel} is scheduled for ${formatFutureRelativeTime(worker.nextRunAt, now)}.`
      : `The next recurrence is scheduled for ${formatFutureRelativeTime(worker.nextRunAt, now)}.`;
  }

  const elapsed = formatElapsedSince(worker.stateChangedAt ?? worker.updatedAt, now);
  return mode === "retry"
    ? `${retryLabel} has been waiting for ${elapsed}.`
    : `This worker has been waiting for the next recurrence for ${elapsed}.`;
}

function describeHistoricalStateTimelineItem(
  liveText: Extract<WorkerTimelineLiveText, { kind: "state" }>,
  now: number
) {
  const elapsed = formatElapsedSince(liveText.stateChangedAt ?? liveText.updatedAt, now);
  return liveText.mode === "retry"
    ? `Retry #${liveText.retryAttempt ?? "?"} waited ${elapsed} before restarting.`
    : `Waited ${elapsed} before the next recurrence.`;
}

function formatPendingRetryText(
  retryPending: NonNullable<WorkerFailureDetails["retryPending"]>,
  now?: number
) {
  const effectiveNow = now ?? Date.now();
  const retryLabel = `Retry #${retryPending.retryAttempt ?? "?"}`;
  if (retryPending.nextRunAt) {
    return `${retryLabel} is scheduled for ${formatFutureRelativeTime(retryPending.nextRunAt, effectiveNow)}.`;
  }

  return `${retryLabel} is pending.`;
}

function formatFutureRelativeTime(value: string, now: number) {
  const timestamp = Date.parse(value);
  if (Number.isFinite(timestamp) && timestamp <= now) {
    return "0.00s";
  }

  const relative = formatRelativeTime(value, now);
  return relative.startsWith("in ") ? relative.slice(3) : "0.00s";
}

function formatWorkerStatusTiming(worker: WorkerSnapshot, now: number) {
  switch (worker.state) {
    case "Running":
    case "Failed":
      return formatElapsedSince(worker.stateChangedAt ?? worker.updatedAt, now);
    case "Waiting":
    case "Retrying":
      return worker.nextRunAt
        ? formatFutureRelativeTime(worker.nextRunAt, now)
        : formatElapsedSince(worker.stateChangedAt ?? worker.updatedAt, now);
    default:
      return null;
  }
}

function renderTimelineItemTitle(item: WorkerTimelineItem, now: number) {
  if (item.liveText?.kind === "iteration" && item.liveText.status === "Executing") {
    return `Iteration #${item.liveText.sequence} has been executing for ${formatElapsedSince(item.liveText.startedAt, now)}`;
  }

  return item.title;
}

function renderTimelineItemDescription(item: WorkerTimelineItem, now: number) {
  if (item.liveText?.kind === "state") {
    return describeWaitingTimelineItem(
      {
        nextRunAt: item.liveText.nextRunAt ?? undefined,
        retryAttempt: item.liveText.retryAttempt ?? undefined,
        stateChangedAt: item.liveText.stateChangedAt ?? undefined,
        updatedAt: item.liveText.updatedAt,
      },
      now,
      item.liveText.mode
    );
  }

  return item.description;
}

function shouldRenderTimelineStateDescription(
  item: WorkerTimelineItem,
  title: string,
  description: string
) {
  return item.kind === "state" &&
    item.liveText?.kind !== "state" &&
    description.trim().length > 0 &&
    description !== title;
}

function renderTimelineItemMeta(item: WorkerTimelineItem, now: number) {
  if (item.liveText?.kind === "state") {
    return item.liveText.nextRunAt
      ? formatFutureRelativeTime(item.liveText.nextRunAt, now)
      : formatElapsedSince(item.liveText.stateChangedAt ?? item.liveText.updatedAt, now);
  }

  if (item.liveText?.kind === "iteration" && item.liveText.status === "Executing") {
    return formatElapsedSince(item.liveText.startedAt, now);
  }

  return formatCompactTimelineRelativeTime(item.at, now);
}

function createTimelineRows(items: WorkerTimelineItem[]): WorkerTimelineRow[] {
  const rows: WorkerTimelineRow[] = [];

  for (let index = 0; index < items.length; index++) {
    const item = items[index];
    const previousItem = items[index - 1];
    if (item && previousItem && shouldShowTimelineGap(previousItem, item)) {
      const gapMilliseconds = Math.abs(parseTimelineTimestamp(previousItem.at) - parseTimelineTimestamp(item.at));
      rows.push({
        kind: "gap",
        id: `gap:${previousItem.id}:${item.id}`,
        liveSinceAt: isLiveTimelineGap(previousItem) ? item.at : undefined,
        milliseconds: gapMilliseconds,
      });
    }

    if (!item) {
      continue;
    }

    rows.push({ kind: "item", item });
  }

  return rows;
}

function shouldShowTimelineGap(currentItem: WorkerTimelineItem, nextItem: WorkerTimelineItem) {
  return Boolean(currentItem && nextItem) &&
    !(
    isRecurrenceTimelineWaitItem(currentItem) &&
    nextItem.kind === "iteration"
    );
}

function captureTimelineScrollAnchor(
  rows: WorkerTimelineRow[],
  container: HTMLDivElement,
  rowElements: Map<string, HTMLDivElement>
) {
  const containerRect = container.getBoundingClientRect();
  const threshold = containerRect.top + 12;
  let fallbackAnchor: { key: string; top: number } | null = null;

  for (const row of rows) {
    const key = row.kind === "gap" ? row.id : row.item.id;
    const element = rowElements.get(key);
    if (!element) {
      continue;
    }

    const rect = element.getBoundingClientRect();
    if (rect.bottom > threshold) {
      const anchor = {
        key,
        top: rect.top - containerRect.top,
      };
      if (!fallbackAnchor) {
        fallbackAnchor = anchor;
      }
      if (isStableTimelineAnchorRow(row)) {
        return anchor;
      }
    }
  }

  return fallbackAnchor;
}

function isStableTimelineAnchorRow(row: WorkerTimelineRow) {
  if (row.kind !== "item") {
    return false;
  }

  if (row.item.failureDetails) {
    return true;
  }

  return !row.item.liveText;
}

function isLiveTimelineGap(item: WorkerTimelineItem) {
  return item.liveText?.kind === "state" ||
    (item.liveText?.kind === "iteration" && item.liveText.status === "Executing");
}

function formatIterationTimelineStatus(status: WorkCompletionStatus) {
  switch (status) {
    case "Completed":
      return "completed";
    case "Failed":
      return "failed";
    case "Canceled":
      return "canceled";
    case "Interrupted":
      return "interrupted";
    case "Paused":
      return "paused";
    case "Executing":
      return "executing";
    default:
      return status.toLowerCase();
  }
}

function getChronologicalIterations(iterations?: WorkerIterationSnapshot[] | null) {
  return [...(iterations ?? [])]
    .sort((left, right) => {
      if (left.sequence !== right.sequence) {
        return left.sequence - right.sequence;
      }

      return Date.parse(left.occurredAt) - Date.parse(right.occurredAt);
    });
}

function getChronologicalActionHistory(actionHistory?: WorkerActionHistoryEntry[] | null) {
  return [...(actionHistory ?? [])]
    .sort((left, right) => Date.parse(left.occurredAt) - Date.parse(right.occurredAt));
}

function parseTimelineTimestamp(value: string) {
  const timestamp = Date.parse(value);
  return Number.isFinite(timestamp) ? timestamp : 0;
}

function compareTimelineItems(left: WorkerTimelineItem, right: WorkerTimelineItem) {
  const timestampDifference = parseTimelineTimestamp(right.at) - parseTimelineTimestamp(left.at);
  if (timestampDifference !== 0) {
    return timestampDifference;
  }

  return right.sortOrder - left.sortOrder;
}

function sortTimelineItems(items: WorkerTimelineItem[], direction: WorkerSortDirection) {
  const sorted = [...items].sort(compareTimelineItems);
  return direction === "desc" ? sorted : sorted.reverse();
}

function sortWorkerLogEntries(entries: WorkerLogEntry[], direction: WorkerSortDirection) {
  const sorted = [...entries].sort((left, right) => compareWorkerLogEntries(right, left));
  return direction === "desc" ? sorted : sorted.reverse();
}

function describeActionTimelineSubject(entry: WorkerActionHistoryEntry) {
  if (entry.kind === "Reconfiguration") {
    return "Reconfiguration";
  }

  return entry.action ?? "Action";
}

function formatActionTimelineActorLabel(origin?: WorkableRealtimeOrigin | null) {
  const actor = origin?.actor;
  const name = actor?.name?.trim();
  const id = actor?.id?.trim();
  const channel = formatActionTimelineSourceLabel(origin?.channel);

  if (name) {
    return `${name} via ${channel}`;
  }

  if (id) {
    return `${id} via ${channel}`;
  }

  return undefined;
}

function formatActionTimelineSourceLabel(channel?: string | null) {
  switch ((channel ?? "").trim()) {
    case "DotNet":
      return ".NET";
    case "HttpApi":
      return "HTTP";
    case "Mcp":
      return "MCP";
    case "SignalR":
      return "SignalR";
    default:
      return channel?.trim() || "System";
  }
}

function formatActionTimelineSourceTooltip(channel?: string | null) {
  switch ((channel ?? "").trim()) {
    case "DotNet":
      return "Requested through in-process .NET code.";
    case "HttpApi":
      return "Requested through the Workable HTTP API.";
    case "Mcp":
      return "Requested through the Workable MCP server.";
    case "SignalR":
      return "Requested through a Workable SignalR connection.";
    default:
      return "";
  }
}

function formatActionTimelineStatus(status?: string | null) {
  const trimmed = status?.trim();
  if (!trimmed) {
    return "processed";
  }

  return trimmed
    .replace(/([a-z])([A-Z])/g, "$1 $2")
    .toLowerCase();
}

function hasTimelineActionActor(origin?: WorkableRealtimeOrigin | null) {
  return Boolean(
    origin?.actor?.name?.trim() ||
    origin?.actor?.id?.trim()
  );
}

const workerTimelineFilterKinds: WorkerTimelineFilterKind[] = ["user", "system", "failures"];

function shouldIncludeTimelineItemForFilters(
  item: WorkerTimelineItem,
  selectedFilters: Set<WorkerTimelineFilterKind>,
  hideRecurrenceWaitItems: boolean
) {
  if (item.kind === "queue") {
    return true;
  }

  if (!item.filterKind || !selectedFilters.has(item.filterKind)) {
    return false;
  }

  if (hideRecurrenceWaitItems && isRecurrenceTimelineWaitItem(item)) {
    return false;
  }

  return true;
}

function isRecurrenceTimelineWaitItem(item: WorkerTimelineItem) {
  if (item.kind !== "state") {
    return false;
  }

  if (item.stateMode === "recurrence") {
    return true;
  }

  if (item.liveText?.kind === "state" && item.liveText.mode === "recurrence") {
    return true;
  }

  return item.title.includes("next recurrence");
}

function workerTimelineFilterLabel(filterKind: WorkerTimelineFilterKind) {
  switch (filterKind) {
    case "user":
      return "User";
    case "system":
      return "System";
    case "failures":
      return "Failures";
    default:
      return filterKind;
  }
}

function workerTimelineFilterTone(filterKind: WorkerTimelineFilterKind) {
  switch (filterKind) {
    case "user":
      return "border-sky-500/30 bg-sky-500/10 text-sky-700 dark:text-sky-200";
    case "system":
      return "border-emerald-500/30 bg-emerald-500/10 text-emerald-700 dark:text-emerald-200";
    case "failures":
      return "border-red-500/30 bg-red-500/10 text-red-700 dark:text-red-200";
    default:
      return "border-border bg-muted/40 text-foreground";
  }
}

function actionTimelineIcon(entry: WorkerActionHistoryEntry) {
  if (entry.kind === "Reconfiguration") {
    return Clock3;
  }

  switch (entry.action) {
    case "Start":
      return Play;
    case "Pause":
      return Pause;
    case "Cancel":
    case "Purge":
      return Ban;
    case "Push":
      return Clock3;
    default:
      return Clock3;
  }
}

function actionTimelineTone(entry: WorkerActionHistoryEntry): WorkerTimelineItem["tone"] {
  switch ((entry.status ?? "").trim()) {
    case "Accepted":
      if (entry.kind === "Reconfiguration") {
        return "info";
      }

      switch (entry.action) {
        case "Start":
          return "success";
        case "Pause":
          return "warning";
        case "Cancel":
        case "Purge":
          return "danger";
        default:
          return "info";
      }
    case "Conflict":
      return "warning";
    case "Invalid":
    case "Unauthorized":
    case "NotFound":
      return "danger";
    default:
      return "neutral";
  }
}

function getActiveIteration(iterations?: WorkerIterationSnapshot[] | null) {
  return getSortedIterations(iterations)
    .find((iteration) => iteration.status === "Executing") ?? null;
}

function getLatestIteration(iterations?: WorkerIterationSnapshot[] | null) {
  return getSortedIterations(iterations)[0] ?? null;
}

function getIterationFailureDetails(iteration: WorkerIterationSnapshot): WorkerFailureDetails | null {
  if (iteration.status !== "Failed") {
    return null;
  }

  return resolveWorkerFailureDetails(
    iteration.messages,
    iteration.logs,
    "The retained iteration ended in failure."
  );
}

function getWorkerFailureDetails(
  worker: WorkerSnapshot,
  latestIteration?: WorkerIterationSnapshot | null
) : WorkerFailureDetails {
  const source = latestIteration?.messages?.length
    ? latestIteration.messages
    : worker.messages ?? [];

  return resolveWorkerFailureDetails(
    source,
    latestIteration?.logs,
    "The latest retained execution ended in failure."
  );
}

function resolveWorkerFailureDetails(
  messages: WorkMessage[] | null | undefined,
  logs: WorkerLogEntry[] | null | undefined,
  fallbackMessage: string
): WorkerFailureDetails {
  const errorMessage = (messages ?? []).find((message) => normalizeMessageSeverity(message.severity) === "error");
  const metadata = getWorkMessageMetadata(errorMessage);
  const code = errorMessage?.code?.trim() || undefined;
  const declaredByWork = readMessageMetadataString(metadata, "failureSource") === "executionContext";
  const target = errorMessage?.target?.trim() || undefined;
  const exceptionType = formatExceptionTypeName(readMessageMetadataString(metadata, "exceptionType"));
  const exceptionMessage = sanitizeWorkerFailureText(
    readMessageMetadataString(metadata, "exceptionMessage") || errorMessage?.text
  );
  const stackTrace = readMessageMetadataString(metadata, "exceptionStackTrace");
  const innerExceptions = readInnerExceptions(metadata);
  if (exceptionType) {
    return {
      code,
      declaredByWork,
      exceptionType,
      innerExceptions,
      kind: "exception",
      message: exceptionMessage || "The execution failed because an exception was raised.",
      stackTrace: stackTrace?.trim() || undefined,
      target,
    };
  }

  const latestFailureLog = [...(logs ?? [])]
    .reverse()
    .find((entry) =>
      normalizeLogLevel(entry.level) === "Error" ||
      normalizeLogLevel(entry.level) === "Critical"
    );
  const fallbackExceptionType = formatExceptionTypeName(latestFailureLog?.exceptionType);
  const fallbackExceptionMessage = sanitizeWorkerFailureText(latestFailureLog?.exceptionMessage);
  if (fallbackExceptionType || fallbackExceptionMessage) {
    return {
      code,
      declaredByWork,
      exceptionType: fallbackExceptionType || undefined,
      kind: fallbackExceptionType ? "exception" : "failure",
      message: fallbackExceptionMessage || fallbackMessage,
      target,
    };
  }

  return {
    code,
    declaredByWork,
    kind: "failure",
    message: sanitizeWorkerFailureText(errorMessage?.text) || fallbackMessage,
    target,
  };
}

function getSortedIterations(iterations?: WorkerIterationSnapshot[] | null) {
  return [...(iterations ?? [])]
    .sort((left, right) => {
      if (left.sequence !== right.sequence) {
        return right.sequence - left.sequence;
      }

      return Date.parse(right.occurredAt) - Date.parse(left.occurredAt);
    });
}

function mergeWorkerLogEntries(
  primary: WorkerLogEntry[],
  secondary: WorkerLogEntry[],
  maxEntries = workerExecutionLogStreamLimit
) {
  const merged = new Map<string, WorkerLogEntry>();
  [...primary, ...secondary].forEach((entry) => {
    merged.set(getWorkerLogEntryKey(entry), entry);
  });

  return [...merged.values()]
    .sort((left, right) => compareWorkerLogEntries(left, right))
    .slice(-maxEntries);
}

function filterWorkerLogEntries(entries: WorkerLogEntry[], hiddenLevels: Set<string>) {
  if (hiddenLevels.size === 0) {
    return entries;
  }

  return entries.filter((entry) => !hiddenLevels.has(normalizeLogLevel(entry.level)));
}

function createHiddenLogLevelsForFocus(
  levels: string[],
  focusLevel: "Error" | "Information" | "Warning"
) {
  const allowedLevels = focusLevel === "Error"
    ? new Set(["Critical", "Error"])
    : new Set([focusLevel]);

  return new Set(
    levels
      .map((level) => normalizeLogLevel(level))
      .filter((level) => !allowedLevels.has(level))
  );
}

function compareWorkerLogEntries(left: WorkerLogEntry, right: WorkerLogEntry) {
  const timestampDifference = Date.parse(left.occurredAt) - Date.parse(right.occurredAt);
  if (timestampDifference !== 0) {
    return timestampDifference;
  }

  return getWorkerLogEntryKey(left).localeCompare(getWorkerLogEntryKey(right));
}

function getWorkerLogEntryKey(entry: WorkerLogEntry) {
  return [
    entry.occurredAt,
    entry.category,
    entry.level,
    entry.eventId?.id ?? "",
    entry.eventId?.name ?? "",
    entry.message,
    entry.exceptionType ?? "",
    entry.exceptionMessage ?? "",
  ].join("|");
}

function formatWorkerLogEventId(
  eventId?: WorkerLogEntry["eventId"]
) {
  if (!eventId) {
    return "";
  }

  if (eventId.id === 0 && !eventId.name) {
    return "";
  }

  return eventId.name
    ? `${eventId.name} (${eventId.id})`
    : eventId.id.toString();
}

function getOrderedLogLevels(entries: WorkerLogEntry[]) {
  const preferredOrder = ["Critical", "Error", "Warning", "Information", "Debug", "Trace"];
  const available = new Set(entries.map((entry) => normalizeLogLevel(entry.level)));

  return [
    ...preferredOrder.filter((level) => available.has(level)),
    ...[...available].filter((level) => !preferredOrder.includes(level)).sort(),
  ];
}

function normalizeLogLevel(level: string) {
  const normalized = level.trim().toLowerCase();
  switch (normalized) {
    case "critical":
      return "Critical";
    case "error":
      return "Error";
    case "warning":
      return "Warning";
    case "information":
      return "Information";
    case "debug":
      return "Debug";
    case "trace":
      return "Trace";
    default:
      return level.trim() || "Unknown";
  }
}

function formatWorkerLogTimestamp(value: string) {
  const timestamp = Date.parse(value);
  if (!Number.isFinite(timestamp)) {
    return value;
  }

  return new Intl.DateTimeFormat([], {
    hour: "2-digit",
    minute: "2-digit",
    second: "2-digit",
  }).format(timestamp);
}

function formatTimeOfDay(value: string | null | undefined) {
  if (!value) {
    return "-";
  }

  const timestamp = Date.parse(value);
  if (!Number.isFinite(timestamp)) {
    return value;
  }

  return new Intl.DateTimeFormat([], {
    hour: "2-digit",
    minute: "2-digit",
    second: "2-digit",
  }).format(timestamp);
}

function consoleLogLevelTone(level: string) {
  switch (normalizeLogLevel(level)) {
    case "Trace":
    case "Debug":
      return "text-slate-500";
    case "Information":
      return "text-sky-300";
    case "Warning":
      return "text-amber-300";
    case "Error":
    case "Critical":
      return "text-rose-300";
    default:
      return "text-slate-300";
  }
}

function logLevelFilterTone(level: string) {
  switch (normalizeLogLevel(level)) {
    case "Critical":
    case "Error":
      return "border-rose-500/30 bg-rose-500/10 text-rose-700 dark:text-rose-200";
    case "Warning":
      return "border-amber-500/30 bg-amber-500/10 text-amber-800 dark:text-amber-100";
    case "Information":
      return "border-sky-500/30 bg-sky-500/10 text-sky-700 dark:text-sky-200";
    case "Debug":
    case "Trace":
      return "border-slate-400/40 bg-slate-500/10 text-slate-700 dark:text-slate-200";
    default:
      return "border-border bg-muted/40 text-foreground";
  }
}

function shouldDefaultToTimeline(worker: WorkerSnapshot) {
  return worker.state === "Completed" ||
    worker.state === "Canceled" ||
    worker.configuration?.recurrence?.isEnabled === true;
}

const emptyAvailableWorkerActions: Record<WorkAction, boolean> = {
  Start: false,
  Pause: false,
  Cancel: false,
  Push: false,
  Purge: false,
};

function getAvailableWorkerActions(state: WorkerState): Record<WorkAction, boolean> {
  if (state === "Pausing" || state === "Canceling" || state === "Interrupting") {
    return emptyAvailableWorkerActions;
  }

  return {
    Start: state === "Queued" || state === "Paused" || state === "Failed",
    Pause: state === "Running" || state === "Waiting" || state === "Retrying",
    Cancel: state !== "Canceled" && state !== "Completed",
    Push: state === "Waiting" || state === "Retrying",
    Purge: state === "Canceled" || state === "Completed",
  };
}

function formatCompactTimelineRelativeTime(value: string | null | undefined, now: number) {
  if (!value) {
    return "-";
  }

  const timestamp = Date.parse(value);
  if (!Number.isFinite(timestamp)) {
    return "-";
  }

  const absoluteSeconds = Math.max(0, Math.abs((now - timestamp) / 1000));
  const units = [
    { label: "s", max: 60, seconds: 1 },
    { label: "m", max: 60, seconds: 60 },
    { label: "h", max: 24, seconds: 60 * 60 },
    { label: "d", max: 7, seconds: 24 * 60 * 60 },
    { label: "w", max: 4, seconds: 7 * 24 * 60 * 60 },
    { label: "M", max: 12, seconds: 30 * 24 * 60 * 60 },
    { label: "y", max: Number.POSITIVE_INFINITY, seconds: 365 * 24 * 60 * 60 },
  ] as const;

  for (const unit of units) {
    const valueInUnit = absoluteSeconds / unit.seconds;
    if (valueInUnit < unit.max) {
      return `${valueInUnit.toFixed(2)}${unit.label}`;
    }
  }

  return "99.99y";
}

function formatElapsedSince(value: string | null | undefined, now: number) {
  if (!value) {
    return "-";
  }

  const timestamp = Date.parse(value);
  if (!Number.isFinite(timestamp)) {
    return "-";
  }

  return formatMillisecondsCompact(Math.max(0, now - timestamp));
}

function formatDurationLabel(value: string | null | undefined) {
  const milliseconds = parseDurationMilliseconds(value);
  return milliseconds === null ? (value?.trim() || "-") : formatMillisecondsCompact(milliseconds);
}

function getIterationDurationMilliseconds(iteration: WorkerIterationSnapshot, now: number) {
  if (iteration.status === "Executing" && iteration.startedAt) {
    return Math.max(0, now - parseTimelineTimestamp(iteration.startedAt));
  }

  const parsedDuration = parseDurationMilliseconds(iteration.executionDuration);
  if (parsedDuration !== null) {
    return parsedDuration;
  }

  if (iteration.startedAt && iteration.completedAt) {
    return Math.max(0, parseTimelineTimestamp(iteration.completedAt) - parseTimelineTimestamp(iteration.startedAt));
  }

  if (iteration.startedAt) {
    return Math.max(0, now - parseTimelineTimestamp(iteration.startedAt));
  }

  return null;
}

function parseDurationMilliseconds(value: string | null | undefined) {
  if (!value?.trim()) {
    return null;
  }

  const sanitized = value.trim();
  const match = sanitized.match(
    /^(-)?(?:(\d+)\.)?(\d{1,2}):(\d{2}):(\d{2})(?:\.(\d+))?$/
  );
  if (!match) {
    return null;
  }

  const negative = match[1] === "-";
  const days = Number(match[2] ?? 0);
  const hours = Number(match[3]);
  const minutes = Number(match[4]);
  const seconds = Number(match[5]);
  const fraction = match[6] ? Number(`0.${match[6]}`) : 0;
  const totalMilliseconds =
    (((days * 24 + hours) * 60 + minutes) * 60 + seconds + fraction) * 1000;
  return Math.round(negative ? -totalMilliseconds : totalMilliseconds);
}

function formatMillisecondsCompact(value: number) {
  const absolute = Math.abs(value);
  if (absolute < 10_000) {
    return `${(value / 1000).toFixed(2)}s`;
  }

  const totalSeconds = Math.floor(value / 1000);
  if (Math.abs(totalSeconds) < 60) {
    return `${totalSeconds}s`;
  }

  const totalMinutes = Math.floor(totalSeconds / 60);
  const seconds = Math.abs(totalSeconds % 60);
  if (Math.abs(totalMinutes) < 60) {
    return `${totalMinutes}m ${seconds}s`;
  }

  const totalHours = Math.floor(totalMinutes / 60);
  const minutes = Math.abs(totalMinutes % 60);
  return `${totalHours}h ${minutes}m`;
}

function timelineIconTone(item: WorkerTimelineItem) {
  switch (item.tone) {
    case "success":
      return "border-emerald-500/40 bg-emerald-500/10 text-emerald-600 dark:text-emerald-300";
    case "danger":
      return "border-red-500/40 bg-red-500/10 text-red-600 dark:text-red-300";
    case "warning":
      return "border-amber-500/40 bg-amber-500/10 text-amber-600 dark:text-amber-300";
    case "info":
      return "border-sky-500/40 bg-sky-500/10 text-sky-600 dark:text-sky-300";
    default:
      return "border-border bg-muted/40 text-muted-foreground";
  }
}

function timelineBadgeTone(tone: WorkerTimelineItem["tone"]) {
  switch (tone) {
    case "success":
      return "border-emerald-500/40 bg-emerald-500/10 text-emerald-700 dark:text-emerald-200";
    case "danger":
      return "border-red-500/40 bg-red-500/10 text-red-700 dark:text-red-200";
    case "warning":
      return "border-amber-500/40 bg-amber-500/10 text-amber-700 dark:text-amber-200";
    case "info":
      return "border-sky-500/40 bg-sky-500/10 text-sky-700 dark:text-sky-200";
    default:
      return "border-border bg-muted/40 text-foreground";
  }
}

function iterationTimelineTone(status: WorkCompletionStatus): WorkerTimelineItem["tone"] {
  switch (status) {
    case "Executing":
      return "info";
    case "Completed":
      return "success";
    case "Failed":
      return "danger";
    case "Paused":
      return "warning";
    case "Interrupted":
      return "warning";
    case "Canceled":
      return "neutral";
    default:
      return "neutral";
    }
}

function iterationTimelineIcon(status: WorkCompletionStatus) {
  switch (status) {
    case "Completed":
      return CheckCircle2;
    case "Failed":
    case "Canceled":
      return Ban;
    case "Paused":
      return Pause;
    case "Executing":
      return Activity;
    default:
      return Clock3;
  }
}

function normalizeMessageSeverity(severity: string) {
  return severity.trim().toLowerCase();
}

function getWorkMessageMetadata(message?: WorkMessage) {
  return message?.metadata && typeof message.metadata === "object"
    ? message.metadata
    : null;
}

function readMessageMetadataString(
  metadata: Record<string, unknown> | null,
  key: string
) {
  const value = metadata?.[key];
  return typeof value === "string" && value.trim()
    ? value
    : "";
}

function readInnerExceptions(metadata: Record<string, unknown> | null): WorkerFailureException[] {
  const value = metadata?.innerExceptions;
  if (!Array.isArray(value)) {
    return [];
  }

  const exceptions: WorkerFailureException[] = [];

  for (const entry of value) {
    if (!entry || typeof entry !== "object") {
      continue;
    }

    const record = entry as Record<string, unknown>;
    const exceptionType = formatExceptionTypeName(readUnknownString(record.exceptionType));
    const message = sanitizeWorkerFailureText(readUnknownString(record.exceptionMessage));
    const stackTrace = readUnknownString(record.exceptionStackTrace).trim();
    if (!exceptionType && !message && !stackTrace) {
      continue;
    }

    exceptions.push({
      exceptionType: exceptionType || undefined,
      message: message || "Inner exception",
      stackTrace: stackTrace || undefined,
    });
  }

  return exceptions;
}

function readUnknownString(value: unknown) {
  return typeof value === "string" ? value : "";
}

function formatExceptionTypeName(value?: string | null) {
  if (!value?.trim()) {
    return "";
  }

  const trimmed = value.trim();
  const segments = trimmed.split(".");
  return segments[segments.length - 1] ?? trimmed;
}

function sanitizeWorkerFailureText(value?: string | null) {
  if (!value?.trim()) {
    return "";
  }

  return value
    .replace(/\s+after \d+ log entries?\.?$/i, ".")
    .replace(/\s+/g, " ")
    .trim();
}

function getStackTraceLines(value?: string | null) {
  return value
    ? value
      .split(/\r?\n/)
      .map((line) => line.trimEnd())
      .filter(Boolean)
    : [];
}

function createStackTraceDisplayEntries(
  lines: string[],
  hiddenKinds: StackFrameFilterKind[]
): StackTraceDisplayEntry[] {
  const entries: StackTraceDisplayEntry[] = [];
  let collapsedCounts = createCollapsedStackCounts();

  const flushCollapsed = () => {
    const total = collapsedCounts.application + collapsedCounts.work + collapsedCounts.library;
    if (total > 0) {
      entries.push({
        counts: collapsedCounts,
        total,
        type: "collapsed",
      });
      collapsedCounts = createCollapsedStackCounts();
    }
  };

  lines.forEach((line) => {
    const kind = classifyStackTraceLine(line);
    if (kind !== "detail" && hiddenKinds.includes(kind)) {
      collapsedCounts[kind] += 1;
      return;
    }

    flushCollapsed();
    entries.push({
      kind,
      line,
      type: "line",
    });
  });

  flushCollapsed();
  return entries;
}

function createCollapsedStackCounts(): Record<StackFrameFilterKind, number> {
  return {
    application: 0,
    library: 0,
    work: 0,
  };
}

function formatCollapsedStackEntry(entry: Extract<StackTraceDisplayEntry, { type: "collapsed" }>) {
  const parts = [
    entry.counts.application > 0 ? `${entry.counts.application} app` : "",
    entry.counts.work > 0 ? `${entry.counts.work} workable` : "",
    entry.counts.library > 0 ? `${entry.counts.library} library` : "",
  ].filter(Boolean);

  return `${entry.total} collapsed frame${entry.total === 1 ? "" : "s"}${parts.length > 0 ? `: ${parts.join(", ")}` : ""}`;
}

function classifyStackTraceLine(line: string) {
  const trimmed = line.trim();
  if (!trimmed.startsWith("at ")) {
    return "detail" as const;
  }

  if (
    /^at Workable\./.test(trimmed)
  ) {
    return "work" as const;
  }

  if (
    /^at (System|Microsoft)\./.test(trimmed)
  ) {
    return "library" as const;
  }

  return "application" as const;
}

function stackFrameKindLabel(kind: StackFrameFilterKind) {
  switch (kind) {
    case "application":
      return "App";
    case "work":
      return "Workable";
    case "library":
      return "Library";
    default:
      return kind;
  }
}

function stackFrameFilterTone(kind: StackFrameFilterKind, hidden: boolean) {
  const hiddenTone = "border-slate-700 bg-transparent text-slate-500";
  if (hidden) {
    return hiddenTone;
  }

  switch (kind) {
    case "application":
      return "border-emerald-500/30 bg-emerald-500/10 text-emerald-200";
    case "work":
      return "border-cyan-400/40 bg-cyan-500/15 text-cyan-100";
    case "library":
      return "border-amber-400/40 bg-amber-500/15 text-amber-100";
    default:
      return hiddenTone;
  }
}

function stackTraceLineTone(kind: "application" | "detail" | "library" | "work") {
  switch (kind) {
    case "application":
      return "border-emerald-500/20 bg-emerald-500/10 text-emerald-100";
    case "work":
      return "border-cyan-400/30 bg-cyan-500/12 text-cyan-50";
    case "library":
      return "border-amber-400/25 bg-amber-500/12 text-amber-50";
    default:
      return "border-red-500/20 bg-red-500/10 text-red-100";
  }
}

function MetadataItem({ label, value }: { label: string; value: string }) {
  return (
    <div className="min-w-0 rounded-md border bg-muted/20 p-3">
      <div className="text-muted-foreground text-xs">{label}</div>
      <div className="mt-1 break-words font-mono text-sm">{value}</div>
    </div>
  );
}

function createDefaultWorkerHiddenPanels() {
  return new Set<WorkerDetailPanelId>();
}

function summarizeWorkerLogEntries(entries: WorkerLogEntry[]) {
  return entries.reduce(
    (summary, entry) => {
      summary.total += 1;

      switch (normalizeLogLevel(entry.level)) {
        case "Critical":
        case "Error":
          summary.errors += 1;
          break;
        case "Warning":
          summary.warnings += 1;
          break;
        case "Information":
          summary.information += 1;
          break;
      }

      return summary;
    },
    {
      errors: 0,
      information: 0,
      total: 0,
      warnings: 0,
    }
  );
}

function getWorkerCreatedByLabel(worker: WorkerSnapshot) {
  const earliestAction = [...(worker.actionHistory ?? [])]
    .sort((left, right) => parseTimelineTimestamp(left.occurredAt) - parseTimelineTimestamp(right.occurredAt))[0];
  return formatActionTimelineActorLabel(earliestAction?.origin) ??
    formatActionTimelineSourceLabel(earliestAction?.origin?.channel);
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

function normalizeScopeText(value: unknown) {
  return typeof value === "string" ? value.trim() : "";
}

function splitCategoryPath(category?: string | null) {
  return (category?.trim() || "General")
    .split(":")
    .map((segment) => segment.trim())
    .filter(Boolean);
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

function startsWithCategoryPath(categorySegments: string[], pathSegments: string[]) {
  return pathSegments.every((segment, index) =>
    segment.localeCompare(categorySegments[index] ?? "", undefined, {
      sensitivity: "accent",
    }) === 0
  );
}

function definitionMatchesCatalogScope(
  definition: WorkDefinition,
  scope: OverviewScope | null
) {
  if (!scope) {
    return true;
  }

  if (scope.definitionName) {
    return definition.name === scope.definitionName;
  }

  const scopeSegments = splitCatalogPath(scope.category ?? "");
  if (scopeSegments.length === 0) {
    return true;
  }

  const categorySegments = splitCategoryPath(definition.category);
  return scope.includeSubcategories === false
    ? categorySegments.length === scopeSegments.length &&
        startsWithCategoryPath(categorySegments, scopeSegments)
    : startsWithCategoryPath(categorySegments, scopeSegments);
}

function parseSchemaJsonValue(json?: string | null) {
  if (!json?.trim()) {
    return null;
  }

  try {
    return JSON.parse(json) as unknown;
  } catch {
    return json;
  }
}

function parseJsonText(value: string):
  | { ok: true; value: unknown }
  | { error: string; ok: false } {
  if (!value.trim()) {
    return { ok: true, value: null };
  }

  try {
    return { ok: true, value: JSON.parse(value) as unknown };
  } catch (caught) {
    return {
      error: caught instanceof Error ? caught.message : "Invalid JSON.",
      ok: false,
    };
  }
}

function parseQueueJson(value: string) {
  if (!value.trim()) {
    return undefined;
  }

  try {
    return JSON.parse(value);
  } catch {
    throw new Error("Input must be valid JSON.");
  }
}

function parseOptionalObjectJson<T>(value: string, label: string): T | undefined {
  const parsed = parseQueueJson(value);

  if (parsed === undefined) {
    return undefined;
  }

  if (!isPlainObject(parsed)) {
    throw new Error(`${label} must be a JSON object.`);
  }

  if (Object.keys(parsed).length === 0) {
    return undefined;
  }

  return parsed as T;
}

function createEffectiveConfigurationOptions(
  definition: WorkDefinition | null
): WorkerOptions {
  return {
    profilingEnabled: definition?.defaultOptions?.profilingEnabled ?? false,
    configuration: stripInvocationConfiguration(cloneConfiguration(
      definition?.configuration ?? defaultWorkConfiguration
    )),
  };
}

function getWorkComponentData<T>(result: WorkComponentQueryResult | undefined, id: string) {
  const component = result?.components?.[id];
  return component?.status?.toLowerCase() === "ok"
    ? component.data as T
    : undefined;
}

function createQueueDialogRequest(
  definition: WorkDefinition | null,
  initialRequest?: QueueWorkRequest | null
): QueueWorkRequest {
  if (!initialRequest) {
    return createDefaultQueueRequest(definition);
  }

  const defaultRequest = createDefaultQueueRequest(definition);
  return applyQueueConfigurationRules(sanitizeQueueWorkRequest({
    ...defaultRequest,
    ...cloneQueueWorkRequest(initialRequest),
    options: {
      ...defaultRequest.options,
      ...cloneWorkerOptions(initialRequest.options),
    },
  }));
}

function createDefaultQueueRequest(definition: WorkDefinition | null): QueueWorkRequest {
  return {
    completion: "ReturnAfterAccepted",
    options: createEffectiveConfigurationOptions(definition),
  };
}

function createCopiedWorkerQueueRequest(worker: WorkerSnapshot): QueueWorkRequest {
  return sanitizeQueueWorkRequest({
    completion: "ReturnAfterAccepted",
    subjectId: cloneTypedValue(worker.subjectId) ?? undefined,
    concurrencyKey: cloneTypedValue(worker.concurrencyKey) ?? undefined,
    options: {
      profilingEnabled: worker.options?.profilingEnabled ?? false,
      configuration: stripInvocationConfiguration(cloneConfiguration(
        worker.configuration ?? defaultWorkConfiguration
      )),
    },
  });
}

function cloneQueueWorkRequest(request: QueueWorkRequest): QueueWorkRequest {
  return {
    ...request,
    concurrencyKey: cloneTypedValue(request.concurrencyKey),
    identifiers: cloneTypedValues(request.identifiers),
    input: cloneJsonValue(request.input),
    options: cloneWorkerOptions(request.options),
    subjectId: cloneTypedValue(request.subjectId),
  };
}

function cloneWorkerOptions(options?: WorkerOptions | null): WorkerOptions | undefined {
  if (!options) {
    return undefined;
  }

  return {
    ...options,
    configuration: options.configuration
      ? stripInvocationConfiguration(cloneConfiguration(options.configuration))
      : options.configuration,
  };
}

function cloneTypedValue<T extends { type: string; value: string } | null | undefined>(value: T): T {
  if (!value) {
    return value;
  }

  return { ...value } as T;
}

function cloneTypedValues<T extends { type: string; value: string }>(values?: T[] | null): T[] | undefined {
  return values?.map((value) => ({ ...value }));
}

function cloneJsonValue<T>(value: T): T {
  if (value === undefined || value === null) {
    return value;
  }

  return JSON.parse(JSON.stringify(value)) as T;
}

function sanitizeQueueWorkRequest(request: QueueWorkRequest): QueueWorkRequest {
  const sanitized: QueueWorkRequest = { ...request };

  if (sanitized.subjectId && (!sanitized.subjectId.type.trim() || !sanitized.subjectId.value.trim())) {
    delete sanitized.subjectId;
  }

  if (sanitized.concurrencyKey && (!sanitized.concurrencyKey.type.trim() || !sanitized.concurrencyKey.value.trim())) {
    delete sanitized.concurrencyKey;
  }

  if (!sanitized.options?.configuration) {
    return sanitized;
  }

  return {
    ...sanitized,
    options: {
      ...sanitized.options,
      configuration: stripInvocationConfiguration(sanitized.options.configuration),
    },
  };
}

function applyQueueConfigurationRules(request: QueueWorkRequest): QueueWorkRequest {
  const configuration = request.options?.configuration;
  const coordination = configuration?.coordination;
  if (
    !configuration ||
    !coordination ||
    coordination.storage !== "Persistent" ||
    !coordination.concurrency.isEnabled
  ) {
    return request;
  }

  if (
    coordination.durability.isEnabled &&
    coordination.concurrency.blockingMode === "WhileExecuting" &&
    coordination.concurrency.limitReachedBehavior === "DeferStart"
  ) {
    return request;
  }

  return {
    ...request,
    options: {
      ...request.options,
      configuration: {
        ...configuration,
        coordination: {
          ...coordination,
          concurrency: {
            ...coordination.concurrency,
            blockingMode: "WhileExecuting",
            limitReachedBehavior: "DeferStart",
          },
          durability: {
            ...coordination.durability,
            isEnabled: true,
          },
        },
      },
    },
  };
}

function getQueueConfigurationFieldConstraint(
  request: QueueWorkRequest,
  path: string
): { reason: string; value: string | boolean } | null {
  if (!isPersistentConcurrencyActive(request)) {
    return null;
  }

  const reason = "Required while coordination storage is Persistent and concurrency is enabled.";
  switch (path) {
    case "options.configuration.coordination.concurrency.blockingMode":
      return { reason, value: "WhileExecuting" };
    case "options.configuration.coordination.concurrency.limitReachedBehavior":
      return { reason, value: "DeferStart" };
    case "options.configuration.coordination.durability.isEnabled":
      return {
        reason: "Persistent concurrency requires durable queueing so accepted workers can wait safely for capacity.",
        value: true,
      };
    default:
      return null;
  }
}

function isPersistentConcurrencyActive(request: QueueWorkRequest) {
  const coordination = request.options?.configuration?.coordination;
  return coordination?.storage === "Persistent" &&
    coordination.concurrency.isEnabled;
}

function cloneConfiguration(configuration: WorkConfiguration): WorkConfiguration {
  return JSON.parse(JSON.stringify(configuration)) as WorkConfiguration;
}

function stripInvocationConfiguration(configuration: WorkConfiguration): WorkConfiguration {
  const queueConfiguration = { ...configuration } as WorkConfiguration & {
    invocation?: unknown;
  };
  delete queueConfiguration.invocation;

  return queueConfiguration;
}

function isPlainObject(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}

const defaultWorkConfiguration: WorkConfiguration = {
  start: {
    policy: "StartAndReturnAfterAccepted",
  },
  coordination: {
    isEnabled: false,
    storage: "Local",
    idempotency: {
      isEnabled: false,
      conflictPolicy: "RejectDuplicates",
    },
    concurrency: {
      isEnabled: false,
      maximumCapacity: 0,
      scope: "PerDefinition",
      blockingMode: "WhileExecutingPausedOrFailed",
      limitReachedBehavior: "Ignore",
      overrideBehavior: "Flexible",
    },
    durability: {
      isEnabled: false,
      completeDurably: false,
    },
  },
  recurrence: {
    isEnabled: false,
    interval: "00:00:00",
    continueAfterFailure: true,
    circuitBreakerFailureThreshold: 3,
    retainedIterations: 25,
    raiseCircuitBreakerOpenedEvent: true,
  },
  transientRetry: {
    count: 0,
    initialDelay: "00:00:00.8000000",
    jitter: "00:00:00.5000000",
    maximumDelay: "00:00:30",
    backoff: "Exponential",
  },
  logging: {
    isEnabled: true,
    level: "Information",
    maximumBufferedEntries: 100,
  },
  retention: {
    purgeInterval: "00:10:00",
    maximumFinalWorkers: 1000,
  },
};

function useWorkableResource<T>(
  connection: WorkableConnection,
  path: string | null,
  refreshToken: number,
  options?: {
    retainDataOnNull?: boolean;
    resetKey?: string | number | null;
  }
): Loadable<T> {
  const [state, setState] = useState<Loadable<T>>({ loading: !!path });
  const apiUrl = connection.apiUrl;
  const systemName = connection.systemName;
  const retainDataOnNull = options?.retainDataOnNull === true;
  const resetKey = options?.resetKey ?? null;
  const lastResetKeyRef = useRef<string | number | null>(resetKey);

  useEffect(() => {
    if (!path) {
      queueMicrotask(() => setState((current) => {
        const sameResetKey = lastResetKeyRef.current === resetKey;
        lastResetKeyRef.current = resetKey;

        if (retainDataOnNull && sameResetKey && current.data !== undefined) {
          return {
            data: current.data,
            loading: false,
            refreshing: false,
          };
        }

        return { loading: false };
      }));
      return;
    }

    let canceled = false;
    lastResetKeyRef.current = resetKey;
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
    workableFetch<T>(requestConnection, path)
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
  }, [apiUrl, systemName, path, refreshToken, resetKey, retainDataOnNull]);

  return state;
}
