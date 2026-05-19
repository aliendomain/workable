"use client";

import Image from "next/image";
import {
  Activity,
  Bell,
  Boxes,
  ChevronLeft,
  ChevronRight,
  CircleAlert,
  Clock3,
  FileCode2,
  FileJson,
  Folder,
  Home,
  Info,
  Loader2,
  Maximize2,
  Minimize2,
  Plus,
  RefreshCw,
  RotateCcw,
  Rows2,
  Rows4,
  Settings,
  Wrench,
  Workflow,
  X,
} from "lucide-react";
import { Fragment, type KeyboardEvent, type PointerEvent, useCallback, useEffect, useMemo, useRef, useState } from "react";
import { createPortal } from "react-dom";
import { Button } from "@/components/ui/button";
import {
  Popover,
  PopoverContent,
  PopoverTrigger,
} from "@/components/ui/popover";
import {
  Sidebar,
  SidebarContent,
  SidebarFooter,
  SidebarGroup,
  SidebarGroupAction,
  SidebarGroupContent,
  SidebarGroupLabel,
  SidebarHeader,
  SidebarInset,
  SidebarProvider,
} from "@/components/ui/sidebar";
import {
  Tooltip,
  TooltipContent,
  TooltipTrigger,
} from "@/components/ui/tooltip";
import {
  IterationsView,
  WorkersView,
} from "@/components/workable/console/query-screens";
import {
  OverviewView,
  JsonValue,
  type RealtimeEventMessage,
  useWorkableRealtimeView,
  useWorkableRealtimeEvents,
  type RealtimeViewLoadable,
} from "@/components/workable/console/overview-screen";
import {
  DefinitionView,
  DefinitionsView,
  WorkerConsoleView,
} from "@/components/workable/console/detail-screens";
import {
  OverviewCatalogFilter,
  QueryFilterPopover,
  ViewActionLane,
} from "@/components/workable/console/filters";
import { ErrorPanel } from "@/components/workable/console/feedback-panel";
import {
  ConsoleNavigationHeader,
  DelayedLoadingOverlay,
  DeleteTargetDialog,
  EmptyServerState,
  ServerDialog,
  ServerTree,
  StopSystemDialog,
} from "@/components/workable/console/navigation";
import {
  DEFAULT_WORKABLE_API_URL,
  WorkableApiError,
  workableFetch,
  type WorkCompletionStatus,
  type WorkComponentQueryResult,
  type WorkComponentShape,
  type WorkConcurrencyDiagnosticsCompactComponent,
  type WorkConcurrencyDiagnosticsDetailedComponent,
  type WorkDefinition,
  type WorkDurabilityDiagnosticsCompactComponent,
  type WorkDurabilityDiagnosticsDetailedComponent,
  type WorkIdempotencyDiagnosticsCompactComponent,
  type WorkIdempotencyDiagnosticsDetailedComponent,
  type WorkKeyKind,
  type WorkQueueDiagnosticsCompactComponent,
  type WorkableRealtimeEvent,
  type WorkableRealtimeEventKeyCriteria,
  type WorkableRealtimeEventCriteria,
  type WorkReadModelDiagnosticsCompactComponent,
  type WorkReadModelDiagnosticsDetailedComponent,
  type WorkRetentionDiagnosticsCompactComponent,
  type WorkRetentionDiagnosticsDetailedComponent,
  type WorkSystemConcurrencyDiagnostics,
  type WorkSystemDiagnosticsCompactComponent,
  type WorkSystemDurabilityDiagnostics,
  type WorkSystemIdempotencyDiagnostics,
  type WorkSystemReadModelDiagnostics,
  type WorkSystemRetentionDiagnostics,
  type WorkSystemLifecycleResult,
  type WorkableConnection,
  type WorkerState,
} from "@/lib/workable";

const STORAGE_KEY = "workable-console.state.v1";
const LEGACY_CONNECTION_STORAGE_KEY = "workable-console.connection";

type View = "overview" | "definitions" | "definition" | "workers" | "iterations" | "worker";
type ServerView = Exclude<View, "worker">;
const throughputSeriesIds = ["started", "completed", "failed", "canceled"] as const;
type ThroughputSeriesId = (typeof throughputSeriesIds)[number];

const overviewPanelIds = [
  "workers",
  "failedWorkers",
  "throughput",
  "iterations",
  "failedIterations",
  "completedIterations",
] as const;
type OverviewPanelId = (typeof overviewPanelIds)[number];
type OverviewPanelShapeMap = Record<OverviewPanelId, WorkComponentShape>;

const overviewPanelShapeCapabilities: Record<OverviewPanelId, {
  defaultShape: WorkComponentShape;
  supportedShapes: WorkComponentShape[];
}> = {
  completedIterations: {
    defaultShape: "standard",
    supportedShapes: ["standard", "detailed"],
  },
  failedIterations: {
    defaultShape: "standard",
    supportedShapes: ["standard", "detailed"],
  },
  failedWorkers: {
    defaultShape: "detailed",
    supportedShapes: ["standard", "detailed"],
  },
  iterations: {
    defaultShape: "standard",
    supportedShapes: ["compact", "standard"],
  },
  throughput: {
    defaultShape: "standard",
    supportedShapes: ["compact", "standard"],
  },
  workers: {
    defaultShape: "standard",
    supportedShapes: ["compact", "standard"],
  },
};
type WorkableHostConnection = {
  id: string;
  name: string;
  apiUrl: string;
  systems: WorkableSystemConnection[];
};

type WorkableSystemConnection = {
  id: string;
  hostId: string;
  name: string;
  systemName?: string;
  realtimeEnabled: boolean;
  realtimeHubPath?: string | null;
  realtimeSupported?: boolean;
  realtimeTransport?: string | null;
  state?: string | null;
};

type LegacyWorkableServerConnection = WorkableSystemConnection & {
  apiUrl?: string;
};

type ConsoleStorage = {
  activeSystemId: string;
  expandedHostIds: string[];
  expandedSystemIds: string[];
  hosts: WorkableHostConnection[];
  overviewHiddenPanels: OverviewPanelId[];
  overviewPanelShapes: OverviewPanelShapeMap;
  overviewHiddenThroughputSeries: ThroughputSeriesId[];
  overviewThroughputHidden: boolean;
  view: ServerView;
};

type PendingDelete =
  | { kind: "host"; host: WorkableHostConnection }
  | { kind: "system"; host: WorkableHostConnection; system: WorkableSystemConnection };

type PendingStopSystem = {
  system: WorkableSystemConnection;
};

type OverviewScope = {
  category?: string;
  definitionName?: string;
  includeSubcategories?: boolean;
};

type NavigationEntry = {
  catalogScope: OverviewScope | null;
  iterationCategoryFilter: string;
  iterationDefinitionFilter: string;
  iterationKeyTypeFilter: string;
  iterationStatusFilter: WorkCompletionStatus[];
  overviewScope: OverviewScope | null;
  workerCategoryFilter: string;
  workerDefinitionFilter: string;
  keyTypeFilter: string;
  systemId: string;
  view: View;
  definitionId: string | null;
  workerId: string | null;
  workerStateFilter: WorkerState[];
};

const states: WorkerState[] = [
  "Queued",
  "Running",
  "Waiting",
  "Retrying",
  "Pausing",
  "Paused",
  "Interrupting",
  "Interrupted",
  "Canceling",
  "Failed",
  "Canceled",
  "Completed",
];

const iterationStatuses: WorkCompletionStatus[] = ["Executing", "Completed", "Failed", "Interrupted", "Canceled", "Paused"];

const navItems: Array<{ id: ServerView; label: string; icon: typeof Activity }> = [
  { id: "overview", label: "Overview", icon: Activity },
  { id: "definitions", label: "Catalog", icon: Boxes },
  { id: "workers", label: "Workers", icon: Workflow },
  { id: "iterations", label: "Iterations", icon: Clock3 },
];

const initialRefreshTokens: Record<View, number> = {
  overview: 0,
  definitions: 0,
  definition: 0,
  workers: 0,
  iterations: 0,
  worker: 0,
};
const viewContentOffsetClass = "pt-2";
const readModelLagWarningThreshold = 100;
const concurrencyLagWarningSeconds = 30;
const durabilityAcceptedWorkerWarningSeconds = 30;
const durabilityCleanupWarningSeconds = 30;
const eventViewerEventTypes = [
  "worker.queued",
  "worker.started",
  "worker.completed",
  "worker.failed",
  "worker.canceled",
  "worker.cancel",
  "worker.pause",
  "worker.start",
  "worker.push",
  "worker.waiting",
  "worker.retrying",
  "worker.iteration.completed",
  "worker.iteration.failed",
  "worker.recurrence.circuit_opened",
  "worker.reconfigured",
  "worker.purge",
  "worker.log",
] as const;

export function WorkableConsole() {
  const initialConsoleState = useMemo(() => createDefaultConsoleStorage(), []);
  const [hasMounted, setHasMounted] = useState(false);
  const [consoleState, setConsoleState] = useState<ConsoleStorage>(initialConsoleState);
  const [view, setView] = useState<View>(initialConsoleState.view);
  const [visibleView, setVisibleView] = useState<View>(consoleState.view);
  const [mountedViews, setMountedViews] = useState<Set<View>>(
    () => new Set([initialConsoleState.view])
  );
  const [pendingView, setPendingView] = useState<View | null>(null);
  const [serverDialog, setServerDialog] = useState<{
    mode: "add" | "edit";
    host?: WorkableHostConnection;
  } | null>(null);
  const [pendingDelete, setPendingDelete] = useState<PendingDelete | null>(null);
  const [pendingStopSystem, setPendingStopSystem] = useState<PendingStopSystem | null>(null);
  const [lifecycleActionSystemId, setLifecycleActionSystemId] = useState<string | null>(null);
  const [lifecycleError, setLifecycleError] = useState<string>();
  const [systemNotificationOpen, setSystemNotificationOpen] = useState(false);
  const [acknowledgedRejectedWorkCounts, setAcknowledgedRejectedWorkCounts] = useState<Record<string, number>>({});
  const [diagnosticsAlertsBySystemId, setDiagnosticsAlertsBySystemId] = useState<Record<string, DiagnosticsAlertSnapshot>>({});
  const [readModelDiagnosticsExpanded, setReadModelDiagnosticsExpanded] = useState(false);
  const [retentionDiagnosticsExpanded, setRetentionDiagnosticsExpanded] = useState(false);
  const [concurrencyDiagnosticsExpanded, setConcurrencyDiagnosticsExpanded] = useState(false);
  const [durabilityDiagnosticsExpanded, setDurabilityDiagnosticsExpanded] = useState(false);
  const [idempotencyDiagnosticsExpanded, setIdempotencyDiagnosticsExpanded] = useState(false);
  const [realtimePayloadCaptureEnabled, setRealtimePayloadCaptureEnabled] = useState(true);
  const [realtimePayloadMaxMessages, setRealtimePayloadMaxMessages] = useState(100);
  const [realtimePayloadOpen, setRealtimePayloadOpen] = useState(false);
  const [eventViewerCaptureEnabled, setEventViewerCaptureEnabled] = useState(true);
  const [eventViewerMaxMessages, setEventViewerMaxMessages] = useState(100);
  const [eventViewerOpen, setEventViewerOpen] = useState(false);
  const [eventViewerDefinitions, setEventViewerDefinitions] = useState<WorkDefinition[]>([]);
  const [eventViewerDefinitionsLoading, setEventViewerDefinitionsLoading] = useState(false);
  const [eventViewerDefinitionError, setEventViewerDefinitionError] = useState<string>();
  const [selectedEventViewerDefinitionIds, setSelectedEventViewerDefinitionIds] = useState<string[]>([]);
  const [selectedEventViewerEventTypes, setSelectedEventViewerEventTypes] = useState<string[]>([]);
  const [selectedEventViewerKeys, setSelectedEventViewerKeys] = useState<WorkableRealtimeEventKeyCriteria[]>([]);
  const [refreshTokens, setRefreshTokens] = useState<Record<View, number>>(initialRefreshTokens);
  const [selectedDefinitionId, setSelectedDefinitionId] = useState<string | null>(null);
  const [selectedWorkerId, setSelectedWorkerId] = useState<string | null>(null);
  const [workerCategoryFilter, setWorkerCategoryFilter] = useState("");
  const [workerDefinitionFilter, setWorkerDefinitionFilter] = useState("");
  const [keyTypeFilter, setKeyTypeFilter] = useState("");
  const [workerStateFilter, setWorkerStateFilter] = useState<WorkerState[]>([]);
  const [iterationCategoryFilter, setIterationCategoryFilter] = useState("");
  const [iterationDefinitionFilter, setIterationDefinitionFilter] = useState("");
  const [iterationKeyTypeFilter, setIterationKeyTypeFilter] = useState("");
  const [iterationStatusFilter, setIterationStatusFilter] = useState<WorkCompletionStatus[]>([]);
  const [catalogScopeBySystemId, setCatalogScopeBySystemId] = useState<
    Record<string, OverviewScope | undefined>
  >({});
  const [overviewScopeBySystemId, setOverviewScopeBySystemId] = useState<
    Record<string, OverviewScope | undefined>
  >({});
  const [navigationHistory, setNavigationHistory] = useState<NavigationEntry[]>([]);
  const viewScrollPositions = useRef<Partial<Record<ServerView, number>>>({});
  const readyViews = useRef<Set<string>>(new Set());
  const activeLocation = findSystemLocation(consoleState, consoleState.activeSystemId);
  const activeHost = activeLocation?.host;
  const activeSystem = activeLocation?.system;
  const activeSystemId = activeSystem?.id ?? "";
  const activeApiUrl = activeHost?.apiUrl;
  const activeSystemName = activeSystem?.systemName;
  const activeRealtimeEnabled = activeSystem?.realtimeEnabled ?? false;
  const activeRealtimeHubPath = activeSystem?.realtimeHubPath ?? null;
  const activeCatalogScope = activeSystem
    ? catalogScopeBySystemId[activeSystem.id] ?? null
    : null;
  const activeOverviewScope = activeSystem
    ? overviewScopeBySystemId[activeSystem.id] ?? null
    : null;
  const connection = useMemo<WorkableConnection | null>(
    () =>
      activeApiUrl
        ? {
            apiUrl: activeApiUrl,
            realtimeHubPath: activeRealtimeEnabled
              ? activeRealtimeHubPath
              : null,
            systemName: activeSystemName,
          }
        : null,
    [activeApiUrl, activeRealtimeEnabled, activeRealtimeHubPath, activeSystemName]
  );
  const diagnosticsAlertRequest = useMemo(
    () => ({
      components: [
        {
          id: "systemDiagnostics",
          options: {
            publishMode: "alertChanges",
          },
          shape: "compact",
          type: "systemDiagnostics",
        },
        {
          id: "queueDiagnostics",
          options: {
            publishMode: "alertChanges",
          },
          shape: "compact",
          type: "queueDiagnostics",
        },
        {
          id: "readModelDiagnostics",
          options: {
            publishMode: "alertChanges",
            warningThreshold: readModelLagWarningThreshold,
          },
          shape: "compact",
          type: "readModelDiagnostics",
        },
        {
          id: "retentionDiagnostics",
          options: {
            publishMode: "alertChanges",
            warningSeconds: 30,
          },
          shape: "compact",
          type: "retentionDiagnostics",
        },
        {
          id: "concurrencyDiagnostics",
          options: {
            publishMode: "alertChanges",
            warningSeconds: concurrencyLagWarningSeconds,
          },
          shape: "compact",
          type: "concurrencyDiagnostics",
        },
        {
          id: "durabilityDiagnostics",
          options: {
            publishMode: "alertChanges",
            acceptedWorkerWarningSeconds: durabilityAcceptedWorkerWarningSeconds,
            cleanupWarningSeconds: durabilityCleanupWarningSeconds,
          },
          shape: "compact",
          type: "durabilityDiagnostics",
        },
      ],
    }),
    []
  );
  const diagnosticsTrayRequest = useMemo(
    () => ({
      components: [
        {
          id: "systemDiagnostics",
          options: {
            publishMode: "continuous",
          },
          shape: "compact",
          type: "systemDiagnostics",
        },
        {
          id: "queueDiagnostics",
          options: {
            publishMode: "continuous",
          },
          shape: "compact",
          type: "queueDiagnostics",
        },
        {
          id: "readModelDiagnostics",
          options: {
            publishMode: "continuous",
            warningThreshold: readModelLagWarningThreshold,
          },
          shape: "compact",
          type: "readModelDiagnostics",
        },
        {
          id: "retentionDiagnostics",
          options: {
            publishMode: "continuous",
            warningSeconds: 30,
          },
          shape: "compact",
          type: "retentionDiagnostics",
        },
        {
          id: "concurrencyDiagnostics",
          options: {
            publishMode: "continuous",
            warningSeconds: concurrencyLagWarningSeconds,
          },
          shape: "compact",
          type: "concurrencyDiagnostics",
        },
        {
          id: "durabilityDiagnostics",
          options: {
            publishMode: "continuous",
            acceptedWorkerWarningSeconds: durabilityAcceptedWorkerWarningSeconds,
            cleanupWarningSeconds: durabilityCleanupWarningSeconds,
          },
          shape: "compact",
          type: "durabilityDiagnostics",
        },
        {
          id: "idempotencyDiagnostics",
          options: {
            publishMode: "continuous",
          },
          shape: "compact",
          type: "idempotencyDiagnostics",
        },
      ],
    }),
    []
  );
  const readModelDiagnosticsDetailRequest = useMemo(
    () => ({
      components: [
        {
          id: "readModelDiagnostics",
          options: {
            publishMode: "continuous",
            warningThreshold: readModelLagWarningThreshold,
          },
          shape: "detailed",
          type: "readModelDiagnostics",
        },
      ],
    }),
    []
  );
  const retentionDiagnosticsDetailRequest = useMemo(
    () => ({
      components: [
        {
          id: "retentionDiagnostics",
          options: {
            publishMode: "continuous",
            warningSeconds: 30,
          },
          shape: "detailed",
          type: "retentionDiagnostics",
        },
      ],
    }),
    []
  );
  const concurrencyDiagnosticsDetailRequest = useMemo(
    () => ({
      components: [
        {
          id: "concurrencyDiagnostics",
          options: {
            publishMode: "continuous",
            warningSeconds: concurrencyLagWarningSeconds,
          },
          shape: "detailed",
          type: "concurrencyDiagnostics",
        },
      ],
    }),
    []
  );
  const durabilityDiagnosticsDetailRequest = useMemo(
    () => ({
      components: [
        {
          id: "durabilityDiagnostics",
          options: {
            publishMode: "continuous",
            acceptedWorkerWarningSeconds: durabilityAcceptedWorkerWarningSeconds,
            cleanupWarningSeconds: durabilityCleanupWarningSeconds,
          },
          shape: "detailed",
          type: "durabilityDiagnostics",
        },
      ],
    }),
    []
  );
  const idempotencyDiagnosticsDetailRequest = useMemo(
    () => ({
      components: [
        {
          id: "idempotencyDiagnostics",
          options: {
            publishMode: "continuous",
          },
          shape: "detailed",
          type: "idempotencyDiagnostics",
        },
      ],
    }),
    []
  );
  const diagnosticsRealtimeEnabled = Boolean(connection?.realtimeHubPath);
  const diagnosticsAlertTargets = useMemo(
    () => createDiagnosticsAlertTargets(consoleState.hosts),
    [consoleState.hosts]
  );
  const diagnosticsAlertSources = useMemo<DiagnosticsAlertSource[]>(
    () => diagnosticsAlertTargets.map((target) => ({
      ...(diagnosticsAlertsBySystemId[target.systemId] ?? {
        connectionState: "connecting",
        enabled: true,
        loading: true,
      }),
      target,
    })),
    [diagnosticsAlertTargets, diagnosticsAlertsBySystemId]
  );
  const captureRealtimePayloads = realtimePayloadOpen && realtimePayloadCaptureEnabled;
  const diagnosticsTray = useWorkableRealtimeView<WorkComponentQueryResult>(
    connection,
    "diagnostics",
    diagnosticsTrayRequest,
    diagnosticsRealtimeEnabled && systemNotificationOpen,
    captureRealtimePayloads,
    realtimePayloadMaxMessages,
    "diagnostics:tray"
  );
  const readModelDiagnosticsDetail = useWorkableRealtimeView<WorkComponentQueryResult>(
    connection,
    "diagnostics",
    readModelDiagnosticsDetailRequest,
    diagnosticsRealtimeEnabled && systemNotificationOpen && readModelDiagnosticsExpanded,
    captureRealtimePayloads,
    realtimePayloadMaxMessages,
    "diagnostics:read-model"
  );
  const retentionDiagnosticsDetail = useWorkableRealtimeView<WorkComponentQueryResult>(
    connection,
    "diagnostics",
    retentionDiagnosticsDetailRequest,
    diagnosticsRealtimeEnabled && systemNotificationOpen && retentionDiagnosticsExpanded,
    captureRealtimePayloads,
    realtimePayloadMaxMessages,
    "diagnostics:retention"
  );
  const concurrencyDiagnosticsDetail = useWorkableRealtimeView<WorkComponentQueryResult>(
    connection,
    "diagnostics",
    concurrencyDiagnosticsDetailRequest,
    diagnosticsRealtimeEnabled && systemNotificationOpen && concurrencyDiagnosticsExpanded,
    captureRealtimePayloads,
    realtimePayloadMaxMessages,
    "diagnostics:concurrency"
  );
  const durabilityDiagnosticsDetail = useWorkableRealtimeView<WorkComponentQueryResult>(
    connection,
    "diagnostics",
    durabilityDiagnosticsDetailRequest,
    diagnosticsRealtimeEnabled && systemNotificationOpen && durabilityDiagnosticsExpanded,
    captureRealtimePayloads,
    realtimePayloadMaxMessages,
    "diagnostics:durability"
  );
  const idempotencyDiagnosticsDetail = useWorkableRealtimeView<WorkComponentQueryResult>(
    connection,
    "diagnostics",
    idempotencyDiagnosticsDetailRequest,
    diagnosticsRealtimeEnabled && systemNotificationOpen && idempotencyDiagnosticsExpanded,
    captureRealtimePayloads,
    realtimePayloadMaxMessages,
    "diagnostics:idempotency"
  );
  const diagnosticsRealtimeMessages = useMemo(
    () => [
      ...diagnosticsTray.messages,
      ...readModelDiagnosticsDetail.messages,
      ...retentionDiagnosticsDetail.messages,
      ...concurrencyDiagnosticsDetail.messages,
      ...durabilityDiagnosticsDetail.messages,
      ...idempotencyDiagnosticsDetail.messages,
    ],
    [
      diagnosticsTray.messages,
      readModelDiagnosticsDetail.messages,
      retentionDiagnosticsDetail.messages,
      concurrencyDiagnosticsDetail.messages,
      durabilityDiagnosticsDetail.messages,
      idempotencyDiagnosticsDetail.messages,
    ]
  );
  const eventViewerCriteria = useMemo<WorkableRealtimeEventCriteria>(
    () => ({
      definitionIds: selectedEventViewerDefinitionIds.length > 0
        ? selectedEventViewerDefinitionIds
        : null,
      eventTypes: selectedEventViewerEventTypes.length > 0
        ? selectedEventViewerEventTypes
        : null,
      keys: selectedEventViewerKeys.length > 0
        ? selectedEventViewerKeys
        : null,
    }),
    [selectedEventViewerDefinitionIds, selectedEventViewerEventTypes, selectedEventViewerKeys]
  );
  const realtimeEvents = useWorkableRealtimeEvents(
    connection,
    eventViewerCriteria,
    Boolean(connection?.realtimeHubPath) &&
      eventViewerOpen &&
      eventViewerCaptureEnabled &&
      selectedEventViewerEventTypes.length > 0,
    eventViewerMaxMessages
  );
  const toggleEventViewerEventType = useCallback((eventType: string) => {
    setSelectedEventViewerEventTypes((current) =>
      current.includes(eventType)
        ? current.filter((candidate) => candidate !== eventType)
        : [...current, eventType].sort((left, right) => left.localeCompare(right))
    );
  }, []);
  const toggleEventViewerDefinition = useCallback((definitionId: string) => {
    setSelectedEventViewerDefinitionIds((current) =>
      current.includes(definitionId)
        ? current.filter((candidate) => candidate !== definitionId)
        : [...current, definitionId].sort((left, right) => left.localeCompare(right))
    );
  }, []);
  const addEventViewerKey = useCallback((key: WorkableRealtimeEventKeyCriteria) => {
    setSelectedEventViewerKeys((current) => {
      const normalized = {
        kind: key.kind ?? null,
        type: key.type.trim(),
        value: key.value.trim(),
      };
      if (!normalized.type || !normalized.value) {
        return current;
      }

      if (current.some((candidate) =>
        (candidate.kind ?? null) === normalized.kind &&
        candidate.type === normalized.type &&
        candidate.value === normalized.value
      )) {
        return current;
      }

      return [...current, normalized].sort((left, right) =>
        `${left.kind ?? ""}:${left.type}:${left.value}`.localeCompare(`${right.kind ?? ""}:${right.type}:${right.value}`)
      );
    });
  }, []);
  const removeEventViewerKey = useCallback((key: WorkableRealtimeEventKeyCriteria) => {
    setSelectedEventViewerKeys((current) =>
      current.filter((candidate) =>
        !(
          (candidate.kind ?? null) === (key.kind ?? null) &&
          candidate.type === key.type &&
          candidate.value === key.value
        )
      )
    );
  }, []);
  const clearDiagnosticsTrayMessages = diagnosticsTray.clearMessages;
  const clearReadModelDiagnosticsDetailMessages = readModelDiagnosticsDetail.clearMessages;
  const clearRetentionDiagnosticsDetailMessages = retentionDiagnosticsDetail.clearMessages;
  const clearConcurrencyDiagnosticsDetailMessages = concurrencyDiagnosticsDetail.clearMessages;
  const clearDurabilityDiagnosticsDetailMessages = durabilityDiagnosticsDetail.clearMessages;
  const clearIdempotencyDiagnosticsDetailMessages = idempotencyDiagnosticsDetail.clearMessages;
  const clearDiagnosticsRealtimeMessages = useCallback(() => {
    clearDiagnosticsTrayMessages();
    clearReadModelDiagnosticsDetailMessages();
    clearRetentionDiagnosticsDetailMessages();
    clearConcurrencyDiagnosticsDetailMessages();
    clearDurabilityDiagnosticsDetailMessages();
    clearIdempotencyDiagnosticsDetailMessages();
  }, [
    clearDiagnosticsTrayMessages,
    clearReadModelDiagnosticsDetailMessages,
    clearRetentionDiagnosticsDetailMessages,
    clearConcurrencyDiagnosticsDetailMessages,
    clearDurabilityDiagnosticsDetailMessages,
    clearIdempotencyDiagnosticsDetailMessages,
  ]);
  const handleSystemNotificationOpenChange = useCallback((open: boolean) => {
    setSystemNotificationOpen(open);
    if (!open) {
      setReadModelDiagnosticsExpanded(false);
      setRetentionDiagnosticsExpanded(false);
      setConcurrencyDiagnosticsExpanded(false);
      setDurabilityDiagnosticsExpanded(false);
      setIdempotencyDiagnosticsExpanded(false);
    }
  }, []);
  const acknowledgeQueueRejections = useCallback((systemId: string, count: number) => {
    setAcknowledgedRejectedWorkCounts((current) => ({
      ...current,
      [systemId]: count,
    }));
  }, []);

  const updateSystemState = useCallback((systemId: string, state: string | null) => {
    setConsoleState((current) => {
      let changed = false;
      const hosts = current.hosts.map((host) => ({
        ...host,
        systems: host.systems.map((system) => {
          if (system.id !== systemId || system.state === state) {
            return system;
          }

          changed = true;
          return { ...system, state };
        }),
      }));

      return changed ? { ...current, hosts } : current;
    });
  }, []);

  const updateSystemStateFromDiagnosticsTarget = useCallback((
    target: DiagnosticsAlertTarget,
    state: string | null
  ) => {
    setConsoleState((current) => {
      let changed = false;
      const targetSystemName = target.systemName ?? "";
      const hosts = current.hosts.map((host) => ({
        ...host,
        systems: host.systems.map((system) => {
          const matchesTargetId = system.id === target.systemId;
          const matchesTargetScope =
            host.id === target.hostId &&
            (system.systemName ?? "") === targetSystemName;

          if ((!matchesTargetId && !matchesTargetScope) || system.state === state) {
            return system;
          }

          changed = true;
          return { ...system, state };
        }),
      }));

      return changed ? { ...current, hosts } : current;
    });
  }, []);

  const updateDiagnosticsAlertSnapshot = useCallback((
    systemId: string,
    snapshot: DiagnosticsAlertSnapshot | null
  ) => {
    const systemDiagnostics = getWorkComponentData<WorkSystemDiagnosticsCompactComponent>(
      snapshot?.data,
      "systemDiagnostics"
    );
    if (systemDiagnostics?.systemState) {
      const target = diagnosticsAlertTargets.find((candidate) => candidate.systemId === systemId);
      if (target) {
        updateSystemStateFromDiagnosticsTarget(target, systemDiagnostics.systemState);
      } else {
        updateSystemState(systemId, systemDiagnostics.systemState);
      }
    }

    setDiagnosticsAlertsBySystemId((current) => {
      if (!snapshot) {
        if (!(systemId in current)) {
          return current;
        }

        const next = { ...current };
        delete next[systemId];
        return next;
      }

      if (diagnosticsAlertSnapshotsEqual(current[systemId], snapshot)) {
        return current;
      }

      return {
        ...current,
        [systemId]: snapshot,
      };
    });
  }, [diagnosticsAlertTargets, updateSystemState, updateSystemStateFromDiagnosticsTarget]);

  useEffect(() => {
    queueMicrotask(() => {
      const loaded = loadConsoleStorage();
      setConsoleState(loaded);
      setView(loaded.view);
      setVisibleView(loaded.view);
      setMountedViews(new Set([loaded.view]));
      setPendingView(null);
      setHasMounted(true);
    });
  }, []);

  useEffect(() => {
    if (!hasMounted) {
      return;
    }

    if (typeof window !== "undefined") {
      window.localStorage.setItem(STORAGE_KEY, JSON.stringify(consoleState));
    }
  }, [consoleState, hasMounted]);

  useEffect(() => {
    if (!connection || !eventViewerOpen) {
      return;
    }

    let canceled = false;
    queueMicrotask(() => {
      if (!canceled) {
        setEventViewerDefinitionsLoading(true);
        setEventViewerDefinitionError(undefined);
      }
    });
    workableFetch<WorkDefinition[]>(connection, "definitions")
      .then((definitions) => {
        if (!canceled) {
          setEventViewerDefinitions(definitions);
          setEventViewerDefinitionsLoading(false);
        }
      })
      .catch((error) => {
        if (!canceled) {
          setEventViewerDefinitionError(
            error instanceof Error ? error.message : "Definitions could not be loaded."
          );
          setEventViewerDefinitionsLoading(false);
        }
      });

    return () => {
      canceled = true;
    };
  }, [connection, eventViewerOpen]);

  const currentNavigation = useCallback(
    (): NavigationEntry => ({
      catalogScope: cloneOverviewScope(
        catalogScopeBySystemId[consoleState.activeSystemId] ?? null
      ),
      definitionId: selectedDefinitionId,
      iterationCategoryFilter,
      iterationDefinitionFilter,
      iterationKeyTypeFilter,
      iterationStatusFilter,
      keyTypeFilter,
      overviewScope: cloneOverviewScope(
        overviewScopeBySystemId[consoleState.activeSystemId] ?? null
      ),
      systemId: consoleState.activeSystemId,
      view,
      workerCategoryFilter,
      workerDefinitionFilter,
      workerId: selectedWorkerId,
      workerStateFilter,
    }),
    [
      consoleState.activeSystemId,
      catalogScopeBySystemId,
      iterationCategoryFilter,
      iterationDefinitionFilter,
      iterationKeyTypeFilter,
      iterationStatusFilter,
      keyTypeFilter,
      overviewScopeBySystemId,
      selectedDefinitionId,
      selectedWorkerId,
      workerCategoryFilter,
      view,
      workerDefinitionFilter,
      workerStateFilter,
    ]
  );

  const pushCurrentNavigation = useCallback(() => {
    const entry = currentNavigation();
    setNavigationHistory((current) =>
      navigationEntriesEqual(current.at(-1), entry)
        ? current
        : [...current, entry].slice(-20)
    );
  }, [currentNavigation]);

  const refreshView = useCallback((targetView: View) => {
    setRefreshTokens((current) => ({
      ...current,
      [targetView]: current[targetView] + 1,
    }));
  }, []);

  const setSystemOverviewScope = useCallback((
    systemId: string,
    scope: OverviewScope | null
  ) => {
    setOverviewScopeBySystemId((current) => {
      const normalizedScope = normalizeOverviewScope(scope);
      const next = { ...current };
      if (normalizedScope) {
        next[systemId] = normalizedScope;
      } else {
        delete next[systemId];
      }

      return next;
    });
  }, []);

  const setSystemCatalogScope = useCallback((
    systemId: string,
    scope: OverviewScope | null
  ) => {
    setCatalogScopeBySystemId((current) => {
      const normalizedScope = normalizeOverviewScope(scope);
      const next = { ...current };
      if (normalizedScope) {
        next[systemId] = normalizedScope;
      } else {
        delete next[systemId];
      }

      return next;
    });
  }, []);

  const executeSystemLifecycleAction = async (
    system: WorkableSystemConnection,
    action: "start" | "stop"
  ) => {
    const location = findSystemLocation(consoleState, system.id);
    if (!location) {
      return;
    }

    const targetConnection: WorkableConnection = {
      apiUrl: location.host.apiUrl,
      systemName: location.system.systemName,
    };
    setLifecycleActionSystemId(system.id);
    setLifecycleError(undefined);
    try {
      const result = await workableFetch<WorkSystemLifecycleResult>(
        targetConnection,
        `lifecycle/${action}`,
        { method: "POST" }
      );
      updateSystemState(system.id, result.state);
      if (system.id === activeSystem?.id) {
        refreshView("overview");
      }
    } catch (error) {
      if (!(error instanceof WorkableApiError)) {
        updateSystemState(system.id, null);
      }
      setLifecycleError(
        error instanceof Error ? error.message : `Unable to ${action} Workable system.`
      );
    } finally {
      setLifecycleActionSystemId(null);
    }
  };

  const setOverviewPanelVisible = useCallback((
    panelId: OverviewPanelId,
    visible: boolean
  ) => {
    setConsoleState((current) => {
      const panels = new Set(current.overviewHiddenPanels ?? []);
      if (visible) {
        panels.delete(panelId);
      } else {
        panels.add(panelId);
      }

      return {
        ...current,
        overviewHiddenPanels: overviewPanelIds.filter((id) => panels.has(id)),
        overviewThroughputHidden: panelId === "throughput"
          ? !visible
          : current.overviewThroughputHidden,
      };
    });
  }, []);

  const setOverviewPanelShape = useCallback((
    panelId: OverviewPanelId,
    shape: WorkComponentShape
  ) => {
    setConsoleState((current) => {
      return {
        ...current,
        overviewPanelShapes: normalizeOverviewPanelShapes({
          ...current.overviewPanelShapes,
          [panelId]: shape,
        }),
      };
    });
  }, []);

  const toggleOverviewThroughputSeries = useCallback((seriesId: ThroughputSeriesId) => {
    setConsoleState((current) => {
      const hidden = new Set(current.overviewHiddenThroughputSeries);
      const isHidden = hidden.has(seriesId);
      if (isHidden) {
        hidden.delete(seriesId);
      } else {
        const visibleCount = throughputSeriesIds.filter((id) => !hidden.has(id)).length;
        if (visibleCount <= 1) {
          return current;
        }

        hidden.add(seriesId);
      }

      return {
        ...current,
        overviewHiddenThroughputSeries: normalizeThroughputSeriesIds([...hidden]),
      };
    });
  }, []);

  const resetOverviewUiToDefaults = useCallback(() => {
    setConsoleState((current) => ({
      ...current,
      overviewHiddenPanels: [],
      overviewHiddenThroughputSeries: [],
      overviewPanelShapes: createDefaultOverviewPanelShapes(),
      overviewThroughputHidden: false,
    }));
  }, []);

  const rememberCurrentViewScroll = useCallback(() => {
    if (visibleView !== "worker") {
      viewScrollPositions.current[visibleView] = getWindowScrollTop();
    }
  }, [visibleView]);

  const openWorker = (workerId: string, trackHistory = true) => {
    rememberCurrentViewScroll();
    if (trackHistory) {
      pushCurrentNavigation();
    }
    setSelectedDefinitionId(null);
    setSelectedWorkerId(workerId);
    setVisibleView("worker");
    setPendingView(null);
    setView("worker");
    refreshView("worker");
  };

  const openDefinition = (definitionId: string, systemId = activeSystem?.id ?? "") => {
    rememberCurrentViewScroll();
    pushCurrentNavigation();
    setSelectedWorkerId(null);
    setSelectedDefinitionId(definitionId);
    const isSystemChange = systemId !== activeSystem?.id;
    setConsoleState((current) => ({
      ...current,
      activeSystemId: systemId,
      view: "definition",
    }));
    setMountedViews((current) =>
      isSystemChange ? new Set(["definition"]) : new Set([...current, "definition"])
    );
    setVisibleView("definition");
    setPendingView(null);
    setView("definition");
    refreshView("definition");
  };

  const openView = (
    nextView: View,
    systemId = activeSystem?.id ?? "",
    trackHistory = true
  ) => {
    rememberCurrentViewScroll();
    if (
      trackHistory &&
      !navigationEntriesEqual(currentNavigation(), {
        systemId,
        view: nextView,
        definitionId: nextView === "definition" ? selectedDefinitionId : null,
        workerId: null,
        catalogScope: cloneOverviewScope(catalogScopeBySystemId[systemId] ?? null),
        iterationCategoryFilter,
        iterationDefinitionFilter,
        iterationKeyTypeFilter,
        iterationStatusFilter,
        keyTypeFilter,
        overviewScope: cloneOverviewScope(overviewScopeBySystemId[systemId] ?? null),
        workerCategoryFilter,
        workerDefinitionFilter,
        workerStateFilter,
      })
    ) {
      pushCurrentNavigation();
    }

    if (nextView !== "worker") {
      setSelectedWorkerId(null);
      if (nextView !== "definition") {
        setSelectedDefinitionId(null);
      }
      setConsoleState((current) => ({
        ...current,
        activeSystemId: systemId,
        view: nextView,
      }));

      const isSystemChange = systemId !== activeSystem?.id;
      const nextKey = getViewReadinessKey(systemId, nextView);
      setMountedViews((current) =>
        isSystemChange ? new Set([nextView]) : new Set([...current, nextView])
      );
      if (readyViews.current.has(nextKey)) {
        setVisibleView(nextView);
        setPendingView(null);
      } else {
        if (isSystemChange) {
          setVisibleView(nextView);
        }
        setPendingView(nextView);
      }
    }
    setView(nextView);
    refreshView(nextView);
  };

  const applyWorkerOverviewScope = (scope: OverviewScope | null) => {
    const normalizedScope = normalizeOverviewScope(scope);
    setWorkerCategoryFilter(normalizedScope?.category ?? "");
    setWorkerDefinitionFilter(normalizedScope?.definitionName ?? "");
  };

  const applyIterationOverviewScope = (scope: OverviewScope | null) => {
    const normalizedScope = normalizeOverviewScope(scope);
    setIterationCategoryFilter(normalizedScope?.category ?? "");
    setIterationDefinitionFilter(normalizedScope?.definitionName ?? "");
  };

  const openWorkersFromOverview = (states: WorkerState[] = [], systemId = activeSystem?.id ?? "") => {
    pushCurrentNavigation();
    applyWorkerOverviewScope(overviewScopeBySystemId[systemId] ?? null);
    setKeyTypeFilter("");
    setWorkerStateFilter(states);
    openView("workers", systemId, false);
  };

  const openIterationsFromOverview = (
    statuses: WorkCompletionStatus[] = [],
    keyType = "",
    systemId = activeSystem?.id ?? ""
  ) => {
    pushCurrentNavigation();
    applyIterationOverviewScope(overviewScopeBySystemId[systemId] ?? null);
    setIterationKeyTypeFilter(keyType);
    setIterationStatusFilter(statuses);
    openView("iterations", systemId, false);
  };

  const openWorkersFiltered = (states: WorkerState[]) => {
    openWorkersFromOverview(states);
  };

  const openIterations = () => {
    openIterationsFromOverview();
  };

  const openIterationsByKeyType = (keyType: string) => {
    openIterationsFromOverview([], keyType);
  };

  const openIterationsFiltered = (statuses: WorkCompletionStatus[]) => {
    openIterationsFromOverview(statuses);
  };

  const openCategoryOverview = (systemId: string, category: string) => {
    const normalizedCategory = normalizeCategoryFilter(category);
    const nextScope = normalizedCategory
      ? {
          category: normalizedCategory,
          includeSubcategories: true,
        }
      : null;
    const currentScope = overviewScopeBySystemId[systemId] ?? null;
    const isSameScope = overviewScopesEqual(currentScope, nextScope);
    const isSameOverview = consoleState.activeSystemId === systemId && view === "overview";

    if (!isSameOverview || !isSameScope) {
      pushCurrentNavigation();
    }

    setSystemOverviewScope(systemId, nextScope);
    openView("overview", systemId, false);
  };

  const openDefinitionOverview = (
    systemId: string,
    definitionName: string,
    category: string
  ) => {
    const normalizedDefinitionName = definitionName.trim();
    const normalizedCategory = normalizeCategoryFilter(category);
    const nextScope = normalizedDefinitionName
      ? {
          category: normalizedCategory || undefined,
          definitionName: normalizedDefinitionName,
        }
      : null;
    const currentScope = overviewScopeBySystemId[systemId] ?? null;
    const isSameScope = overviewScopesEqual(currentScope, nextScope);
    const isSameOverview = consoleState.activeSystemId === systemId && view === "overview";

    if (!isSameOverview || !isSameScope) {
      pushCurrentNavigation();
    }

    setSystemOverviewScope(systemId, nextScope);
    openView("overview", systemId, false);
  };

  const openCatalogScope = (systemId: string, scope: OverviewScope | null) => {
    const currentScope = catalogScopeBySystemId[systemId] ?? null;
    const isSameScope = overviewScopesEqual(currentScope, scope);
    const isSameCatalog = consoleState.activeSystemId === systemId && view === "definitions";

    if (!isSameCatalog || !isSameScope) {
      pushCurrentNavigation();
    }

    setSystemCatalogScope(systemId, scope);
    openView("definitions", systemId, false);
  };

  const openMenuView = (nextView: View, systemId: string) => {
    if (view === "overview" && nextView === "workers") {
      openWorkersFromOverview([], systemId);
      return;
    }

    if (view === "overview" && nextView === "iterations") {
      openIterationsFromOverview([], "", systemId);
      return;
    }

    openView(nextView, systemId);
  };

  const restoreNavigation = useCallback((entry: NavigationEntry) => {
    setCatalogScopeBySystemId((current) => ({
      ...current,
      [entry.systemId]: cloneOverviewScope(entry.catalogScope) ?? undefined,
    }));
    setOverviewScopeBySystemId((current) => ({
      ...current,
      [entry.systemId]: cloneOverviewScope(entry.overviewScope) ?? undefined,
    }));
    setIterationCategoryFilter(entry.iterationCategoryFilter);
    setIterationDefinitionFilter(entry.iterationDefinitionFilter);
    setIterationKeyTypeFilter(entry.iterationKeyTypeFilter);
    setIterationStatusFilter(entry.iterationStatusFilter);
    setKeyTypeFilter(entry.keyTypeFilter);
    setSelectedDefinitionId(entry.definitionId);
    setSelectedWorkerId(entry.workerId);
    setWorkerCategoryFilter(entry.workerCategoryFilter);
    setWorkerDefinitionFilter(entry.workerDefinitionFilter);
    setWorkerStateFilter(entry.workerStateFilter);
    setConsoleState((current) => ({
      ...current,
      activeSystemId: entry.systemId,
      view: entry.view === "worker" ? current.view : entry.view,
    }));
    if (entry.view !== "worker") {
      setMountedViews((current) => new Set([...current, entry.view]));
      setVisibleView(entry.view);
    }
    setPendingView(null);
    setView(entry.view);
  }, []);

  const navigateBack = useCallback(() => {
    const previous = navigationHistory.at(-1);
    if (!previous) {
      return;
    }

    restoreNavigation(previous);
    setNavigationHistory((current) => current.slice(0, -1));
  }, [navigationHistory, restoreNavigation]);

  const markViewReady = useCallback((readyView: ServerView) => {
    if (!activeSystemId) {
      return;
    }

    readyViews.current.add(getViewReadinessKey(activeSystemId, readyView));
    if (pendingView === readyView) {
      setVisibleView((current) => (current === readyView ? current : readyView));
      setPendingView((current) => (current === readyView ? null : current));
    }
  }, [activeSystemId, pendingView]);
  const markOverviewReady = useCallback(() => markViewReady("overview"), [markViewReady]);
  const markDefinitionsReady = useCallback(() => markViewReady("definitions"), [markViewReady]);
  const markDefinitionReady = useCallback(() => markViewReady("definition"), [markViewReady]);
  const markWorkersReady = useCallback(() => markViewReady("workers"), [markViewReady]);
  const markIterationsReady = useCallback(() => markViewReady("iterations"), [markViewReady]);

  useEffect(() => {
    if (visibleView === "worker") {
      return;
    }

    const rememberScroll = () => {
      viewScrollPositions.current[visibleView] = getWindowScrollTop();
    };

    window.addEventListener("scroll", rememberScroll, { passive: true });
    return () => {
      window.removeEventListener("scroll", rememberScroll);
    };
  }, [visibleView]);

  useEffect(() => {
    if (visibleView === "worker") {
      return;
    }

    const scrollTop = viewScrollPositions.current[visibleView] ?? 0;
    let canceled = false;
    let frame = 0;
    let attempts = 0;
    const restoreWhenReady = () => {
      if (canceled) {
        return;
      }

      const maxScrollTop = Math.max(0, getDocumentScrollHeight() - window.innerHeight);
      if (scrollTop <= maxScrollTop || attempts >= 12) {
        window.scrollTo({ top: Math.min(scrollTop, maxScrollTop) });
        return;
      }

      attempts += 1;
      frame = requestAnimationFrame(restoreWhenReady);
    };
    frame = requestAnimationFrame(restoreWhenReady);

    return () => {
      canceled = true;
      cancelAnimationFrame(frame);
    };
  }, [visibleView]);

  const handleOverviewStateLoaded = useCallback((state: string) => {
    setLifecycleError(undefined);
    if (activeSystemId) {
      updateSystemState(activeSystemId, state);
    }
  }, [activeSystemId, updateSystemState]);

  const handleOverviewConnectionError = useCallback(() => {
    setLifecycleError(undefined);
    if (activeSystemId) {
      updateSystemState(activeSystemId, null);
    }
  }, [activeSystemId, updateSystemState]);

  const toggleHostExpanded = (hostId: string) => {
    setConsoleState((current) => {
      const isExpanded = current.expandedHostIds.includes(hostId);
      return {
        ...current,
        expandedHostIds: isExpanded
          ? current.expandedHostIds.filter((id) => id !== hostId)
          : [...current.expandedHostIds, hostId],
      };
    });
  };

  const toggleSystemExpanded = (systemId: string) => {
    setConsoleState((current) => {
      const isExpanded = current.expandedSystemIds.includes(systemId);
      return {
        ...current,
        expandedSystemIds: isExpanded
          ? current.expandedSystemIds.filter((id) => id !== systemId)
          : [...current.expandedSystemIds, systemId],
      };
    });
  };

  const saveHost = (host: WorkableHostConnection) => {
    const firstSystem = host.systems[0];
    setConsoleState((current) => {
      const exists = current.hosts.some((item) => item.id === host.id);
      const hosts = exists
        ? current.hosts.map((item) => (item.id === host.id ? host : item))
        : [...current.hosts, host];

      return {
        ...current,
        activeSystemId: firstSystem?.id ?? current.activeSystemId,
        view: "overview",
        expandedHostIds: current.expandedHostIds.includes(host.id)
          ? current.expandedHostIds
          : [...current.expandedHostIds, host.id],
        expandedSystemIds: [
          ...current.expandedSystemIds,
          ...host.systems
            .map((system) => system.id)
            .filter((id) => !current.expandedSystemIds.includes(id)),
        ],
        hosts,
      };
    });
    if (firstSystem) {
      setSelectedDefinitionId(null);
      setSelectedWorkerId(null);
      setNavigationHistory([]);
      setView("overview");
      setVisibleView("overview");
      setPendingView("overview");
      setMountedViews(new Set(["overview"]));
    }
    refreshView("overview");
  };

  const removeHost = (host: WorkableHostConnection) => {
    setConsoleState((current) => {
      const hosts = current.hosts.filter((item) => item.id !== host.id);
      const removedSystemIds = new Set(host.systems.map((system) => system.id));
      const nextActiveSystemId = removedSystemIds.has(current.activeSystemId)
        ? hosts[0]?.systems[0]?.id ?? ""
        : current.activeSystemId;
      const expandedHostIds = current.expandedHostIds.filter((id) => id !== host.id);
      const expandedSystemIds = current.expandedSystemIds.filter((id) => !removedSystemIds.has(id));

      return {
        ...current,
        activeSystemId: nextActiveSystemId,
        expandedHostIds,
        expandedSystemIds,
        hosts,
      };
    });
    setSelectedDefinitionId(null);
    setSelectedWorkerId(null);
    setNavigationHistory([]);
    setView("overview");
    setVisibleView("overview");
    setPendingView("overview");
    setMountedViews(new Set(["overview"]));
    refreshView("overview");
  };

  const removeSystem = (
    host: WorkableHostConnection,
    system: WorkableSystemConnection
  ) => {
    setConsoleState((current) => {
      const hosts = current.hosts
        .map((item) =>
          item.id === host.id
            ? {
                ...item,
                systems: item.systems.filter((candidate) => candidate.id !== system.id),
              }
            : item
        )
        .filter((item) => item.systems.length > 0);
      const nextActiveSystemId =
        current.activeSystemId === system.id
          ? hosts[0]?.systems[0]?.id ?? ""
          : current.activeSystemId;
      const expandedHostIds = current.expandedHostIds.filter((id) =>
        hosts.some((item) => item.id === id)
      );
      const expandedSystemIds = current.expandedSystemIds.filter((id) => id !== system.id);

      return {
        ...current,
        activeSystemId: nextActiveSystemId,
        expandedHostIds,
        expandedSystemIds,
        hosts,
      };
    });
    setSelectedDefinitionId(null);
    setSelectedWorkerId(null);
    setNavigationHistory([]);
    setView("overview");
    setVisibleView("overview");
    setPendingView("overview");
    setMountedViews(new Set(["overview"]));
    refreshView("overview");
  };

  return (
    <SidebarProvider>
      <DiagnosticsAlertSubscriptions
        captureEnabled={false}
        enabled={hasMounted}
        maxMessages={realtimePayloadMaxMessages}
        onSnapshot={updateDiagnosticsAlertSnapshot}
        request={diagnosticsAlertRequest}
        targets={diagnosticsAlertTargets}
      />
      <Sidebar variant="inset">
        <SidebarHeader>
          <div className="flex h-14 items-center px-2">
            <Image
              alt="Workable"
              className="-translate-y-1 h-11 w-auto object-contain"
              height={55}
              priority
              src="/workable-logo-transparent.png"
              width={220}
            />
          </div>
        </SidebarHeader>
        <SidebarContent>
          <SidebarGroup>
            <SidebarGroupLabel>Server Explorer</SidebarGroupLabel>
            <Tooltip delayDuration={500} disableHoverableContent>
              <TooltipTrigger asChild>
                <SidebarGroupAction onClick={() => setServerDialog({ mode: "add" })}>
                  <Plus />
                  <span className="sr-only">Add server</span>
                </SidebarGroupAction>
              </TooltipTrigger>
              <TooltipContent side="right" sideOffset={6}>
                Add server
              </TooltipContent>
            </Tooltip>
            <SidebarGroupContent>
              <ServerTree
                activeSystemId={activeSystem?.id ?? ""}
                catalogScopeBySystemId={catalogScopeBySystemId}
                expandedHostIds={consoleState.expandedHostIds}
                expandedSystemIds={consoleState.expandedSystemIds}
                hosts={consoleState.hosts}
                lifecycleActionSystemId={lifecycleActionSystemId}
                onOpenCatalogScope={openCatalogScope}
                onOpenDefinition={openDefinition}
                onOpenWorker={openWorker}
                onAddServer={() => setServerDialog({ mode: "add" })}
                onEditHost={(host) => setServerDialog({ mode: "edit", host })}
                onLifecycleAction={(system, action) => {
                  if (action === "stop") {
                    const location = findSystemLocation(consoleState, system.id);
                    if (location) {
                      setPendingStopSystem({
                        system,
                      });
                    }
                    return;
                  }

                  void executeSystemLifecycleAction(system, action);
                }}
                onOpenView={openMenuView}
                onRemoveHost={(host) => setPendingDelete({ kind: "host", host })}
                onRemoveSystem={(host, system) =>
                  setPendingDelete({ kind: "system", host, system })
                }
                onToggleHost={toggleHostExpanded}
                onToggleSystem={toggleSystemExpanded}
                view={view}
              />
            </SidebarGroupContent>
          </SidebarGroup>
        </SidebarContent>
        <SidebarFooter />
      </Sidebar>
      <SidebarInset>
        <main className="flex-1 bg-background">
          <div className="relative mx-auto w-full max-w-7xl p-4 md:p-6" data-view-content>
              {!connection && (
                <EmptyServerState onAddServer={() => setServerDialog({ mode: "add" })} />
              )}
              {connection && (
                <>
                  {activeHost && activeSystem && (
                    <ConsoleNavigationHeader
                      canGoBack={navigationHistory.length > 0}
                      definitionId={selectedDefinitionId}
                      host={activeHost}
                      onBack={navigateBack}
                      onOpenView={openView}
                      system={activeSystem}
                      systemNotifications={(
                        <div className="flex items-center gap-1">
                          <SystemToolsMenu
                            eventViewerOpen={eventViewerOpen}
                            onEventViewerOpenChange={setEventViewerOpen}
                            onRealtimePayloadOpenChange={setRealtimePayloadOpen}
                            realtimePayloadOpen={realtimePayloadOpen}
                          />
                          <SystemNotificationTray
                            acknowledgedRejectedWorkCounts={acknowledgedRejectedWorkCounts}
                            activeSystemId={activeSystem.id}
                            alertSources={diagnosticsAlertSources}
                            concurrencyDetailDiagnostics={concurrencyDiagnosticsDetail}
                            concurrencyExpanded={concurrencyDiagnosticsExpanded}
                            durabilityDetailDiagnostics={durabilityDiagnosticsDetail}
                            durabilityExpanded={durabilityDiagnosticsExpanded}
                            idempotencyDetailDiagnostics={idempotencyDiagnosticsDetail}
                            idempotencyExpanded={idempotencyDiagnosticsExpanded}
                            onAcknowledgeQueueRejections={acknowledgeQueueRejections}
                            onConcurrencyExpandedChange={setConcurrencyDiagnosticsExpanded}
                            onDurabilityExpandedChange={setDurabilityDiagnosticsExpanded}
                            onIdempotencyExpandedChange={setIdempotencyDiagnosticsExpanded}
                            onOpenChange={handleSystemNotificationOpenChange}
                            onReadModelExpandedChange={setReadModelDiagnosticsExpanded}
                            onRetentionExpandedChange={setRetentionDiagnosticsExpanded}
                            open={systemNotificationOpen}
                            readModelDetailDiagnostics={readModelDiagnosticsDetail}
                            readModelExpanded={readModelDiagnosticsExpanded}
                            retentionDetailDiagnostics={retentionDiagnosticsDetail}
                            retentionExpanded={retentionDiagnosticsExpanded}
                            systemName={activeSystem.name}
                            trayDiagnostics={diagnosticsTray}
                          />
                          <EventViewerWindow
                            captureEnabled={eventViewerCaptureEnabled}
                            connectionState={realtimeEvents.connectionState}
                            definitionError={eventViewerDefinitionError}
                            definitions={eventViewerDefinitions}
                            definitionsLoading={eventViewerDefinitionsLoading}
                            enabled={realtimeEvents.enabled}
                            eventTypes={eventViewerEventTypes}
                            error={realtimeEvents.error}
                            hubUrl={realtimeEvents.hubUrl ?? null}
                            maxMessages={eventViewerMaxMessages}
                            messages={realtimeEvents.messages}
                            onAddKey={addEventViewerKey}
                            onCaptureEnabledChange={setEventViewerCaptureEnabled}
                            onClearMessages={realtimeEvents.clearMessages}
                            onDefinitionToggle={toggleEventViewerDefinition}
                            onEventTypeToggle={toggleEventViewerEventType}
                            onMaxMessagesChange={setEventViewerMaxMessages}
                            onOpenChange={setEventViewerOpen}
                            onRemoveKey={removeEventViewerKey}
                            open={eventViewerOpen}
                            selectedDefinitionIds={selectedEventViewerDefinitionIds}
                            selectedEventTypes={selectedEventViewerEventTypes}
                            selectedKeys={selectedEventViewerKeys}
                          />
                        </div>
                      )}
                      view={view}
                      workerId={selectedWorkerId}
                    />
                  )}
                  <ErrorPanel errors={[lifecycleError]} />
                  {mountedViews.has("overview") && (
                    <div className={visibleView === "overview" ? viewContentOffsetClass : "hidden"}>
                      <OverviewView
                        connection={connection}
                        externalRealtimeMessages={diagnosticsRealtimeMessages}
                        hiddenPanelIds={consoleState.overviewHiddenPanels}
                        hiddenThroughputSeries={consoleState.overviewHiddenThroughputSeries}
                        isVisible={visibleView === "overview"}
                        onClearExternalRealtimeMessages={clearDiagnosticsRealtimeMessages}
                        onConnectionError={handleOverviewConnectionError}
                        onStateLoaded={handleOverviewStateLoaded}
                        onOpenCatalog={() => openView("definitions")}
                        onOpenIterations={openIterations}
                        onOpenKeyType={openIterationsByKeyType}
                        onReady={markOverviewReady}
                        onPanelShapeChange={setOverviewPanelShape}
                        onPanelVisibilityChange={setOverviewPanelVisible}
                        onThroughputSeriesToggle={toggleOverviewThroughputSeries}
                        panelShapes={consoleState.overviewPanelShapes}
                        realtimePayloadCaptureEnabled={realtimePayloadCaptureEnabled}
                        realtimePayloadMaxMessages={realtimePayloadMaxMessages}
                        realtimePayloadOpen={realtimePayloadOpen}
                        onRealtimePayloadCaptureEnabledChange={setRealtimePayloadCaptureEnabled}
                        onRealtimePayloadMaxMessagesChange={setRealtimePayloadMaxMessages}
                        onRealtimePayloadOpenChange={setRealtimePayloadOpen}
                        onViewIterationsByStatus={openIterationsFiltered}
                        onViewWorkersByState={openWorkersFiltered}
                        overviewScope={activeOverviewScope}
                        refreshToken={refreshTokens.overview}
                        onOpenWorker={openWorker}
                        renderToolbar={({ loading, refreshing }) => (
                          <ViewActionLane>
                            <OverviewCatalogFilter
                              connection={connection}
                              loading={loading || refreshing}
                              onClear={() => {
                                if (activeSystem) {
                                  openCategoryOverview(activeSystem.id, "");
                                }
                              }}
                              onSelectCategory={(category) => {
                                if (activeSystem) {
                                  openCategoryOverview(activeSystem.id, category);
                                }
                              }}
                              onSelectDefinition={(definitionName, category) => {
                                if (activeSystem) {
                                  openDefinitionOverview(
                                    activeSystem.id,
                                    definitionName,
                                    category
                                  );
                                }
                              }}
                              refreshToken={refreshTokens.overview}
                              scope={activeOverviewScope}
                            />
                            <Tooltip delayDuration={500} disableHoverableContent>
                              <TooltipTrigger asChild>
                                <Button
                                  aria-label="Refresh overview"
                                  className="text-muted-foreground hover:bg-transparent hover:text-foreground dark:hover:bg-transparent"
                                  onClick={() => refreshView("overview")}
                                  size="icon-sm"
                                  variant="ghost"
                                >
                                  <RefreshCw className="size-4" />
                                </Button>
                              </TooltipTrigger>
                              <TooltipContent side="bottom" sideOffset={6}>
                                Refresh overview
                              </TooltipContent>
                            </Tooltip>
                            <OverviewPanelSettings
                              hiddenPanelIds={consoleState.overviewHiddenPanels}
                              onPanelVisibilityChange={setOverviewPanelVisible}
                              onResetUi={resetOverviewUiToDefaults}
                            />
                          </ViewActionLane>
                        )}
                      />
                    </div>
                  )}
                  {mountedViews.has("definitions") && (
                    <div className={visibleView === "definitions" ? viewContentOffsetClass : "hidden"}>
                      <DefinitionsView
                        catalogScope={activeCatalogScope}
                        connection={connection}
                        onCatalogScopeChange={(scope) => {
                          if (activeSystem) {
                            openCatalogScope(activeSystem.id, scope);
                          }
                        }}
                        onOpenDefinition={(definitionId) =>
                          openDefinition(definitionId, activeSystem?.id ?? "")
                        }
                        onOpenWorker={openWorker}
                        onReady={markDefinitionsReady}
                        refreshToken={refreshTokens.definitions}
                      />
                    </div>
                  )}
                  {mountedViews.has("definition") && selectedDefinitionId && (
                    <div className={visibleView === "definition" ? viewContentOffsetClass : "hidden"}>
                      <DefinitionView
                        connection={connection}
                        definitionId={selectedDefinitionId}
                        onOpenWorker={openWorker}
                        onReady={markDefinitionReady}
                        refreshToken={refreshTokens.definition}
                      />
                    </div>
                  )}
                  {mountedViews.has("workers") && (
                    <div className={visibleView === "workers" ? viewContentOffsetClass : "hidden"}>
                      <WorkersView
                        categoryFilter={workerCategoryFilter}
                        connection={connection}
                        filterControls={(
                          <QueryFilterPopover
                            allFacetLabel="All states"
                            catalogScope={createQueryCatalogScope(workerCategoryFilter, workerDefinitionFilter)}
                            connection={connection}
                            facetLabel="Worker states"
                            facetOptions={states}
                            facetValue={workerStateFilter}
                            keyTypeFilter={keyTypeFilter}
                            onClearCatalog={() => {
                              setWorkerCategoryFilter("");
                              setWorkerDefinitionFilter("");
                            }}
                            onFacetChange={setWorkerStateFilter}
                            onKeyTypeFilterChange={setKeyTypeFilter}
                            onSelectCategory={(category) => {
                              setWorkerCategoryFilter(category);
                              setWorkerDefinitionFilter("");
                            }}
                            onSelectDefinition={(definitionName, category) => {
                              setWorkerCategoryFilter(category);
                              setWorkerDefinitionFilter(definitionName);
                            }}
                            refreshToken={refreshTokens.workers}
                          />
                        )}
                        isLoadingTarget={visibleView === "workers" || pendingView === "workers"}
                        isVisible={visibleView === "workers"}
                        onOpenWorker={openWorker}
                        onReady={markWorkersReady}
                        definitionFilter={workerDefinitionFilter}
                        keyTypeFilter={keyTypeFilter}
                        stateFilter={workerStateFilter}
                        refreshToken={refreshTokens.workers}
                      />
                    </div>
                  )}
                  {mountedViews.has("iterations") && (
                    <div className={visibleView === "iterations" ? viewContentOffsetClass : "hidden"}>
                      <IterationsView
                        categoryFilter={iterationCategoryFilter}
                        connection={connection}
                        definitionFilter={iterationDefinitionFilter}
                        filterControls={(
                          <QueryFilterPopover
                            allFacetLabel="All statuses"
                            catalogScope={createQueryCatalogScope(iterationCategoryFilter, iterationDefinitionFilter)}
                            connection={connection}
                            facetLabel="Iteration statuses"
                            facetOptions={iterationStatuses}
                            facetValue={iterationStatusFilter}
                            keyTypeFilter={iterationKeyTypeFilter}
                            onClearCatalog={() => {
                              setIterationCategoryFilter("");
                              setIterationDefinitionFilter("");
                            }}
                            onFacetChange={setIterationStatusFilter}
                            onKeyTypeFilterChange={setIterationKeyTypeFilter}
                            onSelectCategory={(category) => {
                              setIterationCategoryFilter(category);
                              setIterationDefinitionFilter("");
                            }}
                            onSelectDefinition={(definitionName, category) => {
                              setIterationCategoryFilter(category);
                              setIterationDefinitionFilter(definitionName);
                            }}
                            refreshToken={refreshTokens.iterations}
                          />
                        )}
                        isLoadingTarget={visibleView === "iterations" || pendingView === "iterations"}
                        isVisible={visibleView === "iterations"}
                        keyTypeFilter={iterationKeyTypeFilter}
                        onOpenWorker={openWorker}
                        onReady={markIterationsReady}
                        refreshToken={refreshTokens.iterations}
                        statusFilter={iterationStatusFilter}
                      />
                    </div>
                  )}
                  <DelayedLoadingOverlay
                    active={!!pendingView && view !== "worker"}
                    label={`Loading ${pendingView ? navTitle(pendingView) : "view"}`}
                  />
                </>
              )}
              {connection && view === "worker" && selectedWorkerId && (
                <div className={viewContentOffsetClass}>
                  <WorkerConsoleView
                    backLabel={`Back to ${navTitle(getWorkerParentView(navigationHistory))}`}
                    connection={connection}
                    onBack={navigationHistory.length > 0 ? navigateBack : () => openView(getWorkerParentView(navigationHistory))}
                    refreshToken={refreshTokens.worker}
                    workerId={selectedWorkerId}
                  />
                </div>
              )}
          </div>
        </main>
      </SidebarInset>
      <ServerDialog
        key={`${serverDialog?.mode ?? "closed"}:${serverDialog?.host?.id ?? "new"}`}
        mode={serverDialog?.mode ?? "add"}
        onOpenChange={(open) => !open && setServerDialog(null)}
        onSave={saveHost}
        open={!!serverDialog}
        host={serverDialog?.host}
      />
      <DeleteTargetDialog
        onConfirm={() => {
          if (pendingDelete?.kind === "host") {
            removeHost(pendingDelete.host);
          }
          if (pendingDelete?.kind === "system") {
            removeSystem(pendingDelete.host, pendingDelete.system);
          }
          setPendingDelete(null);
        }}
        onOpenChange={(open) => !open && setPendingDelete(null)}
        target={pendingDelete}
      />
      <StopSystemDialog
        onConfirm={() => {
          if (!pendingStopSystem) {
            return;
          }

          void executeSystemLifecycleAction(pendingStopSystem.system, "stop");
          setPendingStopSystem(null);
        }}
        onOpenChange={(open) => !open && setPendingStopSystem(null)}
        target={pendingStopSystem}
      />
    </SidebarProvider>
  );
}

const overviewPanelOptions: Array<{
  description: string;
  id: OverviewPanelId;
  label: string;
}> = [
  {
    description: "Worker states and current worker totals.",
    id: "workers",
    label: "Workers",
  },
  {
    description: "Recent workers in the failed state.",
    id: "failedWorkers",
    label: "Recent Failed Workers",
  },
  {
    description: "Throughput and execution charts.",
    id: "throughput",
    label: "Throughput",
  },
  {
    description: "Worker iteration statuses and common relationship filters.",
    id: "iterations",
    label: "Iterations",
  },
  {
    description: "Recent failed worker iterations.",
    id: "failedIterations",
    label: "Recent Failed Iterations",
  },
  {
    description: "Recent completed worker iterations.",
    id: "completedIterations",
    label: "Recent Completed Iterations",
  },
];

type SystemDiagnosticsViewState = RealtimeViewLoadable<WorkComponentQueryResult>;

type DiagnosticsAlertTarget = {
  apiUrl: string;
  displayName: string;
  hostId: string;
  hostName: string;
  realtimeHubPath: string;
  systemId: string;
  systemName?: string;
};

type DiagnosticsAlertSnapshot = {
  connectionState: string;
  data?: WorkComponentQueryResult;
  enabled: boolean;
  error?: string;
  loading: boolean;
  refreshing?: boolean;
};

type DiagnosticsAlertSource = DiagnosticsAlertSnapshot & {
  target: DiagnosticsAlertTarget;
};

function SystemToolsMenu({
  eventViewerOpen,
  onEventViewerOpenChange,
  onRealtimePayloadOpenChange,
  realtimePayloadOpen,
}: {
  eventViewerOpen: boolean;
  onEventViewerOpenChange: (open: boolean) => void;
  onRealtimePayloadOpenChange: (open: boolean) => void;
  realtimePayloadOpen: boolean;
}) {
  const [open, setOpen] = useState(false);

  return (
    <Popover open={open} onOpenChange={setOpen}>
      <Tooltip delayDuration={500} disableHoverableContent>
        <TooltipTrigger asChild>
          <PopoverTrigger asChild>
            <Button
              aria-label="System tools"
              className="text-muted-foreground hover:bg-transparent hover:text-foreground dark:hover:bg-transparent"
              size="icon-sm"
              variant="ghost"
            >
              <Wrench className="size-4" />
            </Button>
          </PopoverTrigger>
        </TooltipTrigger>
        <TooltipContent side="bottom" sideOffset={6}>
          System tools
        </TooltipContent>
      </Tooltip>
      <PopoverContent align="end" className="w-56 p-1">
        <button
          className="flex w-full items-center gap-2 rounded-md px-2 py-1.5 text-left text-sm hover:bg-accent hover:text-accent-foreground"
          onClick={() => {
            onEventViewerOpenChange(!eventViewerOpen);
            setOpen(false);
          }}
          type="button"
        >
          <FileJson className="size-4" />
          <span className="flex-1">Event viewer</span>
          <span className="text-muted-foreground text-xs">{eventViewerOpen ? "Open" : ""}</span>
        </button>
        <button
          className="flex w-full items-center gap-2 rounded-md px-2 py-1.5 text-left text-sm hover:bg-accent hover:text-accent-foreground"
          onClick={() => {
            onRealtimePayloadOpenChange(!realtimePayloadOpen);
            setOpen(false);
          }}
          type="button"
        >
          <Rows4 className="size-4" />
          <span className="flex-1">Realtime payloads</span>
          <span className="text-muted-foreground text-xs">{realtimePayloadOpen ? "Open" : ""}</span>
        </button>
      </PopoverContent>
    </Popover>
  );
}

function DiagnosticsAlertSubscriptions({
  captureEnabled,
  enabled,
  maxMessages,
  onSnapshot,
  request,
  targets,
}: {
  captureEnabled: boolean;
  enabled: boolean;
  maxMessages: number;
  onSnapshot: (systemId: string, snapshot: DiagnosticsAlertSnapshot | null) => void;
  request: unknown;
  targets: DiagnosticsAlertTarget[];
}) {
  return (
    <>
      {targets.map((target) => (
        <DiagnosticsAlertSubscription
          captureEnabled={captureEnabled}
          enabled={enabled}
          key={target.systemId}
          maxMessages={maxMessages}
          onSnapshot={onSnapshot}
          request={request}
          target={target}
        />
      ))}
    </>
  );
}

function DiagnosticsAlertSubscription({
  captureEnabled,
  enabled,
  maxMessages,
  onSnapshot,
  request,
  target,
}: {
  captureEnabled: boolean;
  enabled: boolean;
  maxMessages: number;
  onSnapshot: (systemId: string, snapshot: DiagnosticsAlertSnapshot | null) => void;
  request: unknown;
  target: DiagnosticsAlertTarget;
}) {
  const connection = useMemo<WorkableConnection>(
    () => ({
      apiUrl: target.apiUrl,
      realtimeHubPath: target.realtimeHubPath,
      systemName: target.systemName,
    }),
    [target.apiUrl, target.realtimeHubPath, target.systemName]
  );
  const diagnostics = useWorkableRealtimeView<WorkComponentQueryResult>(
    connection,
    "diagnostics",
    request,
    enabled,
    captureEnabled,
    maxMessages,
    `diagnostics:alerts:${target.systemId}`
  );
  const lastSnapshotRef = useRef<DiagnosticsAlertSnapshot | null>(null);

  useEffect(() => {
    const snapshot = {
      connectionState: diagnostics.connectionState,
      data: diagnostics.data,
      enabled: diagnostics.enabled,
      error: diagnostics.error,
      loading: diagnostics.loading,
      refreshing: diagnostics.refreshing,
    };

    if (diagnosticsAlertSnapshotsEqual(lastSnapshotRef.current, snapshot)) {
      return;
    }

    lastSnapshotRef.current = snapshot;
    onSnapshot(target.systemId, snapshot);
  }, [
    diagnostics.connectionState,
    diagnostics.data,
    diagnostics.enabled,
    diagnostics.error,
    diagnostics.loading,
    diagnostics.refreshing,
    onSnapshot,
    target.systemId,
  ]);

  useEffect(
    () => () => {
      lastSnapshotRef.current = null;
      onSnapshot(target.systemId, null);
    },
    [onSnapshot, target.systemId]
  );

  return null;
}

function EventViewerWindow({
  captureEnabled,
  connectionState,
  definitionError,
  definitions,
  definitionsLoading,
  enabled,
  error,
  eventTypes,
  hubUrl,
  maxMessages,
  messages,
  onAddKey,
  onCaptureEnabledChange,
  onClearMessages,
  onDefinitionToggle,
  onEventTypeToggle,
  onMaxMessagesChange,
  onOpenChange,
  onRemoveKey,
  open,
  selectedDefinitionIds,
  selectedEventTypes,
  selectedKeys,
}: {
  captureEnabled: boolean;
  connectionState: string;
  definitionError?: string;
  definitions: WorkDefinition[];
  definitionsLoading: boolean;
  enabled: boolean;
  error?: string;
  eventTypes: readonly string[];
  hubUrl: string | null;
  maxMessages: number;
  messages: RealtimeEventMessage[];
  onAddKey: (key: WorkableRealtimeEventKeyCriteria) => void;
  onCaptureEnabledChange: (enabled: boolean) => void;
  onClearMessages: () => void;
  onDefinitionToggle: (definitionId: string) => void;
  onEventTypeToggle: (eventType: string) => void;
  onMaxMessagesChange: (maxMessages: number) => void;
  onOpenChange: (open: boolean) => void;
  onRemoveKey: (key: WorkableRealtimeEventKeyCriteria) => void;
  open: boolean;
  selectedDefinitionIds: string[];
  selectedEventTypes: string[];
  selectedKeys: WorkableRealtimeEventKeyCriteria[];
}) {
  const [position, setPosition] = useState({ x: 0, y: 0 });
  const [catalogPath, setCatalogPath] = useState("");
  const [windowSize, setWindowSize] = useState<"compact" | "large">("large");
  const [eventTableHeight, setEventTableHeight] = useState(208);
  const [filtersCollapsed, setFiltersCollapsed] = useState(false);
  const [maximized, setMaximized] = useState(false);
  const [messagesCollapsed, setMessagesCollapsed] = useState(false);
  const [selectedEventIndex, setSelectedEventIndex] = useState(0);
  const [selectedMessageId, setSelectedMessageId] = useState<string | null>(null);
  const [keyKind, setKeyKind] = useState<WorkKeyKind | "Any">("Any");
  const [keyType, setKeyType] = useState("");
  const [keyValue, setKeyValue] = useState("");
  const dragRef = useRef<{
    originX: number;
    originY: number;
    startX: number;
    startY: number;
  } | null>(null);
  const eventRowRefs = useRef<Array<HTMLButtonElement | null>>([]);
  const eventTableResizeRef = useRef<{
    startHeight: number;
    startY: number;
  } | null>(null);
  const panelRef = useRef<HTMLDivElement | null>(null);
  const wasOpenRef = useRef(false);
  const selectedMessage = messages.find((message) => message.id === selectedMessageId) ?? messages[0];
  const selectedEvent = selectedMessage?.events[Math.min(selectedEventIndex, selectedMessage.events.length - 1)];
  const selectedEventIndexInBounds = selectedEvent
    ? Math.min(selectedEventIndex, (selectedMessage?.events.length ?? 1) - 1)
    : 0;
  const isCompactWindow = windowSize === "compact";
  const selectedFilterText = formatEventViewerFilterSummary(
    selectedEventTypes.length,
    selectedDefinitionIds.length,
    selectedKeys.length
  );
  const catalogLevel = useMemo(
    () => createEventViewerCatalogLevel(definitions, catalogPath),
    [catalogPath, definitions]
  );
  const catalogSegments = splitCatalogPath(catalogPath);
  const catalogLabel = catalogSegments.at(-1) ?? "Catalog";
  const canGoBackInCatalog = catalogSegments.length > 0;
  const selectCatalogCategory = (category: string) => {
    setCatalogPath(category);
  };
  const goBackInCatalog = () => {
    setCatalogPath(catalogSegments.slice(0, -1).join(":"));
  };
  const selectedMessageBatchText = selectedMessage?.batchSize
    ? `Batch ${selectedMessage.batchSize}`
    : "Single";
  const hasEventTable = Boolean(selectedMessage && selectedMessage.events.length > 1);
  const addKey = () => {
    const type = keyType.trim();
    const value = keyValue.trim();
    if (!type || !value) {
      return;
    }

    onAddKey({
      kind: keyKind === "Any" ? null : keyKind,
      type,
      value,
    });
    setKeyType("");
    setKeyValue("");
  };

  useEffect(() => {
    if (open && !wasOpenRef.current) {
      setPosition(getCenteredEventViewerPosition(windowSize));
    }
    wasOpenRef.current = open;
  }, [open, windowSize]);

  useEffect(() => {
    const row = eventRowRefs.current[selectedEventIndexInBounds];
    row?.scrollIntoView({ block: "nearest" });
    if (document.activeElement && eventRowRefs.current.includes(document.activeElement as HTMLButtonElement)) {
      row?.focus();
    }
  }, [selectedEventIndexInBounds, selectedMessage?.id]);

  const toggleWindowSize = () => {
    const nextSize = isCompactWindow ? "large" : "compact";
    setMaximized(false);
    setWindowSize(nextSize);
    setPosition(getCenteredEventViewerPosition(nextSize));
  };

  const toggleMaximized = () => {
    setMaximized((current) => !current);
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
    if (!dragRef.current || maximized) {
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

  const startEventTableResize = (event: PointerEvent<HTMLDivElement>) => {
    event.preventDefault();
    event.stopPropagation();
    event.currentTarget.setPointerCapture(event.pointerId);
    eventTableResizeRef.current = {
      startHeight: eventTableHeight,
      startY: event.clientY,
    };
  };

  const resizeEventTable = (event: PointerEvent<HTMLDivElement>) => {
    if (!eventTableResizeRef.current) {
      return;
    }

    const nextHeight = eventTableResizeRef.current.startHeight + event.clientY - eventTableResizeRef.current.startY;
    setEventTableHeight(clampEventTableHeight(nextHeight));
  };

  const stopEventTableResize = (event: PointerEvent<HTMLDivElement>) => {
    eventTableResizeRef.current = null;
    event.currentTarget.releasePointerCapture(event.pointerId);
  };

  const moveSelectedEvent = (delta: number) => {
    if (!selectedMessage) {
      return;
    }

    setSelectedEventIndex((current) =>
      Math.max(0, Math.min(selectedMessage.events.length - 1, current + delta))
    );
  };

  const handleEventRowKeyDown = (event: KeyboardEvent<HTMLButtonElement>) => {
    if (event.key === "ArrowDown") {
      event.preventDefault();
      moveSelectedEvent(1);
    } else if (event.key === "ArrowUp") {
      event.preventDefault();
      moveSelectedEvent(-1);
    }
  };

  return (
    <>
      {open && typeof document !== "undefined"
        ? createPortal(
          <div
            className={`fixed z-50 grid resize grid-rows-[auto_minmax(0,1fr)] overflow-hidden rounded-lg border bg-popover text-sm text-popover-foreground shadow-2xl ring-1 ring-foreground/10 ${
              maximized ? "" : isCompactWindow ? "min-h-[28rem] min-w-[42rem]" : "min-h-[32rem] min-w-[48rem]"
            }`}
            ref={panelRef}
            style={{
              height: maximized
                ? "calc(100vh - 16px)"
                : isCompactWindow
                  ? "min(82vh, 32rem)"
                  : "min(88vh, 56rem)",
              left: maximized ? 8 : position.x,
              top: maximized ? 8 : position.y,
              width: maximized
                ? "calc(100vw - 16px)"
                : isCompactWindow
                  ? "min(96vw, 48rem)"
                  : "min(96vw, 96rem)",
            }}
          >
            <div
              className={`flex items-center justify-between gap-3 border-b px-4 py-3 select-none ${maximized ? "" : "cursor-move"}`}
              onPointerDown={startDrag}
              onPointerMove={drag}
              onPointerUp={stopDrag}
            >
              <div className="min-w-0">
                <div className="font-medium text-base">Event viewer</div>
                <div className="truncate text-muted-foreground text-xs">
                  {enabled ? connectionState : "disabled"} - {messages.length}/{maxMessages} batches - {selectedFilterText}
                </div>
              </div>
              <div className="flex shrink-0 items-center gap-1">
                <Button
                  aria-label={isCompactWindow ? "Expand event viewer" : "Compact event viewer"}
                  className="cursor-pointer"
                  onClick={toggleWindowSize}
                  onPointerDown={(event) => event.stopPropagation()}
                  size="icon-sm"
                  variant="ghost"
                >
                  {isCompactWindow ? <Rows4 className="size-4" /> : <Rows2 className="size-4" />}
                </Button>
                <Button
                  aria-label={maximized ? "Restore event viewer" : "Maximize event viewer"}
                  className="cursor-pointer"
                  onClick={toggleMaximized}
                  onPointerDown={(event) => event.stopPropagation()}
                  size="icon-sm"
                  variant="ghost"
                >
                  {maximized ? <Minimize2 className="size-4" /> : <Maximize2 className="size-4" />}
                </Button>
                <Button
                  aria-label="Close event viewer"
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
              <div className="grid gap-2 rounded-md border px-3 py-2 xl:grid-cols-[minmax(0,1fr)_auto]">
                <div className="flex min-w-0 flex-wrap items-center gap-x-4 gap-y-1 text-xs">
                  <EventInlineMetric label="Received" value={selectedMessage ? formatEventViewerTime(selectedMessage.receivedAt) : "No events"} />
                  <EventInlineMetric label="Batch" value={selectedMessage ? selectedMessageBatchText : "-"} />
                  <EventInlineMetric label="Type" value={selectedEvent?.eventType ?? "-"} />
                  <EventInlineMetric label="Worker" value={selectedEvent?.workerId?.value ?? "-"} />
                  <EventInlineMetric label="Hub" value={hubUrl ?? "-"} wide />
                </div>
                <div className="flex flex-wrap items-center gap-3">
                  <Button
                    className="h-8 px-2 text-xs"
                    disabled={messages.length === 0}
                    onClick={() => {
                      setSelectedMessageId(null);
                      setSelectedEventIndex(0);
                      onClearMessages();
                    }}
                    size="sm"
                    variant="ghost"
                  >
                    Clear
                  </Button>
                  <label className="flex items-center gap-2 text-muted-foreground">
                    <input
                      checked={captureEnabled}
                      className="size-4 accent-primary"
                      onChange={(event) => onCaptureEnabledChange(event.currentTarget.checked)}
                      type="checkbox"
                    />
                    <span>Capture incoming events</span>
                  </label>
                  <label className="flex items-center gap-2">
                    <span className="text-muted-foreground">Max</span>
                    <input
                      className="h-8 w-20 rounded-md border bg-background px-2 font-mono text-foreground"
                      max={1000}
                      min={1}
                      onChange={(event) =>
                        onMaxMessagesChange(normalizeEventViewerMaxMessages(event.currentTarget.value))
                      }
                      type="number"
                      value={maxMessages}
                    />
                  </label>
                </div>
                {error && (
                  <div className="col-span-full rounded-md border border-red-500/30 bg-red-500/10 px-2 py-1.5 text-red-200 text-xs">
                    {error}
                  </div>
                )}
              </div>
              <div
                className={`grid min-h-0 gap-3 ${
                  messagesCollapsed && filtersCollapsed
                    ? "md:grid-cols-[2.75rem_2.75rem_minmax(0,1fr)]"
                    : messagesCollapsed
                      ? "md:grid-cols-[2.75rem_minmax(20rem,22rem)_minmax(0,1fr)]"
                      : filtersCollapsed
                        ? "md:grid-cols-[22rem_2.75rem_minmax(0,1fr)]"
                        : "md:grid-cols-[22rem_minmax(20rem,22rem)_minmax(0,1fr)]"
                }`}
              >
                <div className="grid min-h-0 grid-rows-[auto_minmax(0,1fr)] overflow-hidden rounded-md border">
                  <div className="flex items-center justify-between gap-2 border-b px-2 py-1.5">
                    {!messagesCollapsed && (
                      <div className="font-medium text-muted-foreground text-xs">Batches</div>
                    )}
                    <Button
                      aria-label={messagesCollapsed ? "Show events" : "Collapse events"}
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
                        {messages.length}
                      </div>
                    </div>
                  ) : (
                    <div className="min-h-0 overflow-auto p-2">
                      {messages.length === 0 ? (
                        <div className="p-3 text-muted-foreground text-sm">
                          {selectedEventTypes.length === 0
                            ? "Select one or more event types to start capture."
                            : "Waiting for realtime events."}
                        </div>
                      ) : (
                        <div className="space-y-1">
                          {messages.map((message) => (
                            <button
                              className={`grid w-full gap-1 rounded-md px-2 py-2 text-left text-xs transition-colors ${
                                message.id === selectedMessage?.id
                                  ? "bg-accent text-accent-foreground"
                                  : "hover:bg-accent/50"
                              }`}
                              key={message.id}
                              onClick={() => {
                                setSelectedMessageId(message.id);
                                setSelectedEventIndex(0);
                              }}
                              type="button"
                            >
                              <span className="flex items-center justify-between gap-2">
                                <span className="truncate font-mono font-medium">
                                  {message.batchSize ? `Batch of ${message.batchSize}` : "Single event"}
                                </span>
                                <span className="font-mono text-muted-foreground">
                                  {message.bytesEstimated ? ">=" : ""}{message.bytes.toLocaleString()}b
                                </span>
                              </span>
                              <span className="truncate text-muted-foreground">
                                {formatEventBatchTypeSummary(message.eventTypes)}
                              </span>
                              <span className="truncate font-mono text-muted-foreground">
                                {formatEventViewerTime(message.receivedAt)}
                              </span>
                              <span className="truncate text-muted-foreground">
                                {formatEventBatchDefinitionSummary(message.events)}
                              </span>
                            </button>
                          ))}
                        </div>
                      )}
                    </div>
                  )}
                </div>
                <div className="grid min-h-0 grid-rows-[auto_minmax(0,1fr)] overflow-hidden rounded-md border">
                  <div className="flex items-center justify-between gap-2 border-b px-3 py-2">
                    {!filtersCollapsed && (
                      <div className="font-medium text-muted-foreground text-xs">Filters</div>
                    )}
                    <Button
                      aria-label={filtersCollapsed ? "Show filters" : "Collapse filters"}
                      className="ml-auto"
                      onClick={() => setFiltersCollapsed((current) => !current)}
                      size="icon-sm"
                      variant="ghost"
                    >
                      <ChevronRight
                        className={`size-4 transition-transform ${
                          filtersCollapsed ? "" : "rotate-180"
                        }`}
                      />
                    </Button>
                  </div>
                  {filtersCollapsed ? (
                    <div className="flex min-h-0 items-start justify-center overflow-hidden py-2">
                      <div className="font-mono text-muted-foreground text-xs [writing-mode:vertical-rl]">
                        filters
                      </div>
                    </div>
                  ) : (
                  <div className="min-h-0 space-y-3 overflow-auto overflow-x-hidden p-2">
                    <div className="space-y-1">
                      <div className="flex items-center justify-between gap-2 px-1">
                        <div className="font-medium text-muted-foreground text-xs">Event types</div>
                      </div>
                      {eventTypes.map((eventType) => (
                        <label
                          className="flex cursor-pointer items-center gap-2 rounded-md px-2 py-1.5 text-xs hover:bg-accent/50"
                          key={eventType}
                        >
                          <input
                            checked={selectedEventTypes.includes(eventType)}
                            className="size-4 accent-primary"
                            onChange={() => onEventTypeToggle(eventType)}
                            type="checkbox"
                          />
                          <span className={`rounded-full px-1.5 py-0.5 font-mono ${eventTypeTone(eventType)}`}>
                            {eventType}
                          </span>
                        </label>
                      ))}
                    </div>
                    <div className="space-y-1 border-t pt-3">
                      <div className="flex items-center justify-between gap-2 px-1">
                        <div className="font-medium text-muted-foreground text-xs">Catalog</div>
                      </div>
                      {definitionError && (
                        <div className="rounded-md border border-red-500/30 bg-red-500/10 px-2 py-1.5 text-red-200 text-xs">
                          {definitionError}
                        </div>
                      )}
                      {definitionsLoading && definitions.length === 0 ? (
                        <div className="px-2 py-1.5 text-muted-foreground text-xs">Loading definitions.</div>
                      ) : definitions.length === 0 ? (
                        <div className="px-2 py-1.5 text-muted-foreground text-xs">No definitions loaded.</div>
                      ) : (
                        <div className="overflow-hidden rounded-md border">
                          <div className="flex h-9 min-w-0 items-center gap-1 border-b px-2">
                            <button
                              aria-label={canGoBackInCatalog ? "Back to parent category" : "Catalog root"}
                              className="flex size-7 shrink-0 items-center justify-center rounded-md hover:bg-accent hover:text-accent-foreground disabled:pointer-events-none disabled:opacity-40"
                              disabled={!canGoBackInCatalog}
                              onClick={goBackInCatalog}
                              type="button"
                            >
                              {canGoBackInCatalog ? <ChevronLeft className="size-4" /> : <Home className="size-4" />}
                            </button>
                            <span className="min-w-0 flex-1 truncate font-medium text-xs">
                              {catalogLabel}
                            </span>
                            <span className="shrink-0 text-muted-foreground text-[11px] tabular-nums">
                              {selectedDefinitionIds.length}
                            </span>
                          </div>
                          <div className="py-1">
                            {catalogLevel.categories.map((category) => (
                              <button
                                className="flex h-8 w-full min-w-0 items-center gap-2 px-2 text-left text-xs hover:bg-accent hover:text-accent-foreground"
                                key={category.path}
                                onClick={() => selectCatalogCategory(category.path)}
                                type="button"
                              >
                                <Folder className="size-4 shrink-0 text-muted-foreground" />
                                <span className="min-w-0 flex-1 truncate">{category.label}</span>
                                <span className="shrink-0 text-muted-foreground text-[11px] tabular-nums">
                                  {category.count}
                                </span>
                              </button>
                            ))}
                            {catalogLevel.definitions.map((definition) => (
                              <label
                                className="flex cursor-pointer items-start gap-2 px-2 py-1.5 text-xs hover:bg-accent/50"
                                key={definition.id.value}
                              >
                                <input
                                  checked={selectedDefinitionIds.includes(definition.id.value)}
                                  className="mt-0.5 size-4 accent-primary"
                                  onChange={() => onDefinitionToggle(definition.id.value)}
                                  type="checkbox"
                                />
                                <FileCode2 className="mt-0.5 size-4 shrink-0 text-muted-foreground" />
                                <span className="min-w-0">
                                  <span className="block truncate font-medium">{definition.name}</span>
                                  <span className="block truncate font-mono text-muted-foreground">{definition.id.value}</span>
                                </span>
                              </label>
                            ))}
                            {catalogLevel.categories.length === 0 && catalogLevel.definitions.length === 0 && (
                              <div className="px-2 py-2 text-muted-foreground text-xs">No catalog entries.</div>
                            )}
                          </div>
                        </div>
                      )}
                    </div>
                    <div className="space-y-2 border-t pt-3">
                      <div className="flex items-center justify-between gap-2 px-1">
                        <div className="font-medium text-muted-foreground text-xs">Keys</div>
                      </div>
                      <div className="grid gap-2">
                        <select
                          className="h-8 rounded-md border bg-background px-2 text-xs"
                          onChange={(event) => setKeyKind(event.currentTarget.value as WorkKeyKind | "Any")}
                          value={keyKind}
                        >
                          <option value="Any">Any key</option>
                          <option value="Subject">Subject</option>
                          <option value="ConcurrencyKey">Concurrency key</option>
                          <option value="Identifier">Identifier</option>
                        </select>
                        <input
                          className="h-8 rounded-md border bg-background px-2 font-mono text-xs"
                          onChange={(event) => setKeyType(event.currentTarget.value)}
                          onKeyDown={(event) => {
                            if (event.key === "Enter") {
                              addKey();
                            }
                          }}
                          placeholder="type"
                          value={keyType}
                        />
                        <input
                          className="h-8 rounded-md border bg-background px-2 font-mono text-xs"
                          onChange={(event) => setKeyValue(event.currentTarget.value)}
                          onKeyDown={(event) => {
                            if (event.key === "Enter") {
                              addKey();
                            }
                          }}
                          placeholder="value"
                          value={keyValue}
                        />
                        <Button className="h-8 text-xs" onClick={addKey} size="sm" variant="secondary">
                          Add key
                        </Button>
                      </div>
                      {selectedKeys.length > 0 && (
                        <div className="flex flex-wrap gap-1">
                          {selectedKeys.map((key) => (
                            <button
                              className="rounded-full border bg-muted/40 px-2 py-1 font-mono text-[11px] hover:bg-accent"
                              key={`${key.kind ?? "Any"}:${key.type}:${key.value}`}
                              onClick={() => onRemoveKey(key)}
                              type="button"
                            >
                              {(key.kind ?? "Any")}:{key.type}:{key.value}
                            </button>
                          ))}
                        </div>
                      )}
                    </div>
                  </div>
                  )}
                </div>
                <div className={`grid min-h-0 overflow-hidden rounded-md border ${
                  hasEventTable
                    ? "grid-rows-[auto_auto_auto_minmax(0,1fr)]"
                    : "grid-rows-[auto_minmax(0,1fr)]"
                }`}>
                  <div className="flex items-center justify-between gap-2 border-b px-3 py-2">
                    <div className="font-medium text-muted-foreground text-xs">Event JSON</div>
                    <div className="min-w-0 truncate font-mono text-muted-foreground text-xs">
                      {selectedMessage && selectedMessage.events.length > 1
                        ? `Event ${selectedEventIndexInBounds + 1}/${selectedMessage.events.length}`
                        : selectedEvent?.eventType ?? "No event selected"}
                    </div>
                  </div>
                  {hasEventTable && selectedMessage && (
                    <div
                      className="grid min-h-0 grid-rows-[auto_minmax(0,1fr)] border-b"
                      style={{ height: eventTableHeight }}
                    >
                      <div className="grid grid-cols-[4rem_minmax(8rem,1fr)_minmax(11rem,1.4fr)_minmax(8rem,1fr)] gap-2 border-b bg-muted/30 px-3 py-1.5 font-medium text-muted-foreground text-[11px]">
                        <span>#</span>
                        <span>Type</span>
                        <span>Worker</span>
                        <span>Definition</span>
                      </div>
                      <div className="min-h-0 overflow-auto">
                        {selectedMessage.events.map((workEvent, index) => (
                          <button
                            className={`grid w-full grid-cols-[4rem_minmax(8rem,1fr)_minmax(11rem,1.4fr)_minmax(8rem,1fr)] gap-2 px-3 py-1.5 text-left text-xs ${
                              index === selectedEventIndexInBounds
                                ? "bg-accent text-accent-foreground"
                                : "hover:bg-accent/50"
                            }`}
                            key={`${workEvent.eventType}:${workEvent.workerId?.value ?? "system"}:${index}`}
                            onClick={() => setSelectedEventIndex(index)}
                            onKeyDown={handleEventRowKeyDown}
                            ref={(element) => {
                              eventRowRefs.current[index] = element;
                            }}
                            type="button"
                          >
                            <span className="font-mono text-muted-foreground">{index + 1}</span>
                            <span className={`w-fit rounded-full px-1.5 py-0.5 font-mono ${eventTypeTone(workEvent.eventType)}`}>
                              {workEvent.eventType}
                            </span>
                            <span className="truncate font-mono text-muted-foreground">
                              {workEvent.workerId?.value ?? "-"}
                            </span>
                            <span className="truncate font-mono text-muted-foreground">
                              {workEvent.definitionId?.value ?? "-"}
                            </span>
                          </button>
                        ))}
                      </div>
                    </div>
                  )}
                  {hasEventTable && (
                    <div
                      aria-label="Resize event table"
                      className="group flex h-2 cursor-row-resize items-center justify-center border-b bg-muted/20 hover:bg-accent/40"
                      onPointerDown={startEventTableResize}
                      onPointerMove={resizeEventTable}
                      onPointerUp={stopEventTableResize}
                      role="separator"
                    >
                      <div className="h-px w-12 bg-border group-hover:bg-foreground/40" />
                    </div>
                  )}
                  <pre className="min-h-0 overflow-auto whitespace-pre-wrap break-words bg-muted/30 p-3 font-mono text-xs leading-relaxed">
                    {selectedEvent ? (
                      <JsonValue maxExpandedArrayItems={100} value={selectedEvent} />
                    ) : (
                      "Waiting for the first realtime event."
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

function EventInlineMetric({
  label,
  value,
  wide = false,
}: {
  label: string;
  value: string;
  wide?: boolean;
}) {
  return (
    <div className={`flex min-w-0 gap-1 ${wide ? "basis-full" : ""}`}>
      <span className="text-muted-foreground">{label}</span>
      <span className="truncate font-mono text-foreground">{value}</span>
    </div>
  );
}

function createEventViewerCatalogLevel(definitions: WorkDefinition[], path: string) {
  const normalizedPath = normalizeCategoryFilter(path);
  const pathSegments = splitCatalogPath(normalizedPath);
  const categoriesByPath = new Map<string, { count: number; label: string; path: string }>();
  const levelDefinitions: WorkDefinition[] = [];

  definitions.forEach((definition) => {
    const categorySegments = splitCatalogPath(definition.category);
    if (!startsWithEventViewerCatalogPath(categorySegments, pathSegments)) {
      return;
    }

    if (categorySegments.length > pathSegments.length) {
      const childSegments = categorySegments.slice(0, pathSegments.length + 1);
      const childPath = childSegments.join(":");
      const category = categoriesByPath.get(childPath) ?? {
        count: 0,
        label: childSegments.at(-1) ?? childPath,
        path: childPath,
      };
      category.count++;
      categoriesByPath.set(childPath, category);
      return;
    }

    levelDefinitions.push(definition);
  });

  return {
    categories: [...categoriesByPath.values()].sort((left, right) => left.label.localeCompare(right.label)),
    definitions: levelDefinitions.sort((left, right) => left.name.localeCompare(right.name)),
  };
}

function startsWithEventViewerCatalogPath(categorySegments: string[], pathSegments: string[]) {
  return pathSegments.every((segment, index) => categorySegments[index] === segment);
}

function formatEventBatchTypeSummary(eventTypes: string[]) {
  if (eventTypes.length === 0) {
    return "No event types";
  }

  return eventTypes.length === 1
    ? eventTypes[0]
    : `${eventTypes.length} types: ${eventTypes.slice(0, 3).join(", ")}${eventTypes.length > 3 ? ", ..." : ""}`;
}

function formatEventBatchDefinitionSummary(events: WorkableRealtimeEvent[]) {
  const definitionIds = [...new Set(events
    .map((workEvent) => workEvent.definitionId?.value)
    .filter((definitionId): definitionId is string => Boolean(definitionId)))];

  if (definitionIds.length === 0) {
    return "No definition";
  }

  return definitionIds.length === 1
    ? definitionIds[0]
    : `${definitionIds.length} definitions`;
}

function getCenteredEventViewerPosition(size: "compact" | "large") {
  if (typeof window === "undefined") {
    return { x: 0, y: 0 };
  }

  const viewportWidth = window.visualViewport?.width ?? window.innerWidth;
  const viewportHeight = window.visualViewport?.height ?? window.innerHeight;
  const width = size === "compact" ? Math.min(viewportWidth * 0.96, 768) : Math.min(viewportWidth * 0.96, 1536);
  const height = size === "compact" ? Math.min(viewportHeight * 0.82, 512) : Math.min(viewportHeight * 0.88, 896);

  return {
    x: Math.max(8, (viewportWidth - width) / 2),
    y: Math.max(8, (viewportHeight - height) / 2),
  };
}

function clampFloatingWindowPosition(value: number, viewport: number, size: number) {
  return Math.min(Math.max(8, value), Math.max(8, viewport - size - 8));
}

function clampEventTableHeight(value: number) {
  return Math.min(Math.max(96, value), 520);
}

function formatEventViewerTime(value: number) {
  return new Intl.DateTimeFormat(undefined, {
    hour: "numeric",
    minute: "2-digit",
    second: "2-digit",
  }).format(value);
}

function normalizeEventViewerMaxMessages(value: string) {
  const parsed = Number.parseInt(value, 10);
  if (Number.isNaN(parsed)) {
    return 100;
  }

  return Math.max(1, Math.min(1000, parsed));
}

function formatEventViewerFilterSummary(
  eventTypeCount: number,
  definitionCount: number,
  keyCount: number
) {
  const parts = [
    eventTypeCount > 0
      ? `${eventTypeCount} type${eventTypeCount === 1 ? "" : "s"}`
      : "No event types selected",
    definitionCount > 0 ? `${definitionCount} definition${definitionCount === 1 ? "" : "s"}` : null,
    keyCount > 0 ? `${keyCount} key${keyCount === 1 ? "" : "s"}` : null,
  ].filter(Boolean);

  return parts.join(", ");
}

function eventTypeTone(eventType: string) {
  if (eventType.includes("failed") || eventType === "worker.log") {
    return "border border-red-500/30 bg-red-500/10 text-red-200";
  }

  if (eventType.includes("completed")) {
    return "border border-emerald-500/30 bg-emerald-500/10 text-emerald-200";
  }

  if (eventType.includes("waiting") || eventType.includes("retrying")) {
    return "border border-amber-500/30 bg-amber-500/10 text-amber-100";
  }

  if (eventType.includes("purge") || eventType.includes("cancel")) {
    return "border border-sky-500/30 bg-sky-500/10 text-sky-200";
  }

  return "border border-border bg-muted/40 text-muted-foreground";
}

type SystemNotification = {
  description: string;
  id: string;
  rejectedWorkCount?: number;
  sourceId?: string;
  tone: "critical" | "warning";
  title: string;
};

function SystemNotificationTray({
  acknowledgedRejectedWorkCounts,
  activeSystemId,
  alertSources,
  concurrencyDetailDiagnostics,
  concurrencyExpanded,
  durabilityDetailDiagnostics,
  durabilityExpanded,
  idempotencyDetailDiagnostics,
  idempotencyExpanded,
  onAcknowledgeQueueRejections,
  onConcurrencyExpandedChange,
  onDurabilityExpandedChange,
  onIdempotencyExpandedChange,
  onOpenChange,
  onReadModelExpandedChange,
  onRetentionExpandedChange,
  open,
  readModelDetailDiagnostics,
  readModelExpanded,
  retentionDetailDiagnostics,
  retentionExpanded,
  systemName,
  trayDiagnostics,
}: {
  acknowledgedRejectedWorkCounts: Record<string, number>;
  activeSystemId: string;
  alertSources: DiagnosticsAlertSource[];
  concurrencyDetailDiagnostics: SystemDiagnosticsViewState;
  concurrencyExpanded: boolean;
  durabilityDetailDiagnostics: SystemDiagnosticsViewState;
  durabilityExpanded: boolean;
  idempotencyDetailDiagnostics: SystemDiagnosticsViewState;
  idempotencyExpanded: boolean;
  onAcknowledgeQueueRejections: (systemId: string, count: number) => void;
  onConcurrencyExpandedChange: (expanded: boolean) => void;
  onDurabilityExpandedChange: (expanded: boolean) => void;
  onIdempotencyExpandedChange: (expanded: boolean) => void;
  onOpenChange: (open: boolean) => void;
  onReadModelExpandedChange: (expanded: boolean) => void;
  onRetentionExpandedChange: (expanded: boolean) => void;
  open: boolean;
  readModelDetailDiagnostics: SystemDiagnosticsViewState;
  readModelExpanded: boolean;
  retentionDetailDiagnostics: SystemDiagnosticsViewState;
  retentionExpanded: boolean;
  systemName: string;
  trayDiagnostics: SystemDiagnosticsViewState;
}) {
  const activeAlertSource = alertSources.find((source) => source.target.systemId === activeSystemId);
  const alertReadModelCompact = getWorkComponentData<WorkReadModelDiagnosticsCompactComponent>(
    activeAlertSource?.data,
    "readModelDiagnostics"
  );
  const trayReadModelCompact = getWorkComponentData<WorkReadModelDiagnosticsCompactComponent>(
    trayDiagnostics.data,
    "readModelDiagnostics"
  );
  const detailedReadModel = getWorkComponentData<WorkReadModelDiagnosticsDetailedComponent>(
    readModelDetailDiagnostics.data,
    "readModelDiagnostics"
  );
  const alertRetentionCompact = getWorkComponentData<WorkRetentionDiagnosticsCompactComponent>(
    activeAlertSource?.data,
    "retentionDiagnostics"
  );
  const trayRetentionCompact = getWorkComponentData<WorkRetentionDiagnosticsCompactComponent>(
    trayDiagnostics.data,
    "retentionDiagnostics"
  );
  const detailedRetention = getWorkComponentData<WorkRetentionDiagnosticsDetailedComponent>(
    retentionDetailDiagnostics.data,
    "retentionDiagnostics"
  );
  const alertConcurrencyCompact = getWorkComponentData<WorkConcurrencyDiagnosticsCompactComponent>(
    activeAlertSource?.data,
    "concurrencyDiagnostics"
  );
  const trayConcurrencyCompact = getWorkComponentData<WorkConcurrencyDiagnosticsCompactComponent>(
    trayDiagnostics.data,
    "concurrencyDiagnostics"
  );
  const detailedConcurrency = getWorkComponentData<WorkConcurrencyDiagnosticsDetailedComponent>(
    concurrencyDetailDiagnostics.data,
    "concurrencyDiagnostics"
  );
  const alertDurabilityCompact = getWorkComponentData<WorkDurabilityDiagnosticsCompactComponent>(
    activeAlertSource?.data,
    "durabilityDiagnostics"
  );
  const trayDurabilityCompact = getWorkComponentData<WorkDurabilityDiagnosticsCompactComponent>(
    trayDiagnostics.data,
    "durabilityDiagnostics"
  );
  const detailedDurability = getWorkComponentData<WorkDurabilityDiagnosticsDetailedComponent>(
    durabilityDetailDiagnostics.data,
    "durabilityDiagnostics"
  );
  const trayIdempotencyCompact = getWorkComponentData<WorkIdempotencyDiagnosticsCompactComponent>(
    trayDiagnostics.data,
    "idempotencyDiagnostics"
  );
  const detailedIdempotency = getWorkComponentData<WorkIdempotencyDiagnosticsDetailedComponent>(
    idempotencyDetailDiagnostics.data,
    "idempotencyDiagnostics"
  );
  const readModelDetailCompact = createCompactReadModelDiagnosticsFromDetailed(detailedReadModel);
  const retentionDetailCompact = createCompactRetentionDiagnosticsFromDetailed(detailedRetention);
  const concurrencyDetailCompact = createCompactConcurrencyDiagnosticsFromDetailed(detailedConcurrency);
  const durabilityDetailCompact = createCompactDurabilityDiagnosticsFromDetailed(detailedDurability);
  const idempotencyDetailCompact = createCompactIdempotencyDiagnosticsFromDetailed(detailedIdempotency);
  const readModelCompact = open
    ? (readModelExpanded ? readModelDetailCompact ?? trayReadModelCompact : trayReadModelCompact) ?? alertReadModelCompact
    : alertReadModelCompact;
  const retentionCompact = open
    ? (retentionExpanded ? retentionDetailCompact ?? trayRetentionCompact : trayRetentionCompact) ?? alertRetentionCompact
    : alertRetentionCompact;
  const concurrencyCompact = open
    ? (concurrencyExpanded ? concurrencyDetailCompact ?? trayConcurrencyCompact : trayConcurrencyCompact) ?? alertConcurrencyCompact
    : alertConcurrencyCompact;
  const durabilityCompact = open
    ? (durabilityExpanded ? durabilityDetailCompact ?? trayDurabilityCompact : trayDurabilityCompact) ?? alertDurabilityCompact
    : alertDurabilityCompact;
  const idempotencyCompact = open
    ? (idempotencyExpanded ? idempotencyDetailCompact ?? trayIdempotencyCompact : trayIdempotencyCompact) ?? trayIdempotencyCompact
    : trayIdempotencyCompact;
  const notifications = alertSources.flatMap((source) =>
    createSystemNotifications(
      getWorkComponentData<WorkSystemDiagnosticsCompactComponent>(source.data, "systemDiagnostics"),
      getWorkComponentData<WorkQueueDiagnosticsCompactComponent>(source.data, "queueDiagnostics"),
      acknowledgedRejectedWorkCounts[source.target.systemId] ?? 0,
      getWorkComponentData<WorkReadModelDiagnosticsCompactComponent>(source.data, "readModelDiagnostics"),
      getWorkComponentData<WorkRetentionDiagnosticsCompactComponent>(source.data, "retentionDiagnostics"),
      getWorkComponentData<WorkConcurrencyDiagnosticsCompactComponent>(source.data, "concurrencyDiagnostics"),
      getWorkComponentData<WorkDurabilityDiagnosticsCompactComponent>(source.data, "durabilityDiagnostics"),
      source.error,
      source.target
    )
  );
  const hasNotifications = notifications.length > 0;
  const hasCriticalNotifications = notifications.some((notification) => notification.tone === "critical");
  const busy = alertSources.some((source) => source.loading || source.refreshing) ||
    (open && !readModelExpanded && !retentionExpanded && !concurrencyExpanded && !durabilityExpanded && !idempotencyExpanded && (trayDiagnostics.loading || trayDiagnostics.refreshing)) ||
    (readModelExpanded && (readModelDetailDiagnostics.loading || readModelDetailDiagnostics.refreshing)) ||
    (retentionExpanded && (retentionDetailDiagnostics.loading || retentionDetailDiagnostics.refreshing)) ||
    (concurrencyExpanded && (concurrencyDetailDiagnostics.loading || concurrencyDetailDiagnostics.refreshing)) ||
    (durabilityExpanded && (durabilityDetailDiagnostics.loading || durabilityDetailDiagnostics.refreshing)) ||
    (idempotencyExpanded && (idempotencyDetailDiagnostics.loading || idempotencyDetailDiagnostics.refreshing));
  const connectedAlertCount = alertSources.filter((source) => source.connectionState === "connected").length;
  const alertSubscriptionText = alertSources.length > 0
    ? `${connectedAlertCount}/${alertSources.length} alert streams connected`
    : "No realtime alert streams";
  const readModelLastUpdatedAt = readModelDetailDiagnostics.data?.generatedAt
    ? new Date(readModelDetailDiagnostics.data.generatedAt)
    : undefined;
  const retentionLastUpdatedAt = retentionDetailDiagnostics.data?.generatedAt
    ? new Date(retentionDetailDiagnostics.data.generatedAt)
    : undefined;
  const concurrencyLastUpdatedAt = concurrencyDetailDiagnostics.data?.generatedAt
    ? new Date(concurrencyDetailDiagnostics.data.generatedAt)
    : undefined;
  const durabilityLastUpdatedAt = durabilityDetailDiagnostics.data?.generatedAt
    ? new Date(durabilityDetailDiagnostics.data.generatedAt)
    : undefined;
  const idempotencyLastUpdatedAt = idempotencyDetailDiagnostics.data?.generatedAt
    ? new Date(idempotencyDetailDiagnostics.data.generatedAt)
    : undefined;

  return (
    <Popover onOpenChange={onOpenChange} open={open}>
      <Tooltip delayDuration={500} disableHoverableContent>
        <TooltipTrigger asChild>
          <PopoverTrigger asChild>
            <Button
              aria-label="System notifications"
              className={`relative ${hasCriticalNotifications ? "text-red-400 hover:text-red-300" : hasNotifications ? "text-amber-400 hover:text-amber-300" : "text-muted-foreground hover:text-foreground"} hover:bg-transparent dark:hover:bg-transparent`}
              size="icon-sm"
              variant="ghost"
            >
              {hasNotifications ? (
                <CircleAlert className="size-4" />
              ) : (
                <Bell className="size-4" />
              )}
              {hasNotifications && (
                <span className={`absolute right-0.5 top-0.5 flex min-w-3 translate-x-1/4 -translate-y-1/4 items-center justify-center rounded-full border border-background px-0.5 text-[9px] font-semibold leading-3 ${hasCriticalNotifications ? "bg-red-500 text-white" : "bg-amber-400 text-black"}`}>
                  {notifications.length}
                </span>
              )}
            </Button>
          </PopoverTrigger>
        </TooltipTrigger>
        <TooltipContent side="bottom" sideOffset={6}>
          System notifications
        </TooltipContent>
      </Tooltip>
      <PopoverContent align="end" className="w-[min(420px,calc(100vw-2rem))] gap-0 p-0">
        <div className="flex items-center justify-between gap-3 border-b px-3 py-2">
          <div className="min-w-0">
            <div className="font-medium text-sm">System notifications</div>
            <div className="truncate text-muted-foreground text-xs">
              {alertSubscriptionText} - details: {systemName}
            </div>
          </div>
          {busy && <Loader2 className="size-4 shrink-0 animate-spin text-muted-foreground" />}
        </div>
        <div className="max-h-[70vh] overflow-auto">
          <div className="space-y-2 border-b p-3">
            {alertSources.some((source) => source.loading) && notifications.length === 0 ? (
              <div className="flex items-center gap-2 text-muted-foreground text-sm">
                <Loader2 className="size-4 animate-spin" />
                Loading diagnostics.
              </div>
            ) : notifications.length > 0 ? (
              notifications.map((notification) => (
                <div
                  className={`rounded-md border px-3 py-2 ${notification.tone === "critical" ? "border-red-500/30 bg-red-500/10 text-red-200" : "border-amber-500/30 bg-amber-500/10 text-amber-100"}`}
                  key={notification.id}
                >
                  <div className="flex items-start gap-2">
                    <CircleAlert className="mt-0.5 size-4 shrink-0" />
                    <div className="min-w-0">
                      <div className="font-medium text-sm">{notification.title}</div>
                      <div className="text-xs opacity-85">{notification.description}</div>
                      {notification.sourceId && notification.rejectedWorkCount !== undefined ? (
                        <Button
                          className="mt-2 border-red-500/30 bg-red-500/10 text-red-100 hover:bg-red-500/20 hover:text-red-50"
                          onClick={() => {
                            if (notification.sourceId && notification.rejectedWorkCount !== undefined) {
                              onAcknowledgeQueueRejections(notification.sourceId, notification.rejectedWorkCount);
                            }
                          }}
                          size="xs"
                          variant="outline"
                        >
                          Acknowledge
                        </Button>
                      ) : null}
                    </div>
                  </div>
                </div>
              ))
            ) : (
              <div className="rounded-md border border-border bg-muted/20 px-3 py-2 text-muted-foreground text-sm">
                No system notifications.
              </div>
            )}
          </div>
          <ReadModelDiagnosticsSummary
            compact={readModelCompact}
            expanded={readModelExpanded}
            lastUpdatedAt={readModelLastUpdatedAt}
            loading={readModelDetailDiagnostics.loading && !detailedReadModel}
            onExpandedChange={onReadModelExpandedChange}
            readModel={detailedReadModel?.readModel}
          />
          <RetentionDiagnosticsSummary
            compact={retentionCompact}
            expanded={retentionExpanded}
            lastUpdatedAt={retentionLastUpdatedAt}
            loading={retentionDetailDiagnostics.loading && !detailedRetention}
            onExpandedChange={onRetentionExpandedChange}
            retention={detailedRetention?.retention}
          />
          <ConcurrencyDiagnosticsSummary
            compact={concurrencyCompact}
            concurrency={detailedConcurrency?.concurrency}
            expanded={concurrencyExpanded}
            lastUpdatedAt={concurrencyLastUpdatedAt}
            loading={concurrencyDetailDiagnostics.loading && !detailedConcurrency}
            onExpandedChange={onConcurrencyExpandedChange}
          />
          <DurabilityDiagnosticsSummary
            compact={durabilityCompact}
            durability={detailedDurability?.durability}
            expanded={durabilityExpanded}
            lastUpdatedAt={durabilityLastUpdatedAt}
            loading={durabilityDetailDiagnostics.loading && !detailedDurability}
            onExpandedChange={onDurabilityExpandedChange}
          />
          <IdempotencyDiagnosticsSummary
            compact={idempotencyCompact}
            expanded={idempotencyExpanded}
            idempotency={detailedIdempotency?.idempotency}
            lastUpdatedAt={idempotencyLastUpdatedAt}
            loading={idempotencyDetailDiagnostics.loading && !detailedIdempotency}
            onExpandedChange={onIdempotencyExpandedChange}
          />
        </div>
      </PopoverContent>
    </Popover>
  );
}

function ReadModelDiagnosticsSummary({
  compact,
  expanded,
  lastUpdatedAt,
  loading,
  onExpandedChange,
  readModel,
}: {
  compact?: WorkReadModelDiagnosticsCompactComponent;
  expanded: boolean;
  lastUpdatedAt?: Date;
  loading: boolean;
  onExpandedChange: (expanded: boolean) => void;
  readModel?: WorkSystemReadModelDiagnostics;
}) {
  return (
    <div className="border-b p-3 last:border-b-0">
      <button
        className="flex w-full items-center justify-between gap-3 text-left"
        onClick={() => onExpandedChange(!expanded)}
        type="button"
      >
        <div className="flex min-w-0 items-center gap-2">
          <ChevronRight className={`size-4 shrink-0 transition-transform ${expanded ? "rotate-90" : ""}`} />
          <div className="min-w-0">
            <div className="font-medium text-sm">Read model diagnostics</div>
            <div className="truncate text-muted-foreground text-xs">
              Pending {formatNumber(compact?.pendingUpdateCount)}
              {compact?.isReadModelBehind
                ? `, threshold ${formatNumber(compact.readModelLagWarningThreshold)}`
                : ""}
            </div>
          </div>
        </div>
        <div className="shrink-0 text-muted-foreground text-xs">
          {expanded && lastUpdatedAt ? formatLocalTime(lastUpdatedAt) : expanded ? "Waiting" : "Collapsed"}
        </div>
      </button>
      {expanded && (
        <div className="mt-3 space-y-2">
          {loading && !readModel ? (
            <div className="flex items-center gap-2 rounded-md border border-border px-3 py-2 text-muted-foreground text-sm">
              <Loader2 className="size-4 animate-spin" />
              Loading read model diagnostics.
            </div>
          ) : null}
          {!loading && !readModel ? (
            <div className="rounded-md border border-border bg-muted/20 px-3 py-2 text-muted-foreground text-sm">
              Expand this section while realtime is connected to load read model diagnostics.
            </div>
          ) : null}
          {readModel && (
            <>
              <div className="grid grid-cols-2 gap-2 text-xs">
                <DiagnosticsMetric
                  label="Pending"
                  description="How many read-model updates are still waiting to be projected. If this keeps growing, queries may be looking at stale projected data."
                  tone={compact?.isReadModelBehind ? "warning" : undefined}
                  value={formatNumber(readModel?.pendingUpdateCount)}
                />
                <DiagnosticsMetric
                  label="Last batch"
                  description="How many updates were applied in the most recent projection batch. This helps show whether the projector is making meaningful progress."
                  value={formatNumber(readModel?.lastBatchSize)}
                />
                <DiagnosticsMetric
                  label="Enqueued"
                  description="The latest sequence number handed to the read-model pipeline. Compare this with Applied to understand how much projection work is still outstanding."
                  value={formatNumber(readModel?.enqueuedSequence)}
                />
                <DiagnosticsMetric
                  label="Applied"
                  description="The latest sequence number successfully projected into the read model. If this lags behind Enqueued, the projector is behind."
                  value={formatNumber(readModel?.appliedSequence)}
                />
                <DiagnosticsMetric
                  label="Snapshots"
                  description="How many read-model snapshots have been published. This is mostly useful for confirming the projection pipeline has been actively emitting updated views."
                  value={formatNumber(readModel?.publishedSnapshotCount)}
                />
                <DiagnosticsMetric
                  label="Last projection"
                  description="How long the most recent projection pass took. Rising durations can hint at expensive projection work or downstream pressure."
                  value={formatDuration(readModel?.lastProjectionDuration)}
                />
              </div>
              <div className="rounded-md border border-border px-3 py-2 text-xs">
                <div className="flex items-center justify-between gap-3">
                  <span className="text-muted-foreground">
                    <TooltipLabel
                      description="When the projector last finished applying updates. If this goes stale while Pending grows, the read-model pipeline may be stuck."
                      label="Last projected"
                    />
                  </span>
                  <span className="min-w-0 truncate font-mono">
                    {formatDateTimeShort(readModel?.lastProjectedAt)}
                  </span>
                </div>
              </div>
            </>
          )}
        </div>
      )}
    </div>
  );
}

function RetentionDiagnosticsSummary({
  compact,
  expanded,
  lastUpdatedAt,
  loading,
  onExpandedChange,
  retention,
}: {
  compact?: WorkRetentionDiagnosticsCompactComponent;
  expanded: boolean;
  lastUpdatedAt?: Date;
  loading: boolean;
  onExpandedChange: (expanded: boolean) => void;
  retention?: WorkSystemRetentionDiagnostics;
}) {
  return (
    <div className="border-b p-3 last:border-b-0">
      <button
        className="flex w-full items-center justify-between gap-3 text-left"
        onClick={() => onExpandedChange(!expanded)}
        type="button"
      >
        <div className="flex min-w-0 items-center gap-2">
          <ChevronRight className={`size-4 shrink-0 transition-transform ${expanded ? "rotate-90" : ""}`} />
          <div className="min-w-0">
            <div className="font-medium text-sm">Retention diagnostics</div>
            <div className="truncate text-muted-foreground text-xs">
              Scheduled {formatNumber(compact?.scheduledPurgeCount)}, tracked final {formatNumber(compact?.trackedFinalWorkerCount)}
            </div>
          </div>
        </div>
        <div className="shrink-0 text-muted-foreground text-xs">
          {expanded && lastUpdatedAt ? formatLocalTime(lastUpdatedAt) : expanded ? "Waiting" : "Collapsed"}
        </div>
      </button>
      {expanded && (
        <div className="mt-3 space-y-2">
          {loading && !retention ? (
            <div className="flex items-center gap-2 rounded-md border border-border px-3 py-2 text-muted-foreground text-sm">
              <Loader2 className="size-4 animate-spin" />
              Loading retention diagnostics.
            </div>
          ) : null}
          {!loading && !retention ? (
            <div className="rounded-md border border-border bg-muted/20 px-3 py-2 text-muted-foreground text-sm">
              Expand this section while realtime is connected to load retention diagnostics.
            </div>
          ) : null}
          {retention && (
            <>
              <div className="grid grid-cols-2 gap-2 text-xs">
                <DiagnosticsMetric
                  label="Tracked final"
                  description="How many final workers retention is still tracking. This is the pool retention may purge from over time."
                  value={formatNumber(retention.trackedFinalWorkerCount)}
                />
                <DiagnosticsMetric
                  label="Scheduled"
                  description="How many purge operations are currently scheduled. Growth here can be normal briefly, but a large lingering queue means retention may be behind."
                  tone={compact?.isRetentionBehind ? "warning" : undefined}
                  value={formatNumber(retention.scheduledPurgeCount)}
                />
                <DiagnosticsMetric
                  label="High water"
                  description="The largest scheduled purge queue size seen so far. Useful for understanding how large retention backlog has gotten under load."
                  value={formatNumber(retention.scheduledPurgeHighWaterMark)}
                />
                <DiagnosticsMetric
                  label="Overdue age"
                  description="How late the oldest due purge currently is. This is the clearest signal that retention cleanup is falling behind."
                  tone={compact?.isRetentionBehind ? "warning" : undefined}
                  value={formatDuration(retention.oldestDuePurgeAge)}
                />
                <DiagnosticsMetric
                  label="Last purged"
                  description="How many workers were purged in the most recent retention run. This helps show whether the scheduler is actively clearing backlog."
                  value={formatNumber(retention.lastPurgedCount)}
                />
                <DiagnosticsMetric
                  label="Total purged"
                  description="Lifetime total of workers purged by retention. Useful for rough workload context, but less important than current backlog."
                  value={formatNumber(retention.totalPurgedCount)}
                />
              </div>
              <div className="rounded-md border border-border px-3 py-2 text-xs">
                <div className="flex items-center justify-between gap-3">
                  <span className="text-muted-foreground">
                    <TooltipLabel
                      description="When the oldest queued purge was originally due. If this is far in the past, retention work is overdue."
                      label="Oldest scheduled purge"
                    />
                  </span>
                  <span className="min-w-0 truncate font-mono">
                    {formatDateTimeShort(retention.oldestScheduledPurgeDueAt)}
                  </span>
                </div>
                <div className="mt-1 flex items-center justify-between gap-3">
                  <span className="text-muted-foreground">
                    <TooltipLabel
                      description="When the retention scheduler last completed a run. If this goes stale while Scheduled or Overdue age grows, the scheduler may be stalled."
                      label="Last run"
                    />
                  </span>
                  <span className="min-w-0 truncate font-mono">
                    {formatDateTimeShort(retention.lastRunAt)}
                  </span>
                </div>
              </div>
            </>
          )}
        </div>
      )}
    </div>
  );
}

function ConcurrencyDiagnosticsSummary({
  compact,
  concurrency,
  expanded,
  lastUpdatedAt,
  loading,
  onExpandedChange,
}: {
  compact?: WorkConcurrencyDiagnosticsCompactComponent;
  concurrency?: WorkSystemConcurrencyDiagnostics;
  expanded: boolean;
  lastUpdatedAt?: Date;
  loading: boolean;
  onExpandedChange: (expanded: boolean) => void;
}) {
  return (
    <div className="border-b p-3 last:border-b-0">
      <button
        className="flex w-full items-center justify-between gap-3 text-left"
        onClick={() => onExpandedChange(!expanded)}
        type="button"
      >
        <div className="flex min-w-0 items-center gap-2">
          <ChevronRight className={`size-4 shrink-0 transition-transform ${expanded ? "rotate-90" : ""}`} />
          <div className="min-w-0">
            <div className="font-medium text-sm">Concurrency diagnostics</div>
            <div className="truncate text-muted-foreground text-xs">
              Deferred {formatNumber(compact?.deferredStartCount)}, oldest {formatDuration(compact?.oldestDeferredStartAge)}
            </div>
          </div>
        </div>
        <div className="shrink-0 text-muted-foreground text-xs">
          {expanded && lastUpdatedAt ? formatLocalTime(lastUpdatedAt) : expanded ? "Waiting" : "Collapsed"}
        </div>
      </button>
      {expanded && (
        <div className="mt-3 space-y-2">
          {loading && !concurrency ? (
            <div className="flex items-center gap-2 rounded-md border border-border px-3 py-2 text-muted-foreground text-sm">
              <Loader2 className="size-4 animate-spin" />
              Loading concurrency diagnostics.
            </div>
          ) : null}
          {!loading && !concurrency ? (
            <div className="rounded-md border border-border bg-muted/20 px-3 py-2 text-muted-foreground text-sm">
              Expand this section while realtime is connected to load concurrency diagnostics.
            </div>
          ) : null}
          {concurrency && (
            <>
              <div className="grid grid-cols-2 gap-2 text-xs">
                <DiagnosticsMetric
                  label="Deferred"
                  description="How many workers are currently blocked by concurrency and waiting to start. Rising counts usually mean capacity is saturated or work is not draining."
                  tone={compact?.isConcurrencyBehind ? "warning" : undefined}
                  value={formatNumber(concurrency.deferredStartCount)}
                />
                <DiagnosticsMetric
                  label="Oldest age"
                  description="How long the longest-waiting deferred worker has been stuck. This is the clearest signal that concurrency backlog is becoming unhealthy."
                  tone={compact?.isConcurrencyBehind ? "warning" : undefined}
                  value={formatDuration(concurrency.oldestDeferredStartAge)}
                />
                <DiagnosticsMetric
                  label="Last released"
                  description="How many deferred workers were released in the most recent drain. Low or zero release counts can explain why backlog is not shrinking."
                  value={formatNumber(concurrency.lastDrainReleasedCount)}
                />
              </div>
              <div className={`rounded-md border px-3 py-2 text-xs ${
                compact?.isConcurrencyBehind
                  ? "border-amber-500/30 bg-amber-500/10"
                  : "border-border bg-muted/10"
              }`}>
                <div className="flex items-center justify-between gap-3">
                  <TooltipLabel
                    description="A quick status view of whether deferred workers are waiting longer than the configured warning threshold."
                    label="Backlog state"
                  />
                  <span className="font-medium">
                    {compact?.isConcurrencyBehind ? "Warning" : "Healthy"}
                  </span>
                </div>
                <div className="mt-1 text-muted-foreground">
                  {compact?.isConcurrencyBehind
                    ? `Oldest deferred start has been waiting longer than ${formatNumber(compact?.concurrencyLagWarningSeconds)} seconds.`
                    : "Deferred starts are within the warning threshold."}
                </div>
              </div>
              <div className="rounded-md border border-border px-3 py-2 text-xs">
                Concurrency diagnostics are intentionally limited to current backlog and the most recent drain result.
              </div>
            </>
          )}
        </div>
      )}
    </div>
  );
}

function DurabilityDiagnosticsSummary({
  compact,
  durability,
  expanded,
  lastUpdatedAt,
  loading,
  onExpandedChange,
}: {
  compact?: WorkDurabilityDiagnosticsCompactComponent;
  durability?: WorkSystemDurabilityDiagnostics;
  expanded: boolean;
  lastUpdatedAt?: Date;
  loading: boolean;
  onExpandedChange: (expanded: boolean) => void;
}) {
  return (
    <div className="border-b p-3 last:border-b-0">
      <button
        className="flex w-full items-center justify-between gap-3 text-left"
        onClick={() => onExpandedChange(!expanded)}
        type="button"
      >
        <div className="flex min-w-0 items-center gap-2">
          <ChevronRight className={`size-4 shrink-0 transition-transform ${expanded ? "rotate-90" : ""}`} />
          <div className="min-w-0">
            <div className="font-medium text-sm">Durability diagnostics</div>
            <div className="truncate text-muted-foreground text-xs">
              Waiters {formatNumber(compact?.acceptedWaiterCount)}, cleanup {formatNumber(compact?.pendingCleanupCount)}
            </div>
          </div>
        </div>
        <div className="shrink-0 text-muted-foreground text-xs">
          {expanded && lastUpdatedAt ? formatLocalTime(lastUpdatedAt) : expanded ? "Waiting" : "Collapsed"}
        </div>
      </button>
      {expanded && (
        <div className="mt-3 space-y-2">
          {loading && !durability ? (
            <div className="flex items-center gap-2 rounded-md border border-border px-3 py-2 text-muted-foreground text-sm">
              <Loader2 className="size-4 animate-spin" />
              Loading durability diagnostics.
            </div>
          ) : null}
          {!loading && !durability ? (
            <div className="rounded-md border border-border bg-muted/20 px-3 py-2 text-muted-foreground text-sm">
              Expand this section while realtime is connected to load durability diagnostics.
            </div>
          ) : null}
          {durability && (
            <>
              <div className="grid grid-cols-2 gap-2 text-xs">
                <DiagnosticsMetric
                  label="Accepted waiters"
                  description="How many accepted durable requests are still waiting to materialize into in-memory workers. If this grows, callers are waiting on the durability path."
                  tone={compact?.isAcceptedWorkerMaterializationBehind ? "warning" : undefined}
                  value={formatNumber(durability.acceptedWaiterCount)}
                />
                <DiagnosticsMetric
                  label="Waiter age"
                  description="How long the oldest accepted durable request has been waiting to materialize. This is the main signal that durable worker creation is unhealthy."
                  tone={compact?.isAcceptedWorkerMaterializationBehind ? "warning" : undefined}
                  value={formatDuration(durability.oldestAcceptedWaiterAge)}
                />
                <DiagnosticsMetric
                  label="Pending cleanup"
                  description="How many durable cleanup actions are queued. If this climbs, final durable rows are not being cleaned up promptly."
                  tone={compact?.isCleanupBehind ? "warning" : undefined}
                  value={formatNumber(durability.pendingCleanupCount)}
                />
                <DiagnosticsMetric
                  label="Cleanup age"
                  description="How long the oldest queued durable cleanup item has been waiting. This is the clearest signal that cleanup is falling behind."
                  tone={compact?.isCleanupBehind ? "warning" : undefined}
                  value={formatDuration(durability.oldestPendingCleanupAge)}
                />
              </div>
              <div className={`rounded-md border px-3 py-2 text-xs ${
                compact?.hasReaderFailure || compact?.hasLeaseRenewalFailure || compact?.hasCleanupFailure ||
                compact?.isAcceptedWorkerMaterializationBehind || compact?.isCleanupBehind
                  ? "border-amber-500/30 bg-amber-500/10"
                  : "border-border bg-muted/10"
              }`}>
                <div className="flex items-center justify-between gap-3">
                  <TooltipLabel
                    description="Summarizes whether the durability background loops are healthy. Reader, lease renewal, or cleanup failures here usually need investigation."
                    label="Health"
                  />
                  <span className="font-medium">
                    {compact?.hasReaderFailure || compact?.hasLeaseRenewalFailure || compact?.hasCleanupFailure ||
                    compact?.isAcceptedWorkerMaterializationBehind || compact?.isCleanupBehind
                      ? "Warning"
                      : "Healthy"}
                  </span>
                </div>
                <div className="mt-1 text-muted-foreground">
                  Reader {compact?.hasReaderFailure ? "failed" : "ok"}; lease renewal {compact?.hasLeaseRenewalFailure ? "failed" : "ok"}; cleanup {compact?.hasCleanupFailure ? "failed" : "ok"}.
                </div>
                {compact?.isAcceptedWorkerMaterializationBehind ? (
                  <div className="mt-1 text-muted-foreground">
                    Accepted workers are waiting longer than {formatNumber(compact?.acceptedWorkerWarningSeconds)} seconds to materialize.
                  </div>
                ) : null}
                {compact?.isCleanupBehind ? (
                  <div className="mt-1 text-muted-foreground">
                    Cleanup backlog is older than {formatNumber(compact?.cleanupWarningSeconds)} seconds.
                  </div>
                ) : null}
              </div>
            </>
          )}
        </div>
      )}
    </div>
  );
}

function IdempotencyDiagnosticsSummary({
  compact,
  expanded,
  idempotency,
  lastUpdatedAt,
  loading,
  onExpandedChange,
}: {
  compact?: WorkIdempotencyDiagnosticsCompactComponent;
  expanded: boolean;
  idempotency?: WorkSystemIdempotencyDiagnostics;
  lastUpdatedAt?: Date;
  loading: boolean;
  onExpandedChange: (expanded: boolean) => void;
}) {
  return (
    <div className="border-b p-3 last:border-b-0">
      <button
        className="flex w-full items-center justify-between gap-3 text-left"
        onClick={() => onExpandedChange(!expanded)}
        type="button"
      >
        <div className="flex min-w-0 items-center gap-2">
          <ChevronRight className={`size-4 shrink-0 transition-transform ${expanded ? "rotate-90" : ""}`} />
          <div className="min-w-0">
            <div className="font-medium text-sm">Idempotency diagnostics</div>
            <div className="truncate text-muted-foreground text-xs">
              Duplicate rejects {formatNumber(compact?.duplicateRejectionCount)}, storage {compact?.lastDuplicateRejectedStorage ?? "-"}
            </div>
          </div>
        </div>
        <div className="shrink-0 text-muted-foreground text-xs">
          {expanded && lastUpdatedAt ? formatLocalTime(lastUpdatedAt) : expanded ? "Waiting" : "Collapsed"}
        </div>
      </button>
      {expanded && (
        <div className="mt-3 space-y-2">
          {loading && !idempotency ? (
            <div className="flex items-center gap-2 rounded-md border border-border px-3 py-2 text-muted-foreground text-sm">
              <Loader2 className="size-4 animate-spin" />
              Loading idempotency diagnostics.
            </div>
          ) : null}
          {!loading && !idempotency ? (
            <div className="rounded-md border border-border bg-muted/20 px-3 py-2 text-muted-foreground text-sm">
              Expand this section while realtime is connected to load idempotency diagnostics.
            </div>
          ) : null}
          {idempotency && (
            <>
              <div className="grid grid-cols-2 gap-2 text-xs">
                <DiagnosticsMetric
                  label="Duplicate rejects"
                  description="How many queue requests were rejected as duplicates by idempotency protection. Useful for spotting duplicate traffic or unexpected replays."
                  value={formatNumber(idempotency.duplicateRejectionCount)}
                />
                <DiagnosticsMetric
                  label="Last storage"
                  description="Where the most recent duplicate rejection was detected. This tells you whether local or persistence-backed idempotency caught it."
                  value={idempotency.lastDuplicateRejectedStorage ?? "-"}
                />
              </div>
            </>
          )}
        </div>
      )}
    </div>
  );
}

function DiagnosticsMetric({
  description,
  label,
  tone,
  value,
}: {
  description?: string;
  label: string;
  tone?: "warning";
  value: string;
}) {
  return (
    <div className={`rounded-md border px-3 py-2 ${tone === "warning" ? "border-amber-500/30 bg-amber-500/10" : "border-border"}`}>
      <div className="text-muted-foreground">
        <TooltipLabel description={description} label={label} />
      </div>
      <div className="truncate font-mono text-foreground">{value}</div>
    </div>
  );
}

function TooltipLabel({
  description,
  label,
}: {
  description?: string;
  label: string;
}) {
  if (!description) {
    return <span>{label}</span>;
  }

  return (
    <span className="inline-flex items-center gap-1">
      <span>{label}</span>
      <Tooltip delayDuration={500} disableHoverableContent>
        <TooltipTrigger asChild>
          <button
            aria-label={`${label} explanation`}
            className="inline-flex size-3.5 items-center justify-center rounded text-muted-foreground transition-colors hover:text-foreground"
            type="button"
          >
            <Info className="size-3" />
          </button>
        </TooltipTrigger>
        <TooltipContent className="max-w-64 whitespace-normal text-left" side="top" sideOffset={6}>
          {description}
        </TooltipContent>
      </Tooltip>
    </span>
  );
}

function createSystemNotifications(
  system?: WorkSystemDiagnosticsCompactComponent,
  queue?: WorkQueueDiagnosticsCompactComponent,
  acknowledgedRejectedWorkCount = 0,
  readModel?: WorkReadModelDiagnosticsCompactComponent,
  retention?: WorkRetentionDiagnosticsCompactComponent,
  concurrency?: WorkConcurrencyDiagnosticsCompactComponent,
  durability?: WorkDurabilityDiagnosticsCompactComponent,
  error?: string,
  source?: DiagnosticsAlertTarget
): SystemNotification[] {
  const notifications: SystemNotification[] = [];
  const sourcePrefix = source ? `${source.displayName}: ` : "";
  const sourceSuffix = source ? ` on ${source.displayName}` : "";

  if (system?.isShuttingDown) {
    notifications.push({
      description: `Workable is shutting down${sourceSuffix}. Active workers are being asked to stop.`,
      id: `${source?.systemId ?? "active"}:system-stopping`,
      tone: "warning",
      title: `${sourcePrefix}System is shutting down`,
    });
  }

  if (error) {
    notifications.push({
      description: error,
      id: `${source?.systemId ?? "active"}:diagnostics-unavailable`,
      tone: "warning",
      title: `${sourcePrefix}Diagnostics unavailable`,
    });
  }

  const hasUnacknowledgedRejectedWork = queue?.hasAlertableRejectedWork &&
    queue.alertableRejectedWorkCount !== acknowledgedRejectedWorkCount;
  if (queue && hasUnacknowledgedRejectedWork) {
    const rejectedWorkCount = queue.alertableRejectedWorkCount > acknowledgedRejectedWorkCount
      ? queue.alertableRejectedWorkCount - acknowledgedRejectedWorkCount
      : queue.alertableRejectedWorkCount;
    notifications.push({
      description: `${formatNumber(rejectedWorkCount)} new alertable queue rejection${rejectedWorkCount === 1 ? "" : "s"} (${formatNumber(queue.alertableRejectedWorkCount)} total)${queue.lastAlertableRejectedMessage ? `. Last: ${queue.lastAlertableRejectedMessage}` : "."}`,
      id: `${source?.systemId ?? "active"}:queue-rejections`,
      rejectedWorkCount: queue.alertableRejectedWorkCount,
      sourceId: source?.systemId,
      tone: "critical",
      title: `${sourcePrefix}Work is being rejected`,
    });
  }

  if (readModel?.hasProjectorFailure) {
    notifications.push({
      description: `${readModel.projectorFailureType ?? "Projector failure"}${readModel.projectorFailureMessage ? `: ${readModel.projectorFailureMessage}` : ""}`,
      id: `${source?.systemId ?? "active"}:read-model-failure`,
      tone: "critical",
      title: `${sourcePrefix}Read model projector failed`,
    });
  }

  if (readModel?.isReadModelBehind) {
    notifications.push({
      description: `${formatNumber(readModel.pendingUpdateCount)} update${readModel.pendingUpdateCount === 1 ? "" : "s"} waiting to be projected${sourceSuffix}.`,
      id: `${source?.systemId ?? "active"}:read-model-lag`,
      tone: readModel.pendingUpdateCount >= readModel.readModelLagWarningThreshold * 10
        ? "critical"
        : "warning",
      title: `${sourcePrefix}Read model is behind`,
    });
  }

  if (retention?.hasSchedulerFailure) {
    notifications.push({
      description: `${retention.schedulerFailureType ?? "Retention scheduler failure"}${retention.schedulerFailureMessage ? `: ${retention.schedulerFailureMessage}` : ""}`,
      id: `${source?.systemId ?? "active"}:retention-failure`,
      tone: "critical",
      title: `${sourcePrefix}Retention scheduler failed`,
    });
  }

  if (retention?.isRetentionBehind) {
    notifications.push({
      description: `Oldest due purge is overdue by ${formatDuration(retention.oldestDuePurgeAge)}${sourceSuffix}.`,
      id: `${source?.systemId ?? "active"}:retention-lag`,
      tone: parseDurationSeconds(retention.oldestDuePurgeAge) >= retention.retentionLagWarningSeconds * 10
        ? "critical"
        : "warning",
      title: `${sourcePrefix}Retention is behind`,
    });
  }

  if (concurrency?.isConcurrencyBehind) {
    notifications.push({
      description: `${formatNumber(concurrency.deferredStartCount)} deferred worker${concurrency.deferredStartCount === 1 ? "" : "s"} waiting, oldest deferred for ${formatDuration(concurrency.oldestDeferredStartAge)}${sourceSuffix}.`,
      id: `${source?.systemId ?? "active"}:concurrency-lag`,
      tone: parseDurationSeconds(concurrency.oldestDeferredStartAge) >= concurrency.concurrencyLagWarningSeconds * 10
        ? "critical"
        : "warning",
      title: `${sourcePrefix}Concurrency is backed up`,
    });
  }

  if (durability?.hasReaderFailure) {
    notifications.push({
      description: `${durability.readerFailureType ?? "Durable reader failure"}${durability.readerFailureMessage ? `: ${durability.readerFailureMessage}` : ""}`,
      id: `${source?.systemId ?? "active"}:durability-reader-failure`,
      tone: "critical",
      title: `${sourcePrefix}Durable reader failed`,
    });
  }

  if (durability?.hasLeaseRenewalFailure) {
    notifications.push({
      description: `${durability.leaseRenewalFailureType ?? "Lease renewal failure"}${durability.leaseRenewalFailureMessage ? `: ${durability.leaseRenewalFailureMessage}` : ""}`,
      id: `${source?.systemId ?? "active"}:durability-renewal-failure`,
      tone: "critical",
      title: `${sourcePrefix}Durable lease renewal failed`,
    });
  }

  if (durability?.hasCleanupFailure) {
    notifications.push({
      description: `${durability.cleanupFailureType ?? "Cleanup failure"}${durability.cleanupFailureMessage ? `: ${durability.cleanupFailureMessage}` : ""}`,
      id: `${source?.systemId ?? "active"}:durability-cleanup-failure`,
      tone: "critical",
      title: `${sourcePrefix}Durable cleanup failed`,
    });
  }

  if (durability?.isAcceptedWorkerMaterializationBehind) {
    notifications.push({
      description: `${formatNumber(durability.acceptedWaiterCount)} accepted durable worker${durability.acceptedWaiterCount === 1 ? "" : "s"} waiting to materialize, oldest wait ${formatDuration(durability.oldestAcceptedWaiterAge)}${sourceSuffix}.`,
      id: `${source?.systemId ?? "active"}:durability-waiters`,
      tone: parseDurationSeconds(durability.oldestAcceptedWaiterAge) >= durability.acceptedWorkerWarningSeconds * 10
        ? "critical"
        : "warning",
      title: `${sourcePrefix}Durable worker materialization is behind`,
    });
  }

  if (durability?.isCleanupBehind) {
    notifications.push({
      description: `${formatNumber(durability.pendingCleanupCount)} durable cleanup item${durability.pendingCleanupCount === 1 ? "" : "s"} pending, oldest waiting ${formatDuration(durability.oldestPendingCleanupAge)}${sourceSuffix}.`,
      id: `${source?.systemId ?? "active"}:durability-cleanup-lag`,
      tone: parseDurationSeconds(durability.oldestPendingCleanupAge) >= durability.cleanupWarningSeconds * 10
        ? "critical"
        : "warning",
      title: `${sourcePrefix}Durable cleanup is behind`,
    });
  }

  return notifications;
}

function createCompactReadModelDiagnosticsFromDetailed(
  detailed?: WorkReadModelDiagnosticsDetailedComponent | WorkReadModelDiagnosticsCompactComponent
): WorkReadModelDiagnosticsCompactComponent | undefined {
  if (!detailed) {
    return undefined;
  }

  const readModel = "readModel" in detailed ? detailed.readModel : undefined;

  return {
    hasProjectorFailure: readModel?.hasProjectorFailure ?? detailed.hasProjectorFailure ?? false,
    isReadModelBehind: detailed.isReadModelBehind,
    pendingUpdateCount: readModel?.pendingUpdateCount ?? detailed.pendingUpdateCount ?? 0,
    projectorFailureMessage: readModel?.projectorFailureMessage ?? detailed.projectorFailureMessage,
    projectorFailureType: readModel?.projectorFailureType ?? detailed.projectorFailureType,
    readModelLagWarningThreshold: detailed.readModelLagWarningThreshold,
  };
}

function createCompactRetentionDiagnosticsFromDetailed(
  detailed?: WorkRetentionDiagnosticsDetailedComponent | WorkRetentionDiagnosticsCompactComponent
): WorkRetentionDiagnosticsCompactComponent | undefined {
  if (!detailed) {
    return undefined;
  }

  const retention = "retention" in detailed ? detailed.retention : undefined;

  return {
    hasSchedulerFailure: retention?.hasSchedulerFailure ?? detailed.hasSchedulerFailure ?? false,
    isRetentionBehind: detailed.isRetentionBehind,
    oldestDuePurgeAge: retention?.oldestDuePurgeAge ?? detailed.oldestDuePurgeAge ?? "00:00:00",
    retentionLagWarningSeconds: detailed.retentionLagWarningSeconds,
    scheduledPurgeCount: retention?.scheduledPurgeCount ?? detailed.scheduledPurgeCount ?? 0,
    schedulerFailureMessage: retention?.schedulerFailureMessage ?? detailed.schedulerFailureMessage,
    schedulerFailureType: retention?.schedulerFailureType ?? detailed.schedulerFailureType,
    trackedFinalWorkerCount: retention?.trackedFinalWorkerCount ?? detailed.trackedFinalWorkerCount ?? 0,
  };
}

function createCompactConcurrencyDiagnosticsFromDetailed(
  detailed?: WorkConcurrencyDiagnosticsDetailedComponent | WorkConcurrencyDiagnosticsCompactComponent
): WorkConcurrencyDiagnosticsCompactComponent | undefined {
  if (!detailed) {
    return undefined;
  }

  const concurrency = "concurrency" in detailed ? detailed.concurrency : undefined;

  return {
    concurrencyLagWarningSeconds: detailed.concurrencyLagWarningSeconds,
    deferredStartCount: concurrency?.deferredStartCount ?? detailed.deferredStartCount ?? 0,
    isConcurrencyBehind: detailed.isConcurrencyBehind,
    lastDrainReleasedCount: concurrency?.lastDrainReleasedCount ?? detailed.lastDrainReleasedCount ?? 0,
    oldestDeferredStartAge: concurrency?.oldestDeferredStartAge ?? detailed.oldestDeferredStartAge ?? "00:00:00",
  };
}

function createCompactDurabilityDiagnosticsFromDetailed(
  detailed?: WorkDurabilityDiagnosticsDetailedComponent | WorkDurabilityDiagnosticsCompactComponent
): WorkDurabilityDiagnosticsCompactComponent | undefined {
  if (!detailed) {
    return undefined;
  }

  const durability = "durability" in detailed ? detailed.durability : undefined;

  return {
    acceptedWaiterCount: durability?.acceptedWaiterCount ?? detailed.acceptedWaiterCount ?? 0,
    acceptedWorkerWarningSeconds: detailed.acceptedWorkerWarningSeconds,
    cleanupFailureMessage: durability?.cleanupFailureMessage ?? detailed.cleanupFailureMessage,
    cleanupFailureType: durability?.cleanupFailureType ?? detailed.cleanupFailureType,
    cleanupWarningSeconds: detailed.cleanupWarningSeconds,
    hasCleanupFailure: durability?.hasCleanupFailure ?? detailed.hasCleanupFailure ?? false,
    hasLeaseRenewalFailure: durability?.hasLeaseRenewalFailure ?? detailed.hasLeaseRenewalFailure ?? false,
    hasReaderFailure: durability?.hasReaderFailure ?? detailed.hasReaderFailure ?? false,
    isAcceptedWorkerMaterializationBehind: detailed.isAcceptedWorkerMaterializationBehind,
    isCleanupBehind: detailed.isCleanupBehind,
    leaseRenewalFailureMessage: durability?.leaseRenewalFailureMessage ?? detailed.leaseRenewalFailureMessage,
    leaseRenewalFailureType: durability?.leaseRenewalFailureType ?? detailed.leaseRenewalFailureType,
    oldestAcceptedWaiterAge: durability?.oldestAcceptedWaiterAge ?? detailed.oldestAcceptedWaiterAge ?? "00:00:00",
    oldestPendingCleanupAge: durability?.oldestPendingCleanupAge ?? detailed.oldestPendingCleanupAge ?? "00:00:00",
    pendingCleanupCount: durability?.pendingCleanupCount ?? detailed.pendingCleanupCount ?? 0,
    readerFailureMessage: durability?.readerFailureMessage ?? detailed.readerFailureMessage,
    readerFailureType: durability?.readerFailureType ?? detailed.readerFailureType,
  };
}

function createCompactIdempotencyDiagnosticsFromDetailed(
  detailed?: WorkIdempotencyDiagnosticsDetailedComponent | WorkIdempotencyDiagnosticsCompactComponent
): WorkIdempotencyDiagnosticsCompactComponent | undefined {
  if (!detailed) {
    return undefined;
  }

  const idempotency = "idempotency" in detailed ? detailed.idempotency : undefined;

  return {
    duplicateRejectionCount: idempotency?.duplicateRejectionCount ?? detailed.duplicateRejectionCount ?? 0,
    lastDuplicateRejectedStorage: idempotency?.lastDuplicateRejectedStorage ?? detailed.lastDuplicateRejectedStorage,
  };
}

function getWorkComponentData<T>(result: WorkComponentQueryResult | undefined, id: string) {
  const component = result?.components?.[id];
  return component?.status?.toLowerCase() === "ok" ? component.data as T : undefined;
}

function diagnosticsAlertSnapshotsEqual(
  left: DiagnosticsAlertSnapshot | null | undefined,
  right: DiagnosticsAlertSnapshot | null | undefined
) {
  if (left === right) {
    return true;
  }

  if (!left || !right) {
    return false;
  }

  return left.connectionState === right.connectionState &&
    left.data === right.data &&
    left.enabled === right.enabled &&
    left.error === right.error &&
    left.loading === right.loading &&
    left.refreshing === right.refreshing;
}

function formatNumber(value?: number | null) {
  return typeof value === "number" ? value.toLocaleString() : "-";
}

function formatLocalTime(value: Date) {
  return new Intl.DateTimeFormat(undefined, {
    hour: "numeric",
    minute: "2-digit",
    second: "2-digit",
  }).format(value);
}

function formatDateTimeShort(value?: string | null) {
  if (!value) {
    return "-";
  }

  return new Intl.DateTimeFormat(undefined, {
    hour: "numeric",
    minute: "2-digit",
    second: "2-digit",
  }).format(new Date(value));
}

function formatDuration(value?: string | null) {
  if (!value) {
    return "-";
  }

  const milliseconds = parseTimeSpanMilliseconds(value);
  if (milliseconds === null) {
    return value;
  }

  return `${milliseconds.toLocaleString(undefined, {
    maximumFractionDigits: milliseconds < 10 ? 3 : 1,
  })} ms`;
}

function parseTimeSpanMilliseconds(value: string) {
  const match = /^(?:(\d+)\.)?(\d{1,2}):(\d{2}):(\d{2})(?:\.(\d+))?$/.exec(value);
  if (!match) {
    return null;
  }

  const days = Number(match[1] ?? 0);
  const hours = Number(match[2]);
  const minutes = Number(match[3]);
  const seconds = Number(match[4]);
  const fraction = match[5] ? Number(`0.${match[5]}`) : 0;
  return (((days * 24 + hours) * 60 + minutes) * 60 + seconds + fraction) * 1000;
}

function parseDurationSeconds(value: string) {
  return (parseTimeSpanMilliseconds(value) ?? 0) / 1000;
}

function OverviewPanelSettings({
  hiddenPanelIds,
  onPanelVisibilityChange,
  onResetUi,
}: {
  hiddenPanelIds: OverviewPanelId[];
  onPanelVisibilityChange: (panelId: OverviewPanelId, visible: boolean) => void;
  onResetUi: () => void;
}) {
  return (
    <Popover>
      <Tooltip delayDuration={500} disableHoverableContent>
        <TooltipTrigger asChild>
          <PopoverTrigger asChild>
            <Button
              aria-label="Overview panel settings"
              className="text-muted-foreground hover:bg-transparent hover:text-foreground dark:hover:bg-transparent"
              size="icon-sm"
              variant="ghost"
            >
              <Settings className="size-4" />
            </Button>
          </PopoverTrigger>
        </TooltipTrigger>
        <TooltipContent side="bottom" sideOffset={6}>
          Overview panel settings
        </TooltipContent>
      </Tooltip>
      <PopoverContent align="end" className="w-80 p-0">
        <div className="flex items-start justify-between gap-3 border-b px-3 py-2">
          <div className="min-w-0">
            <div className="font-medium text-sm">Overview panels</div>
            <div className="text-muted-foreground text-xs">
              Checked panels are shown on the overview screen.
            </div>
          </div>
          <Button
            className="h-6 shrink-0 px-2 text-xs"
            onClick={() => overviewPanelIds.forEach((id) => onPanelVisibilityChange(id, true))}
            size="sm"
            variant="ghost"
          >
            All
          </Button>
        </div>
        <div className="space-y-1 p-2">
          {overviewPanelOptions.map((panel) => {
            const visible = !hiddenPanelIds.includes(panel.id);
            return (
              <label
                className="flex cursor-pointer items-start gap-3 rounded-md px-2 py-2 transition-colors hover:bg-accent/40"
                key={panel.id}
              >
                <input
                  checked={visible}
                  className="mt-0.5 size-4 accent-primary"
                  onChange={(event) =>
                    onPanelVisibilityChange(panel.id, event.currentTarget.checked)
                  }
                  type="checkbox"
                />
                <span className="min-w-0">
                  <span className="block font-medium text-sm">{panel.label}</span>
                  <span className="block text-muted-foreground text-xs">
                    {panel.description}
                  </span>
                </span>
              </label>
            );
          })}
        </div>
        <div className="border-t p-2">
          <Button
            className="h-9 w-full justify-start gap-2 text-muted-foreground"
            onClick={() => {
              onResetUi();
            }}
            size="sm"
            variant="ghost"
          >
            <RotateCcw className="size-4" />
            Reset UI to defaults
          </Button>
        </div>
      </PopoverContent>
    </Popover>
  );
}

function loadConsoleStorage(): ConsoleStorage {
  const fallback = createDefaultConsoleStorage();

  if (typeof window === "undefined") {
    return fallback;
  }

  const stored = window.localStorage.getItem(STORAGE_KEY);
  if (stored) {
    try {
      const parsed = JSON.parse(stored) as Partial<ConsoleStorage> & {
        activeServerId?: string;
        expandedServerIds?: string[];
        overviewCollapsedPanels?: unknown;
        overviewPanelShapes?: unknown;
        servers?: LegacyWorkableServerConnection[];
      };

      if (Array.isArray(parsed.hosts)) {
        const hosts = parsed.hosts.map(normalizeStoredHost);
        if (hosts.length === 0) {
          return {
            activeSystemId: "",
            expandedHostIds: [],
            expandedSystemIds: [],
            hosts: [],
            overviewHiddenPanels: normalizeOverviewHiddenPanels(
              parsed.overviewHiddenPanels,
              parsed.overviewThroughputHidden
            ),
            overviewPanelShapes: normalizeOverviewPanelShapes(
              parsed.overviewPanelShapes,
              parsed.overviewCollapsedPanels
            ),
            overviewHiddenThroughputSeries: normalizeThroughputSeriesIds(
              parsed.overviewHiddenThroughputSeries
            ),
            overviewThroughputHidden: parsed.overviewThroughputHidden ?? false,
            view: isServerView(parsed.view) ? parsed.view : "overview",
          };
        }

        const systemIds = new Set(hosts.flatMap((host) => host.systems.map((system) => system.id)));
        const activeSystemId = parsed.activeSystemId && systemIds.has(parsed.activeSystemId)
          ? parsed.activeSystemId
          : hosts[0].systems[0].id;

        return {
          activeSystemId,
          expandedHostIds: parsed.expandedHostIds?.filter((id) =>
            hosts.some((host) => host.id === id)
          ) ?? [hosts[0].id],
          expandedSystemIds: parsed.expandedSystemIds?.filter((id) => systemIds.has(id)) ?? [
            activeSystemId,
          ],
          hosts,
          overviewHiddenPanels: normalizeOverviewHiddenPanels(
            parsed.overviewHiddenPanels,
            parsed.overviewThroughputHidden
          ),
          overviewPanelShapes: normalizeOverviewPanelShapes(
            parsed.overviewPanelShapes,
            parsed.overviewCollapsedPanels
          ),
          overviewHiddenThroughputSeries: normalizeThroughputSeriesIds(
            parsed.overviewHiddenThroughputSeries
          ),
          overviewThroughputHidden: parsed.overviewThroughputHidden ?? false,
          view: isServerView(parsed.view) ? parsed.view : "overview",
        };
      }

      if (Array.isArray(parsed.servers) && parsed.servers.length > 0) {
        const hosts = parsed.servers.map((server) => migrateFlatServer(server));
        const activeSystemId =
          parsed.activeServerId && hosts.some((host) => host.systems[0].id === parsed.activeServerId)
            ? parsed.activeServerId
            : hosts[0].systems[0].id;

        return {
          activeSystemId,
          expandedHostIds: hosts.map((host) => host.id),
          expandedSystemIds: parsed.expandedServerIds ?? [activeSystemId],
          hosts,
          overviewHiddenPanels: [],
          overviewPanelShapes: createDefaultOverviewPanelShapes(),
          overviewHiddenThroughputSeries: [],
          overviewThroughputHidden: false,
          view: isServerView(parsed.view) ? parsed.view : "overview",
        };
      }
    } catch {
      window.localStorage.removeItem(STORAGE_KEY);
    }
  }

  const legacy = window.localStorage.getItem(LEGACY_CONNECTION_STORAGE_KEY);
  if (!legacy) {
    return fallback;
  }

  try {
    const connection = JSON.parse(legacy) as WorkableConnection;
    const migratedHost = createDefaultHost();
    migratedHost.apiUrl = connection.apiUrl || DEFAULT_WORKABLE_API_URL;
    migratedHost.systems[0].systemName = connection.systemName;

    return {
      activeSystemId: migratedHost.systems[0].id,
      expandedHostIds: [migratedHost.id],
      expandedSystemIds: [migratedHost.systems[0].id],
      hosts: [migratedHost],
      overviewHiddenPanels: [],
      overviewPanelShapes: createDefaultOverviewPanelShapes(),
      overviewHiddenThroughputSeries: [],
      overviewThroughputHidden: false,
      view: "overview",
    };
  } catch {
    window.localStorage.removeItem(LEGACY_CONNECTION_STORAGE_KEY);
    return fallback;
  }
}

function createDefaultConsoleStorage(): ConsoleStorage {
  const defaultHost = createDefaultHost();
  const defaultSystem = defaultHost.systems[0];

  return {
    activeSystemId: defaultSystem.id,
    expandedHostIds: [defaultHost.id],
    expandedSystemIds: [defaultSystem.id],
    hosts: [defaultHost],
    overviewHiddenPanels: [],
    overviewPanelShapes: createDefaultOverviewPanelShapes(),
    overviewHiddenThroughputSeries: [],
    overviewThroughputHidden: false,
    view: "overview",
  };
}

function createDefaultOverviewPanelShapes(): OverviewPanelShapeMap {
  return Object.fromEntries(
    overviewPanelIds.map((panelId) => [
      panelId,
      overviewPanelShapeCapabilities[panelId].defaultShape,
    ])
  ) as OverviewPanelShapeMap;
}

function normalizeOverviewPanelShapes(
  value: unknown,
  legacyCollapsedPanels?: unknown
): OverviewPanelShapeMap {
  const shapes = createDefaultOverviewPanelShapes();

  if (value && typeof value === "object" && !Array.isArray(value)) {
    const requested = value as Partial<Record<OverviewPanelId, unknown>>;
    for (const panelId of overviewPanelIds) {
      shapes[panelId] = normalizeOverviewPanelShape(panelId, requested[panelId]);
    }
  }

  for (const panelId of normalizeOverviewPanelIds(legacyCollapsedPanels)) {
    if (overviewPanelShapeCapabilities[panelId].supportedShapes.includes("compact")) {
      shapes[panelId] = "compact";
    }
  }

  return shapes;
}

function normalizeOverviewPanelShape(
  panelId: OverviewPanelId,
  value: unknown
): WorkComponentShape {
  const capabilities = overviewPanelShapeCapabilities[panelId];
  return typeof value === "string" &&
    capabilities.supportedShapes.includes(value as WorkComponentShape)
    ? value as WorkComponentShape
    : capabilities.defaultShape;
}

function normalizeOverviewHiddenPanels(
  value: unknown,
  legacyThroughputHidden = false
): OverviewPanelId[] {
  const requested = new Set(normalizeOverviewPanelIds(value));

  if (legacyThroughputHidden) {
    requested.add("throughput");
  }

  return overviewPanelIds.filter((id) => requested.has(id));
}

function normalizeThroughputSeriesIds(value: unknown): ThroughputSeriesId[] {
  if (!Array.isArray(value)) {
    return [];
  }

  const requested = new Set(value.filter(isThroughputSeriesId));
  const hidden = throughputSeriesIds.filter((id) => requested.has(id));
  return hidden.length >= throughputSeriesIds.length ? hidden.slice(1) : hidden;
}

function isThroughputSeriesId(value: unknown): value is ThroughputSeriesId {
  return typeof value === "string" &&
    throughputSeriesIds.includes(value as ThroughputSeriesId);
}

function normalizeOverviewPanelIds(value: unknown): OverviewPanelId[] {
  const requested = new Set(
    Array.isArray(value)
      ? value.filter((item): item is OverviewPanelId =>
          typeof item === "string" &&
          overviewPanelIds.includes(item as OverviewPanelId)
        )
      : []
  );

  return overviewPanelIds.filter((id) => requested.has(id));
}

function normalizeStoredHost(host: WorkableHostConnection): WorkableHostConnection {
  const hostId = host.id || createServerId();
  const systems = host.systems?.length
    ? host.systems.map((system) => normalizeStoredSystem(hostId, system))
    : [createDefaultSystem(hostId)];

  return {
    id: hostId,
    name: host.name || "Workable host",
    apiUrl: host.apiUrl || DEFAULT_WORKABLE_API_URL,
    systems,
  };
}

function normalizeStoredSystem(
  hostId: string,
  system: WorkableSystemConnection
): WorkableSystemConnection {
  const isDefaultLocalSampleSystem =
    hostId === "local-sample-host" &&
    (system.id === "local-sample-default" || !system.id) &&
    !normalizeOptional(system.systemName);
  const realtimeHubPath = isDefaultLocalSampleSystem
    ? system.realtimeHubPath ?? "/workable/realtime"
    : system.realtimeHubPath ?? null;
  const realtimeSupported = isDefaultLocalSampleSystem
    ? true
    : Boolean(system.realtimeSupported);
  const realtimeEnabled = isDefaultLocalSampleSystem
    ? true
    : Boolean(system.realtimeEnabled && realtimeSupported);
  const realtimeTransport = isDefaultLocalSampleSystem
    ? system.realtimeTransport ?? "signalr"
    : system.realtimeTransport ?? null;

  return {
    id: system.id || createServerId(),
    hostId,
    name: system.name || "Default",
    systemName: normalizeOptional(system.systemName),
    realtimeEnabled,
    realtimeHubPath,
    realtimeSupported,
    realtimeTransport,
    state: system.state ?? null,
  };
}

function migrateFlatServer(server: LegacyWorkableServerConnection): WorkableHostConnection {
  const hostId = `host-${server.id || createServerId()}`;

  return {
    id: hostId,
    name: server.name || "Workable host",
    apiUrl: server.apiUrl || DEFAULT_WORKABLE_API_URL,
    systems: [
      normalizeStoredSystem(hostId, {
        ...server,
        id: server.id || createServerId(),
        hostId,
      }),
    ],
  };
}

function findSystemLocation(
  state: ConsoleStorage,
  systemId: string
): { host: WorkableHostConnection; system: WorkableSystemConnection } | null {
  for (const host of state.hosts) {
    const system = host.systems.find((item) => item.id === systemId);
    if (system) {
      return { host, system };
    }
  }

  const fallbackHost = state.hosts[0];
  if (!fallbackHost) {
    return null;
  }

  return { host: fallbackHost, system: fallbackHost.systems[0] };
}

function createDiagnosticsAlertTargets(hosts: WorkableHostConnection[]): DiagnosticsAlertTarget[] {
  return hosts.flatMap((host) =>
    host.systems
      .filter((system) => system.realtimeEnabled && !!system.realtimeHubPath)
      .map((system) => ({
        apiUrl: host.apiUrl,
        displayName: `${system.name} @ ${host.name}`,
        hostId: host.id,
        hostName: host.name,
        realtimeHubPath: system.realtimeHubPath!,
        systemId: system.id,
        systemName: system.systemName,
      }))
  );
}

function isServerView(value: unknown): value is ServerView {
  return (
    value === "overview" ||
    value === "definitions" ||
    value === "workers" ||
    value === "iterations"
  );
}

function getViewReadinessKey(systemId: string, view: View) {
  return `${systemId}:${view}`;
}

function createDefaultHost(): WorkableHostConnection {
  const hostId = "local-sample-host";
  return {
    id: hostId,
    name: "Local sample",
    apiUrl: DEFAULT_WORKABLE_API_URL,
    systems: [createDefaultSystem(hostId)],
  };
}

function createDefaultSystem(hostId: string): WorkableSystemConnection {
  return {
    id: "local-sample-default",
    hostId,
    name: "Default",
    realtimeEnabled: true,
    realtimeHubPath: "/workable/realtime",
    realtimeSupported: true,
    realtimeTransport: "signalr",
    state: null,
  };
}

function createServerId() {
  if (typeof crypto !== "undefined" && "randomUUID" in crypto) {
    return crypto.randomUUID();
  }

  return `server-${Date.now().toString(36)}`;
}

function normalizeOptional(value?: string | null) {
  const trimmed = value?.trim();
  return trimmed ? trimmed : undefined;
}

function navTitle(view: View) {
  if (view === "worker") {
    return "Worker Console";
  }
  if (view === "definition") {
    return "Definition";
  }

  return navItems.find((item) => item.id === view)?.label ?? "Overview";
}

function cloneOverviewScope(scope: OverviewScope | null): OverviewScope | null {
  return normalizeOverviewScope(scope);
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

function createQueryCatalogScope(categoryFilter: string, definitionFilter: string): OverviewScope | null {
  const category = normalizeCategoryFilter(categoryFilter);
  const definitionName = definitionFilter.trim();
  if (!category && !definitionName) {
    return null;
  }

  return {
    category: category || undefined,
    definitionName: definitionName || undefined,
    includeSubcategories: true,
  };
}

function overviewScopesEqual(
  left: OverviewScope | null,
  right: OverviewScope | null
) {
  const normalizedLeft = normalizeOverviewScope(left);
  const normalizedRight = normalizeOverviewScope(right);
  return (
    normalizedLeft?.category === normalizedRight?.category &&
    normalizedLeft?.definitionName === normalizedRight?.definitionName &&
    normalizedLeft?.includeSubcategories === normalizedRight?.includeSubcategories
  );
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

function getWindowScrollTop() {
  return document.scrollingElement?.scrollTop ?? window.scrollY;
}

function getDocumentScrollHeight() {
  return Math.max(
    document.body.scrollHeight,
    document.documentElement.scrollHeight
  );
}

function getWorkerParentView(history: NavigationEntry[]): ServerView {
  const previous = history.at(-1);
  return previous && isServerView(previous.view) ? previous.view : "workers";
}

function navigationEntriesEqual(left: NavigationEntry | undefined, right: NavigationEntry) {
  return (
    left?.systemId === right.systemId &&
    overviewScopesEqual(left.catalogScope, right.catalogScope) &&
    left.definitionId === right.definitionId &&
    left.iterationCategoryFilter === right.iterationCategoryFilter &&
    left.iterationDefinitionFilter === right.iterationDefinitionFilter &&
    left.iterationKeyTypeFilter === right.iterationKeyTypeFilter &&
    left.iterationStatusFilter.length === right.iterationStatusFilter.length &&
    left.iterationStatusFilter.every(
      (status, index) => status === right.iterationStatusFilter[index]
    ) &&
    left.keyTypeFilter === right.keyTypeFilter &&
    overviewScopesEqual(left.overviewScope, right.overviewScope) &&
    left.view === right.view &&
    left.workerCategoryFilter === right.workerCategoryFilter &&
    left.workerDefinitionFilter === right.workerDefinitionFilter &&
    left.workerId === right.workerId &&
    left.workerStateFilter.length === right.workerStateFilter.length &&
    left.workerStateFilter.every((state, index) => state === right.workerStateFilter[index])
  );
}
