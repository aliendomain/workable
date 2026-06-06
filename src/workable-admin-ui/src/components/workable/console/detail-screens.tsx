"use client";

import {
  Activity,
  ArrowDownWideNarrow,
  ArrowUpNarrowWide,
  Ban,
  Braces,
  CheckCircle2,
  Clock3,
  Eye,
  Info,
  Loader2,
  Maximize2,
  Minimize2,
  Pause,
  Play,
  RotateCw,
  Rows4,
  Search,
  Send,
  Trash2,
  X,
} from "lucide-react";
import type { Dispatch, ReactNode, SetStateAction } from "react";
import { Fragment, memo, useCallback, useEffect, useLayoutEffect, useMemo, useRef, useState } from "react";
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
import { Tabs, TabsContent, TabsList, TabsTrigger } from "@/components/ui/tabs";
import { Textarea } from "@/components/ui/textarea";
import { Tooltip, TooltipContent, TooltipTrigger } from "@/components/ui/tooltip";
import { ConsoleEmptyState } from "@/components/features/console/empty-state";
import {
  FormEmptyState,
  FormFieldHeader,
  ReadonlyFormValue,
} from "@/components/features/console/form-controls";
import {
  updateHiddenPanelIds,
  usePanelVisibilityState,
} from "@/components/features/console/panel-visibility-state";
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
import { createRealtimePayloadMessage, type RealtimePayloadMessage } from "@/components/features/console/realtime-payload";
import { useConsoleRealtimeView } from "@/components/features/console/realtime";
import {
  useRegisterConsoleHeaderCapabilities,
  type ConsoleHeaderCapabilities,
} from "@/components/features/console/header-capabilities";
import { PanelScrollViewport, PanelShell } from "@/components/features/console/panel-shell";
import type { PanelVisibilityOption } from "@/components/features/console/panel-visibility-settings";
import { StackedSkeleton } from "@/components/features/console/stacked-skeleton";
import { ToolbarIconButton } from "@/components/features/console/toolbar-icon-button";
import type { Loadable, OverviewScope } from "@/components/features/console/types";
import { splitCatalogPath } from "@/components/workable/console/catalog-path";
import { invalidateDefinitionCatalogLevelCache } from "@/components/workable/console/catalog-browser-data";
import {
  applyQueueConfigurationRules,
  cloneConfiguration,
  cloneJsonValue,
  cloneQueueWorkRequest,
  createConfigurationFieldSections,
  createCopiedWorkerQueueRequest,
  createDefaultQueueRequest,
  createDefinitionConfigurationDescriptor,
  createEffectiveConfigurationOptions,
  createQueueDialogRequest,
  createWorkerConfigurationDescriptor,
  createWorkerConfigurationDifferences,
  createWorkerConfigurationRequest,
  createWorkerReconfiguration,
  defaultWorkConfiguration,
  formatConfigurationValue,
  getQueueConfigurationFieldConstraint,
  parseJsonText,
  parseOptionalObjectJson,
  parseSchemaJsonValue,
  sanitizeQueueWorkRequest,
  stripInvocationConfiguration,
  type QueueConfigurationField,
  type QueueConfigurationTab,
  type WorkerConfigurationDifference,
} from "@/components/workable/console/detail-configuration-data";
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
  WorkableApiError,
  type WorkActionOutcome,
  workableFetch,
  type QueueRequestSchemaDescriptor,
  type QueueWorkRequest,
  type WorkAction,
  type WorkCompletionStatus,
  type WorkComponentShape,
  type WorkData,
  type WorkDefinition,
  type WorkDefinitionReconfigurationOutcome,
  type WorkInfo,
  type WorkIterationMessageSection,
  type WorkMessage,
  type WorkableRealtimeOrigin,
  type WorkableConnection,
  type WorkableHttpWorkerConfiguration,
  type WorkableHttpWorkerIterationDetail,
  type WorkableHttpWorkerIterationSnapshot,
  type WorkWorkerOverviewComponent,
  type WorkWorkerOverviewFailure,
  type WorkWorkerOverviewActivity,
  type WorkWorkerOverviewLatestIteration,
  type WorkWorkerOverviewLogEntry,
  type WorkWorkerOverviewLogSection,
  type WorkWorkerOverviewLogSummary,
  type WorkWorkerOverviewOrigin,
  type WorkWorkerOverviewRealtimeCriteria,
  type WorkWorkerOverviewRealtimeUpdate,
  type WorkWorkerOverviewRecentIteration,
  type WorkWorkerOverviewTimelineCategory,
  type WorkWorkerOverviewTimelineItem,
  type WorkWorkerOverviewTimelineSection,
  type WorkWorkerOverviewTimelineSummary,
  type WorkWorkerOverviewWorker,
  type WorkIterationLogSection,
  type WorkIterationMessageSummary,
  type WorkTypedValue,
  type WorkerIterationFailure,
  type WorkerIterationSnapshot,
  type WorkerLogEntry,
  type WorkerSummary,
  type WorkerOptions,
  type WorkerState,
  type WorkerSnapshot,
} from "@/lib/workable";
import { cn } from "@/lib/utils";

export {
  applyQueueConfigurationRules,
  cloneConfiguration,
  cloneJsonValue,
  cloneQueueWorkRequest,
  cloneTypedValue,
  cloneTypedValues,
  cloneWorkerOptions,
  configurationValuesEqual,
  createConfigurationFieldSections,
  createCopiedWorkerQueueRequest,
  createDefaultQueueRequest,
  createDefinitionConfigurationDescriptor,
  createEffectiveConfigurationOptions,
  createQueueDialogRequest,
  createWorkerConfigurationDescriptor,
  createWorkerConfigurationDifferences,
  createWorkerConfigurationRequest,
  createWorkerReconfiguration,
  defaultWorkConfiguration,
  descriptionForFieldSection,
  findTabBasePath,
  formatConfigurationValue,
  getFieldSectionId,
  getQueueConfigurationFieldConstraint,
  getValueAtPath,
  humanizePathSegment,
  isPersistentConcurrencyActive,
  isPlainObject,
  labelForFieldSection,
  parseJsonText,
  parseOptionalObjectJson,
  parseQueueJson,
  parseSchemaJsonValue,
  sanitizeQueueWorkRequest,
  stripInvocationConfiguration,
  type QueueConfigurationField,
  type QueueConfigurationFieldSection,
  type QueueConfigurationTab,
  type WorkerConfigurationDifference,
  type WorkerReconfigurationRequest,
} from "@/components/workable/console/detail-configuration-data";

type IterationDetailPanelId =
  | "iterationSummary"
  | "iterationMessages"
  | "iterationOutput"
  | "iterationLogs";
type IterationFocusedPanelId = "iterationMessages";
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
type WorkerConfigurationDisplayMode = "auto" | "all-values" | "only-changes";
export type StackFrameFilterKind = "application" | "library" | "work";
export type StackTraceDisplayEntry =
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
  | "workerConfiguration"
  | "workerLogs"
  | "workerDuration"
  | "workerTimeline";
type WorkerFocusedPanelId = "workerLogs" | "workerTimeline";
export type WorkerConsoleViewUiStateSnapshot = {
  focusedWorkerHiddenSnapshotPanelIds: WorkerDetailPanelId[] | null;
  focusedWorkerPanel: WorkerFocusedPanelId | null;
  hiddenPanelIds: WorkerDetailPanelId[];
  logSortDirection: WorkerSortDirection;
  selectedLogLevels: WorkerLogFilterLevel[] | null;
  selectedTimelineFilters: WorkerTimelineFilterKind[] | null;
  timelineSortDirection: WorkerSortDirection;
  workerConfigurationAutoShowAllValues: boolean;
  workerConfigurationDisplayMode: WorkerConfigurationDisplayMode;
  workerConfigurationPanelViewState: WorkComponentShape;
  workerControlsPanelViewState: WorkComponentShape;
  workerDurationPanelViewState: WorkComponentShape;
  workerId: string;
  workerLogsPanelViewState: WorkComponentShape;
  workerTimelinePanelViewState: WorkComponentShape;
};

const workerPanelOptions: PanelVisibilityOption<WorkerDetailPanelId>[] = [
  {
    id: "workerControls",
    label: "Worker controls",
    description: "Current worker state, control actions, input, and latest retained output.",
  },
  {
    id: "workerConfiguration",
    label: "Worker configuration",
    description: "Live worker configuration compared to the selected definition defaults.",
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
const iterationPanelOptions: PanelVisibilityOption<IterationDetailPanelId>[] = [
  {
    id: "iterationSummary",
    label: "Iteration summary",
    description: "Status, timing, worker context, and quick links for the selected iteration.",
  },
  {
    id: "iterationMessages",
    label: "Messages",
    description: "Retained Workable messages, including validation and execution failures.",
  },
  {
    id: "iterationOutput",
    label: "Input & Output",
    description: "Worker input plus the retained output payload for this iteration.",
  },
  {
    id: "iterationLogs",
    label: "Logs",
    description: "Retained log entries emitted during this iteration.",
  },
];
const maxWorkerLogPanelEntries = 500;
const workerOverviewRealtimeResyncCooldownMs = 5000;

export function DefinitionsView({
  autoOpenScopedDefinition = true,
  catalogScope,
  connection,
  onCatalogScopeChange,
  onOpenDefinition,
  onOpenWorker,
  onReady,
  refreshToken,
}: {
  autoOpenScopedDefinition?: boolean;
  catalogScope: OverviewScope | null;
  connection: WorkableConnection;
  onCatalogScopeChange: (scope: OverviewScope | null) => void;
  onOpenDefinition: (definitionName: string, definitionLabel?: string) => void;
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
  const {
    hiddenPanelIdList,
    isPanelVisible,
    resetPanelVisibility,
    setPanelVisible: setCatalogPanelVisible,
  } = usePanelVisibilityState<"catalog">();
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
        ? `${catalogScope.definitionName}:${filtered[0].name}`
        : "";
    if (
      autoOpenScopedDefinition &&
      !definitions.loading &&
      catalogScope?.definitionName &&
      filtered.length === 1 &&
      autoOpenedDefinitionScope.current !== autoOpenKey
    ) {
      autoOpenedDefinitionScope.current = autoOpenKey;
      onOpenDefinition(filtered[0].name, filtered[0].name);
    }
  }, [autoOpenScopedDefinition, catalogScope?.definitionName, definitions.loading, filtered, onOpenDefinition]);

  const resetCatalogUiToDefaults = useCallback(() => {
    resetPanelVisibility();
    setGridShape("detailed");
  }, [resetPanelVisibility]);

  const isCatalogPanelVisible = isPanelVisible("catalog");

  return (
    <ConsolePageLayout>
      <ErrorPanel errors={[definitions.error]} />
      <PanelAggregateFrame
        hiddenPanelIds={hiddenPanelIdList}
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
                      key={definition.name}
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
                          onClick={() => onOpenDefinition(definition.name, definition.name)}
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
  const [saveError, setSaveError] = useState<string>();
  const [saveStatus, setSaveStatus] = useState<string>();
  const [isSaving, setIsSaving] = useState(false);
  const [updatedDefinition, setUpdatedDefinition] = useState<WorkDefinition | null>(null);
  const definition = updatedDefinition ?? info.data?.definition;
  const isReady = !info.loading;
  const queueRequestSchema = useMemo(
    () => parseJsonSchema(info.data?.queueRequestSchema?.schema?.jsonSchema),
    [info.data?.queueRequestSchema?.schema?.jsonSchema]
  );
  const definitionConfigurationDescriptor = useMemo(
    () => createDefinitionConfigurationDescriptor(info.data?.queueRequestSchema ?? null),
    [info.data?.queueRequestSchema]
  );

  useEffect(() => {
    if (isReady) {
      onReady();
    }
  }, [isReady, onReady]);

  useEffect(() => {
    if (info.errorCause instanceof WorkableApiError && info.errorCause.status === 404) {
      invalidateDefinitionCatalogLevelCache(connection);
    }
  }, [connection, info.errorCause]);

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
    if (updatedDefinition?.name === definitionId) {
      onDefinitionResolved(updatedDefinition.name);
      return;
    }

    if (info.data?.definition?.name === definitionId) {
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
        `definitions/${definition.name}/reconfigure`,
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
        <ConsoleEmptyState padding="spacious">
          Definition not found.
        </ConsoleEmptyState>
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
            fetchQueueSchemaWhenNeeded={false}
            onQueuedWorker={onOpenWorker}
            preloadedQueueSchemaDescriptor={info.data?.queueRequestSchema ?? null}
            onOpenChange={(open) => !open && setQueueDefinition(null)}
          />
        </>
      )}
    </ConsolePageLayout>
  );
}

export function WorkerConsoleView({
  clearSystemNotification,
  connection,
  initialUiState,
  onActiveRealtimeConnectionCountChange,
  onRealtimePayloadOpenChange,
  onOpenDefinitionCatalog,
  onOpenIteration,
  onNavigateBack,
  onOpenWorker,
  onUiStateChange,
  reportSystemNotification,
  refreshToken,
  realtimePayloadCaptureEnabled,
  realtimePayloadMaxMessages,
  realtimePayloadOpen,
  workerId,
}: {
  clearSystemNotification: (notificationId: string) => void;
  connection: WorkableConnection;
  initialUiState?: WorkerConsoleViewUiStateSnapshot | null;
  onActiveRealtimeConnectionCountChange: (count: number) => void;
  onOpenDefinitionCatalog: (definitionName: string, category?: string | null) => void;
  onOpenIteration: (workerId: string, sequence: number) => void;
  onNavigateBack: () => void;
  onOpenWorker: (workerId: string) => void;
  onRealtimePayloadOpenChange: (open: boolean) => void;
  onUiStateChange?: (state: WorkerConsoleViewUiStateSnapshot) => void;
  reportSystemNotification: (notification: {
    description: string;
    id: string;
    tone: "critical" | "warning";
    title: string;
  } | null) => void;
  refreshToken: number;
  realtimePayloadCaptureEnabled: boolean;
  realtimePayloadMaxMessages: number;
  realtimePayloadOpen: boolean;
  workerId: string;
}) {
  const restoredUiState = initialUiState?.workerId === workerId ? initialUiState : null;
  const [actionFeedback, setActionFeedback] = useState<{
    message: string;
    title?: string;
    tone: FeedbackTone;
  }>();
  const [pendingAction, setPendingAction] = useState<WorkAction | null>(null);
  const [actionRefreshToken, setActionRefreshToken] = useState(0);
  const [manualRefreshToken, setManualRefreshToken] = useState(0);
  const [realtimeSubscriptionResetToken, setRealtimeSubscriptionResetToken] = useState(0);
  const [copyQueueDialog, setCopyQueueDialog] = useState<{
    definition: WorkDefinition;
    formValue: unknown;
    queueRequestSchema: QueueRequestSchemaDescriptor;
    request: QueueWorkRequest;
  } | null>(null);
  const [openingCopyQueue, setOpeningCopyQueue] = useState(false);
  const [workerConfigurationRequest, setWorkerConfigurationRequest] = useState<QueueWorkRequest>(() =>
    createDefaultQueueRequest(null)
  );
  const [isSavingWorkerConfiguration, setIsSavingWorkerConfiguration] = useState(false);
  const lastSavedWorkerConfigurationRequestRef = useRef<QueueWorkRequest | null>(null);
  const [hiddenPanelIds, setHiddenPanelIds] = useState<ReadonlySet<WorkerDetailPanelId>>(() =>
    restoredUiState
      ? new Set(restoredUiState.hiddenPanelIds)
      : createDefaultWorkerHiddenPanels()
  );
  const [workerControlsPanelViewState, setWorkerControlsPanelViewState] = useState<WorkComponentShape>(
    () => restoredUiState?.workerControlsPanelViewState ?? "compact"
  );
  const [workerConfigurationPanelViewState, setWorkerConfigurationPanelViewState] = useState<WorkComponentShape>(
    () => restoredUiState?.workerConfigurationPanelViewState ?? "compact"
  );
  const [workerConfigurationDisplayMode, setWorkerConfigurationDisplayMode] =
    useState<WorkerConfigurationDisplayMode>(() => restoredUiState?.workerConfigurationDisplayMode ?? "auto");
  const [workerConfigurationAutoShowAllValues, setWorkerConfigurationAutoShowAllValues] = useState(
    () => restoredUiState?.workerConfigurationAutoShowAllValues ?? true
  );
  const [workerLogsPanelViewState, setWorkerLogsPanelViewStateState] = useState<WorkComponentShape>(
    () => restoredUiState?.workerLogsPanelViewState ?? "compact"
  );
  const [workerDurationPanelViewState, setWorkerDurationPanelViewState] = useState<WorkComponentShape>(
    () => restoredUiState?.workerDurationPanelViewState ?? "standard"
  );
  const [workerTimelinePanelViewState, setWorkerTimelinePanelViewStateState] = useState<WorkComponentShape>(
    () => restoredUiState?.workerTimelinePanelViewState ?? "standard"
  );
  const [focusedWorkerPanel, setFocusedWorkerPanel] = useState<WorkerFocusedPanelId | null>(
    () => restoredUiState?.focusedWorkerPanel ?? null
  );
  const [selectedLogLevels, setSelectedLogLevels] = useState<WorkerLogFilterLevel[] | null>(
    () => restoredUiState?.selectedLogLevels ?? null
  );
  const [logSortDirection, setLogSortDirection] = useState<WorkerSortDirection>(
    () => restoredUiState?.logSortDirection ?? "desc"
  );
  const [selectedTimelineFilters, setSelectedTimelineFilters] = useState<WorkerTimelineFilterKind[] | null>(
    () => restoredUiState?.selectedTimelineFilters ?? null
  );
  const [timelineSortDirection, setTimelineSortDirection] = useState<WorkerSortDirection>(
    () => restoredUiState?.timelineSortDirection ?? "desc"
  );
  const [extraLogEntries, setExtraLogEntries] = useState<WorkWorkerOverviewLogEntry[]>([]);
  const [realtimeLogEntries, setRealtimeLogEntries] = useState<WorkWorkerOverviewLogEntry[]>([]);
  const [logPageLoadState, setLogPageLoadState] = useState<WorkerOverviewPageLoadState>({
    hasMore: false,
    loadingMore: false,
  });
  const [logPageResetSeed, setLogPageResetSeed] = useState(0);
  const [extraTimelineItems, setExtraTimelineItems] = useState<WorkWorkerOverviewTimelineItem[]>([]);
  const [realtimeTimelineItems, setRealtimeTimelineItems] = useState<WorkWorkerOverviewTimelineItem[]>([]);
  const [timelinePageLoadState, setTimelinePageLoadState] = useState<WorkerOverviewPageLoadState>({
    hasMore: false,
    loadingMore: false,
  });
  const [timelinePageResetSeed, setTimelinePageResetSeed] = useState(0);
  const [realtimeWorker, setRealtimeWorker] = useState<WorkWorkerOverviewWorker | null>(null);
  const [realtimeLatestIteration, setRealtimeLatestIteration] = useState<WorkWorkerOverviewLatestIteration | null>(null);
  const [realtimeLogSummary, setRealtimeLogSummary] = useState<WorkWorkerOverviewLogSummary | null>(null);
  const [realtimeTimelineSummary, setRealtimeTimelineSummary] = useState<WorkWorkerOverviewTimelineSummary | null>(null);
  const [stableAggregateLogSummary, setStableAggregateLogSummary] = useState<{
    critical: number;
    error: number;
    warning: number;
    information: number;
    debug: number;
    trace: number;
    errors: number;
    warnings: number;
    total: number;
  } | null>(null);
  const [realtimeRecentIterations, setRealtimeRecentIterations] = useState<WorkWorkerOverviewRecentIteration[]>([]);
  const [realtimeUpdateError, setRealtimeUpdateError] = useState<string>();
  const focusedWorkerHiddenSnapshotRef = useRef<ReadonlySet<WorkerDetailPanelId> | null>(
    restoredUiState?.focusedWorkerHiddenSnapshotPanelIds
      ? new Set(restoredUiState.focusedWorkerHiddenSnapshotPanelIds)
      : null
  );
  const lastPublishedUiStateKeyRef = useRef<string | null>(null);
  const initializedWorkerPanelsRef = useRef<string | null>(restoredUiState ? workerId : null);
  const initializedWorkerConfigurationVisibilityRef = useRef<string | null>(restoredUiState ? workerId : null);
  const initializedWorkerConfigurationAutoModeRef = useRef<string | null>(restoredUiState ? workerId : null);
  const workerOverviewRealtimeResyncCooldownUntilRef = useRef(0);
  const timelinePageRequestGenerationRef = useRef(0);
  const refreshSeed = refreshToken + actionRefreshToken + manualRefreshToken;
  const landingSnapshot = useWorkableResource<WorkWorkerOverviewComponent>(
    connection,
    createWorkerOverviewPath(workerId, {
      activityTake: workerOverviewInitialActivityPageSize,
    }),
    refreshSeed,
    {
      retainDataOnNull: true,
      resetKey: workerId,
    }
  );
  const relativeNow = useLiveRelativeTimeNow();
  const baseLanding = landingSnapshot.data ?? null;
  const landing = useMemo(
    () => applyWorkerOverviewRealtimeState(
      baseLanding,
      realtimeWorker,
      realtimeLatestIteration,
      realtimeLogSummary,
      realtimeTimelineSummary,
      realtimeRecentIterations
    ),
    [
      baseLanding,
      realtimeLatestIteration,
      realtimeLogSummary,
      realtimeRecentIterations,
      realtimeTimelineSummary,
      realtimeWorker,
    ]
  );
  const activity = landing?.activity ?? "Logs";
  const defaultWorkerLogsPanelViewState = activity === "Timeline" ? "compact" : "standard";
  const defaultWorkerTimelinePanelViewState = activity === "Timeline" ? "standard" : "compact";
  const workerOverviewPanelsInitialized = landing !== null &&
    initializedWorkerPanelsRef.current === workerId;
  const shouldSubscribeToWorkerOverviewRealtime = workerOverviewPanelsInitialized &&
    landing?.worker.state !== "Canceled" &&
    landing?.worker.state !== "Completed";
  const effectiveWorkerLogsPanelViewState = landing && !workerOverviewPanelsInitialized
    ? defaultWorkerLogsPanelViewState
    : workerLogsPanelViewState;
  const effectiveWorkerTimelinePanelViewState = landing && !workerOverviewPanelsInitialized
    ? defaultWorkerTimelinePanelViewState
    : workerTimelinePanelViewState;
  const isWorkerLogsPanelExpanded = effectiveWorkerLogsPanelViewState !== "compact";
  const isWorkerTimelinePanelExpanded = effectiveWorkerTimelinePanelViewState !== "compact";
  const isWorkerPanelFocused = focusedWorkerPanel !== null;
  const normalizedSelectedLogLevels = useMemo(
    () => normalizeSelectedLogLevelsForRequest(selectedLogLevels),
    [selectedLogLevels]
  );
  const normalizedSelectedTimelineFilters = useMemo(
    () => normalizeSelectedTimelineFiltersForRequest(selectedTimelineFilters),
    [selectedTimelineFilters]
  );
  const logQueryKey = useMemo(
    () => serializeWorkerLogQuery(normalizedSelectedLogLevels, logSortDirection),
    [logSortDirection, normalizedSelectedLogLevels]
  );
  const timelineQueryKey = useMemo(
    () => serializeWorkerTimelineQuery(normalizedSelectedTimelineFilters, timelineSortDirection),
    [normalizedSelectedTimelineFilters, timelineSortDirection]
  );
  const workerOverviewRealtimeCriteria = useMemo<WorkWorkerOverviewRealtimeCriteria>(
    () => ({
      workerControls: workerControlsPanelViewState === "detailed" ? "standard" : workerControlsPanelViewState,
      workerLogs: effectiveWorkerLogsPanelViewState,
      workerDuration: workerDurationPanelViewState,
      workerTimeline: effectiveWorkerTimelinePanelViewState,
      logSortDirection: logSortDirection === "asc" ? "Asc" : "Desc",
      logLevels: normalizedSelectedLogLevels,
      timelineSortDirection: timelineSortDirection === "asc" ? "Asc" : "Desc",
      timelineCategories: normalizedSelectedTimelineFilters?.map(mapTimelineFilterKindToServerCategory) ?? null,
    }),
    [
      logSortDirection,
      normalizedSelectedLogLevels,
      normalizedSelectedTimelineFilters,
      timelineSortDirection,
      effectiveWorkerLogsPanelViewState,
      effectiveWorkerTimelinePanelViewState,
      workerControlsPanelViewState,
      workerDurationPanelViewState,
    ]
  );
  const workerOverviewRealtimeCriteriaKey = useMemo(
    () => JSON.stringify(workerOverviewRealtimeCriteria),
    [workerOverviewRealtimeCriteria]
  );
  const workerOverviewRealtimeSubscriptionInstanceKey = useMemo(
    // Keep the worker overview watch identity stable across criteria-only changes
    // so compact/standard transitions can update the existing watch in place.
    () => `${workerId}:${realtimeSubscriptionResetToken}`,
    [realtimeSubscriptionResetToken, workerId]
  );
  const workerOverviewRealtimeConnectionInstanceKey = useMemo(
    () => `worker-overview:${workerId}`,
    [workerId]
  );
  const workerOverviewRealtime = useConsoleRealtimeView<WorkWorkerOverviewRealtimeUpdate, RealtimePayloadMessage>({
    body: workerOverviewRealtimeCriteria,
    captureEnabled: realtimePayloadCaptureEnabled && realtimePayloadOpen,
    clientMethod: "workable.workerOverview",
    connectionInstanceKey: workerOverviewRealtimeConnectionInstanceKey,
    connection,
    createMessage: (result, nextMessageId) => {
      const payloadJson = JSON.stringify(result);
      return createRealtimePayloadMessage(
        result,
        payloadJson,
        `worker-overview:${workerId}:${nextMessageId}`,
        "worker-overview",
        `worker-overview:${workerId}`,
        connection
      );
    },
    enabled: Boolean(connection.realtimeHubPath) && shouldSubscribeToWorkerOverviewRealtime,
    maxMessages: realtimePayloadMaxMessages,
    subscription: `worker-overview:${workerId}`,
    subscriptionInstanceKey: workerOverviewRealtimeSubscriptionInstanceKey,
    subscriptionErrorMessage: "Realtime worker overview subscription failed.",
    viewName: workerId,
    watchMethod: "WatchWorkerOverview",
    unwatchMethod: "UnwatchWorkerOverview",
  });
  const shouldRefreshWorkerOverviewAfterAction = !(
    workerOverviewRealtime.enabled &&
    workerOverviewRealtime.connectionState === "connected"
  );
  const usingBootstrapLogsPage = activity === "Logs" &&
    isDefaultWorkerLogQuery(normalizedSelectedLogLevels, logSortDirection);
  const usingBootstrapTimelinePage = activity === "Timeline" &&
    isDefaultWorkerTimelineQuery(normalizedSelectedTimelineFilters, timelineSortDirection);
  const worker = useMemo(
    () => landing ? createWorkerSnapshotFromLanding(landing) : null,
    [landing]
  );
  const latestIteration = useMemo(
    () => landing?.latestIteration
      ? createWorkerIterationSnapshotFromLandingLatestIteration(landing.latestIteration)
      : null,
    [landing]
  );
  const primaryIteration = latestIteration;
  const hasActiveIteration = latestIteration?.status === "Executing";
  const timelineIterations = useMemo(
    () => getChronologicalIterations(
      landing?.recentIterations.map((iteration) =>
        createWorkerIterationSnapshotFromLandingRecentIteration(iteration, landing.latestIteration)
      ) ?? []
    ),
    [landing]
  );
  const workerDetailSnapshot = useWorkableResource<WorkableHttpWorkerConfiguration>(
    connection,
    workerConfigurationPanelViewState === "standard"
      ? `workers/${workerId}/configuration`
      : null,
    refreshSeed,
    {
      retainDataOnNull: true,
      resetKey: workerId,
    }
  );
  const workerConfigurationSource = workerDetailSnapshot.data ?? null;
  const queueSchemaDescriptor = workerDetailSnapshot.data?.queueRequestSchema ?? null;
  const queueRequestSchema = useMemo(
    () => parseJsonSchema(queueSchemaDescriptor?.schema?.jsonSchema),
    [queueSchemaDescriptor?.schema?.jsonSchema]
  );
  const workerConfigurationDescriptor = useMemo(
    () => createWorkerConfigurationDescriptor(queueSchemaDescriptor),
    [queueSchemaDescriptor]
  );
  const workerDefinition = workerDetailSnapshot.data?.definitionInfo?.definition ?? null;
  const workerDefaultConfigurationRequest = useMemo(
    () => ({
      options: createEffectiveConfigurationOptions(workerDefinition),
    } satisfies QueueWorkRequest),
    [workerDefinition]
  );
  const workerConfigurationDifferences = useMemo(
    () => workerDefinition
      ? createWorkerConfigurationDifferences(
        workerConfigurationRequest,
        workerDefaultConfigurationRequest,
        workerConfigurationDescriptor
      )
      : [],
    [
      workerDefinition,
      workerConfigurationDescriptor,
      workerConfigurationRequest,
      workerDefaultConfigurationRequest,
    ]
  );
  const currentWorkerConfigurationRequest = useMemo(
    () => workerConfigurationSource ? createWorkerConfigurationRequest(workerConfigurationSource) : null,
    [workerConfigurationSource]
  );
  const hasUnsavedWorkerConfigurationChanges = useMemo(
    () => currentWorkerConfigurationRequest
      ? compactJson(createWorkerReconfiguration(workerConfigurationRequest)) !==
        compactJson(createWorkerReconfiguration(currentWorkerConfigurationRequest))
      : false,
    [currentWorkerConfigurationRequest, workerConfigurationRequest]
  );
  const canResetWorkerConfigurationToDefaults = useMemo(
    () => workerDefinition
      ? compactJson(createWorkerReconfiguration(workerConfigurationRequest)) !==
        compactJson(createWorkerReconfiguration(workerDefaultConfigurationRequest))
      : false,
    [workerConfigurationRequest, workerDefaultConfigurationRequest, workerDefinition]
  );
  const workerConfigurationSeedRef = useRef("");
  const logsPageSnapshot = useWorkableResource<WorkWorkerOverviewLogSection>(
    connection,
    landing && workerOverviewPanelsInitialized && isWorkerLogsPanelExpanded && !usingBootstrapLogsPage
      ? createWorkerOverviewLogsPath(workerId, {
          activityTake: workerLogActivityPageSize,
          logLevels: normalizedSelectedLogLevels,
          logSortDirection,
        })
      : null,
    refreshSeed,
    {
      retainDataOnNull: true,
      resetKey: `${workerId}:logs:${logQueryKey}:${logPageResetSeed}`,
    }
  );
  const timelinePageSnapshot = useWorkableResource<WorkWorkerOverviewTimelineSection>(
    connection,
    landing && workerOverviewPanelsInitialized && isWorkerTimelinePanelExpanded && !usingBootstrapTimelinePage
      ? createWorkerOverviewTimelinePath(workerId, {
          activityTake: workerTimelineActivityPageSize,
          timelineFilters: normalizedSelectedTimelineFilters,
          timelineSortDirection,
        })
      : null,
    refreshSeed,
    {
      retainDataOnNull: true,
      resetKey: `${workerId}:timeline:${timelineQueryKey}:${timelinePageResetSeed}`,
    }
  );
  const logBasePage = useMemo(
    () => usingBootstrapLogsPage
      ? landing?.logs.page ?? null
      : logsPageSnapshot.data?.page ?? null,
    [landing?.logs.page, logsPageSnapshot.data?.page, usingBootstrapLogsPage]
  );
  const timelineBasePage = useMemo(
    () => usingBootstrapTimelinePage
      ? landing?.timeline.page ?? null
      : timelinePageSnapshot.data?.page ?? null,
    [landing?.timeline.page, timelinePageSnapshot.data?.page, usingBootstrapTimelinePage]
  );
  const executionLogs = useMemo(
    () => {
      const overlayEntries = logSortDirection === "desc"
        ? [...realtimeLogEntries, ...extraLogEntries]
        : [...extraLogEntries, ...realtimeLogEntries];
      const items = sortWorkerOverviewLogEntries(
        filterWorkerOverviewLogEntriesBySelectedLevels(
        mergeWorkerOverviewRealtimeEntries(
          logBasePage?.items ?? [],
          overlayEntries,
          logSortDirection
        ),
        normalizedSelectedLogLevels
        ),
        logSortDirection
      );
      return items.map(createWorkerLogEntryFromLandingLogEntry);
    },
    [
      extraLogEntries,
      logSortDirection,
      logBasePage?.items,
      normalizedSelectedLogLevels,
      realtimeLogEntries,
    ]
  );
  const logSummary = useMemo(
    () => landing?.logs.summary
      ? createWorkerLogSummaryFromLanding(landing.logs.summary)
      : undefined,
    [landing]
  );
  useEffect(() => {
    if (logSummary) {
      setStableAggregateLogSummary(logSummary);
    }
  }, [logSummary]);
  const workerConfigurationDifferenceCount = landing?.worker.configDifferenceCount ?? 0;
  const timelineItems = useMemo(
    () => {
      const queuedTimelineItem = worker
        ? createWorkerQueuedTimelineItem(worker)
        : null;
      const overlayItems = timelineSortDirection === "desc"
        ? [...realtimeTimelineItems, ...extraTimelineItems]
        : [...extraTimelineItems, ...realtimeTimelineItems];
      const items = normalizeVisibleWorkerTimelineItems(
        mergeWorkerOverviewItemsById(overlayItems, timelineBasePage?.items ?? []),
        worker?.state ?? null
      )
        .map((item) => createWorkerTimelineItemFromLandingTimelineItem(item));
      const sortedItems = [...items].sort((left, right) => {
        const waitingPriorityDifference = getWorkerTimelineWaitingPriority(right) -
          getWorkerTimelineWaitingPriority(left);
        if (waitingPriorityDifference !== 0) {
          return waitingPriorityDifference;
        }

        const timestampDifference = parseTimelineTimestamp(right.at) - parseTimelineTimestamp(left.at);
        if (timestampDifference !== 0) {
          return timestampDifference;
        }

        return right.sortOrder - left.sortOrder;
      });
      if (timelineSortDirection === "desc") {
        return queuedTimelineItem
          ? [...sortedItems.filter((item) => item.id !== queuedTimelineItem.id), queuedTimelineItem]
          : sortedItems;
      }

      const pinnedItems = sortedItems.filter((item) => getWorkerTimelineWaitingPriority(item) > 0);
      const flowingItems = sortedItems.filter((item) => getWorkerTimelineWaitingPriority(item) === 0);
      const ascendingItems = [...flowingItems.reverse(), ...pinnedItems];
      return queuedTimelineItem
        ? [queuedTimelineItem, ...ascendingItems.filter((item) => item.id !== queuedTimelineItem.id)]
        : ascendingItems;
    },
    [
      extraTimelineItems,
      realtimeTimelineItems,
      timelineBasePage?.items,
      timelineSortDirection,
      worker,
    ]
  );
  const terminalFailure = worker && worker.state === "Failed" && landing?.latestIteration?.failure
    ? createWorkerFailureDetailsFromLandingFailure(landing.latestIteration.failure)
    : null;
  const terminalFailureKey = terminalFailure && worker
    ? `${worker.id.value}:${worker.stateSequence}:failed`
    : null;
  const [dismissedWorkerFailureKey, setDismissedWorkerFailureKey] = useState<string | null>(null);
  const workerActionRefreshInProgress = landingSnapshot.loading || landingSnapshot.refreshing === true;
  const availableActions = worker
    ? getAvailableWorkerActions(worker.state)
    : emptyAvailableWorkerActions;
  const refreshWorkerSnapshot = useCallback(() => {
    setManualRefreshToken((value) => value + 1);
  }, []);
  const toggleRealtimePayloadOpen = useCallback(() => {
    onRealtimePayloadOpenChange(!realtimePayloadOpen);
  }, [onRealtimePayloadOpenChange, realtimePayloadOpen]);
  const headerCapabilities = useMemo<ConsoleHeaderCapabilities>(
    () => ({
      realtime: {
        connectionState: workerOverviewRealtime.connectionState,
        enabled: workerOverviewRealtime.enabled,
        menuItems: [
          {
            active: realtimePayloadOpen,
            icon: <Rows4 className="size-4" />,
            id: "worker-overview-realtime-payloads",
            label: "Realtime payloads",
            onSelect: toggleRealtimePayloadOpen,
          },
        ],
        title: workerOverviewRealtime.error ?? undefined,
      },
      refresh: {
        disabled: landingSnapshot.loading || landingSnapshot.refreshing === true,
        onRefresh: refreshWorkerSnapshot,
        refreshing: landingSnapshot.refreshing === true,
        title: "Refresh worker logs and snapshot",
      },
    }),
    [
      landingSnapshot.loading,
      landingSnapshot.refreshing,
      refreshWorkerSnapshot,
      realtimePayloadOpen,
      toggleRealtimePayloadOpen,
      workerOverviewRealtime.connectionState,
      workerOverviewRealtime.enabled,
      workerOverviewRealtime.error,
    ]
  );
  const openCopyQueueDialog = async () => {
    if (!worker) {
      return;
    }

    setOpeningCopyQueue(true);
    try {
      const workerConfiguration = await workableFetch<WorkableHttpWorkerConfiguration>(
        connection,
        `workers/${worker.id.value}/configuration`
      );
      const definition = workerConfiguration.definitionInfo?.definition;
      if (!definition) {
        throw new Error("Queue settings are unavailable for this worker.");
      }

      setCopyQueueDialog({
        definition,
        formValue: cloneJsonValue(parseSchemaJsonValue(workerConfiguration.input?.json)),
        queueRequestSchema: workerConfiguration.queueRequestSchema,
        request: createCopiedWorkerQueueRequest(workerConfiguration, lastSavedWorkerConfigurationRequestRef.current),
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
    if (!workerConfigurationSource) {
      return;
    }

    const seedKey = compactJson(workerConfigurationSource);
    if (workerConfigurationSeedRef.current === seedKey) {
      return;
    }

    workerConfigurationSeedRef.current = seedKey;
    queueMicrotask(() => setWorkerConfigurationRequest(createWorkerConfigurationRequest(workerConfigurationSource)));
  }, [workerConfigurationSource]);

  useEffect(() => {
    if (!lastSavedWorkerConfigurationRequestRef.current || !workerConfigurationSource) {
      return;
    }

    if (
      compactJson(createWorkerReconfiguration(lastSavedWorkerConfigurationRequestRef.current)) ===
      compactJson(createWorkerReconfiguration(createWorkerConfigurationRequest(workerConfigurationSource)))
    ) {
      lastSavedWorkerConfigurationRequestRef.current = null;
    }
  }, [workerConfigurationSource]);

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
    const isActive = workerOverviewRealtime.enabled &&
      workerOverviewRealtime.connectionState !== "disabled";
    onActiveRealtimeConnectionCountChange(isActive ? 1 : 0);

    return () => onActiveRealtimeConnectionCountChange(0);
  }, [
    onActiveRealtimeConnectionCountChange,
    workerOverviewRealtime.connectionState,
    workerOverviewRealtime.enabled,
  ]);

  useEffect(() => {
    const notificationId = `worker-overview-realtime:${workerId}`;
    const description = realtimeUpdateError ?? workerOverviewRealtime.error;
    if (!description) {
      clearSystemNotification(notificationId);
      return;
    }

    reportSystemNotification({
      description,
      id: notificationId,
      tone: "warning",
      title: "Worker overview realtime issue",
    });

    return () => {
      clearSystemNotification(notificationId);
    };
  }, [
    clearSystemNotification,
    realtimeUpdateError,
    reportSystemNotification,
    workerId,
    workerOverviewRealtime.error,
  ]);

  useRegisterConsoleHeaderCapabilities({
    active: true,
    capabilities: headerCapabilities,
    id: "worker-console",
  });

  useEffect(() => {
    setRealtimeWorker(null);
    setRealtimeLatestIteration(null);
    setRealtimeLogSummary(null);
    setRealtimeTimelineSummary(null);
    setRealtimeRecentIterations([]);
    setRealtimeLogEntries([]);
    setRealtimeTimelineItems([]);
    setRealtimeUpdateError(undefined);
    setStableAggregateLogSummary(null);
  }, [refreshSeed, workerId, workerOverviewRealtimeCriteriaKey]);

  useEffect(() => {
    const update = workerOverviewRealtime.data;
    if (!update) {
      return;
    }

    try {
      if (update.requiresRefresh) {
        const now = Date.now();
        if (now < workerOverviewRealtimeResyncCooldownUntilRef.current) {
          return;
        }

        workerOverviewRealtimeResyncCooldownUntilRef.current =
          now + workerOverviewRealtimeResyncCooldownMs;
        setActionFeedback({
          message: update.refreshReason?.trim().length
            ? update.refreshReason
            : "Realtime worker updates fell behind and were refreshed.",
          title: "Realtime resynced",
          tone: "warning",
        });
        // The server restarts the worker-overview pump after issuing a resync
        // instruction, so we only need a fresh HTTP snapshot here.
        setManualRefreshToken((current) => current + 1);
        setRealtimeUpdateError(undefined);
        return;
      }

      if (update.worker !== undefined) {
        setRealtimeWorker(update.worker ?? null);
      }

      if (update.latestIteration !== undefined) {
        setRealtimeLatestIteration(update.latestIteration ?? null);
      }

      if (update.logSummary !== undefined) {
        setRealtimeLogSummary(update.logSummary ?? null);
      }

      if (update.timelineSummary !== undefined) {
        setRealtimeTimelineSummary(update.timelineSummary ?? null);
      }

      if (update.recentIterations && update.recentIterations.length > 0) {
        const recentIterations = update.recentIterations;
        setRealtimeRecentIterations((current) =>
          mergeWorkerOverviewRecentIterations(current, recentIterations)
        );
      }

      if (update.logEntries && update.logEntries.length > 0) {
        const logEntries = update.logEntries;
        setRealtimeLogEntries((current) =>
          capWorkerLogEntries(
            mergeWorkerOverviewRealtimeEntries(current, logEntries, logSortDirection),
            maxWorkerLogPanelEntries
          )
        );
      }

      if (update.timelineItems && update.timelineItems.length > 0) {
        const timelineItems = update.timelineItems;
        setRealtimeTimelineItems((current) => {
          const mergedItems = mergeWorkerOverviewRealtimeEntries(current, timelineItems, timelineSortDirection);
          return current.length === 0
            ? retainLatestWorkerTimelineRealtimeItems(
                mergedItems,
                timelineSortDirection,
                workerTimelineRetainedRealtimeItemCount
              )
            : mergedItems;
        });
      }

      setRealtimeUpdateError(undefined);
    } catch (error) {
      console.error("Worker overview realtime update processing failed.", error);
      setRealtimeUpdateError(
        error instanceof Error && error.message.trim().length > 0
          ? error.message
          : "A realtime worker overview update could not be processed."
      );
    }
  }, [
    logSortDirection,
    timelineSortDirection,
    workerOverviewRealtime.data,
  ]);

  useEffect(() => {
    setExtraLogEntries([]);
    setRealtimeLogEntries([]);
    setLogPageLoadState({
      hasMore: false,
      loadingMore: false,
    });
  }, [logQueryKey, refreshSeed, workerId]);

  useEffect(() => {
    timelinePageRequestGenerationRef.current += 1;
    setExtraTimelineItems([]);
    setRealtimeTimelineItems([]);
    setTimelinePageLoadState({
      hasMore: false,
      loadingMore: false,
    });
  }, [refreshSeed, timelineQueryKey, workerId]);

  useEffect(() => {
    if (!logBasePage) {
      return;
    }

    setLogPageLoadState((current) => ({
      ...current,
      error: undefined,
      hasMore: logBasePage.hasMore,
      loadingMore: false,
      nextCursor: logBasePage.cursor ?? null,
    }));
  }, [logBasePage]);

  useEffect(() => {
    if (!timelineBasePage) {
      return;
    }

    setTimelinePageLoadState((current) => ({
      ...current,
      error: undefined,
      hasMore: timelineBasePage.hasMore,
      loadingMore: false,
      nextCursor: timelineBasePage.cursor ?? null,
    }));
  }, [timelineBasePage]);

  const releaseDetailedLogData = useCallback(() => {
    setExtraLogEntries([]);
    setRealtimeLogEntries([]);
    setLogPageLoadState({
      hasMore: false,
      loadingMore: false,
    });
    setLogPageResetSeed((current) => current + 1);
  }, []);

  const releaseDetailedTimelineData = useCallback(() => {
    timelinePageRequestGenerationRef.current += 1;
    setExtraTimelineItems([]);
    setRealtimeTimelineItems([]);
    setTimelinePageLoadState({
      hasMore: false,
      loadingMore: false,
    });
    setTimelinePageResetSeed((current) => current + 1);
  }, []);

  useEffect(() => {
    if (isWorkerLogsPanelExpanded && !hiddenPanelIds.has("workerLogs")) {
      return;
    }

    releaseDetailedLogData();
  }, [hiddenPanelIds, isWorkerLogsPanelExpanded, releaseDetailedLogData]);

  useEffect(() => {
    if (isWorkerTimelinePanelExpanded && !hiddenPanelIds.has("workerTimeline")) {
      return;
    }

    releaseDetailedTimelineData();
  }, [hiddenPanelIds, isWorkerTimelinePanelExpanded, releaseDetailedTimelineData]);

  useEffect(() => {
    if (workerDurationPanelViewState !== "compact" && !hiddenPanelIds.has("workerDuration")) {
      return;
    }

    setRealtimeRecentIterations([]);
  }, [hiddenPanelIds, workerDurationPanelViewState]);

  const loadMoreLogs = useCallback(async () => {
    if (
      logPageLoadState.loadingMore ||
      !logPageLoadState.hasMore ||
      !logPageLoadState.nextCursor
    ) {
      return;
    }

    setLogPageLoadState((current) => ({
      ...current,
      error: undefined,
      loadingMore: true,
    }));

    try {
      const logs = await workableFetch<WorkWorkerOverviewLogSection>(
        connection,
        createWorkerOverviewLogsPath(workerId, {
          activityCursor: logPageLoadState.nextCursor,
          activityTake: workerLogActivityPageSize,
          logLevels: normalizedSelectedLogLevels,
          logSortDirection,
        })
      );
      const page = logs.page;
      if (!page) {
        setLogPageLoadState((current) => ({
          ...current,
          hasMore: false,
          loadingMore: false,
          nextCursor: null,
        }));
        return;
      }

      setExtraLogEntries((current) => [...current, ...page.items]);
      setLogPageLoadState({
        error: undefined,
        hasMore: page.hasMore,
        loadingMore: false,
        nextCursor: page.cursor ?? null,
      });
    } catch (caught) {
      setLogPageLoadState((current) => ({
        ...current,
        error: caught instanceof Error ? caught.message : "Could not load more logs.",
        loadingMore: false,
      }));
    }
  }, [
    connection,
    logPageLoadState.hasMore,
    logPageLoadState.loadingMore,
    logPageLoadState.nextCursor,
    logSortDirection,
    normalizedSelectedLogLevels,
    workerId,
  ]);

  const loadMoreTimeline = useCallback(async () => {
    if (timelinePageLoadState.loadingMore || !timelinePageLoadState.hasMore || !timelinePageLoadState.nextCursor) {
      return;
    }

    const requestGeneration = timelinePageRequestGenerationRef.current;
    setTimelinePageLoadState((current) => ({
      ...current,
      error: undefined,
      loadingMore: true,
    }));

    try {
      const timeline = await workableFetch<WorkWorkerOverviewTimelineSection>(
        connection,
        createWorkerOverviewTimelinePath(workerId, {
          activityCursor: timelinePageLoadState.nextCursor,
          activityTake: workerTimelineActivityPageSize,
          timelineFilters: normalizedSelectedTimelineFilters,
          timelineSortDirection,
        })
      );
      const page = timeline.page;
      if (timelinePageRequestGenerationRef.current !== requestGeneration) {
        return;
      }

      if (!page) {
        setTimelinePageLoadState((current) => ({
          ...current,
          hasMore: false,
          loadingMore: false,
          nextCursor: null,
        }));
        return;
      }

      setExtraTimelineItems((current) => [...current, ...page.items]);
      setTimelinePageLoadState({
        error: undefined,
        hasMore: page.hasMore,
        loadingMore: false,
        nextCursor: page.cursor ?? null,
      });
    } catch (caught) {
      if (timelinePageRequestGenerationRef.current !== requestGeneration) {
        return;
      }

      setTimelinePageLoadState((current) => ({
        ...current,
        error: caught instanceof Error ? caught.message : "Could not load more timeline events.",
        loadingMore: false,
      }));
    }
  }, [
    connection,
    normalizedSelectedTimelineFilters,
    timelinePageLoadState.hasMore,
    timelinePageLoadState.loadingMore,
    timelinePageLoadState.nextCursor,
    timelineSortDirection,
    workerId,
  ]);

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
      if (result.status === "Conflict" || shouldRefreshWorkerOverviewAfterAction) {
        setActionRefreshToken((value) => value + 1);
        setRealtimeSubscriptionResetToken((value) => value + 1);
      }
    } catch (error) {
      setActionFeedback({
        message: error instanceof Error ? error.message : `Unable to ${action.toLowerCase()} worker.`,
        tone: "warning",
      });
    } finally {
      setPendingAction(null);
    }
  };

  const saveWorkerConfiguration = useCallback(async () => {
    if (!worker || !hasUnsavedWorkerConfigurationChanges) {
      return;
    }

    setIsSavingWorkerConfiguration(true);
    try {
      const result = await workableFetch<WorkActionOutcome>(
        connection,
        `workers/${worker.id.value}/reconfigure`,
        {
          method: "POST",
          body: JSON.stringify({
            revision: worker.revision,
            changes: createWorkerReconfiguration(workerConfigurationRequest),
          }),
        }
      );
      setActionFeedback({
        message: result.messages.map((message) => message.text).filter(Boolean).join(" ") ||
          `Worker configuration ${result.status.toLowerCase()}.`,
        tone: result.status === "Accepted" ? "success" : "warning",
      });
      if (result.status === "Accepted") {
        lastSavedWorkerConfigurationRequestRef.current = cloneQueueWorkRequest(workerConfigurationRequest);
      }
      setActionRefreshToken((value) => value + 1);
    } catch (caught) {
      setActionFeedback({
        message: caught instanceof Error ? caught.message : "Worker reconfiguration failed.",
        tone: "warning",
      });
    } finally {
      setIsSavingWorkerConfiguration(false);
    }
  }, [connection, hasUnsavedWorkerConfigurationChanges, worker, workerConfigurationRequest]);

  const exitWorkerPanelFocus = useCallback(() => {
    const snapshot = focusedWorkerHiddenSnapshotRef.current;
    focusedWorkerHiddenSnapshotRef.current = null;
    setFocusedWorkerPanel(null);
    setHiddenPanelIds(snapshot ? new Set(snapshot) : createDefaultWorkerHiddenPanels(workerConfigurationDifferenceCount > 0));
  }, [workerConfigurationDifferenceCount]);

  const enterWorkerPanelFocus = useCallback((panelId: WorkerFocusedPanelId) => {
    setHiddenPanelIds((current) => {
      if (focusedWorkerHiddenSnapshotRef.current === null) {
        focusedWorkerHiddenSnapshotRef.current = new Set(current);
      }

      return createWorkerFocusedHiddenPanels(panelId);
    });
    setFocusedWorkerPanel(panelId);
  }, []);

  const setWorkerLogsPanelViewState = useCallback((shape: WorkComponentShape) => {
    if (shape === "detailed") {
      enterWorkerPanelFocus("workerLogs");
    } else if (focusedWorkerPanel === "workerLogs") {
      exitWorkerPanelFocus();
    }

    setWorkerLogsPanelViewStateState(shape);
  }, [enterWorkerPanelFocus, exitWorkerPanelFocus, focusedWorkerPanel]);

  const setWorkerTimelinePanelViewState = useCallback((shape: WorkComponentShape) => {
    if (shape === "detailed") {
      enterWorkerPanelFocus("workerTimeline");
    } else if (focusedWorkerPanel === "workerTimeline") {
      exitWorkerPanelFocus();
    }

    setWorkerTimelinePanelViewStateState(shape);
  }, [enterWorkerPanelFocus, exitWorkerPanelFocus, focusedWorkerPanel]);

  const setWorkerPanelVisible = useCallback((panelId: WorkerDetailPanelId, visible: boolean) => {
    if (!visible && (panelId === "workerLogs" || panelId === "workerTimeline") && focusedWorkerPanel === panelId) {
      const snapshot = focusedWorkerHiddenSnapshotRef.current;
      focusedWorkerHiddenSnapshotRef.current = null;
      setFocusedWorkerPanel(null);
      setHiddenPanelIds(() => {
        return updateHiddenPanelIds(
          snapshot ? snapshot : createDefaultWorkerHiddenPanels(workerConfigurationDifferenceCount > 0),
          panelId,
          false
        );
      });
      return;
    }

    setHiddenPanelIds((current) => updateHiddenPanelIds(current, panelId, visible));
  }, [focusedWorkerPanel, workerConfigurationDifferenceCount]);
  const openWorkerConfigurationPanel = useCallback(() => {
    if (focusedWorkerPanel !== null) {
      const snapshot = new Set(
        focusedWorkerHiddenSnapshotRef.current ??
        createDefaultWorkerHiddenPanels(workerConfigurationDifferenceCount > 0)
      );
      const visible = snapshot.has("workerConfiguration");
      if (visible) {
        setWorkerConfigurationPanelViewState("standard");
      }
      focusedWorkerHiddenSnapshotRef.current = updateHiddenPanelIds(snapshot, "workerConfiguration", visible);
      return;
    }

    setHiddenPanelIds((current) => {
      const visible = current.has("workerConfiguration");
      if (visible) {
        setWorkerConfigurationPanelViewState("standard");
      }
      return updateHiddenPanelIds(current, "workerConfiguration", visible);
    });
  }, [focusedWorkerPanel, workerConfigurationDifferenceCount]);

  useEffect(() => {
    if (!worker || initializedWorkerPanelsRef.current === workerId) {
      return;
    }

    setHiddenPanelIds(createDefaultWorkerHiddenPanels());
    focusedWorkerHiddenSnapshotRef.current = null;
    setFocusedWorkerPanel(null);
    setWorkerControlsPanelViewState("compact");
    setWorkerConfigurationPanelViewState("compact");
    setWorkerConfigurationDisplayMode("auto");
    setWorkerConfigurationAutoShowAllValues(true);
    setWorkerLogsPanelViewStateState(activity === "Timeline" ? "compact" : "standard");
    setWorkerDurationPanelViewState("standard");
    setWorkerTimelinePanelViewStateState(activity === "Timeline" ? "standard" : "compact");
    setSelectedLogLevels(null);
    setLogSortDirection("desc");
    setSelectedTimelineFilters(null);
    setTimelineSortDirection("desc");
    initializedWorkerPanelsRef.current = workerId;
    initializedWorkerConfigurationVisibilityRef.current = null;
    initializedWorkerConfigurationAutoModeRef.current = null;
  }, [activity, worker, workerId]);

  useEffect(() => {
    if (!worker || initializedWorkerConfigurationAutoModeRef.current === workerId) {
      return;
    }

    initializedWorkerConfigurationAutoModeRef.current = workerId;
    setWorkerConfigurationAutoShowAllValues(workerConfigurationDifferenceCount === 0);
  }, [worker, workerConfigurationDifferenceCount, workerId]);

  useEffect(() => {
    if (!worker || initializedWorkerConfigurationVisibilityRef.current === workerId) {
      return;
    }

    initializedWorkerConfigurationVisibilityRef.current = workerId;
    if (workerConfigurationDifferenceCount === 0) {
      return;
    }

    queueMicrotask(() => {
      setHiddenPanelIds((current) => updateHiddenPanelIds(current, "workerConfiguration", true));
    });
  }, [worker, workerConfigurationDifferenceCount, workerId]);

  const uiStateSnapshot = useMemo<WorkerConsoleViewUiStateSnapshot>(() => ({
      focusedWorkerHiddenSnapshotPanelIds: focusedWorkerHiddenSnapshotRef.current
        ? [...focusedWorkerHiddenSnapshotRef.current]
        : null,
      focusedWorkerPanel,
      hiddenPanelIds: [...hiddenPanelIds],
      logSortDirection,
      selectedLogLevels,
      selectedTimelineFilters,
      timelineSortDirection,
      workerConfigurationAutoShowAllValues,
      workerConfigurationDisplayMode,
      workerConfigurationPanelViewState,
      workerControlsPanelViewState,
      workerDurationPanelViewState,
      workerId,
      workerLogsPanelViewState,
      workerTimelinePanelViewState,
    }), [
      focusedWorkerPanel,
      hiddenPanelIds,
      logSortDirection,
      selectedLogLevels,
      selectedTimelineFilters,
      timelineSortDirection,
      workerConfigurationAutoShowAllValues,
      workerConfigurationDisplayMode,
      workerConfigurationPanelViewState,
      workerControlsPanelViewState,
      workerDurationPanelViewState,
      workerId,
      workerLogsPanelViewState,
      workerTimelinePanelViewState,
    ]);

  useEffect(() => {
    if (!onUiStateChange) {
      return;
    }

    const snapshotKey = JSON.stringify(uiStateSnapshot);
    if (lastPublishedUiStateKeyRef.current === snapshotKey) {
      return;
    }

    lastPublishedUiStateKeyRef.current = snapshotKey;
    onUiStateChange(uiStateSnapshot);
  }, [
    onUiStateChange,
    uiStateSnapshot,
  ]);

  const resetWorkerUiToDefaults = useCallback(() => {
    setHiddenPanelIds(createDefaultWorkerHiddenPanels(workerConfigurationDifferenceCount > 0));
    focusedWorkerHiddenSnapshotRef.current = null;
    setFocusedWorkerPanel(null);
    setWorkerControlsPanelViewState("compact");
    setWorkerConfigurationPanelViewState("compact");
    setWorkerConfigurationDisplayMode("auto");
    setWorkerLogsPanelViewStateState(activity === "Timeline" ? "compact" : "standard");
    setWorkerDurationPanelViewState("standard");
    setWorkerTimelinePanelViewStateState(activity === "Timeline" ? "standard" : "compact");
    setSelectedLogLevels(null);
    setLogSortDirection("desc");
    setSelectedTimelineFilters(null);
    setTimelineSortDirection("desc");
  }, [activity, workerConfigurationDifferenceCount]);
  const logsLoading = isWorkerLogsPanelExpanded && !usingBootstrapLogsPage &&
    (logsPageSnapshot.loading && !logBasePage);
  const timelineLoading = isWorkerTimelinePanelExpanded && !usingBootstrapTimelinePage &&
    (timelinePageSnapshot.loading && !timelineBasePage);
  const logPanelError = logsPageSnapshot.error ?? logPageLoadState.error;
  const timelinePanelError = timelinePageSnapshot.error ?? timelinePageLoadState.error;
  const setLogLevelVisible = useCallback((level: WorkerLogFilterLevel, visible: boolean) => {
    setSelectedLogLevels((current) => updateSelectedLogLevels(current, level, visible));
  }, []);
  const focusLogLevel = useCallback((level: WorkerLogFilterLevel) => {
    setSelectedLogLevels(createSelectedLogLevelsForFocus(level));
    setWorkerLogsPanelViewState("standard");
  }, [setWorkerLogsPanelViewState]);
  const focusTimelineFilter = useCallback((filterKind: WorkerTimelineFilterKind) => {
    setSelectedTimelineFilters(createSelectedTimelineFiltersForFocus(filterKind));
    setWorkerTimelinePanelViewStateState("standard");
  }, []);
  const setTimelineFilterSelected = useCallback((filterKind: WorkerTimelineFilterKind, selected: boolean) => {
    setSelectedTimelineFilters((current) => updateSelectedTimelineFilters(current, filterKind, selected));
  }, []);
  const forgetPagedTimelineItems = useCallback(() => {
    if (extraTimelineItems.length === 0) {
      return;
    }

    timelinePageRequestGenerationRef.current += 1;
    setExtraTimelineItems([]);
    setTimelinePageLoadState({
      error: undefined,
      hasMore: timelineBasePage?.hasMore ?? false,
      loadingMore: false,
      nextCursor: timelineBasePage?.cursor ?? null,
    });
  }, [extraTimelineItems.length, timelineBasePage]);
  const forgetPushedTimelineItems = useCallback(() => {
    setRealtimeTimelineItems((current) => {
      const retainedItems = retainLatestWorkerTimelineRealtimeItems(
        current,
        timelineSortDirection,
        workerTimelineRetainedRealtimeItemCount
      );

      return retainedItems.length === current.length ? current : retainedItems;
    });
  }, [timelineSortDirection]);
  const clearTimelineFilters = useCallback(() => {
    setSelectedTimelineFilters(null);
  }, []);
  const closeWorkerTimelinePanel = useCallback(() => {
    setWorkerPanelVisible("workerTimeline", false);
  }, [setWorkerPanelVisible]);
  const handleLoadMoreTimeline = useCallback(() => {
    void loadMoreTimeline();
  }, [loadMoreTimeline]);
  const openWorkerIteration = useCallback((sequence: number) => {
    onOpenIteration(workerId, sequence);
  }, [onOpenIteration, workerId]);
  const toggleTimelineSortDirection = useCallback(() => {
    setTimelineSortDirection((current) => current === "desc" ? "asc" : "desc");
  }, []);

  return (
    <ConsolePageLayout fill={isWorkerPanelFocused} scrollMode={isWorkerPanelFocused ? "panel" : "browser"}>
      <PanelAggregateFrame
        fill={isWorkerPanelFocused}
        hiddenPanelIds={[...hiddenPanelIds]}
        onPanelVisibilityChange={setWorkerPanelVisible}
        onResetUi={resetWorkerUiToDefaults}
        padding="tightTop"
        panelOptions={workerPanelOptions}
        scrollMode={isWorkerPanelFocused ? "panel" : "browser"}
        settingsButtonLabel="Worker panel settings"
        settingsDescription="Checked panels are shown on the worker details page."
        settingsTitle="Worker panels"
      >
        {landingSnapshot.loading && <StackedSkeleton count={8} />}
        {landingSnapshot.error && !worker && (
          <ErrorBanner key={landingSnapshot.error} message={landingSnapshot.error} title="Unable to load worker" />
        )}
        {worker && (
          <div className={cn("relative flex min-h-0 flex-1 flex-col gap-6", isWorkerPanelFocused && "overflow-hidden")}>
            {actionFeedback?.tone === "success" && (
              <div className="pointer-events-none fixed inset-x-4 bottom-4 z-50 flex justify-end">
                <div className="pointer-events-auto w-full max-w-md">
                  <FeedbackBanner
                    key={actionFeedback.message}
                    message={actionFeedback.message}
                    onDismiss={() => setActionFeedback(undefined)}
                    title={actionFeedback.title ?? "Action result"}
                    tone={actionFeedback.tone}
                  />
                </div>
              </div>
            )}
            {!hiddenPanelIds.has("workerControls") ? (
              <PanelShell
                actions={(
                  <>
                    <Tooltip delayDuration={250}>
                      <TooltipTrigger asChild>
                        <Button
                          className={workerActionToneClassName("Start", pendingAction !== null || workerActionRefreshInProgress || openingCopyQueue || !worker)}
                          disabled={pendingAction !== null || workerActionRefreshInProgress || openingCopyQueue || !worker}
                          onClick={() => void openCopyQueueDialog()}
                          size="sm"
                          variant="outline"
                        >
                          {openingCopyQueue ? <Loader2 className="size-4 animate-spin" /> : <Send className="size-4" />}
                          New
                        </Button>
                      </TooltipTrigger>
                      <TooltipContent side="top" sideOffset={6}>
                        Queue a new worker using this worker&apos;s current input and runtime settings.
                      </TooltipContent>
                    </Tooltip>
                    <Tooltip delayDuration={250}>
                      <TooltipTrigger asChild>
                        <Button
                          disabled={!worker}
                          onClick={openWorkerConfigurationPanel}
                          size="sm"
                          type="button"
                          variant="outline"
                        >
                          <Braces className="size-4" />
                          Config
                        </Button>
                      </TooltipTrigger>
                      <TooltipContent side="top" sideOffset={6}>
                        Open the worker configuration panel.
                      </TooltipContent>
                    </Tooltip>
                  </>
                )}
                leadingActions={(
                  <div className={`flex min-w-0 flex-wrap items-center ${consolePanelActionGapClassName}`}>
                    <WorkerActionButton
                      action="Start"
                      disabled={pendingAction !== null || workerActionRefreshInProgress || !availableActions.Start}
                      icon={Play}
                      onAction={executeAction}
                    />
                    <WorkerActionButton
                      action="Pause"
                      disabled={pendingAction !== null || workerActionRefreshInProgress || !availableActions.Pause}
                      icon={Pause}
                      onAction={executeAction}
                    />
                    <WorkerActionButton
                      action="Cancel"
                      cancellationMayStopExecution={worker.state !== "Paused" && worker.state !== "Failed"}
                      disabled={pendingAction !== null || workerActionRefreshInProgress || !availableActions.Cancel}
                      icon={Ban}
                      onAction={executeAction}
                    />
                    <WorkerActionButton
                      action="Push"
                      disabled={pendingAction !== null || workerActionRefreshInProgress || !availableActions.Push}
                      icon={Clock3}
                      onAction={executeAction}
                      tooltip="Request the next scheduled run immediately."
                    />
                    <WorkerActionButton
                      action="Purge"
                      disabled={pendingAction !== null || workerActionRefreshInProgress || !availableActions.Purge}
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
                <div className="grid gap-3 md:grid-cols-2 xl:grid-cols-3">
                  <MetadataItem label="Created on" value={formatDateTime(worker.createdAt)} />
                  <MetadataItem label="Created by" value={getWorkerCreatedByLabel(worker)} />
                  <MetadataItem
                    label="Definition"
                    value={(
                      <button
                        className="text-left font-mono text-sm text-sky-300 underline-offset-4 hover:underline"
                        onClick={() => onOpenDefinitionCatalog(worker.definitionName, worker.definitionCategory)}
                        type="button"
                      >
                        {worker.definitionName}
                      </button>
                    )}
                  />
                </div>
                <div className="grid gap-4 xl:grid-cols-2">
                  <WorkDataCard data={worker.input} label="Input" />
                  <WorkDataCard
                    data={primaryIteration?.output}
                    label={primaryIteration ? `Latest output (iteration #${primaryIteration.sequence})` : "Latest output"}
                  />
                </div>
              </PanelShell>
            ) : null}
            {!hiddenPanelIds.has("workerConfiguration") ? (
              <PanelShell
                contentClassName={workerConfigurationPanelViewState === "compact" ? "hidden" : "space-y-4"}
                onClose={() => setWorkerPanelVisible("workerConfiguration", false)}
                onViewStateChange={setWorkerConfigurationPanelViewState}
                supportedViewStates={["compact", "standard"]}
                title={(
                  <div className="flex flex-wrap items-center gap-2">
                    <span className="font-medium">Worker configuration</span>
                    <WorkerConfigurationStatusBadge
                      definition={workerDefinition}
                      differenceCount={workerConfigurationDifferenceCount}
                      differences={workerConfigurationDifferences}
                    />
                  </div>
                )}
                viewState={workerConfigurationPanelViewState}
              >
                {workerConfigurationPanelViewState === "standard" && !workerConfigurationSource ? (
                  <StackedSkeleton count={6} />
                ) : (
                  <ConfigurationEditorSurface
                    descriptor={workerConfigurationDescriptor}
                    differences={workerConfigurationDifferences}
                    onRequestChange={setWorkerConfigurationRequest}
                    onToggleShowAllValues={() => setWorkerConfigurationDisplayMode((current) =>
                      current === "all-values" ? "only-changes" : "all-values"
                    )}
                    request={workerConfigurationRequest}
                    schema={queueRequestSchema}
                    showDefaultTooltipsInChangedView
                    showAllValues={workerConfigurationDisplayMode === "all-values" ||
                      (workerConfigurationDisplayMode === "auto" && workerConfigurationAutoShowAllValues)}
                    footer={(
                      <div className="flex flex-wrap items-center justify-end gap-2 border-t pt-4">
                        <Button
                          disabled={isSavingWorkerConfiguration || !canResetWorkerConfigurationToDefaults}
                          onClick={() => setWorkerConfigurationRequest(cloneQueueWorkRequest(workerDefaultConfigurationRequest))}
                          size="sm"
                          type="button"
                          variant="outline"
                        >
                          <RotateCw className="size-4" />
                          Reset to defaults
                        </Button>
                        <Button
                          disabled={isSavingWorkerConfiguration || !hasUnsavedWorkerConfigurationChanges || !workerConfigurationSource}
                          onClick={() => workerConfigurationSource && setWorkerConfigurationRequest(createWorkerConfigurationRequest(workerConfigurationSource))}
                          size="sm"
                          type="button"
                          variant="outline"
                        >
                          <RotateCw className="size-4" />
                          Discard
                        </Button>
                        <Button
                          disabled={isSavingWorkerConfiguration || !hasUnsavedWorkerConfigurationChanges}
                          onClick={() => void saveWorkerConfiguration()}
                          size="sm"
                          type="button"
                        >
                          {isSavingWorkerConfiguration ? (
                            <Loader2 className="size-4 animate-spin" />
                          ) : (
                            <CheckCircle2 className="size-4" />
                          )}
                          Save
                        </Button>
                      </div>
                    )}
                  />
                )}
              </PanelShell>
            ) : null}
            {terminalFailure && terminalFailureKey !== dismissedWorkerFailureKey ? (
              <WorkerFailureBanner
                key={terminalFailureKey}
                details={terminalFailure}
                now={relativeNow}
                onDismiss={() => setDismissedWorkerFailureKey(terminalFailureKey)}
              />
            ) : null}
            {landingSnapshot.error && (
              <ErrorBanner key={landingSnapshot.error} message={landingSnapshot.error} title="Unable to load worker" />
            )}
            {workerDetailSnapshot.error && workerConfigurationPanelViewState === "standard" ? (
              <ErrorBanner
                key={workerDetailSnapshot.error}
                message={workerDetailSnapshot.error}
                title="Unable to load worker configuration"
              />
            ) : null}
            {actionFeedback && actionFeedback.tone !== "success" && (
              <FeedbackBanner
                key={actionFeedback.message}
                message={actionFeedback.message}
                onDismiss={() => setActionFeedback(undefined)}
                title={actionFeedback.title ?? "Action result"}
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
                  <IterationDurationGraph
                    iterations={timelineIterations}
                    now={relativeNow}
                    onOpenIteration={openWorkerIteration}
                  />
                  {timelineIterations.length <= 1 ? (
                    <EmptyListState message="At least two iteration points are needed to draw the duration chart." />
                  ) : null}
                </PanelShell>
            ) : null}
            {!hiddenPanelIds.has("workerLogs") ? (
              <WorkerLogPanel
                connectionError={logPanelError}
                entries={executionLogs}
                hasActiveIteration={hasActiveIteration}
                hasMore={logPageLoadState.hasMore}
                isLoading={logsLoading}
                isLoadingMore={logPageLoadState.loadingMore}
                onClearFilters={() => setSelectedLogLevels(null)}
                onClose={() => setWorkerPanelVisible("workerLogs", false)}
                onFocusLevel={focusLogLevel}
                onLoadMore={() => void loadMoreLogs()}
                onOpenIteration={(sequence) => onOpenIteration(worker.id.value, sequence)}
                onSetLevelVisible={setLogLevelVisible}
                onToggleSortDirection={() => setLogSortDirection((current) => current === "desc" ? "asc" : "desc")}
                summaryOverride={logSummary ?? stableAggregateLogSummary ?? undefined}
                selectedLevels={normalizedSelectedLogLevels}
                sortDirection={logSortDirection}
                onViewStateChange={setWorkerLogsPanelViewState}
                viewState={workerLogsPanelViewState}
              />
            ) : null}

            {!hiddenPanelIds.has("workerTimeline") ? (
              <WorkerTimelinePanel
                error={timelinePanelError}
                hasMore={timelinePageLoadState.hasMore}
                items={timelineItems}
                isLoading={timelineLoading}
                isLoadingMore={timelinePageLoadState.loadingMore}
                onClearFilters={clearTimelineFilters}
                onClose={closeWorkerTimelinePanel}
                onForgetPagedItems={forgetPagedTimelineItems}
                onForgetPushedItems={forgetPushedTimelineItems}
                onFocusFilter={focusTimelineFilter}
                onLoadMore={handleLoadMoreTimeline}
                onOpenIteration={openWorkerIteration}
                pagedItemCount={extraTimelineItems.length}
                pushedItemCount={realtimeTimelineItems.length}
                onSetFilterSelected={setTimelineFilterSelected}
                onToggleSortDirection={toggleTimelineSortDirection}
                onViewStateChange={setWorkerTimelinePanelViewState}
                selectedFilters={normalizedSelectedTimelineFilters}
                sortDirection={timelineSortDirection}
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
        preloadedQueueSchemaDescriptor={copyQueueDialog?.queueRequestSchema ?? null}
        fetchQueueSchemaWhenNeeded={false}
        initialRequest={copyQueueDialog?.request}
        onOpenChange={(open) => !open && setCopyQueueDialog(null)}
        onQueuedWorker={onOpenWorker}
      />
    </ConsolePageLayout>
  );
}

export function IterationConsoleView({
  connection,
  onOpenDefinition,
  refreshToken,
  sequence,
  workerId,
}: {
  connection: WorkableConnection;
  onNavigateBack: () => void;
  onOpenDefinition: (definitionName: string, definitionLabel?: string | null) => void;
  refreshToken: number;
  sequence: number;
  workerId: string;
}) {
  const iterationDetail = useWorkableResource<WorkableHttpWorkerIterationDetail>(
    connection,
    `workers/${workerId}/iterations/${sequence}/detail`,
    refreshToken
  );
  const relativeNow = useLiveRelativeTimeNow();
  const [hiddenPanelIds, setHiddenPanelIds] = useState<ReadonlySet<IterationDetailPanelId>>(() => new Set());
  const [focusedIterationPanel, setFocusedIterationPanel] = useState<IterationFocusedPanelId | null>(null);
  const [summaryViewState, setSummaryViewState] = useState<WorkComponentShape>("compact");
  const [messagesViewState, setMessagesViewState] = useState<WorkComponentShape>("compact");
  const [outputViewState, setOutputViewState] = useState<WorkComponentShape>("standard");
  const [logsViewState, setLogsViewState] = useState<WorkComponentShape>("detailed");
  const [iterationLogSortDirection, setIterationLogSortDirection] = useState<WorkerSortDirection>("desc");
  const [iterationSelectedLogLevels, setIterationSelectedLogLevels] = useState<WorkerLogFilterLevel[] | null>(null);
  const [extraIterationLogEntries, setExtraIterationLogEntries] = useState<WorkerLogEntry[]>([]);
  const [iterationLogPageLoadState, setIterationLogPageLoadState] = useState<WorkerOverviewPageLoadState>({
    hasMore: false,
    loadingMore: false,
  });
  const focusedIterationHiddenSnapshotRef = useRef<ReadonlySet<IterationDetailPanelId> | null>(null);
  const detail = iterationDetail.data;
  const activeIteration = detail?.iteration;
  const normalizedSelectedIterationLogLevels = useMemo(
    () => iterationSelectedLogLevels ?? workerLogFilterLevels,
    [iterationSelectedLogLevels]
  );
  const usingIterationLandingLogPage = iterationLogSortDirection === "desc" && iterationSelectedLogLevels === null;
  const iterationLogQueryKey = useMemo(
    () => `${iterationLogSortDirection}|${normalizedSelectedIterationLogLevels.join(",")}`,
    [iterationLogSortDirection, normalizedSelectedIterationLogLevels]
  );
  const iterationLogsSnapshot = useWorkableResource<WorkIterationLogSection>(
    connection,
    usingIterationLandingLogPage
      ? null
      : createIterationLogsPath(workerId, sequence, {
          logLevels: normalizedSelectedIterationLogLevels,
          sortDirection: iterationLogSortDirection,
          take: workerLogActivityPageSize,
        }),
    refreshToken,
    {
      resetKey: `${workerId}:${sequence}:${iterationLogQueryKey}`,
    }
  );
  const iterationLogBaseSection = usingIterationLandingLogPage ? detail?.logs : iterationLogsSnapshot.data;
  const iterationLogBasePage = iterationLogBaseSection?.page;
  const iterationLogSummary = iterationLogBaseSection?.summary;
  const iterationLogEntries = useMemo(
    () => [...(iterationLogBasePage?.items ?? []), ...extraIterationLogEntries],
    [extraIterationLogEntries, iterationLogBasePage?.items]
  );
  const failureDetails = useMemo(
    () => activeIteration?.failure ? createWorkerFailureDetailsFromIterationFailure(activeIteration.failure) : null,
    [activeIteration?.failure]
  );
  const isIterationPanelFocused = focusedIterationPanel !== null;

  const exitIterationPanelFocus = useCallback(() => {
    const snapshot = focusedIterationHiddenSnapshotRef.current;
    focusedIterationHiddenSnapshotRef.current = null;
    setFocusedIterationPanel(null);
    setHiddenPanelIds(snapshot ? new Set(snapshot) : new Set());
  }, []);

  const enterIterationPanelFocus = useCallback((panelId: IterationFocusedPanelId) => {
    setHiddenPanelIds((current) => {
      if (focusedIterationPanel === panelId) {
        return current;
      }

      focusedIterationHiddenSnapshotRef.current = new Set(current);
      setFocusedIterationPanel(panelId);
      return createIterationFocusedHiddenPanels(panelId);
    });
  }, [focusedIterationPanel]);

  const setIterationPanelVisible = useCallback((panelId: IterationDetailPanelId, visible: boolean) => {
    if (!visible && panelId === focusedIterationPanel) {
      focusedIterationHiddenSnapshotRef.current = null;
      setFocusedIterationPanel(null);
    }
    setHiddenPanelIds((current) => updateHiddenPanelIds(current, panelId, visible));
  }, [focusedIterationPanel]);

  const resetIterationUiToDefaults = useCallback(() => {
    setHiddenPanelIds(new Set());
    focusedIterationHiddenSnapshotRef.current = null;
    setFocusedIterationPanel(null);
    setSummaryViewState("compact");
    setMessagesViewState("compact");
    setOutputViewState("standard");
    setLogsViewState("detailed");
  }, []);

  const setIterationMessagesPanelViewState = useCallback((shape: WorkComponentShape) => {
    setMessagesViewState(shape);
    if (shape === "detailed") {
      enterIterationPanelFocus("iterationMessages");
    } else if (focusedIterationPanel === "iterationMessages") {
      exitIterationPanelFocus();
    }
  }, [enterIterationPanelFocus, exitIterationPanelFocus, focusedIterationPanel]);

  useEffect(() => {
    setExtraIterationLogEntries([]);
    setIterationLogPageLoadState({
      hasMore: false,
      loadingMore: false,
    });
  }, [iterationLogQueryKey, refreshToken, sequence, workerId]);

  useEffect(() => {
    if (!iterationLogBasePage) {
      return;
    }

    setIterationLogPageLoadState((current) => ({
      ...current,
      error: undefined,
      hasMore: iterationLogBasePage.hasMore,
      loadingMore: false,
      nextCursor: iterationLogBasePage.cursor ?? null,
    }));
  }, [iterationLogBasePage]);

  const loadMoreIterationLogs = useCallback(async () => {
    if (
      iterationLogPageLoadState.loadingMore ||
      !iterationLogPageLoadState.hasMore ||
      !iterationLogPageLoadState.nextCursor
    ) {
      return;
    }

    setIterationLogPageLoadState((current) => ({
      ...current,
      error: undefined,
      loadingMore: true,
    }));

    try {
      const logs = await workableFetch<WorkIterationLogSection>(
        connection,
        createIterationLogsPath(workerId, sequence, {
          cursor: iterationLogPageLoadState.nextCursor,
          logLevels: normalizedSelectedIterationLogLevels,
          sortDirection: iterationLogSortDirection,
          take: workerLogActivityPageSize,
        })
      );
      const page = logs.page;
      setExtraIterationLogEntries((current) => [...current, ...page.items]);
      setIterationLogPageLoadState({
        error: undefined,
        hasMore: page.hasMore,
        loadingMore: false,
        nextCursor: page.cursor ?? null,
      });
    } catch (caught) {
      setIterationLogPageLoadState((current) => ({
        ...current,
        error: caught instanceof Error ? caught.message : "Could not load more logs.",
        loadingMore: false,
      }));
    }
  }, [
    connection,
    iterationLogPageLoadState.hasMore,
    iterationLogPageLoadState.loadingMore,
    iterationLogPageLoadState.nextCursor,
    iterationLogSortDirection,
    normalizedSelectedIterationLogLevels,
    sequence,
    workerId,
  ]);

  return (
    <ConsolePageLayout fill={isIterationPanelFocused} scrollMode={isIterationPanelFocused ? "panel" : "browser"}>
      <ErrorPanel errors={[iterationDetail.error]} />
      <PanelAggregateFrame
        fill={isIterationPanelFocused}
        hiddenPanelIds={[...hiddenPanelIds]}
        onPanelVisibilityChange={setIterationPanelVisible}
        onResetUi={resetIterationUiToDefaults}
        padding="tightTop"
        panelOptions={iterationPanelOptions}
        scrollMode={isIterationPanelFocused ? "panel" : "browser"}
        settingsButtonLabel="Iteration panel settings"
        settingsDescription="Checked panels are shown on the iteration page."
        settingsTitle="Iteration panels"
      >
        {iterationDetail.loading && !activeIteration ? <StackedSkeleton count={6} /> : null}
        {!iterationDetail.loading && !activeIteration ? (
          <ConsoleEmptyState padding="spacious">
            Iteration not found.
          </ConsoleEmptyState>
        ) : null}
        {activeIteration ? (
          <div className="flex min-h-0 flex-1 flex-col gap-6">
            {!hiddenPanelIds.has("iterationSummary") ? (
              <PanelShell
                contentClassName={summaryViewState === "compact" ? "hidden" : "space-y-4"}
                onClose={() => setIterationPanelVisible("iterationSummary", false)}
                onViewStateChange={setSummaryViewState}
                supportedViewStates={["compact", "standard"]}
                title={<IterationStatusBadge iteration={activeIteration} />}
                viewState={summaryViewState}
              >
                <div className="grid gap-3 md:grid-cols-2 xl:grid-cols-5">
                  <MetadataItem label="Started" value={formatDateTime(activeIteration.startedAt)} />
                  <MetadataItem label="Completed" value={formatDateTime(activeIteration.completedAt)} />
                  <MetadataItem label="Duration" value={formatDurationLabel(activeIteration.executionDuration)} />
                  <MetadataItem
                    label="Attempts"
                    value={`${activeIteration.attemptCount} ${activeIteration.attemptCount === 1 ? "attempt" : "attempts"}`}
                  />
                  <MetadataItem
                    label="Definition"
                    value={(() => {
                      return (
                        <button
                          className="cursor-pointer text-left text-sky-700 underline underline-offset-4 transition-colors hover:text-sky-600 dark:text-sky-300 dark:hover:text-sky-200"
                          onClick={() => onOpenDefinition(
                            detail.definitionName,
                            detail.definitionName
                          )}
                          type="button"
                        >
                          {detail.definitionName}
                        </button>
                      );
                    })()}
                  />
                </div>
                <IterationContextCard
                  concurrencyKey={detail.concurrencyKey}
                  identifiers={detail.identifiers}
                  subjectId={detail.subjectId}
                />
              </PanelShell>
            ) : null}
            {failureDetails ? <WorkerFailureBanner details={failureDetails} now={relativeNow} /> : null}
            {!hiddenPanelIds.has("iterationOutput") ? (
              <PanelShell
                onClose={() => setIterationPanelVisible("iterationOutput", false)}
                onViewStateChange={setOutputViewState}
                supportedViewStates={["standard"]}
                title="Input & Output"
                viewState={outputViewState}
              >
                <div className="grid gap-4 xl:grid-cols-2">
                  <WorkDataCard data={detail.input} label="Worker input" />
                  <WorkDataCard data={activeIteration.output} label="Iteration output" />
                </div>
              </PanelShell>
            ) : null}
            {!hiddenPanelIds.has("iterationMessages") ? (
              <IterationMessagePanel
                connection={connection}
                initialSummary={detail.messageSummary}
                onClose={() => setIterationPanelVisible("iterationMessages", false)}
                refreshToken={refreshToken}
                sequence={sequence}
                onViewStateChange={setIterationMessagesPanelViewState}
                viewState={messagesViewState}
                workerId={workerId}
              />
            ) : null}
            {!hiddenPanelIds.has("iterationLogs") ? (
              <WorkerLogPanel
                connectionError={iterationLogPageLoadState.error}
                entries={iterationLogEntries}
                hasMore={iterationLogPageLoadState.hasMore}
                hasActiveIteration={activeIteration.status === "Executing"}
                isLoading={iterationDetail.loading || iterationLogsSnapshot.loading}
                isLoadingMore={iterationLogPageLoadState.loadingMore}
                onClearFilters={() => setIterationSelectedLogLevels(null)}
                onClose={() => setIterationPanelVisible("iterationLogs", false)}
                onFocusLevel={(level) => setIterationSelectedLogLevels([level])}
                onLoadMore={() => void loadMoreIterationLogs()}
                pauseEnabled={false}
                onSetLevelVisible={(level, visible) => {
                  setIterationSelectedLogLevels((current) => {
                    const base = new Set((current ?? workerLogFilterLevels).map((item) => item));
                    if (visible) {
                      base.add(level);
                    } else {
                      base.delete(level);
                    }

                    const next = workerLogFilterLevels.filter((item) => base.has(item));
                    return next.length === workerLogFilterLevels.length ? null : next;
                  });
                }}
                onToggleSortDirection={() => setIterationLogSortDirection((current) => current === "desc" ? "asc" : "desc")}
                summaryOverride={iterationLogSummary ?? undefined}
                selectedLevels={iterationSelectedLogLevels}
                sortDirection={iterationLogSortDirection}
                onViewStateChange={setLogsViewState}
                viewState={logsViewState}
              />
            ) : null}
          </div>
        ) : null}
      </PanelAggregateFrame>
    </ConsolePageLayout>
  );
}

export function QueueDialog({
  connection,
  definition,
  fetchQueueSchemaWhenNeeded = true,
  initialFormValue,
  initialRequest,
  onQueuedWorker,
  onOpenChange,
  preloadedQueueSchemaDescriptor,
}: {
  connection: WorkableConnection;
  definition: WorkDefinition | null;
  fetchQueueSchemaWhenNeeded?: boolean;
  initialFormValue?: unknown;
  initialRequest?: QueueWorkRequest | null;
  onQueuedWorker: (workerId: string) => void;
  onOpenChange: (open: boolean) => void;
  preloadedQueueSchemaDescriptor?: QueueRequestSchemaDescriptor | null;
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
  const [queueConfigurationDisplayMode, setQueueConfigurationDisplayMode] =
    useState<WorkerConfigurationDisplayMode>("all-values");
  const [isQueueing, setIsQueueing] = useState(false);
  const [status, setStatus] = useState<string | undefined>();
  const [error, setError] = useState<string | undefined>();
  const baselineQueueRequest = useMemo(
    () => createQueueDialogRequest(definition, initialRequest),
    [definition, initialRequest]
  );
  const queueDefaultComparisonRequest = useMemo(
    () => createDefaultQueueRequest(definition),
    [definition]
  );
  const queueRequestSchema = useMemo(
    () => parseJsonSchema(queueSchemaDescriptor?.schema?.jsonSchema),
    [queueSchemaDescriptor?.schema?.jsonSchema]
  );
  const queueConfigurationDifferences = useMemo(
    () => createWorkerConfigurationDifferences(
      queueRequest,
      queueDefaultComparisonRequest,
      queueSchemaDescriptor
    ),
    [queueDefaultComparisonRequest, queueRequest, queueSchemaDescriptor]
  );

  useEffect(() => {
    if (!definition) {
      queueMicrotask(() => setQueueSchemaDescriptor(null));
      return;
    }

    if (preloadedQueueSchemaDescriptor) {
      queueMicrotask(() => setQueueSchemaDescriptor(preloadedQueueSchemaDescriptor));
      return;
    }

    if (!fetchQueueSchemaWhenNeeded) {
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
  }, [connection, definition, fetchQueueSchemaWhenNeeded, preloadedQueueSchemaDescriptor]);

  useEffect(() => {
    if (!definition) {
      return;
    }

    const nextValue = initialFormValue === undefined
      ? createDefaultValue(inputSchema)
      : cloneJsonValue(initialFormValue);
    queueMicrotask(() => {
      setActiveTab(inputSchema ? "input" : "manual");
      setFormValue(nextValue);
      setManualRequestJson(compactJson({
        ...baselineQueueRequest,
        input: nextValue,
      }));
      setQueueRequest(cloneQueueWorkRequest(baselineQueueRequest));
      setQueueConfigurationDisplayMode("all-values");
      setIsQueueing(false);
      setStatus(undefined);
      setError(undefined);
    });
  }, [baselineQueueRequest, definition, initialFormValue, inputSchema]);

  const updateFormValue = (nextValue: unknown) => {
    setFormValue(nextValue);
  };

  const discardQueueConfigurationChanges = () => {
    setQueueRequest(cloneQueueWorkRequest(baselineQueueRequest));
    setQueueConfigurationDisplayMode("all-values");
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
            </div>
            <TabsContent className="mt-4 min-h-0 flex-1 overflow-y-auto pr-2" value="input">
              <SchemaForm
                onChange={updateFormValue}
                schema={inputSchema}
                value={formValue}
              />
            </TabsContent>
            <TabsContent className="mt-4 min-h-0 flex-1 overflow-y-auto pr-2" value="config">
              <ConfigurationEditorSurface
                descriptor={queueSchemaDescriptor}
                differences={queueConfigurationDifferences}
                onRequestChange={setQueueRequest}
                onToggleShowAllValues={() => {
                  setQueueConfigurationDisplayMode((current) =>
                    current === "only-changes" ? "all-values" : "only-changes"
                  );
                }}
                request={queueRequest}
                schema={queueRequestSchema}
                showAllValues={queueConfigurationDisplayMode === "all-values"}
                footer={(
                  <div className="flex flex-wrap items-center justify-end gap-2 border-t pt-4">
                    <Tooltip delayDuration={250}>
                      <TooltipTrigger asChild>
                        <Button
                          disabled={queueConfigurationDifferences.length === 0}
                          onClick={discardQueueConfigurationChanges}
                          size="sm"
                          type="button"
                          variant="outline"
                        >
                          <RotateCw className="size-4" />
                          Discard
                        </Button>
                      </TooltipTrigger>
                      <TooltipContent side="top" sideOffset={6}>
                        Restore the queue configuration values shown here to the dialog&apos;s starting values.
                      </TooltipContent>
                    </Tooltip>
                  </div>
                )}
              />
            </TabsContent>
            <TabsContent className="mt-4 min-h-0 flex-1 overflow-y-auto pr-2" value="manual">
              <JsonTextEditor
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
              <Tooltip delayDuration={250}>
                <TooltipTrigger asChild>
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
                </TooltipTrigger>
                <TooltipContent side="top" sideOffset={6}>
                  Queue the worker and close this dialog without navigating to the new worker.
                </TooltipContent>
              </Tooltip>
              <Tooltip delayDuration={250}>
                <TooltipTrigger asChild>
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
                </TooltipTrigger>
                <TooltipContent side="top" sideOffset={6}>
                  Queue the worker and open its detail screen when the new worker id is returned.
                </TooltipContent>
              </Tooltip>
            </div>
          </div>
        </div>
      </DialogContent>
    </Dialog>
  );
}

function QueueConfigurationTabs({
  descriptor,
  emptyStateMessage,
  fieldPathFilter,
  fieldTooltipByPath,
  onRequestChange,
  request,
  schema,
}: {
  descriptor: QueueRequestSchemaDescriptor | null;
  emptyStateMessage?: string;
  fieldPathFilter?: (path: string) => boolean;
  fieldTooltipByPath?: Map<string, string>;
  onRequestChange: Dispatch<SetStateAction<QueueWorkRequest>>;
  request: QueueWorkRequest;
  schema: ReturnType<typeof parseJsonSchema>;
}) {
  if (!descriptor || !schema) {
    return (
      <FormEmptyState>
        Queue configuration schema is not available from this Workable host.
      </FormEmptyState>
    );
  }

  const tabs = descriptor.tabs
    .map((tab) => ({
      ...tab,
      fields: fieldPathFilter ? tab.fields.filter((field) => fieldPathFilter(field.path)) : tab.fields,
    }))
    .filter((tab) => tab.fields.length > 0);
  const firstTab = tabs[0]?.id ?? "queue";
  const tabsKey = tabs.map((tab) => tab.id).join("|");

  if (tabs.length === 0) {
    return (
      <FormEmptyState>
        {emptyStateMessage ?? "No configuration fields are available in this view."}
      </FormEmptyState>
    );
  }

  return (
    <Tabs className="flex min-h-full flex-col" defaultValue={firstTab} key={tabsKey}>
      <TabsList className="shrink-0 flex h-auto w-full flex-wrap justify-start">
        {tabs.map((tab) => (
          <TabsTrigger key={tab.id} value={tab.id}>
            {tab.label}
          </TabsTrigger>
        ))}
      </TabsList>

      {tabs.map((tab) => (
        <TabsContent className="mt-4 min-h-0 flex-1 space-y-4" key={tab.id} value={tab.id}>
          <ConfigTabHeader description={tab.description} title={tab.label} />
          <ConfigFieldSections
            fieldTooltipByPath={fieldTooltipByPath}
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

function ConfigurationEditorSurface({
  descriptor,
  differences,
  footer,
  onRequestChange,
  onToggleShowAllValues,
  request,
  schema,
  showDefaultTooltipsInChangedView = false,
  showAllValues,
}: {
  descriptor: QueueRequestSchemaDescriptor | null;
  differences: WorkerConfigurationDifference[];
  footer?: ReactNode;
  onRequestChange: Dispatch<SetStateAction<QueueWorkRequest>>;
  onToggleShowAllValues: () => void;
  request: QueueWorkRequest;
  schema: ReturnType<typeof parseJsonSchema>;
  showDefaultTooltipsInChangedView?: boolean;
  showAllValues: boolean;
}) {
  const showingOnlyChanges = differences.length > 0 && !showAllValues;
  const changedPaths = useMemo(
    () => new Set(differences.map((difference) => difference.path)),
    [differences]
  );
  const fieldTooltipByPath = useMemo(
    () => showDefaultTooltipsInChangedView
      ? new Map(
        differences.map((difference) => [
          difference.path,
          `Default: ${formatConfigurationValue(difference.defaultValue)}`,
        ])
      )
      : undefined,
    [differences, showDefaultTooltipsInChangedView]
  );

  return (
    <div className="space-y-4">
      {differences.length > 0 ? (
        <div className="text-muted-foreground text-sm">
          {showingOnlyChanges
            ? "Showing only settings that differ from the definition defaults. "
            : "Showing all settings, including values that match the definition defaults. "}
          <button
            className="text-sky-300 underline underline-offset-4 hover:text-sky-200"
            onClick={onToggleShowAllValues}
            type="button"
          >
            {showingOnlyChanges ? "Show all settings" : "Show only differences from defaults"}
          </button>
          .
        </div>
      ) : null}
      <QueueConfigurationTabs
        descriptor={descriptor}
        emptyStateMessage="No settings in this tab differ from the definition defaults."
        fieldPathFilter={showingOnlyChanges ? ((path) => changedPaths.has(path)) : undefined}
        fieldTooltipByPath={showingOnlyChanges ? fieldTooltipByPath : undefined}
        onRequestChange={onRequestChange}
        request={request}
        schema={schema}
      />
      {footer}
    </div>
  );
}

function ConfigFieldSections({
  fieldTooltipByPath,
  onRequestChange,
  request,
  schema,
  tab,
}: {
  fieldTooltipByPath?: Map<string, string>;
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
            tooltipText={fieldTooltipByPath?.get(field.path)}
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
              tooltipText={fieldTooltipByPath?.get(field.path)}
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
  tooltipText,
}: {
  field: QueueConfigurationField;
  onRequestChange: Dispatch<SetStateAction<QueueWorkRequest>>;
  request: QueueWorkRequest;
  schema: ReturnType<typeof parseJsonSchema>;
  tabId: string;
  tooltipText?: string;
}) {
  const constraint = getQueueConfigurationFieldConstraint(request, field.path);
  const content = constraint ? (
    <LockedConfigurationField
      description={field.description}
      label={field.label}
      reason={constraint.reason}
      value={constraint.value}
    />
  ) : (
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

  if (!tooltipText) {
    return content;
  }

  return (
    <Tooltip delayDuration={250}>
      <TooltipTrigger asChild>
        <div className="w-full">
          {content}
        </div>
      </TooltipTrigger>
      <TooltipContent className="max-w-80 whitespace-pre-wrap break-words text-left" side="top" sideOffset={6}>
        <div className="text-sm">
          {tooltipText}
        </div>
      </TooltipContent>
    </Tooltip>
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
      <FormFieldHeader
        description={description}
        details={(
          <div className="text-amber-200 text-xs">
            {reason}
          </div>
        )}
        label={label}
      />
      <ReadonlyFormValue>
        {String(value)}
      </ReadonlyFormValue>
      <p className="text-amber-200 text-xs">{reason}</p>
    </div>
  );
}

function WorkerConfigurationStatusBadge({
  definition,
  differenceCount,
  differences,
}: {
  definition: WorkDefinition | null;
  differenceCount: number;
  differences: WorkerConfigurationDifference[];
}) {
  if (!definition) {
    return differenceCount === 0
      ? (
        <div className="rounded-md border border-emerald-500/25 bg-emerald-500/8 px-3 py-1.5 text-emerald-900 text-sm dark:text-emerald-100">
          Matches default configuration
        </div>
      )
      : (
        <div className="rounded-md border border-amber-500/25 bg-amber-500/10 px-3 py-1.5 text-amber-900 text-sm dark:text-amber-100">
          Differs from defaults in {differenceCount} place{differenceCount === 1 ? "" : "s"}
        </div>
      );
  }

  if (differences.length === 0) {
    return (
      <div className="rounded-md border border-emerald-500/25 bg-emerald-500/8 px-3 py-1.5 text-emerald-900 text-sm dark:text-emerald-100">
        Matches default <span className="font-mono">{definition.name}</span> configuration
      </div>
    );
  }

  return (
    <div className="rounded-md border border-amber-500/25 bg-amber-500/10 px-3 py-1.5 text-amber-900 text-sm dark:text-amber-100">
      Differs from <span className="font-mono">{definition.name}</span> defaults in {differences.length} place{differences.length === 1 ? "" : "s"}
    </div>
  );
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

export function workerStatusTextTone(state: WorkerState) {
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

export function detailCompletionTone(status: WorkCompletionStatus) {
  switch (status) {
    case "Completed":
      return "border-emerald-500/40 bg-emerald-500/10 text-emerald-700 dark:text-emerald-200";
    case "Executing":
      return "border-sky-500/40 bg-sky-500/10 text-sky-700 dark:text-sky-200";
    case "Failed":
      return "border-red-500/40 bg-red-500/10 text-red-700 dark:text-red-200";
    case "Paused":
    case "Interrupted":
      return "border-amber-500/40 bg-amber-500/10 text-amber-800 dark:text-amber-100";
    case "Canceled":
      return "border-border bg-muted/40 text-foreground";
    default:
      return "border-border bg-muted/40 text-foreground";
  }
}

export function messageSeverityTone(severity: string) {
  switch (normalizeMessageSeverity(severity)) {
    case "critical":
      return "border-fuchsia-500/40 bg-fuchsia-500/10 text-fuchsia-800 dark:text-fuchsia-100";
    case "error":
      return "border-red-500/40 bg-red-500/10 text-red-700 dark:text-red-200";
    case "warning":
      return "border-amber-500/40 bg-amber-500/10 text-amber-800 dark:text-amber-100";
    case "debug":
      return "border-violet-500/40 bg-violet-500/10 text-violet-800 dark:text-violet-100";
    case "info":
    case "information":
      return "border-sky-500/40 bg-sky-500/10 text-sky-700 dark:text-sky-200";
    case "trace":
      return "border-slate-500/40 bg-slate-500/10 text-slate-700 dark:text-slate-200";
    default:
      return "border-border bg-muted/40 text-foreground";
  }
}

export function messageSeverityFilterTone(severity: string) {
  switch (normalizeMessageSeverity(severity)) {
    case "critical":
      return "border-fuchsia-500/30 bg-fuchsia-500/10 text-fuchsia-800 dark:text-fuchsia-100";
    case "error":
      return "border-rose-500/30 bg-rose-500/10 text-rose-700 dark:text-rose-200";
    case "warning":
      return "border-amber-500/30 bg-amber-500/10 text-amber-800 dark:text-amber-100";
    case "debug":
      return "border-violet-500/30 bg-violet-500/10 text-violet-800 dark:text-violet-100";
    case "info":
    case "information":
      return "border-sky-500/30 bg-sky-500/10 text-sky-700 dark:text-sky-200";
    case "trace":
      return "border-slate-500/30 bg-slate-500/10 text-slate-700 dark:text-slate-200";
    default:
      return "border-border bg-muted/40 text-foreground";
  }
}

export function workerActionToneClassName(_action: WorkAction, disabled: boolean) {
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
      <pre className="max-h-64 overflow-auto whitespace-pre-wrap break-words rounded-lg border bg-muted/30 p-3 font-mono text-xs leading-relaxed">
        <JsonValue value={value ?? null} />
      </pre>
    </div>
  );
}

function JsonTextEditor({
  onChange,
  value,
}: {
  onChange: (value: string) => void;
  value: string;
}) {
  const [editorMode, setEditorMode] = useState<"pretty" | "formatted" | "compact">("pretty");
  const parsed = parseJsonText(value);

  const applyEditorMode = (nextMode: "pretty" | "formatted" | "compact") => {
    setEditorMode(nextMode);
    if (nextMode === "pretty" || !parsed.ok) {
      return;
    }

    onChange(nextMode === "formatted"
      ? JSON.stringify(parsed.value, null, 2)
      : JSON.stringify(parsed.value));
  };

  return (
    <Tabs
      className="flex h-full min-h-0 flex-col gap-3"
      onValueChange={(nextValue) => applyEditorMode(nextValue as "pretty" | "formatted" | "compact")}
      value={editorMode}
    >
      <TabsList className="grid h-9 w-full grid-cols-3">
        <TabsTrigger value="pretty">Pretty</TabsTrigger>
        <TabsTrigger value="formatted">Formatted</TabsTrigger>
        <TabsTrigger value="compact">Compact</TabsTrigger>
      </TabsList>
      <TabsContent className="mt-0 min-h-0 flex-1" value="pretty">
        <pre className="h-full overflow-auto rounded-lg border bg-muted/30 p-3 font-mono text-xs leading-relaxed">
          {parsed.ok ? (
            <JsonValue value={parsed.value} />
          ) : (
            <span className="text-red-300">{parsed.error}</span>
          )}
        </pre>
      </TabsContent>
      <TabsContent className="mt-0 min-h-0 flex-1" value="formatted">
        <Textarea
          className="h-full min-h-0 resize-none overflow-y-auto font-mono text-xs"
          onChange={(event) => onChange(event.target.value)}
          spellCheck={false}
          value={value}
        />
      </TabsContent>
      <TabsContent className="mt-0 min-h-0 flex-1" value="compact">
        <Textarea
          className="h-full min-h-0 resize-none overflow-y-auto font-mono text-xs"
          onChange={(event) => onChange(event.target.value)}
          spellCheck={false}
          value={value}
        />
      </TabsContent>
    </Tabs>
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
        </div>
      </CardHeader>
      <CardContent>
        <pre className="max-h-72 overflow-auto whitespace-pre-wrap break-words rounded-lg border bg-muted/30 p-3 font-mono text-xs leading-relaxed">
          <JsonValue value={preview ?? null} />
        </pre>
      </CardContent>
    </Card>
  );
}

function IterationStatusBadge({
  iteration,
}: {
  iteration: Pick<WorkerIterationSnapshot, "sequence" | "status"> | Pick<WorkableHttpWorkerIterationSnapshot, "sequence" | "status">;
}) {
  return (
    <div className="flex flex-wrap items-center gap-2">
      <span className="font-medium">Iteration #{iteration.sequence}</span>
      <Badge className={detailCompletionTone(iteration.status)} variant="outline">
        {iteration.status}
      </Badge>
    </div>
  );
}

function IterationContextCard({
  concurrencyKey,
  identifiers,
  subjectId,
}: {
  concurrencyKey?: WorkTypedValue | null;
  identifiers?: WorkTypedValue[] | null;
  subjectId?: WorkTypedValue | null;
}) {
  return (
    <div className="grid gap-4">
      <SnapshotBlock
        label="Keys"
        value={{
          subjectId: subjectId ?? null,
          concurrencyKey: concurrencyKey ?? null,
          identifiers: identifiers ?? [],
        }}
      />
    </div>
  );
}

function IterationMessagePanel({
  connection,
  initialSummary,
  onClose,
  refreshToken,
  sequence,
  onViewStateChange,
  viewState,
  workerId,
}: {
  connection: WorkableConnection;
  initialSummary: WorkIterationMessageSummary;
  onClose: () => void;
  refreshToken: number;
  sequence: number;
  onViewStateChange: (shape: WorkComponentShape) => void;
  viewState: WorkComponentShape;
  workerId: string;
}) {
  const [hiddenSeverities, setHiddenSeverities] = useState<Set<string>>(() => new Set());
  const [isolateOnNextFilterSelection, setIsolateOnNextFilterSelection] = useState(false);
  const [sortDirection, setSortDirection] = useState<"asc" | "desc">("desc");
  const messagePageRequestGenerationRef = useRef(0);
  const [messagesState, setMessagesState] = useState<{
    baseCursor?: string | null;
    baseHasMore: boolean;
    baseItems: WorkMessage[];
    error?: string;
    hasMore: boolean;
    items: WorkMessage[];
    loading: boolean;
    loadingMore: boolean;
    nextCursor?: string | null;
    summary: WorkIterationMessageSummary;
  }>(() => ({
    baseCursor: null,
    baseHasMore: false,
    baseItems: [],
    hasMore: false,
    items: [],
    loading: false,
    loadingMore: false,
    nextCursor: null,
    summary: initialSummary,
  }));
  const visibleSeverities = useMemo(
    () => iterationMessageSeverityLabels.filter((severity) => !hiddenSeverities.has(normalizeMessageSeverity(severity))),
    [hiddenSeverities]
  );
  const severityQueryValue = useMemo(
    () => visibleSeverities.length === iterationMessageSeverityLabels.length
      ? null
      : visibleSeverities.join(","),
    [visibleSeverities]
  );
  const requestPath = useMemo(
    () => createIterationMessagesPath(workerId, sequence, {
      severities: severityQueryValue,
      sortDirection,
      take: iterationMessagePanelPageSize,
    }),
    [sequence, severityQueryValue, sortDirection, workerId]
  );
  const summary = messagesState.summary;
  const availableSeverities = useMemo(
    () => getOrderedMessageSeverities(summary),
    [summary]
  );
  const visibleMessages = messagesState.items;
  const pagedMessageCount = Math.max(0, messagesState.items.length - messagesState.baseItems.length);
  const hasMoreVisibleMessages = messagesState.hasMore;
  const filtersActive = hiddenSeverities.size > 0;
  const selectedSeverityCount = filtersActive
    ? availableSeverities.filter((severity) => !hiddenSeverities.has(normalizeMessageSeverity(severity))).length
    : 0;
  const title = useMemo(
    () => (
      <IterationMessagePanelTitle
        onSelectSeverity={(severity) => {
          setHiddenSeverities(createHiddenMessageSeveritiesForFocus(availableSeverities, severity));
          onViewStateChange("detailed");
        }}
        summary={summary}
        viewState={viewState}
      />
    ),
    [availableSeverities, onViewStateChange, summary, viewState]
  );
  const setSeverityVisible = (severity: string, visible: boolean) => {
    if (isolateOnNextFilterSelection && availableSeverities.length - hiddenSeverities.size > 1) {
      setIsolateOnNextFilterSelection(false);
      setHiddenSeverities(createHiddenMessageSeveritiesForFocus(availableSeverities, severity));
      return;
    }

    setIsolateOnNextFilterSelection(false);
    const normalizedSeverity = normalizeMessageSeverity(severity);
    setHiddenSeverities((current) => {
      const next = new Set(current);
      if (visible) {
        next.delete(normalizedSeverity);
      } else {
        next.add(normalizedSeverity);
      }

      return next;
    });
  };

  useEffect(() => {
    let canceled = false;
    queueMicrotask(() => {
      if (!canceled) {
        setMessagesState((current) => ({
          ...current,
          summary: initialSummary,
        }));
      }
    });
    return () => {
      canceled = true;
    };
  }, [initialSummary]);

  useEffect(() => {
    let canceled = false;
    messagePageRequestGenerationRef.current += 1;

    if (viewState === "compact") {
      queueMicrotask(() => {
        if (!canceled) {
          setMessagesState((current) => ({
            ...current,
            baseCursor: null,
            baseHasMore: false,
            baseItems: [],
            error: undefined,
            hasMore: false,
            items: [],
            loading: false,
            loadingMore: false,
            nextCursor: null,
            summary: initialSummary,
          }));
        }
      });
      return () => {
        canceled = true;
      };
    }

    queueMicrotask(() => {
      if (!canceled) {
        setMessagesState((current) => ({
          ...current,
          baseCursor: null,
          baseHasMore: false,
          baseItems: [],
          error: undefined,
          hasMore: false,
          items: [],
          loading: true,
          loadingMore: false,
          nextCursor: null,
        }));
      }
    });

    const requestGeneration = messagePageRequestGenerationRef.current;
    workableFetch<WorkIterationMessageSection>(connection, requestPath)
      .then((data) => {
        if (canceled || messagePageRequestGenerationRef.current !== requestGeneration) {
          return;
        }

        setMessagesState({
          baseCursor: data.page.cursor ?? null,
          baseHasMore: data.page.hasMore,
          baseItems: data.page.items,
          hasMore: data.page.hasMore,
          items: data.page.items,
          loading: false,
          loadingMore: false,
          nextCursor: data.page.cursor ?? null,
          summary: data.summary,
        });
      })
      .catch((error) => {
        if (canceled || messagePageRequestGenerationRef.current !== requestGeneration) {
          return;
        }

        setMessagesState((current) => ({
          ...current,
          baseCursor: null,
          baseHasMore: false,
          baseItems: [],
          error: error instanceof Error ? error.message : "Unable to load messages.",
          hasMore: false,
          items: [],
          loading: false,
          loadingMore: false,
          nextCursor: null,
        }));
      });

    return () => {
      canceled = true;
    };
  }, [connection, initialSummary, refreshToken, requestPath, viewState]);

  const forgetPagedMessages = useCallback(() => {
    if (pagedMessageCount === 0) {
      return;
    }

    messagePageRequestGenerationRef.current += 1;
    setMessagesState((current) => {
      if (current.items.length <= current.baseItems.length) {
        return current;
      }

      return {
        ...current,
        hasMore: current.baseHasMore,
        items: current.baseItems,
        loadingMore: false,
        nextCursor: current.baseCursor ?? null,
      };
    });
  }, [pagedMessageCount]);

  const loadMore = useCallback(() => {
    if (messagesState.loading || messagesState.loadingMore || !messagesState.hasMore || !messagesState.nextCursor) {
      return;
    }

    const requestGeneration = messagePageRequestGenerationRef.current;
    setMessagesState((current) => ({
      ...current,
      loadingMore: true,
    }));

    workableFetch<WorkIterationMessageSection>(
      connection,
      createIterationMessagesPath(workerId, sequence, {
        cursor: messagesState.nextCursor,
        severities: severityQueryValue,
        sortDirection,
        take: iterationMessagePanelPageSize,
      })
    )
      .then((data) => {
        if (messagePageRequestGenerationRef.current !== requestGeneration) {
          return;
        }

        setMessagesState((current) => ({
          ...current,
          hasMore: data.page.hasMore,
          items: [...current.items, ...data.page.items],
          loadingMore: false,
          nextCursor: data.page.cursor ?? null,
          summary: data.summary,
        }));
      })
      .catch((error) => {
        if (messagePageRequestGenerationRef.current !== requestGeneration) {
          return;
        }

        setMessagesState((current) => ({
          ...current,
          error: error instanceof Error ? error.message : "Unable to load more messages.",
          loadingMore: false,
        }));
      });
  }, [
    connection,
    messagesState.hasMore,
    messagesState.loading,
    messagesState.loadingMore,
    messagesState.nextCursor,
    sequence,
    severityQueryValue,
    sortDirection,
    workerId,
  ]);

  return (
    <PanelShell
      className="flex min-h-0 flex-1 flex-col overflow-hidden"
      contentClassName={viewState === "compact"
        ? "hidden"
        : "mt-4 flex min-h-0 flex-1 flex-col overflow-hidden"}
      filterControl={viewState !== "compact"
        ? {
            activeCount: selectedSeverityCount,
            content: (
              <IterationMessageFilterContent
                availableSeverities={availableSeverities}
                hiddenSeverities={hiddenSeverities}
                onClearFilters={() => setHiddenSeverities(new Set())}
                onSetSeverityVisible={setSeverityVisible}
              />
            ),
            label: "Filter message severities",
            onOpenChange: setIsolateOnNextFilterSelection,
          }
        : undefined}
      actions={viewState !== "compact" ? (
        <IterationMessagePanelActions
          onToggleSortDirection={() => setSortDirection((current) => current === "desc" ? "asc" : "desc")}
          sortDirection={sortDirection}
        />
      ) : null}
      onClose={onClose}
      onViewStateChange={onViewStateChange}
      supportedViewStates={["compact", "standard", "detailed"]}
      title={title}
      viewState={viewState}
      >
        {messagesState.error ? (
          <div className="mb-3 rounded-lg border border-amber-500/30 bg-amber-500/10 px-3 py-2 text-amber-100 text-sm">
            {messagesState.error}
          </div>
        ) : null}
        <section
          className={cn(
            "flex h-full min-h-0 flex-col rounded-xl border bg-muted/10 p-4",
          viewState === "standard" && "min-h-[24rem] max-h-[70vh]",
          viewState === "detailed" && "max-h-[calc(100svh-11rem)]"
        )}
      >
          <PanelScrollViewport
            className="rounded-xl border bg-background/60 p-4"
            hasMore={hasMoreVisibleMessages}
            loadedCount={visibleMessages.length}
            loading={messagesState.loading}
            loadingMore={messagesState.loadingMore}
            noun="message"
            onLoadMore={loadMore}
            onScroll={(event) => {
              if (shouldForgetPagedIterationMessages(event.currentTarget.scrollTop, pagedMessageCount)) {
                forgetPagedMessages();
              }
            }}
            showLoadedCount={false}
          >
            <IterationMessageList messages={visibleMessages} />
        </PanelScrollViewport>
      </section>
    </PanelShell>
  );
}

function IterationMessagePanelTitle({
  onSelectSeverity,
  summary,
  viewState,
}: {
  onSelectSeverity: (severity: string) => void;
  summary: {
    critical: number;
    debug: number;
    errors: number;
    information: number;
    trace: number;
    total: number;
    warnings: number;
    error: number;
    warning: number;
  };
  viewState: WorkComponentShape;
}) {
  const compactSeverities = [
    { count: summary.critical, label: "Critical", severity: "Critical" },
    { count: summary.error, label: "Error", severity: "Error" },
    { count: summary.warning, label: "Warning", severity: "Warning" },
    { count: summary.information, label: "Info", severity: "Information" },
    { count: summary.debug, label: "Debug", severity: "Debug" },
    { count: summary.trace, label: "Trace", severity: "Trace" },
  ];

  return (
    <>
      <span>Messages</span>
      {viewState === "compact" ? (
        <>
          {compactSeverities.map((severity) => (
            <LogSummaryPill
              count={severity.count}
              key={severity.severity}
              label={severity.label}
              onClick={() => onSelectSeverity(severity.severity)}
              tone={messageSeverityFilterTone(severity.severity)}
            />
          ))}
        </>
      ) : null}
    </>
  );
}

function IterationMessageFilterContent({
  availableSeverities,
  hiddenSeverities,
  onClearFilters,
  onSetSeverityVisible,
}: {
  availableSeverities: string[];
  hiddenSeverities: ReadonlySet<string>;
  onClearFilters: () => void;
  onSetSeverityVisible: (severity: string, visible: boolean) => void;
}) {
  return (
    <>
      <div className="flex items-center justify-between border-b px-3 py-2">
        <span className="font-medium text-sm">Message severities</span>
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
        {availableSeverities.map((severity) => {
          const visible = !hiddenSeverities.has(normalizeMessageSeverity(severity));

          return (
            <label
              className="flex cursor-pointer items-center gap-3 rounded-md px-2 py-2 transition-colors hover:bg-accent/40"
              key={severity}
            >
              <input
                checked={visible}
                className="size-4 accent-primary"
                onChange={(event) => onSetSeverityVisible(severity, event.currentTarget.checked)}
                type="checkbox"
              />
              <span className={`inline-flex rounded-full border px-2 py-0.5 font-mono text-[11px] ${messageSeverityFilterTone(severity)}`}>
                {severity}
              </span>
            </label>
          );
        })}
      </div>
    </>
  );
}

function IterationMessagePanelActions({
  onToggleSortDirection,
  sortDirection,
}: {
  onToggleSortDirection: () => void;
  sortDirection: "asc" | "desc";
}) {
  return (
    <ToolbarIconButton
      label={sortDirection === "desc" ? "Show oldest messages first" : "Show newest messages first"}
      onClick={onToggleSortDirection}
      type="button"
      tooltip={sortDirection === "desc" ? "Show oldest messages first" : "Show newest messages first"}
    >
      {sortDirection === "desc"
        ? <ArrowDownWideNarrow className="size-3.5" />
        : <ArrowUpNarrowWide className="size-3.5" />}
    </ToolbarIconButton>
  );
}

function IterationMessageList({
  messages,
}: {
  messages: WorkMessage[];
}) {
  if (messages.length === 0) {
    return <EmptyListState message="No retained messages for this iteration." />;
  }

  return (
    <div className="grid gap-3">
      {messages.map((message, index) => (
        <section className="rounded-xl border bg-muted/10 p-4" key={`${message.code}:${message.target ?? ""}:${message.occurredAt}:${index}`}>
          <div className="flex flex-wrap items-center gap-2">
            <Badge className={messageSeverityTone(message.severity)} variant="outline">
              {formatMessageSeverity(message.severity)}
            </Badge>
            <span className="font-medium text-sm">{message.code}</span>
          </div>
          <div className="mt-2 flex flex-wrap items-center gap-2 text-xs">
            <span className="rounded-full border border-border/60 bg-background/60 px-2 py-1 text-muted-foreground">
              Time: {formatDateTime(message.occurredAt)}
            </span>
            {message.target ? (
              <span className="rounded-full border border-border/60 bg-background/60 px-2 py-1 font-mono text-muted-foreground">
                Target: {message.target}
              </span>
            ) : null}
          </div>
          <p className="mt-3 whitespace-pre-wrap break-words text-sm leading-6">{message.text}</p>
          {message.metadata ? (
            <div className="mt-3">
              <SnapshotBlock label="Metadata" value={message.metadata} />
            </div>
          ) : null}
        </section>
      ))}
    </div>
  );
}

type WorkerTimelineItem = {
  attemptCount?: number;
  actorLabel?: string;
  at: string;
  badge: string;
  description: string;
  failureDetails?: WorkerFailureDetails | null;
  facts: Array<{ label: string; value: string }>;
  filterKind?: WorkerTimelineFilterKind;
  icon: typeof Clock3;
  id: string;
  isFinal?: boolean;
  iterationStatus?: WorkCompletionStatus;
  kind: "action" | "iteration" | "queue" | "state";
  liveText?: WorkerTimelineLiveText;
  marker?: "current" | "latest";
  sequence?: number;
  sortOrder: number;
  sourceLabel?: string;
  sourceTooltip?: string;
  stateMode?: "recurrence" | "retry";
  title: string;
  tone: "danger" | "info" | "neutral" | "success" | "warning";
};

export type WorkerTimelineFilterKind = "failures" | "system" | "user";
export type WorkerLogFilterLevel = "Critical" | "Debug" | "Error" | "Information" | "Trace" | "Warning";
export type WorkerSortDirection = "asc" | "desc";
type WorkerOverviewPageLoadState = {
  error?: string;
  hasMore: boolean;
  loadingMore: boolean;
  nextCursor?: string | null;
};

const workerLogActivityPageSize = 50;
const workerTimelineActivityPageSize = 25;
const workerOverviewInitialActivityPageSize = workerTimelineActivityPageSize;
const iterationMessagePanelPageSize = 25;
const panelForgetPagedItemsScrollTop = 160;
const workerTimelineForgetPushedItemsIntervalMs = 5_000;
const workerTimelineForgetPushedItemsThreshold = workerTimelineActivityPageSize * 2;
const workerTimelineRetainedRealtimeItemCount = workerTimelineActivityPageSize;
const iterationMessageSeverityLabels = ["Critical", "Error", "Warning", "Information", "Debug", "Trace"] as const;
const workerLogFilterLevels: WorkerLogFilterLevel[] = [
  "Critical",
  "Error",
  "Warning",
  "Information",
  "Debug",
  "Trace",
];
const workerTimelineFilterKinds: WorkerTimelineFilterKind[] = ["user", "system", "failures"];

type WorkerTimelineLiveText =
  | {
    attemptCount?: number | null;
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

const WorkerTimelinePanel = memo(function WorkerTimelinePanel({
  error,
  hasMore,
  items,
  isLoading,
  isLoadingMore,
  onClearFilters,
  onClose,
  onForgetPagedItems,
  onForgetPushedItems,
  onFocusFilter,
  onLoadMore,
  onOpenIteration,
  pagedItemCount,
  pushedItemCount,
  onSetFilterSelected,
  onToggleSortDirection,
  onViewStateChange,
  selectedFilters,
  sortDirection,
  viewState,
}: {
  error?: string;
  hasMore: boolean;
  items: WorkerTimelineItem[];
  isLoading: boolean;
  isLoadingMore: boolean;
  onClearFilters: () => void;
  onClose: () => void;
  onForgetPagedItems: () => void;
  onForgetPushedItems: () => void;
  onFocusFilter: (filterKind: WorkerTimelineFilterKind) => void;
  onLoadMore: () => void;
  onOpenIteration: (sequence: number) => void;
  pagedItemCount: number;
  pushedItemCount: number;
  onSetFilterSelected: (filterKind: WorkerTimelineFilterKind, selected: boolean) => void;
  onToggleSortDirection: () => void;
  onViewStateChange: (shape: WorkComponentShape) => void;
  selectedFilters: WorkerTimelineFilterKind[] | null;
  sortDirection: WorkerSortDirection;
  viewState: WorkComponentShape;
}) {
  const scrollRef = useRef<HTMLDivElement | null>(null);
  const rowRefs = useRef(new Map<string, HTMLDivElement>());
  const scrollAnchorRef = useRef<{ key: string; top: number } | null>(null);
  const onForgetPushedItemsRef = useRef(onForgetPushedItems);
  const pushedItemCountRef = useRef(pushedItemCount);
  const sortDirectionRef = useRef(sortDirection);
  const [isolateOnNextFilterSelection, setIsolateOnNextFilterSelection] = useState(false);
  const now = useLiveRelativeTimeNow();
  const normalizedSelectedFilters = useMemo(
    () => selectedFilters ?? workerTimelineFilterKinds,
    [selectedFilters]
  );
  const filtersActive = selectedFilters !== null;
  const handleSetFilterSelected = useCallback((filterKind: WorkerTimelineFilterKind, selected: boolean) => {
    if (isolateOnNextFilterSelection && normalizedSelectedFilters.length > 1) {
      setIsolateOnNextFilterSelection(false);
      onFocusFilter(filterKind);
      return;
    }

    setIsolateOnNextFilterSelection(false);
    onSetFilterSelected(filterKind, selected);
  }, [
    isolateOnNextFilterSelection,
    normalizedSelectedFilters.length,
    onFocusFilter,
    onSetFilterSelected,
  ]);
  const visibleItems = items;
  const visibleRows = useMemo(
    () => createTimelineRows(visibleItems),
    [visibleItems]
  );

  useEffect(() => {
    onForgetPushedItemsRef.current = onForgetPushedItems;
  }, [onForgetPushedItems]);

  useEffect(() => {
    pushedItemCountRef.current = pushedItemCount;
  }, [pushedItemCount]);

  useEffect(() => {
    sortDirectionRef.current = sortDirection;
  }, [sortDirection]);

  useEffect(() => {
    const interval = window.setInterval(() => {
      const viewport = scrollRef.current;
      if (!viewport) {
        return;
      }

      if (
        shouldForgetPushedWorkerTimelineItems(
          viewport.scrollTop,
          pushedItemCountRef.current,
          sortDirectionRef.current
        )
      ) {
        onForgetPushedItemsRef.current();
      }
    }, workerTimelineForgetPushedItemsIntervalMs);

    return () => {
      window.clearInterval(interval);
    };
  }, []);

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
      className="flex min-h-0 flex-1 flex-col overflow-hidden"
      contentClassName={viewState === "compact"
        ? "hidden"
        : "mt-4 flex min-h-0 flex-1 flex-col overflow-hidden"}
      actions={viewState !== "compact" ? (
        <WorkerTimelinePanelActions
          onToggleSortDirection={onToggleSortDirection}
          sortDirection={sortDirection}
        />
      ) : null}
      filterControl={viewState !== "compact"
        ? {
            activeCount: filtersActive ? normalizedSelectedFilters.length : 0,
            content: (
              <WorkerTimelineFilterContent
                onClearFilters={onClearFilters}
                onSetFilterSelected={handleSetFilterSelected}
                selectedFilters={normalizedSelectedFilters}
              />
            ),
            label: "Filter timeline",
            onOpenChange: setIsolateOnNextFilterSelection,
          }
        : undefined}
      onClose={onClose}
      onViewStateChange={onViewStateChange}
      supportedViewStates={["compact", "standard", "detailed"]}
      title="Iteration Timeline"
      viewState={viewState}
    >
      <section
        className={cn(
          "flex h-full min-h-0 flex-col rounded-xl border bg-muted/10 p-4",
          viewState === "standard" && "min-h-[24rem] max-h-[70vh]",
          viewState === "detailed" && "max-h-[calc(100svh-11rem)]"
        )}
      >
        {error ? (
          <div className="mb-3 rounded-lg border border-amber-500/30 bg-amber-500/10 px-3 py-2 text-amber-900 text-sm dark:text-amber-100">
            {error}
          </div>
        ) : null}
        {isLoading && visibleItems.length === 0 ? (
          <StackedSkeleton count={6} />
        ) : visibleItems.length === 0 ? (
          <EmptyListState
            message={filtersActive
              ? "No retained timeline events match the current filters."
              : "No retained timeline events yet."}
          />
        ) : (
          <PanelScrollViewport
            className="rounded-xl border bg-background/60 p-4"
            hasMore={hasMore}
            loadedCount={visibleItems.length}
            loading={isLoading}
            loadingMore={isLoadingMore}
            noun="timeline event"
            onLoadMore={onLoadMore}
            onScroll={(event) => {
              const viewport = event.currentTarget;
              scrollAnchorRef.current = captureTimelineScrollAnchor(
                visibleRows,
                viewport,
                rowRefs.current
              );
              if (shouldForgetPagedWorkerTimelineItems(viewport.scrollTop, pagedItemCount)) {
                onForgetPagedItems();
              }
              if (shouldForgetPushedWorkerTimelineItems(viewport.scrollTop, pushedItemCount, sortDirection)) {
                onForgetPushedItems();
              }
            }}
            showLoadedCount={false}
            viewportRef={scrollRef}
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
              const itemTitleSuffix = item.kind === "iteration" && item.sequence !== undefined
                ? trimIterationTitlePrefix(itemTitle, item.sequence)
                : itemTitle;
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
                              {item.kind === "iteration" && item.sequence !== undefined ? (
                                <button
                                  className="font-medium text-sky-300 underline-offset-4 hover:text-sky-200 hover:underline"
                                  onClick={() => onOpenIteration(item.sequence!)}
                                  type="button"
                                >
                                  #{item.sequence}
                                </button>
                              ) : null}
                              <div className="font-medium">{itemTitleSuffix}</div>
                            </div>
                            {item.actorLabel ? (
                              <div className="mt-1 text-muted-foreground text-xs">
                                {item.actorLabel}
                              </div>
                          ) : null}
                        </div>
                        <div className="flex flex-wrap items-center justify-end gap-2">
                          {item.kind === "iteration" &&
                          item.sequence !== undefined &&
                          item.isFinal ? (
                            <Button
                              className="h-7 gap-1.5 px-2 text-xs"
                              onClick={() => onOpenIteration(item.sequence!)}
                              size="sm"
                              type="button"
                              variant="outline"
                            >
                              <Eye className="size-3.5" />
                              Open
                            </Button>
                          ) : null}
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
                      {shouldRenderTimelineDescription(item, itemTitle, itemDescription) ? (
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
          </PanelScrollViewport>
        )}
      </section>
    </PanelShell>
  );
});

function WorkerTimelineFilterContent({
  onClearFilters,
  onSetFilterSelected,
  selectedFilters,
}: {
  onClearFilters: () => void;
  onSetFilterSelected: (filterKind: WorkerTimelineFilterKind, selected: boolean) => void;
  selectedFilters: readonly WorkerTimelineFilterKind[];
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
          const selected = selectedFilters.includes(filterKind);

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
  onToggleSortDirection,
  sortDirection,
}: {
  onToggleSortDirection: () => void;
  sortDirection: WorkerSortDirection;
}) {
  return (
    <>
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
  onOpenIteration,
}: {
  iterations: WorkerIterationSnapshot[];
  now: number;
  onOpenIteration: (sequence: number) => void;
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
          isFinal: iteration.isFinal,
          isExecuting: iteration.status === "Executing",
          sequence: iteration.sequence,
          status: iteration.status,
        };
      })
      .filter((point): point is NonNullable<typeof point> => point !== null),
    [iterations, now]
  );
  const graphScale = useMemo(
    () => createIterationDurationGraphScale(points),
    [points]
  );
  const lastPointKey = points[points.length - 1]?.sequence ?? null;
  const trackMinWidth = useMemo(
    () => Math.max(points.length * 14, 0),
    [points.length]
  );
  const maxLabel = useMemo(
    () => formatMillisecondsCompact(graphScale.maxDuration),
    [graphScale.maxDuration]
  );
  const minLabel = useMemo(
    () => formatMillisecondsCompact(graphScale.lowerBound),
    [graphScale.lowerBound]
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
              const height = getIterationDurationGraphBarHeight(point, graphScale);
              const isOpenable = point.isFinal;
              const label = `${formatMillisecondsCompact(point.durationMs)} (${formatIterationTimelineStatus(point.status)})`;

              return (
                <button
                  aria-label={isOpenable
                    ? `Open iteration ${point.sequence}`
                    : `Iteration ${point.sequence} is not final yet`}
                  className={`group flex min-w-[6px] flex-1 basis-0 flex-col items-center justify-end rounded-sm ${
                    isOpenable
                      ? "focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-primary/60 focus-visible:ring-offset-2"
                      : "cursor-default"
                  }`}
                  disabled={!isOpenable}
                  key={`iteration-graph:${point.sequence}`}
                  onClick={() => {
                    if (isOpenable) {
                      onOpenIteration(point.sequence);
                    }
                  }}
                  title={isOpenable
                    ? `Iteration #${point.sequence} ${label}`
                    : `Iteration #${point.sequence} is not final yet.`}
                  type="button"
                >
                  <div
                    className={`w-full rounded-t-sm transition-opacity group-hover:opacity-85 ${
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
                </button>
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

export type IterationDurationGraphScalePoint = {
  durationMs: number;
  isFinal: boolean;
};

export type IterationDurationGraphScale = {
  lowerBound: number;
  maxDuration: number;
};

export function createIterationDurationGraphScale(points: readonly IterationDurationGraphScalePoint[]): IterationDurationGraphScale {
  const finalPoints = points.filter((point) => point.isFinal);
  const scalePoints = finalPoints.length > 0 ? finalPoints : points;
  const maxDuration = scalePoints.reduce((max, point) => Math.max(max, point.durationMs), 0);
  const minDuration = scalePoints.reduce(
    (min, point) => Math.min(min, point.durationMs),
    Number.POSITIVE_INFINITY
  );
  const lowerBound = maxDuration > 0 && Number.isFinite(minDuration) && minDuration / maxDuration >= 0.6
    ? minDuration
    : 0;

  return {
    lowerBound,
    maxDuration,
  };
}

export function getIterationDurationGraphBarHeight(
  point: IterationDurationGraphScalePoint,
  scale: IterationDurationGraphScale
) {
  const lowerBound = point.isFinal ? scale.lowerBound : 0;
  const scaledRange = Math.max(1, scale.maxDuration - lowerBound);
  const normalizedHeight = scale.maxDuration > 0
    ? Math.max(0, Math.min(1, (point.durationMs - lowerBound) / scaledRange))
    : 0;

  return scale.maxDuration > 0
    ? Math.max(10, Math.round(normalizedHeight * 56))
    : 10;
}

function WorkerLogPanel({
  connectionError,
  entries,
  hasActiveIteration,
  hasMore,
  isLoading,
  isLoadingMore,
  onClearFilters,
  onClose,
  onFocusLevel,
  onLoadMore,
  onOpenIteration,
  pauseEnabled = true,
  onSetLevelVisible,
  onToggleSortDirection,
  summaryOverride,
  selectedLevels,
  sortDirection,
  onViewStateChange,
  viewState,
}: {
  connectionError?: string;
  entries: WorkerLogEntry[];
  hasActiveIteration: boolean;
  hasMore: boolean;
  isLoading: boolean;
  isLoadingMore: boolean;
  onClearFilters: () => void;
  onClose: () => void;
  onFocusLevel: (level: WorkerLogFilterLevel) => void;
  onLoadMore: () => void;
  onOpenIteration?: (sequence: number) => void;
  pauseEnabled?: boolean;
  onSetLevelVisible: (level: WorkerLogFilterLevel, visible: boolean) => void;
  onToggleSortDirection: () => void;
  summaryOverride?: {
    critical: number;
    debug: number;
    error: number;
    errors: number;
    information: number;
    trace: number;
    total: number;
    warning: number;
    warnings: number;
  };
  selectedLevels: WorkerLogFilterLevel[] | null;
  sortDirection: WorkerSortDirection;
  onViewStateChange: (shape: WorkComponentShape) => void;
  viewState: WorkComponentShape;
}) {
  const [isolateOnNextFilterSelection, setIsolateOnNextFilterSelection] = useState(false);
  const [pausedEntries, setPausedEntries] = useState<WorkerLogEntry[] | null>(null);
  const normalizedSelectedLevels = useMemo(
    () => selectedLevels ?? workerLogFilterLevels,
    [selectedLevels]
  );
  const isPaused = pausedEntries !== null;
  const filtersActive = selectedLevels !== null;
  const selectedLevelCount = filtersActive ? normalizedSelectedLevels.length : 0;
  const visibleEntries = useMemo(
    () => sortWorkerLogEntries(pausedEntries ?? entries, sortDirection),
    [entries, pausedEntries, sortDirection]
  );
  const pendingPausedCount = useMemo(() => {
    if (!pausedEntries) {
      return 0;
    }

    const visibleIds = new Set(pausedEntries.map((entry) => entry.id));
    return entries.reduce(
      (count, entry) => visibleIds.has(entry.id) ? count : count + 1,
      0
    );
  }, [entries, pausedEntries]);
  const summary = useMemo(
    () => summaryOverride ?? summarizeWorkerLogEntries(visibleEntries),
    [summaryOverride, visibleEntries]
  );
  const title = useMemo(
    () => (
      <WorkerLogPanelTitle
        onSelectLevel={(level) => {
          onFocusLevel(level);
          onViewStateChange("standard");
        }}
        summary={summary}
        viewState={viewState}
      />
    ),
    [onFocusLevel, onViewStateChange, summary, viewState]
  );
  const handleSetLevelVisible = useCallback((level: WorkerLogFilterLevel, visible: boolean) => {
    if (isolateOnNextFilterSelection && normalizedSelectedLevels.length > 1) {
      setIsolateOnNextFilterSelection(false);
      onFocusLevel(level);
      return;
    }

    setIsolateOnNextFilterSelection(false);
    onSetLevelVisible(level, visible);
  }, [isolateOnNextFilterSelection, normalizedSelectedLevels.length, onFocusLevel, onSetLevelVisible]);
  const togglePause = useCallback(() => {
    setPausedEntries((current) => current ? null : visibleEntries);
  }, [visibleEntries]);

  return (
    <PanelShell
      className="flex min-h-0 flex-1 flex-col overflow-hidden"
      contentClassName={viewState === "compact"
        ? connectionError
          ? "mt-4 flex min-h-0 flex-1 flex-col overflow-hidden"
          : "hidden"
        : "mt-4 flex min-h-0 flex-1 flex-col overflow-hidden"}
      filterControl={viewState !== "compact"
        ? {
            activeCount: selectedLevelCount,
            content: (
              <WorkerLogFilterContent
                availableLevels={workerLogFilterLevels}
                onClearFilters={onClearFilters}
                onSetLevelVisible={handleSetLevelVisible}
                selectedLevels={normalizedSelectedLevels}
              />
            ),
            label: "Filter log levels",
            onOpenChange: setIsolateOnNextFilterSelection,
          }
        : undefined}
      actions={viewState !== "compact" ? (
        <WorkerLogPanelActions
          isPaused={isPaused}
          pauseEnabled={pauseEnabled}
          onTogglePause={togglePause}
          onToggleSortDirection={onToggleSortDirection}
          sortDirection={sortDirection}
        />
      ) : null}
      onClose={onClose}
      onViewStateChange={onViewStateChange}
      supportedViewStates={["compact", "standard", "detailed"]}
      title={title}
      viewState={viewState}
    >
      <WorkerLogStreamCard
        connectionError={connectionError}
        hasMore={hasMore}
        hasActiveIteration={hasActiveIteration}
        isPaused={isPaused}
        isLoading={isLoading}
        isLoadingMore={isLoadingMore}
        onLoadMore={onLoadMore}
        onOpenIteration={onOpenIteration}
        pauseEnabled={pauseEnabled}
        pendingPausedCount={pendingPausedCount}
        sortDirection={sortDirection}
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
  onSelectLevel: (level: WorkerLogFilterLevel) => void;
  summary: {
    critical: number;
    debug: number;
    error: number;
    errors: number;
    information: number;
    trace: number;
    total: number;
    warning: number;
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
            count={summary.critical}
            label="Critical"
            onClick={() => onSelectLevel("Critical")}
            tone={logLevelFilterTone("Critical")}
          />
          <LogSummaryPill
            count={summary.error}
            label="Error"
            onClick={() => onSelectLevel("Error")}
            tone={logLevelFilterTone("Error")}
          />
          <LogSummaryPill
            count={summary.warning}
            label="Warn"
            onClick={() => onSelectLevel("Warning")}
            tone={logLevelFilterTone("Warning")}
          />
          <LogSummaryPill
            count={summary.information}
            label="Info"
            onClick={() => onSelectLevel("Information")}
            tone={logLevelFilterTone("Information")}
          />
          <LogSummaryPill
            count={summary.debug}
            label="Debug"
            onClick={() => onSelectLevel("Debug")}
            tone={logLevelFilterTone("Debug")}
          />
          <LogSummaryPill
            count={summary.trace}
            label="Trace"
            onClick={() => onSelectLevel("Trace")}
            tone={logLevelFilterTone("Trace")}
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
  onClearFilters,
  onSetLevelVisible,
  selectedLevels,
}: {
  availableLevels: readonly WorkerLogFilterLevel[];
  onClearFilters: () => void;
  onSetLevelVisible: (level: WorkerLogFilterLevel, visible: boolean) => void;
  selectedLevels: readonly WorkerLogFilterLevel[];
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
          const visible = selectedLevels.includes(level);

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
  pauseEnabled,
  onTogglePause,
  onToggleSortDirection,
  sortDirection,
}: {
  isPaused: boolean;
  pauseEnabled: boolean;
  onTogglePause: () => void;
  onToggleSortDirection: () => void;
  sortDirection: WorkerSortDirection;
}) {
  return (
    <>
      {pauseEnabled ? (
        <ToolbarIconButton
          label={isPaused ? "Resume log stream" : "Pause log stream"}
          onClick={onTogglePause}
          type="button"
          tooltip={isPaused ? "Resume the log stream" : "Pause the log stream from updating"}
        >
          {isPaused ? <Play className="size-3.5" /> : <Pause className="size-3.5" />}
        </ToolbarIconButton>
      ) : null}
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
  connectionError,
  hasMore,
  hasActiveIteration,
  isPaused,
  isLoading,
  isLoadingMore,
  onLoadMore,
  onOpenIteration,
  pauseEnabled,
  pendingPausedCount,
  sortDirection,
  viewState,
  visibleEntries,
}: {
  connectionError?: string;
  hasMore: boolean;
  hasActiveIteration: boolean;
  isPaused: boolean;
  isLoading: boolean;
  isLoadingMore: boolean;
  onLoadMore: () => void;
  onOpenIteration?: (sequence: number) => void;
  pauseEnabled: boolean;
  pendingPausedCount: number;
  sortDirection: WorkerSortDirection;
  viewState: WorkComponentShape;
  visibleEntries: WorkerLogEntry[];
}) {
  const [windowStart, setWindowStart] = useState(0);
  const scrollRef = useRef<HTMLDivElement | null>(null);
  const rowHeightsRef = useRef<Map<string, number>>(new Map());
  const pendingScrollAdjustmentRef = useRef(0);
  const pendingOlderPageAdvanceLengthRef = useRef<number | null>(null);
  const maxWindowStart = Math.max(0, visibleEntries.length - maxWorkerLogPanelEntries);
  const windowedEntries = useMemo(
    () => visibleEntries.slice(windowStart, windowStart + maxWorkerLogPanelEntries),
    [visibleEntries, windowStart]
  );
  const localHasMore = windowStart + maxWorkerLogPanelEntries < visibleEntries.length;
  const effectiveHasMore = localHasMore || hasMore;

  const advanceWindow = useCallback((count: number) => {
    if (count <= 0 || windowedEntries.length === 0) {
      return;
    }

    const step = Math.min(count, windowedEntries.length);
    const nextWindowStart = Math.min(windowStart + step, maxWindowStart);
    const actualStep = nextWindowStart - windowStart;
    if (actualStep <= 0) {
      return;
    }

    pendingScrollAdjustmentRef.current += windowedEntries
      .slice(0, actualStep)
      .reduce((sum, entry) => sum + (rowHeightsRef.current.get(entry.id) ?? 18), 0);
    setWindowStart(nextWindowStart);
  }, [maxWindowStart, windowedEntries, windowStart]);

  useEffect(() => {
    let canceled = false;
    queueMicrotask(() => {
      if (!canceled) {
        setWindowStart(0);
      }
    });
    pendingOlderPageAdvanceLengthRef.current = null;
    pendingScrollAdjustmentRef.current = 0;
    return () => {
      canceled = true;
    };
  }, [isPaused, sortDirection]);

  useEffect(() => {
    let canceled = false;
    queueMicrotask(() => {
      if (!canceled) {
        setWindowStart((current) => Math.min(current, maxWindowStart));
      }
    });
    return () => {
      canceled = true;
    };
  }, [maxWindowStart]);

  useEffect(() => {
    const previousLength = pendingOlderPageAdvanceLengthRef.current;
    if (previousLength === null) {
      return;
    }

    if (visibleEntries.length > previousLength) {
      pendingOlderPageAdvanceLengthRef.current = null;
      const advanceCount = getWorkerLogServerPageWindowAdvanceCount(
        previousLength,
        visibleEntries.length,
        windowStart
      );
      advanceWindow(advanceCount);
      return;
    }

    if (!isLoadingMore && visibleEntries.length <= previousLength) {
      pendingOlderPageAdvanceLengthRef.current = null;
    }
  }, [advanceWindow, isLoadingMore, visibleEntries.length, windowStart]);

  useLayoutEffect(() => {
    const scrollAdjustment = pendingScrollAdjustmentRef.current;
    if (scrollAdjustment === 0) {
      return;
    }

    const viewport = scrollRef.current;
    if (!viewport) {
      pendingScrollAdjustmentRef.current = 0;
      return;
    }

    viewport.scrollTop = Math.max(0, viewport.scrollTop - scrollAdjustment);
    pendingScrollAdjustmentRef.current = 0;
  }, [windowStart, windowedEntries]);

  const handleLoadMore = useCallback(() => {
    if (localHasMore) {
      advanceWindow(Math.min(
        workerLogActivityPageSize,
        visibleEntries.length - (windowStart + maxWorkerLogPanelEntries)
      ));
      return;
    }

    if (!hasMore || isLoadingMore) {
      return;
    }

    pendingOlderPageAdvanceLengthRef.current = visibleEntries.length;
    onLoadMore();
  }, [advanceWindow, hasMore, isLoadingMore, localHasMore, onLoadMore, visibleEntries.length, windowStart]);

  if (viewState === "compact") {
    return (
      <section className={getWorkerLogStreamCardClassName(viewState)}>
        {connectionError ? (
          <div className="mb-3 rounded-lg border border-amber-500/30 bg-amber-500/10 px-3 py-2 text-amber-900 text-sm dark:text-amber-100">
            {connectionError}
          </div>
        ) : null}
      </section>
    );
  }

  return (
    <section className={getWorkerLogStreamCardClassName(viewState)}>
      <div className="mb-3 flex flex-wrap items-center justify-end gap-2">
        <Badge className="border-slate-500/30 bg-slate-500/10 text-slate-700 dark:text-slate-200" variant="outline">
          {windowedEntries.length} loaded
        </Badge>
        {pauseEnabled && isPaused && pendingPausedCount > 0 ? (
          <Badge className="border-amber-500/30 bg-amber-500/10 text-amber-800 dark:text-amber-100" variant="outline">
            {pendingPausedCount} buffered
          </Badge>
        ) : null}
      </div>
      {connectionError ? (
        <div className="mb-3 rounded-lg border border-amber-500/30 bg-amber-500/10 px-3 py-2 text-amber-900 text-sm dark:text-amber-100">
          {connectionError}
        </div>
      ) : null}
      <PanelScrollViewport
        autoLoadMore={false}
        className={getWorkerLogStreamViewportClassName(viewState)}
        footerClassName="border-slate-800 text-slate-400"
        hasMore={effectiveHasMore}
        loadedCount={windowedEntries.length}
        loading={isLoading}
        loadingMore={isLoadingMore}
        noun="log entry"
        onLoadMore={handleLoadMore}
        onScroll={(event) => {
          if (!isLoading && !isLoadingMore && isNearWorkerLogScrollBottom(event.currentTarget)) {
            handleLoadMore();
          }
        }}
        viewportRef={scrollRef}
      >
        {isLoading && windowedEntries.length === 0 ? (
          <div className="p-4">
            <StackedSkeleton count={6} />
          </div>
        ) : windowedEntries.length === 0 ? (
          <div className="p-4">
            <EmptyListState
              message={hasActiveIteration
                ? "The active iteration has not emitted any log lines yet."
                : "No logs in this view."}
            />
          </div>
        ) : (
          <div className="font-mono text-xs leading-[1.15rem]">
            {windowedEntries.map((entry, index) => {
              const previousEntry = index > 0 ? windowedEntries[index - 1] : null;
              const showCategory = !previousEntry || previousEntry.category !== entry.category;

              return (
                <div
                  className="px-4 py-0"
                  key={entry.id}
                  ref={(element) => {
                    if (element) {
                      rowHeightsRef.current.set(entry.id, element.offsetHeight);
                    }
                  }}
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
                    {entry.sequence && onOpenIteration ? (
                      <button
                        className="shrink-0 text-sky-300 underline-offset-4 hover:text-sky-200 hover:underline"
                        onClick={() => onOpenIteration(entry.sequence!)}
                        type="button"
                      >
                        #{entry.sequence}
                      </button>
                    ) : null}
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
      </PanelScrollViewport>
    </section>
  );
}

export function getWorkerLogStreamCardClassName(viewState: WorkComponentShape) {
  return cn(
    "flex h-full min-h-0 flex-col rounded-xl border bg-muted/10 p-4",
    viewState === "standard" && "min-h-[24rem] max-h-[70vh]",
    viewState === "detailed" && "min-h-[24rem] max-h-[calc(100svh-11rem)]"
  );
}

export function getWorkerLogStreamViewportClassName(viewState: WorkComponentShape) {
  return cn(
    "rounded-xl border border-slate-800 bg-slate-950 text-slate-100 shadow-inner",
    viewState === "standard" && "max-h-[36rem]",
    viewState === "detailed" && "max-h-[42rem]"
  );
}

export function getWorkerLogServerPageWindowAdvanceCount(
  previousLength: number,
  nextLength: number,
  windowStart: number,
  pageSize = workerLogActivityPageSize,
  windowLimit = maxWorkerLogPanelEntries
) {
  const appendedCount = nextLength - previousLength;
  if (appendedCount <= 0) {
    return 0;
  }

  const nextMaxWindowStart = Math.max(0, nextLength - windowLimit);
  const availableAdvance = nextMaxWindowStart - windowStart;
  if (availableAdvance <= 0) {
    return 0;
  }

  return Math.min(pageSize, appendedCount, availableAdvance);
}

function WorkerFailureBanner({
  details,
  now,
  onDismiss,
}: {
  details: WorkerFailureDetails;
  now?: number;
  onDismiss?: () => void;
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
        <div className="flex shrink-0 items-center gap-2">
          {details.kind === "exception" && exceptionChain.some((item) => getStackTraceLines(item.stackTrace).length > 0) ? (
            <Button
              className="h-8 border-red-400/30 bg-red-500/10 text-red-100 hover:bg-red-500/20 hover:text-white"
              onClick={() => setStackOpen(true)}
              size="sm"
              type="button"
              variant="outline"
            >
              <Braces className="size-3.5" />
              Open stack
            </Button>
          ) : null}
          {onDismiss ? (
            <Button
              aria-label="Dismiss failure banner"
              className="h-8 border-red-400/30 bg-red-500/10 px-2 text-red-100 hover:bg-red-500/20 hover:text-white"
              onClick={onDismiss}
              size="sm"
              type="button"
              variant="outline"
            >
              <X className="size-3.5" />
            </Button>
          ) : null}
        </div>
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
    <ConsoleEmptyState className="text-left" padding="compact">
      {message}
    </ConsoleEmptyState>
  );
}

export function describeWaitingTimelineItem(
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

export function formatPendingRetryText(
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

export function formatFutureRelativeTime(value: string, now: number) {
  const timestamp = Date.parse(value);
  if (Number.isFinite(timestamp) && timestamp <= now) {
    return "0.00s";
  }

  const relative = formatRelativeTime(value, now);
  return relative.startsWith("in ") ? relative.slice(3) : "0.00s";
}

export function formatWorkerStatusTiming(worker: WorkerSnapshot, now: number) {
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

export function renderTimelineItemTitle(item: WorkerTimelineItem, now: number) {
  if (item.kind === "iteration" &&
    item.iterationStatus === "Failed" &&
    item.sequence !== undefined &&
    item.attemptCount !== null &&
    item.attemptCount !== undefined &&
    item.attemptCount > 1) {
    const durationLabel = itemDescriptionDurationLabel(item);
    return durationLabel
      ? `Iteration #${item.sequence} failed after ${durationLabel}`
      : `Iteration #${item.sequence} failed`;
  }

  if (item.kind === "iteration" &&
    item.iterationStatus === "Completed" &&
    item.sequence !== undefined &&
    item.attemptCount !== null &&
    item.attemptCount !== undefined &&
    item.attemptCount > 1) {
    const durationLabel = itemDescriptionDurationLabel(item);
    return durationLabel
      ? `Iteration #${item.sequence} completed in ${durationLabel} after ${item.attemptCount} attempts`
      : `Iteration #${item.sequence} completed after ${item.attemptCount} attempts`;
  }

  if (item.liveText?.kind === "iteration" && item.liveText.status === "Executing") {
    const executionLabel = `has been executing for ${formatElapsedSince(item.liveText.startedAt, now)}`;
    return item.liveText.attemptCount !== null &&
      item.liveText.attemptCount !== undefined &&
      item.liveText.attemptCount > 1
      ? `Iteration #${item.liveText.sequence} ${executionLabel}`
      : `Iteration #${item.liveText.sequence} ${executionLabel}`;
  }

  if (item.liveText?.kind === "state" &&
    item.liveText.mode === "retry" &&
    item.liveText.retryAttempt !== null &&
    item.liveText.retryAttempt !== undefined) {
    return `${item.title} (Retry #${item.liveText.retryAttempt})`;
  }

  return item.title;
}

export function trimIterationTitlePrefix(title: string, sequence: number) {
  const prefix = `Iteration #${sequence}`;
  if (!title.startsWith(prefix)) {
    return title;
  }

  const remainder = title.slice(prefix.length).trimStart();
  return remainder.length > 0 ? remainder : "Iteration";
}

export function itemDescriptionDurationLabel(item: WorkerTimelineItem) {
  const match = item.title.match(/after (.+)$/i);
  return match?.[1] ?? null;
}

export function getRetryOriginIterationSequence(
  sequence?: number,
  attemptCount?: number | null) {
  if (sequence === null ||
    sequence === undefined ||
    attemptCount === null ||
    attemptCount === undefined ||
    attemptCount <= 1) {
    return null;
  }

  const originSequence = sequence - (attemptCount - 1);
  return originSequence > 0 ? originSequence : null;
}

export function renderTimelineItemDescription(item: WorkerTimelineItem, now: number) {
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

export function shouldRenderTimelineDescription(
  item: WorkerTimelineItem,
  title: string,
  description: string
) {
  if (description.trim().length === 0 || description === title) {
    return false;
  }

  if (item.kind === "state" || item.kind === "queue") {
    return true;
  }

  return item.kind === "iteration" &&
    item.attemptCount !== null &&
    item.attemptCount !== undefined &&
    item.attemptCount > 1;
}

export function renderTimelineItemMeta(item: WorkerTimelineItem, now: number) {
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

export function createTimelineRows(items: WorkerTimelineItem[]): WorkerTimelineRow[] {
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

export function shouldShowTimelineGap(currentItem: WorkerTimelineItem, nextItem: WorkerTimelineItem) {
  return Boolean(currentItem && nextItem) &&
    !(
    isRecurrenceTimelineWaitItem(currentItem) &&
    nextItem.kind === "iteration"
    );
}

export function shouldForgetPagedWorkerTimelineItems(
  scrollTop: number,
  pagedItemCount: number,
  threshold = panelForgetPagedItemsScrollTop
) {
  return shouldForgetPagedPanelItems(scrollTop, pagedItemCount, threshold);
}

export function shouldForgetPagedIterationMessages(
  scrollTop: number,
  pagedMessageCount: number,
  threshold = panelForgetPagedItemsScrollTop
) {
  return shouldForgetPagedPanelItems(scrollTop, pagedMessageCount, threshold);
}

export function shouldForgetPagedPanelItems(
  scrollTop: number,
  pagedItemCount: number,
  threshold = panelForgetPagedItemsScrollTop
) {
  return pagedItemCount > 0 && scrollTop <= threshold;
}

export function shouldForgetPushedWorkerTimelineItems(
  scrollTop: number,
  pushedItemCount: number,
  sortDirection: WorkerSortDirection,
  threshold = panelForgetPagedItemsScrollTop,
  itemThreshold = workerTimelineForgetPushedItemsThreshold
) {
  return sortDirection === "desc" &&
    pushedItemCount > itemThreshold &&
    scrollTop <= threshold;
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

export function isStableTimelineAnchorRow(row: WorkerTimelineRow) {
  if (row.kind !== "item") {
    return false;
  }

  if (row.item.failureDetails) {
    return true;
  }

  return !row.item.liveText;
}

export function isLiveTimelineGap(item: WorkerTimelineItem) {
  return item.liveText?.kind === "state" ||
    (item.liveText?.kind === "iteration" && item.liveText.status === "Executing");
}

export function formatIterationTimelineStatus(status: WorkCompletionStatus) {
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

export function getChronologicalIterations(iterations?: WorkerIterationSnapshot[] | null) {
  return [...(iterations ?? [])]
    .sort((left, right) => {
      if (left.sequence !== right.sequence) {
        return left.sequence - right.sequence;
      }

      return Date.parse(left.occurredAt) - Date.parse(right.occurredAt);
    });
}

export function parseTimelineTimestamp(value: string) {
  const timestamp = Date.parse(value);
  return Number.isFinite(timestamp) ? timestamp : 0;
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
    case "InProcess":
      return "In-process";
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

function createWorkerFailureDetailsFromIterationFailure(
  failure: WorkerIterationFailure
): WorkerFailureDetails {
  return {
    code: failure.code ?? undefined,
    declaredByWork: failure.declaredByWork,
    exceptionType: failure.exceptionType ?? undefined,
    kind: failure.kind === "Exception" ? "exception" : "failure",
    message: failure.message,
    stackTrace: failure.stackTrace ?? undefined,
    target: failure.target ?? undefined,
  };
}

function getOrderedMessageSeverities(summary: WorkIterationMessageSummary) {
  const available = new Set(
    iterationMessageSeverityLabels.filter((severity) => {
      switch (severity) {
        case "Critical":
          return summary.critical > 0;
        case "Error":
          return summary.error > 0;
        case "Warning":
          return summary.warning > 0;
        case "Information":
          return summary.information > 0;
        case "Debug":
          return summary.debug > 0;
        case "Trace":
          return summary.trace > 0;
        default:
          return false;
      }
    })
  );

  return [
    ...iterationMessageSeverityLabels.filter((severity) => available.has(severity)),
  ];
}

function createIterationMessagesPath(
  workerId: string,
  sequence: number,
  options: {
    cursor?: string | null;
    severities?: string | null;
    sortDirection: "asc" | "desc";
    take: number;
  }
) {
  const searchParams = new URLSearchParams({
    sort: options.sortDirection === "asc" ? "Asc" : "Desc",
    take: String(options.take),
  });
  if (options.cursor) {
    searchParams.set("cursor", options.cursor);
  }
  if (options.severities) {
    searchParams.set("severities", options.severities);
  }

  return `workers/${workerId}/iterations/${sequence}/messages?${searchParams.toString()}`;
}

function createIterationLogsPath(
  workerId: string,
  sequence: number,
  options: {
    cursor?: string | null;
    logLevels?: readonly WorkerLogFilterLevel[] | null;
    sortDirection: "asc" | "desc";
    take: number;
  }
) {
  const searchParams = new URLSearchParams({
    sort: options.sortDirection === "asc" ? "Asc" : "Desc",
    take: String(options.take),
  });
  if (options.cursor) {
    searchParams.set("cursor", options.cursor);
  }
  if (options.logLevels && options.logLevels.length > 0 && options.logLevels.length !== workerLogFilterLevels.length) {
    searchParams.set("logLevels", options.logLevels.join(","));
  }

  return `workers/${workerId}/iterations/${sequence}/logs?${searchParams.toString()}`;
}

function createHiddenMessageSeveritiesForFocus(
  severities: string[],
  focusSeverity: string
) {
  return new Set(
    severities
      .map((severity) => normalizeMessageSeverity(severity))
      .filter((severity) => severity !== normalizeMessageSeverity(focusSeverity))
  );
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

const emptyAvailableWorkerActions: Record<WorkAction, boolean> = {
  Start: false,
  Pause: false,
  Cancel: false,
  Push: false,
  Purge: false,
};

export function getAvailableWorkerActions(state: WorkerState): Record<WorkAction, boolean> {
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

export function formatCompactTimelineRelativeTime(value: string | null | undefined, now: number) {
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

export function formatElapsedSince(value: string | null | undefined, now: number) {
  if (!value) {
    return "-";
  }

  const timestamp = Date.parse(value);
  if (!Number.isFinite(timestamp)) {
    return "-";
  }

  return formatMillisecondsCompact(Math.max(0, now - timestamp));
}

export function formatDurationLabel(value: string | null | undefined) {
  const milliseconds = parseDurationMilliseconds(value);
  return milliseconds === null ? (value?.trim() || "-") : formatMillisecondsCompact(milliseconds);
}

export function getIterationDurationMilliseconds(iteration: WorkerIterationSnapshot, now: number) {
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

export function parseDurationMilliseconds(value: string | null | undefined) {
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

export function formatMillisecondsCompact(value: number) {
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

export function normalizeMessageSeverity(severity: string) {
  return severity.trim().toLowerCase();
}

export function normalizeMessageSeverityLabel(severity: string) {
  switch (normalizeMessageSeverity(severity)) {
    case "critical":
      return "Critical";
    case "error":
      return "Error";
    case "warning":
      return "Warning";
    case "debug":
      return "Debug";
    case "info":
    case "information":
      return "Information";
    case "trace":
      return "Trace";
    default:
      return severity.trim() || "Unknown";
  }
}

export function formatMessageSeverity(severity: string) {
  return normalizeMessageSeverityLabel(severity) === "Information"
    ? "Info"
    : normalizeMessageSeverityLabel(severity);
}

export function getStackTraceLines(value?: string | null) {
  return value
    ? value
      .split(/\r?\n/)
      .map((line) => line.trimEnd())
      .filter(Boolean)
    : [];
}

export function createStackTraceDisplayEntries(
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

export function createCollapsedStackCounts(): Record<StackFrameFilterKind, number> {
  return {
    application: 0,
    library: 0,
    work: 0,
  };
}

export function formatCollapsedStackEntry(entry: Extract<StackTraceDisplayEntry, { type: "collapsed" }>) {
  const parts = [
    entry.counts.application > 0 ? `${entry.counts.application} app` : "",
    entry.counts.work > 0 ? `${entry.counts.work} workable` : "",
    entry.counts.library > 0 ? `${entry.counts.library} library` : "",
  ].filter(Boolean);

  return `${entry.total} collapsed frame${entry.total === 1 ? "" : "s"}${parts.length > 0 ? `: ${parts.join(", ")}` : ""}`;
}

export function classifyStackTraceLine(line: string) {
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

export function stackFrameKindLabel(kind: StackFrameFilterKind) {
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

export function stackFrameFilterTone(kind: StackFrameFilterKind, hidden: boolean) {
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

export function stackTraceLineTone(kind: "application" | "detail" | "library" | "work") {
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

function MetadataItem({ label, value }: { label: string; value: ReactNode }) {
  return (
    <div className="min-w-0 rounded-md border bg-muted/20 p-3">
      <div className="text-muted-foreground text-xs">{label}</div>
      <div className="mt-1 break-words font-mono text-sm">{value}</div>
    </div>
  );
}

export function createWorkerOverviewPath(
  workerId: string,
  options?: {
    activity?: Exclude<WorkWorkerOverviewActivity, "Auto">;
    activityCursor?: string | null;
    activityTake?: number;
    logLevels?: readonly WorkerLogFilterLevel[] | null;
    logIterationSequence?: number | null;
    logSortDirection?: WorkerSortDirection;
    timelineFilters?: readonly WorkerTimelineFilterKind[] | null;
    timelineSortDirection?: WorkerSortDirection;
  }
) {
  const query = createWorkerOverviewActivityQuery(options);
  return query.length > 0
    ? `workers/${workerId}/overview?${query}`
    : `workers/${workerId}/overview`;
}

export function createWorkerOverviewLogsPath(
  workerId: string,
  options?: {
    activityCursor?: string | null;
    activityTake?: number;
    logLevels?: readonly WorkerLogFilterLevel[] | null;
    logIterationSequence?: number | null;
    logSortDirection?: WorkerSortDirection;
  }
) {
  const query = createWorkerOverviewActivityQuery(options);
  return query.length > 0
    ? `workers/${workerId}/overview/logs?${query}`
    : `workers/${workerId}/overview/logs`;
}

export function createWorkerOverviewTimelinePath(
  workerId: string,
  options?: {
    activityCursor?: string | null;
    activityTake?: number;
    timelineFilters?: readonly WorkerTimelineFilterKind[] | null;
    timelineSortDirection?: WorkerSortDirection;
  }
) {
  const query = createWorkerOverviewActivityQuery(options);
  return query.length > 0
    ? `workers/${workerId}/overview/timeline?${query}`
    : `workers/${workerId}/overview/timeline`;
}

export function createWorkerOverviewActivityQuery(
  options?: {
    activity?: Exclude<WorkWorkerOverviewActivity, "Auto">;
    activityCursor?: string | null;
    activityTake?: number;
    logLevels?: readonly WorkerLogFilterLevel[] | null;
    logIterationSequence?: number | null;
    logSortDirection?: WorkerSortDirection;
    timelineFilters?: readonly WorkerTimelineFilterKind[] | null;
    timelineSortDirection?: WorkerSortDirection;
  }
) {
  const search = new URLSearchParams();
  if (options?.activity) {
    search.set("activity", options.activity);
  }
  if (options?.activityTake) {
    search.set("activityTake", String(options.activityTake));
  }
  if (options?.activityCursor) {
    search.set("activityCursor", options.activityCursor);
  }
  if (options?.logLevels && options.logLevels.length > 0) {
    search.set("logLevels", options.logLevels.join(","));
  }
  if (options?.logIterationSequence) {
    search.set("logIterationSequence", String(options.logIterationSequence));
  }
  if (options?.logSortDirection) {
    search.set("logSort", options.logSortDirection === "asc" ? "Asc" : "Desc");
  }
  if (options?.timelineFilters && options.timelineFilters.length > 0) {
    search.set(
      "timelineCategories",
      options.timelineFilters.map(mapTimelineFilterKindToServerCategory).join(",")
    );
  }
  if (options?.timelineSortDirection) {
    search.set("timelineSort", options.timelineSortDirection === "asc" ? "Asc" : "Desc");
  }

  return search.toString();
}

export function serializeWorkerLogQuery(
  selectedLevels: readonly WorkerLogFilterLevel[] | null,
  sortDirection: WorkerSortDirection
) {
  return `${sortDirection}:${(selectedLevels ?? workerLogFilterLevels).join(",")}`;
}

export function serializeWorkerTimelineQuery(
  selectedFilters: readonly WorkerTimelineFilterKind[] | null,
  sortDirection: WorkerSortDirection
) {
  return `${sortDirection}:${(selectedFilters ?? workerTimelineFilterKinds).join(",")}`;
}

export function isDefaultWorkerLogQuery(
  selectedLevels: readonly WorkerLogFilterLevel[] | null,
  sortDirection: WorkerSortDirection
) {
  return sortDirection === "desc" && selectedLevels === null;
}

export function isDefaultWorkerTimelineQuery(
  selectedFilters: readonly WorkerTimelineFilterKind[] | null,
  sortDirection: WorkerSortDirection
) {
  return sortDirection === "desc" && selectedFilters === null;
}

export function normalizeSelectedLogLevelsForRequest(levels: WorkerLogFilterLevel[] | null) {
  if (!levels || levels.length === 0) {
    return levels;
  }

  const normalized = workerLogFilterLevels.filter((level) => levels.includes(level));
  return normalized.length === workerLogFilterLevels.length ? null : normalized;
}

export function normalizeSelectedTimelineFiltersForRequest(filters: WorkerTimelineFilterKind[] | null) {
  if (!filters || filters.length === 0) {
    return filters;
  }

  const normalized = workerTimelineFilterKinds.filter((filterKind) => filters.includes(filterKind));
  return normalized.length === workerTimelineFilterKinds.length ? null : normalized;
}

export function updateSelectedLogLevels(
  current: WorkerLogFilterLevel[] | null,
  level: WorkerLogFilterLevel,
  visible: boolean
) {
  const activeLevels = new Set(current ?? workerLogFilterLevels);
  if (visible) {
    activeLevels.add(level);
  } else {
    activeLevels.delete(level);
  }

  if (activeLevels.size === 0) {
    return current ?? [level];
  }

  return normalizeSelectedLogLevelsForRequest([...activeLevels]);
}

export function createSelectedLogLevelsForFocus(level: WorkerLogFilterLevel) {
  return [level] satisfies WorkerLogFilterLevel[];
}

export function createSelectedTimelineFiltersForFocus(filterKind: WorkerTimelineFilterKind) {
  return [filterKind] satisfies WorkerTimelineFilterKind[];
}

export function updateSelectedTimelineFilters(
  current: WorkerTimelineFilterKind[] | null,
  filterKind: WorkerTimelineFilterKind,
  selected: boolean
) {
  const activeFilters = new Set(current ?? workerTimelineFilterKinds);
  if (selected) {
    activeFilters.add(filterKind);
  } else {
    activeFilters.delete(filterKind);
  }

  if (activeFilters.size === 0) {
    return current ?? [filterKind];
  }

  return normalizeSelectedTimelineFiltersForRequest([...activeFilters]);
}

export function mapTimelineFilterKindToServerCategory(filterKind: WorkerTimelineFilterKind) {
  switch (filterKind) {
    case "failures":
      return "Failure";
    case "user":
      return "UserAction";
    case "system":
    default:
      return "SystemEvent";
  }
}

function createWorkerSnapshotFromLanding(landing: WorkWorkerOverviewComponent): WorkerSnapshot {
  const latestIteration = landing.latestIteration
    ? createWorkerIterationSnapshotFromLandingLatestIteration(landing.latestIteration)
    : null;
  const iterations = getChronologicalIterations(
    landing.recentIterations.map((iteration) =>
      createWorkerIterationSnapshotFromLandingRecentIteration(iteration, landing.latestIteration)
    )
  );

  return {
    id: landing.worker.workerId,
    revision: landing.worker.revision,
    stateSequence: landing.worker.stateSequence,
    definitionName: landing.worker.definitionName,
    definitionCategory: landing.worker.definitionCategory,
    origin: createRealtimeOriginFromLandingOrigin(landing.worker.createdOrigin),
    state: landing.worker.state,
    isFinal: landing.worker.isFinal,
    input: landing.input,
    output: landing.latestIteration?.output ?? null,
    messages: [],
    actionHistory: [],
    iterations,
    currentIterationSequence: landing.latestIteration?.status === "Executing"
      ? landing.latestIteration.sequence
      : null,
    lastIteration: latestIteration,
    lastIterationSequence: landing.latestIteration?.sequence ?? null,
    createdAt: landing.worker.createdAt,
    stateChangedAt: landing.worker.stateChangedAt,
    nextRunAt: landing.worker.nextRunAt ?? null,
    retryAttempt: landing.worker.retryAttempt ?? null,
    updatedAt: landing.worker.updatedAt,
    version: {
      workerId: landing.worker.workerId,
      revision: landing.worker.revision,
    },
  };
}

function createRealtimeOriginFromLandingOrigin(origin: WorkWorkerOverviewOrigin): WorkableRealtimeOrigin {
  return {
    channel: origin.channel,
    actor: origin.actorId || origin.actorName || origin.actorEmail
      ? {
        email: origin.actorEmail ?? undefined,
        id: origin.actorId ?? undefined,
        name: origin.actorName ?? undefined,
      }
      : undefined,
  };
}

function createWorkerIterationSnapshotFromLandingLatestIteration(
  iteration: WorkWorkerOverviewLatestIteration
): WorkerIterationSnapshot {
  return {
    attemptCount: iteration.attemptCount,
    completedAt: iteration.completedAt ?? undefined,
    executionDuration: iteration.executionDuration ?? undefined,
    isFinal: isFinalIterationStatus(iteration.status),
    logs: [],
    messages: [],
    occurredAt: iteration.completedAt ?? iteration.startedAt,
    output: iteration.output ?? null,
    sequence: iteration.sequence,
    startedAt: iteration.startedAt,
    status: iteration.status,
  };
}

function createWorkerIterationSnapshotFromLandingRecentIteration(
  iteration: WorkWorkerOverviewRecentIteration,
  latestIteration?: WorkWorkerOverviewLatestIteration | null
): WorkerIterationSnapshot {
  const matchedLatestIteration = latestIteration?.sequence === iteration.sequence
    ? latestIteration
    : null;
  const source = matchedLatestIteration ?? iteration;

  return {
    attemptCount: source.attemptCount,
    completedAt: source.completedAt ?? undefined,
    executionDuration: source.executionDuration ?? undefined,
    isFinal: isFinalIterationStatus(source.status),
    logs: [],
    messages: [],
    occurredAt: source.completedAt ?? source.startedAt,
    output: matchedLatestIteration?.output ?? null,
    sequence: source.sequence,
    startedAt: source.startedAt,
    status: source.status,
  };
}

function createWorkerLogEntryFromLandingLogEntry(entry: WorkWorkerOverviewLogEntry): WorkerLogEntry {
  return {
    category: entry.category,
    eventId: {
      id: entry.eventId,
      name: entry.eventName ?? undefined,
    },
    exceptionMessage: entry.exceptionMessage ?? undefined,
    exceptionType: entry.exceptionType ?? undefined,
    id: entry.id,
    level: entry.level,
    message: entry.message,
    ordinal: entry.ordinal,
    occurredAt: entry.occurredAt,
    sequence: entry.sequence,
  };
}

function createWorkerLogSummaryFromLanding(summary: WorkWorkerOverviewLogSummary) {
  return {
    critical: summary.critical,
    debug: summary.debug,
    error: summary.error,
    errors: summary.errors,
    information: summary.information,
    trace: summary.trace,
    total: summary.total,
    warning: summary.warning,
    warnings: summary.warnings,
  };
}

function createWorkerFailureDetailsFromLandingFailure(
  failure: WorkWorkerOverviewFailure
): WorkerFailureDetails {
  return {
    code: failure.code ?? undefined,
    declaredByWork: failure.declaredByWork,
    exceptionType: failure.exceptionType ?? undefined,
    kind: failure.kind === "Exception" ? "exception" : "failure",
    message: failure.message,
    retryPending: failure.pendingState?.mode === "Retry"
      ? {
          nextRunAt: failure.pendingState.nextRunAt ?? null,
          retryAttempt: failure.pendingState.retryAttempt ?? null,
          stateChangedAt: failure.pendingState.stateChangedAt,
          updatedAt: failure.pendingState.updatedAt,
        }
      : undefined,
    stackTrace: failure.stackTrace ?? undefined,
    target: failure.target ?? undefined,
  };
}

function createWorkerTimelineItemFromLandingTimelineItem(
  item: WorkWorkerOverviewTimelineItem
): WorkerTimelineItem {
  const iterationStatus = item.iterationStatus ?? undefined;
  const failureDetails = item.failure
    ? createWorkerFailureDetailsFromLandingFailure(item.failure)
    : undefined;
  const origin = item.origin ? createRealtimeOriginFromLandingOrigin(item.origin) : undefined;
  const actorLabel = formatActionTimelineActorLabel(origin);
  const hasActor = Boolean(actorLabel);
  const sourceLabel = hasActor ? undefined : formatActionTimelineSourceLabel(origin?.channel);

  return {
    attemptCount: item.attemptCount ?? undefined,
    actorLabel: actorLabel ?? undefined,
    at: item.at,
    badge: createTimelineBadgeFromLanding(item),
    description: createTimelineDescriptionFromLanding(item, failureDetails),
    failureDetails,
    facts: createTimelineFactsFromLanding(),
    filterKind: createTimelineFilterKindFromLanding(item.category),
    icon: createTimelineIconFromLanding(item),
    id: item.id,
    isFinal: iterationStatus ? isFinalIterationStatus(iterationStatus) : undefined,
    iterationStatus,
    kind: createTimelineKindFromLanding(item.kind),
    liveText: createTimelineLiveTextFromLanding(item),
    sequence: item.sequence ?? undefined,
    sortOrder: createTimelineSortOrderFromLanding(item.kind),
    sourceLabel: sourceLabel && sourceLabel !== "System" ? sourceLabel : undefined,
    stateMode: createTimelineStateModeFromLanding(item),
    title: createTimelineTitleFromLanding(item),
    tone: createTimelineToneFromLanding(item),
  };
}

function createWorkerQueuedTimelineItem(worker: WorkerSnapshot): WorkerTimelineItem {
  const origin = worker.origin;
  const actorLabel = formatActionTimelineActorLabel(origin);
  const hasActor = Boolean(actorLabel);
  const sourceLabel = hasActor ? undefined : formatActionTimelineSourceLabel(origin?.channel);

  return {
    actorLabel: actorLabel ?? undefined,
    at: worker.createdAt,
    badge: formatWorkerTimelineBadgeText(formatLandingWorkerState("Queued"), "State"),
    description: "This worker entered the queue here.",
    facts: [],
    icon: Send,
    id: `worker-queued:${worker.id.value}`,
    kind: "queue",
    sortOrder: -1,
    sourceLabel: sourceLabel && sourceLabel !== "System" ? sourceLabel : undefined,
    title: "Worker queued",
    tone: "info",
  };
}

function createTimelineSortOrderFromLanding(kind: WorkWorkerOverviewTimelineItem["kind"]) {
  switch (kind) {
    case "ActionRequest":
      return 1;
    case "StateChange":
      return 2;
    case "Iteration":
      return 3;
    default:
      return 0;
  }
}

function createTimelineStateModeFromLanding(item: WorkWorkerOverviewTimelineItem): WorkerTimelineItem["stateMode"] {
  if (item.pendingState?.mode === "Retry") {
    return "retry";
  }

  if (item.pendingState?.mode === "Recurrence") {
    return "recurrence";
  }

  if (item.kind !== "StateChange") {
    return undefined;
  }

  switch (item.state) {
    case "Retrying":
      return "retry";
    case "Waiting":
      return "recurrence";
    default:
      return undefined;
  }
}

function createTimelineLiveTextFromLanding(
  item: WorkWorkerOverviewTimelineItem
): WorkerTimelineItem["liveText"] | undefined {
  if (item.kind === "Iteration" && item.iterationStatus === "Executing" && item.sequence !== null && item.sequence !== undefined) {
    return {
      attemptCount: item.attemptCount ?? undefined,
      kind: "iteration",
      executionDuration: item.executionDuration ?? undefined,
      sequence: item.sequence,
      startedAt: item.at,
      status: item.iterationStatus,
    };
  }

  if (item.pendingState) {
    return {
      kind: "state",
      mode: item.pendingState.mode === "Retry" ? "retry" : "recurrence",
      nextRunAt: item.pendingState.nextRunAt ?? undefined,
      retryAttempt: item.pendingState.retryAttempt ?? undefined,
      stateChangedAt: item.pendingState.stateChangedAt,
      updatedAt: item.pendingState.updatedAt,
    };
  }

  if (item.kind !== "StateChange") {
    return undefined;
  }

  switch (item.state) {
    case "Retrying":
      return {
        kind: "state",
        mode: "retry",
        nextRunAt: undefined,
        retryAttempt: undefined,
        stateChangedAt: item.at,
        updatedAt: item.at,
      };
    case "Waiting":
      return {
        kind: "state",
        mode: "recurrence",
        nextRunAt: undefined,
        stateChangedAt: item.at,
        updatedAt: item.at,
      };
    default:
      return undefined;
  }
}

function createTimelineKindFromLanding(kind: WorkWorkerOverviewTimelineItem["kind"]): WorkerTimelineItem["kind"] {
  switch (kind) {
    case "ActionRequest":
      return "action";
    case "Iteration":
      return "iteration";
    case "StateChange":
    default:
      return "state";
  }
}

function createTimelineFilterKindFromLanding(category: WorkWorkerOverviewTimelineCategory): WorkerTimelineFilterKind {
  switch (category) {
    case "Failure":
      return "failures";
    case "UserAction":
      return "user";
    case "SystemEvent":
    default:
      return "system";
  }
}

function createTimelineToneFromLanding(item: WorkWorkerOverviewTimelineItem): WorkerTimelineItem["tone"] {
  if (item.category === "Failure") {
    return "danger";
  }

  if (item.kind === "Iteration" && item.iterationStatus) {
    return iterationTimelineTone(item.iterationStatus);
  }

  if (item.kind === "ActionRequest") {
    switch (item.action) {
      case "Pause":
        return "warning";
      case "Cancel":
      case "Purge":
        return "neutral";
      case "Start":
        return "success";
      case "Push":
        return "info";
      default:
        return item.category === "UserAction" ? "info" : "neutral";
    }
  }

  switch (item.state) {
    case "Completed":
      return "success";
    case "Failed":
    case "Canceled":
      return "neutral";
    case "Paused":
    case "Interrupted":
      return "warning";
    case "Queued":
    case "Running":
    case "Retrying":
    case "Waiting":
      return "info";
    default:
      return item.category === "UserAction" ? "info" : "neutral";
  }
}

function createTimelineIconFromLanding(item: WorkWorkerOverviewTimelineItem) {
  if (item.kind === "Iteration" && item.iterationStatus) {
    return iterationTimelineIcon(item.iterationStatus);
  }

  if (item.kind === "StateChange") {
    switch (item.state) {
      case "Paused":
        return Pause;
      case "Canceled":
        return Ban;
      case "Retrying":
        return RotateCw;
      case "Waiting":
        return Clock3;
      default:
        return Activity;
    }
  }

  switch (item.action) {
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
      return item.category === "Failure" ? Ban : Activity;
  }
}

function createTimelineTitleFromLanding(item: WorkWorkerOverviewTimelineItem) {
  if (item.kind === "Iteration") {
    const sequenceLabel = item.sequence !== null && item.sequence !== undefined
      ? `Iteration #${item.sequence}`
      : "Iteration";
    if (!item.iterationStatus) {
      return sequenceLabel;
    }

    const durationLabel = item.executionDuration
      ? formatDurationLabel(item.executionDuration)
      : null;
    if (durationLabel && item.iterationStatus !== "Executing") {
      return `${sequenceLabel} ${formatIterationTimelineStatus(item.iterationStatus)} after ${durationLabel}`;
    }

    return item.iterationStatus === "Executing"
      ? `${sequenceLabel} has been executing`
      : `${sequenceLabel} ${formatIterationTimelineStatus(item.iterationStatus)}`;
  }

  if (item.kind === "ActionRequest") {
    return item.action
      ? `${item.action} requested`
      : "Worker action requested";
  }

  switch (item.state) {
    case "Paused":
      return "Worker paused";
    case "Canceled":
      return "Worker canceled";
    case "Retrying":
      return "Worker retrying";
    case "Waiting":
      return "Worker waiting";
    default:
      return item.state
        ? `Worker ${formatLandingWorkerState(item.state)}`
        : "Worker state changed";
  }
}

function createTimelineDescriptionFromLanding(
  item: WorkWorkerOverviewTimelineItem,
  failureDetails?: WorkerFailureDetails
) {
  const retryLineage = createRetryLineageDescription(item.sequence, item.attemptCount);
  if (retryLineage) {
    return retryLineage;
  }

  if (failureDetails?.message) {
    return failureDetails.message;
  }

  if (item.kind === "Iteration") {
    return item.iterationStatus && item.sequence !== null && item.sequence !== undefined
      ? describeIterationOutcomeFromLanding(item.sequence, item.iterationStatus, item.executionDuration)
      : "";
  }

  if (item.kind === "ActionRequest") {
    return item.actionStatus
      ? `The request was ${formatLandingActionStatus(item.actionStatus).toLowerCase()}.`
      : "";
  }

  return "";
}

function createTimelineBadgeFromLanding(item: WorkWorkerOverviewTimelineItem) {
  return formatWorkerTimelineBadgeLabel(item);
}

export function formatWorkerTimelineBadgeLabel(
  item: Pick<WorkWorkerOverviewTimelineItem, "actionStatus" | "iterationStatus" | "kind" | "state">
) {
  if (item.kind === "Iteration" && item.iterationStatus) {
    return formatWorkerTimelineBadgeText(formatIterationTimelineStatus(item.iterationStatus), "State");
  }

  if (item.kind === "ActionRequest") {
    return formatWorkerTimelineBadgeText(formatLandingActionStatus(item.actionStatus), "Requested");
  }

  return item.state
    ? formatWorkerTimelineBadgeText(formatLandingWorkerState(item.state), "State")
    : "State";
}

function formatWorkerTimelineBadgeText(value?: string | null, fallback = "State") {
  const label = value?.trim() || fallback;
  return label.charAt(0).toUpperCase() + label.slice(1);
}

function createTimelineFactsFromLanding() {
  return [];
}

function describeIterationOutcomeFromLanding(
  sequence: number,
  status: WorkCompletionStatus,
  executionDuration?: string | null
) {
  const duration = executionDuration
    ? formatDurationLabel(executionDuration)
    : "0.00s";

  switch (status) {
    case "Completed":
      return `Iteration #${sequence} finished successfully after ${duration}.`;
    case "Failed":
      return `Iteration #${sequence} ended in failure after ${duration}.`;
    case "Canceled":
      return `Iteration #${sequence} was canceled after ${duration}.`;
    case "Interrupted":
      return `Iteration #${sequence} was interrupted after ${duration}.`;
    case "Paused":
      return `Iteration #${sequence} paused after ${duration}.`;
    case "Executing":
      return `Iteration #${sequence} is still running.`;
    default:
      return `Iteration #${sequence} changed to ${status.toLowerCase()} after ${duration}.`;
  }
}

function createRetryLineageDescription(
  sequence?: number | null,
  attemptCount?: number | null
) {
  const originSequence = getRetryOriginIterationSequence(sequence ?? undefined, attemptCount);
  if (originSequence === null ||
    attemptCount === null ||
    attemptCount === undefined ||
    attemptCount <= 1) {
    return "";
  }

  return `Retry #${attemptCount - 1} of iteration #${originSequence}.`;
}

function formatLandingActionStatus(status?: string | null) {
  return status?.trim() || "Requested";
}

function formatLandingWorkerState(state: WorkerState) {
  return state.toLowerCase();
}

export function isFinalIterationStatus(status: WorkCompletionStatus) {
  return status === "Completed" ||
    status === "Failed" ||
    status === "Interrupted" ||
    status === "Canceled" ||
    status === "Invalid" ||
    status === "NotFound";
}

export function createDefaultWorkerHiddenPanels(showConfiguration = false) {
  return showConfiguration
    ? new Set<WorkerDetailPanelId>()
    : new Set<WorkerDetailPanelId>(["workerConfiguration"]);
}

export function createWorkerFocusedHiddenPanels(focusedPanelId: WorkerFocusedPanelId) {
  return new Set<WorkerDetailPanelId>(
    ["workerConfiguration", "workerDuration", "workerLogs", "workerTimeline"]
      .filter((panelId): panelId is WorkerDetailPanelId => panelId !== focusedPanelId)
  );
}

export function createIterationFocusedHiddenPanels(focusedPanelId: IterationFocusedPanelId) {
  return new Set<IterationDetailPanelId>(
    ["iterationSummary", "iterationMessages", "iterationOutput", "iterationLogs"]
      .filter((panelId): panelId is IterationDetailPanelId => panelId !== focusedPanelId)
  );
}

function applyWorkerOverviewRealtimeState(
  landing: WorkWorkerOverviewComponent | null,
  worker: WorkWorkerOverviewWorker | null,
  latestIteration: WorkWorkerOverviewLatestIteration | null,
  logSummary: WorkWorkerOverviewLogSummary | null,
  timelineSummary: WorkWorkerOverviewTimelineSummary | null,
  recentIterations: WorkWorkerOverviewRecentIteration[]
) {
  if (!landing) {
    return null;
  }

  return {
    ...landing,
    worker: worker ?? landing.worker,
    latestIteration: latestIteration ?? landing.latestIteration,
    recentIterations: mergeWorkerOverviewRecentIterations(landing.recentIterations, recentIterations),
    logs: {
      ...landing.logs,
      summary: logSummary ?? landing.logs.summary,
    },
    timeline: {
      ...landing.timeline,
      summary: timelineSummary ?? landing.timeline.summary,
    },
  } satisfies WorkWorkerOverviewComponent;
}

export function mergeWorkerOverviewRecentIterations(
  baseItems: readonly WorkWorkerOverviewRecentIteration[],
  nextItems: readonly WorkWorkerOverviewRecentIteration[]
) {
  if (nextItems.length === 0) {
    return [...baseItems];
  }

  const bySequence = new Map<number, WorkWorkerOverviewRecentIteration>();
  for (const item of [...baseItems, ...nextItems]) {
    const current = bySequence.get(item.sequence);
    bySequence.set(item.sequence, current ? getPreferredWorkerOverviewRecentIteration(current, item) : item);
  }

  return [...bySequence.values()]
    .sort((left, right) => right.sequence - left.sequence)
    .slice(0, 25);
}

export function getPreferredWorkerOverviewRecentIteration(
  left: WorkWorkerOverviewRecentIteration,
  right: WorkWorkerOverviewRecentIteration
) {
  const leftTime = parseTimelineTimestamp(left.completedAt ?? left.startedAt);
  const rightTime = parseTimelineTimestamp(right.completedAt ?? right.startedAt);
  if (leftTime !== rightTime) {
    return rightTime > leftTime ? right : left;
  }

  const leftFinal = isFinalIterationStatus(left.status);
  const rightFinal = isFinalIterationStatus(right.status);
  if (leftFinal !== rightFinal) {
    return rightFinal ? right : left;
  }

  return right;
}

export function mergeWorkerOverviewRealtimeEntries<T extends { id: string }>(
  current: readonly T[],
  next: readonly T[],
  sortDirection: WorkerSortDirection
) {
  if (next.length === 0) {
    return [...current];
  }

  const preferredById = new Map<string, T>();
  for (const item of current) {
    preferredById.set(item.id, item);
  }

  for (const item of next) {
    preferredById.set(item.id, item);
  }

  const orderedItems = sortDirection === "desc"
    ? [...next, ...current]
    : [...current, ...next];
  const seen = new Set<string>();
  const merged: T[] = [];

  for (const item of orderedItems) {
    if (seen.has(item.id)) {
      continue;
    }

    seen.add(item.id);
    const preferred = preferredById.get(item.id);
    if (preferred) {
      merged.push(preferred);
    }
  }

  return merged;
}

export function retainLatestWorkerTimelineRealtimeItems(
  items: readonly WorkWorkerOverviewTimelineItem[],
  sortDirection: WorkerSortDirection,
  maximum = workerTimelineRetainedRealtimeItemCount
) {
  const retainedCount = Math.max(0, maximum);
  if (items.length <= retainedCount) {
    return [...items];
  }

  const latestItems = sortWorkerOverviewTimelineItems(items, "desc").slice(0, retainedCount);
  return sortWorkerOverviewTimelineItems(latestItems, sortDirection);
}

export function sortWorkerOverviewTimelineItems(
  items: readonly WorkWorkerOverviewTimelineItem[],
  sortDirection: WorkerSortDirection
) {
  return [...items].sort((left, right) => {
    const comparison = compareWorkerOverviewTimelineItems(left, right);
    return sortDirection === "asc" ? comparison : -comparison;
  });
}

export function compareWorkerOverviewTimelineItems(
  left: WorkWorkerOverviewTimelineItem,
  right: WorkWorkerOverviewTimelineItem
) {
  const timestampDifference = parseTimelineTimestamp(left.at) - parseTimelineTimestamp(right.at);
  if (timestampDifference !== 0) {
    return timestampDifference;
  }

  const leftSequence = left.sequence ?? Number.MIN_SAFE_INTEGER;
  const rightSequence = right.sequence ?? Number.MIN_SAFE_INTEGER;
  const sequenceDifference = leftSequence - rightSequence;
  if (sequenceDifference !== 0) {
    return sequenceDifference;
  }

  const sortOrderDifference = createTimelineSortOrderFromLanding(left.kind) -
    createTimelineSortOrderFromLanding(right.kind);
  if (sortOrderDifference !== 0) {
    return sortOrderDifference;
  }

  return left.id.localeCompare(right.id);
}

export function filterWorkerOverviewLogEntriesBySelectedLevels(
  entries: readonly WorkWorkerOverviewLogEntry[],
  selectedLevels: readonly WorkerLogFilterLevel[] | null
) {
  if (!selectedLevels || selectedLevels.length === 0) {
    return [...entries];
  }

  const allowedLevels = new Set(selectedLevels);
  return entries.filter((entry) => allowedLevels.has(normalizeLogLevel(entry.level) as WorkerLogFilterLevel));
}

export function sortWorkerOverviewLogEntries(
  entries: readonly WorkWorkerOverviewLogEntry[],
  sortDirection: WorkerSortDirection
) {
  return [...entries].sort((left, right) => {
    const comparison = compareLogLikeEntries(left, right);
    return sortDirection === "asc" ? comparison : -comparison;
  });
}

export function capWorkerLogEntries<T extends { id: string; occurredAt: string }>(
  entries: readonly T[],
  maximum: number
) {
  if (entries.length <= maximum) {
    return [...entries];
  }

  return [...entries]
    .sort((left, right) => -compareLogLikeEntries(left, right))
    .slice(0, maximum);
}

export function mergeWorkerOverviewItemsById<T extends { id: string }>(
  baseItems: readonly T[],
  extraItems: readonly T[]
) {
  if (baseItems.length === 0) {
    return [...extraItems];
  }

  if (extraItems.length === 0) {
    return [...baseItems];
  }

  const seen = new Set<string>();
  const merged: T[] = [];

  for (const item of [...baseItems, ...extraItems]) {
    if (seen.has(item.id)) {
      continue;
    }

    seen.add(item.id);
    merged.push(item);
  }

  return merged;
}

export function normalizeVisibleWorkerTimelineItems(
  items: readonly WorkWorkerOverviewTimelineItem[],
  workerState: WorkerState | null
) {
  if (shouldRetainVisibleWorkerWaitingTile(items, workerState)) {
    return [...items];
  }

  return items.filter((item) => item.id !== "live-state:waiting");
}

export function shouldRetainVisibleWorkerWaitingTile(
  items: readonly WorkWorkerOverviewTimelineItem[],
  workerState: WorkerState | null
) {
  return workerState === "Waiting" &&
    items.every((item) => item.kind !== "Iteration" || item.iterationStatus !== "Executing");
}

export function getWorkerTimelineWaitingPriority(item: WorkerTimelineItem) {
  return item.id === "live-state:waiting" ? 1 : 0;
}

export function summarizeWorkerLogEntries(entries: WorkerLogEntry[]) {
  return entries.reduce(
    (summary, entry) => {
      summary.total += 1;

      switch (normalizeLogLevel(entry.level)) {
        case "Critical":
          summary.critical += 1;
          summary.errors += 1;
          break;
        case "Error":
          summary.error += 1;
          summary.errors += 1;
          break;
        case "Warning":
          summary.warning += 1;
          summary.warnings += 1;
          break;
        case "Information":
          summary.information += 1;
          break;
        case "Debug":
          summary.debug += 1;
          break;
        case "Trace":
          summary.trace += 1;
          break;
      }

      return summary;
    },
    {
      critical: 0,
      debug: 0,
      error: 0,
      errors: 0,
      information: 0,
      trace: 0,
      total: 0,
      warning: 0,
      warnings: 0,
    }
  );
}

export function sortWorkerLogEntries(entries: readonly WorkerLogEntry[], sortDirection: WorkerSortDirection) {
  return [...entries].sort((left, right) => {
    const comparison = compareWorkerLogEntries(left, right);
    return sortDirection === "desc" ? -comparison : comparison;
  });
}

export function compareWorkerLogEntries(left: WorkerLogEntry, right: WorkerLogEntry) {
  return compareLogLikeEntries(left, right);
}

export function compareLogLikeEntries<T extends { id: string; occurredAt: string; sequence?: number | null; ordinal?: number | null }>(left: T, right: T) {
  const timestampDifference = Date.parse(left.occurredAt) - Date.parse(right.occurredAt);
  if (timestampDifference !== 0) {
    return timestampDifference;
  }

  const leftSequence = left.sequence ?? Number.MIN_SAFE_INTEGER;
  const rightSequence = right.sequence ?? Number.MIN_SAFE_INTEGER;
  const sequenceDifference = leftSequence - rightSequence;
  if (sequenceDifference !== 0) {
    return sequenceDifference;
  }

  const leftOrdinal = left.ordinal ?? Number.MIN_SAFE_INTEGER;
  const rightOrdinal = right.ordinal ?? Number.MIN_SAFE_INTEGER;
  const ordinalDifference = leftOrdinal - rightOrdinal;
  if (ordinalDifference !== 0) {
    return ordinalDifference;
  }

  return left.id.localeCompare(right.id);
}

function isNearWorkerLogScrollBottom(element: HTMLElement) {
  return element.scrollHeight - element.clientHeight - element.scrollTop <= 96;
}

function getWorkerCreatedByLabel(worker: WorkerSnapshot) {
  return formatActionTimelineActorLabel(worker.origin) ??
    formatActionTimelineSourceLabel(worker.origin?.channel);
}

export function splitCategoryPath(category?: string | null) {
  const segments = splitCatalogPath(category);
  return segments.length > 0 ? segments : ["General"];
}

export function startsWithCategoryPath(categorySegments: string[], pathSegments: string[]) {
  return pathSegments.every((segment, index) =>
    segment.localeCompare(categorySegments[index] ?? "", undefined, {
      sensitivity: "accent",
    }) === 0
  );
}

export function definitionMatchesCatalogScope(
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
            errorCause: undefined,
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
          errorCause: undefined,
          loading: current.data === undefined,
          refreshing: current.data !== undefined,
        }));
      }
    });

    const requestConnection = { apiUrl, systemName };
    workableFetch<T>(requestConnection, path)
      .then((data) => {
        if (!canceled) {
          setState({ data, errorCause: undefined, loading: false, refreshing: false });
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
                  errorCause: error,
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

