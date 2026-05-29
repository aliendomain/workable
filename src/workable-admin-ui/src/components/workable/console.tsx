"use client";

import Image from "next/image";
import { useRouter } from "next/navigation";
import {
  Activity,
  Bell,
  Boxes,
  ChevronLeft,
  ChevronRight,
  CircleAlert,
  Clock3,
  FileCode2,
  Folder,
  Home,
  Info,
  Loader2,
  LogOut,
  Pause,
  Play,
  Plus,
  Rows4,
  X,
  Workflow,
} from "lucide-react";
import { Fragment, type KeyboardEvent, type PointerEvent, type ReactNode, useCallback, useDeferredValue, useEffect, useMemo, useRef, useState } from "react";
import {
  overviewPanelIds,
  type OverviewPanelId,
  overviewPanelShapeCapabilities,
  type OverviewPanelShapeMap,
} from "@/components/features/console/overview-panels";
import {
  ConsoleViewport,
  ConsoleViewportContent,
  ConsoleViewMount,
} from "@/components/features/console/console-primitives";
import {
  ConsoleHeaderCapabilitiesProvider,
  type ConsoleHeaderCapabilities,
} from "@/components/features/console/header-capabilities";
import {
  ConsolePageRealtimeViewProvider,
} from "@/components/features/console/page-realtime-view";
import {
  clearConsoleRealtimeEventCapture,
  clearConsoleRealtimePayloadCapture,
  useConsoleRealtimeEventCapture,
  useConsoleRealtimePayloadCapture,
  useConsoleRealtimeStats,
} from "@/components/features/console/realtime";
import {
  JsonValue,
  RealtimePayloadWindow,
  RealtimeStatsMenu,
  type RealtimePayloadWindowTab,
} from "@/components/features/console/realtime-payload-window";
import type {
  OverviewScope,
  PendingDelete,
  PendingStopSystem,
  ServerView,
  View,
  WorkableHostConnection,
  WorkableSystemConnection,
} from "@/components/features/console/types";
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
  type RealtimeEventMessage,
  useWorkableRealtimeView,
  useWorkableRealtimeEvents,
  type RealtimeViewLoadable,
} from "@/components/workable/console/overview-screen";
import {
  DefinitionView,
  DefinitionsView,
  IterationConsoleView,
  WorkerConsoleView,
} from "@/components/workable/console/detail-screens";
import {
  OverviewCatalogFilter,
  QueryFilterPanelContent,
  getQueryFilterActiveCount,
} from "@/components/workable/console/filters";
import { ErrorPanel } from "@/components/workable/console/feedback-panel";
import {
  ConsoleNavigationHeader,
  DelayedLoadingOverlay,
  DeleteTargetDialog,
  discoverHost,
  EmptyServerState,
  reconcileStoredHostWithDiscovery,
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
  type WorkSystemAccessSummary,
  type WorkableConnection,
  type WorkerState,
} from "@/lib/workable";

const STORAGE_KEY = "workable-console.state.v1";

const throughputSeriesIds = ["started", "completed", "failed", "canceled"] as const;
type ThroughputSeriesId = (typeof throughputSeriesIds)[number];

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

type NavigationEntry = {
  catalogScope: OverviewScope | null;
  iterationSequence: number | null;
  iterationCategoryFilter: string;
  iterationDefinitionFilter: string;
  iterationKeyTypeFilter: string;
  iterationStatusFilter: WorkCompletionStatus[];
  overviewScope: OverviewScope | null;
  definitionName: string | null;
  workerCategoryFilter: string;
  workerDefinitionFilter: string;
  keyTypeFilter: string;
  systemId: string;
  view: View;
  definitionId: string | null;
  iterationWorkerId: string | null;
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
  iteration: 0,
};
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
  "worker.iteration.started",
  "worker.iteration.completed",
  "worker.iteration.failed",
  "worker.recurrence.circuit_opened",
  "worker.reconfigured",
  "worker.purge",
  "worker.log",
] as const;

export function WorkableConsole() {
  const router = useRouter();
  const initialConsoleState = useMemo(() => createDefaultConsoleStorage(), []);
  const [hasMounted, setHasMounted] = useState(false);
  const [isSigningOut, setIsSigningOut] = useState(false);
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
  const [systemIssueNotifications, setSystemIssueNotifications] = useState<Record<string, Omit<SystemNotification, "onDismiss">>>({});
  const [acknowledgedRejectedWorkCounts, setAcknowledgedRejectedWorkCounts] = useState<Record<string, number>>({});
  const [diagnosticsAlertsByTargetId, setDiagnosticsAlertsByTargetId] = useState<Record<string, DiagnosticsAlertSnapshot>>({});
  const [readModelDiagnosticsExpanded, setReadModelDiagnosticsExpanded] = useState(false);
  const [retentionDiagnosticsExpanded, setRetentionDiagnosticsExpanded] = useState(false);
  const [concurrencyDiagnosticsExpanded, setConcurrencyDiagnosticsExpanded] = useState(false);
  const [durabilityDiagnosticsExpanded, setDurabilityDiagnosticsExpanded] = useState(false);
  const [idempotencyDiagnosticsExpanded, setIdempotencyDiagnosticsExpanded] = useState(false);
  const realtimePayloadCaptureEnabled = true;
  const [realtimePayloadMaxMessages, setRealtimePayloadMaxMessages] = useState(100);
  const [realtimePayloadOpen, setRealtimePayloadOpen] = useState(false);
  const [realtimePayloadActiveTab, setRealtimePayloadActiveTab] = useState<RealtimePayloadWindowTab>("payloads");
  const [eventViewerMaxMessages, setEventViewerMaxMessages] = useState(100);
  const [eventViewerDefinitions, setEventViewerDefinitions] = useState<WorkDefinition[]>([]);
  const [eventViewerDefinitionsLoading, setEventViewerDefinitionsLoading] = useState(false);
  const [eventViewerDefinitionError, setEventViewerDefinitionError] = useState<string>();
  const [selectedEventViewerDefinitionIds, setSelectedEventViewerDefinitionIds] = useState<string[]>([]);
  const [selectedEventViewerEventTypes, setSelectedEventViewerEventTypes] = useState<string[]>([]);
  const [selectedEventViewerKeys, setSelectedEventViewerKeys] = useState<WorkableRealtimeEventKeyCriteria[]>([]);
  const [refreshTokens, setRefreshTokens] = useState<Record<View, number>>(initialRefreshTokens);
  const realtimeEventCapture = useConsoleRealtimeEventCapture();
  const realtimeStats = useConsoleRealtimeStats();
  const ignoreRealtimeConnectionCountChange = useCallback<(count: number) => void>(() => undefined, []);
  const [selectedDefinitionId, setSelectedDefinitionId] = useState<string | null>(null);
  const [selectedDefinitionName, setSelectedDefinitionName] = useState<string | null>(null);
  const [selectedWorkerId, setSelectedWorkerId] = useState<string | null>(null);
  const [selectedIterationWorkerId, setSelectedIterationWorkerId] = useState<string | null>(null);
  const [selectedIterationSequence, setSelectedIterationSequence] = useState<number | null>(null);
  const [workerCategoryFilter, setWorkerCategoryFilter] = useState("");
  const [workerDefinitionFilter, setWorkerDefinitionFilter] = useState("");
  const [keyTypeFilter, setKeyTypeFilter] = useState("");
  const [workerStateFilter, setWorkerStateFilter] = useState<WorkerState[]>([]);
  const [iterationCategoryFilter, setIterationCategoryFilter] = useState("");
  const [iterationDefinitionFilter, setIterationDefinitionFilter] = useState("");
  const [iterationKeyTypeFilter, setIterationKeyTypeFilter] = useState("");
  const [iterationStatusFilter, setIterationStatusFilter] = useState<WorkCompletionStatus[]>([]);
  const usesPanelOwnedScroll = visibleView === "workers" || visibleView === "iterations";
  const [catalogScopeBySystemId, setCatalogScopeBySystemId] = useState<
    Record<string, OverviewScope | undefined>
  >({});
  const [overviewScopeBySystemId, setOverviewScopeBySystemId] = useState<
    Record<string, OverviewScope | undefined>
  >({});
  const [navigationHistory, setNavigationHistory] = useState<NavigationEntry[]>([]);
  const [forwardNavigation, setForwardNavigation] = useState<NavigationEntry[]>([]);
  const viewScrollPositions = useRef<Partial<Record<ServerView, number>>>({});
  const readyViews = useRef<Set<string>>(new Set());
  const restoredHostsRef = useRef<WorkableHostConnection[] | null>(null);
  const activeLocation = findSystemLocation(consoleState, consoleState.activeSystemId);
  const activeHost = activeLocation?.host;
  const activeSystem = activeLocation?.system;
  const activeSystemId = activeSystem?.id ?? "";
  const activeCanViewDiagnostics = activeSystem?.access?.canViewDiagnostics ?? false;
  const activeCanUseRealtimeEvents = Boolean(
    activeSystem?.access?.canReadAllWork ||
    (activeSystem?.access?.readableDefinitionCount ?? 0) > 0
  );
  const activeCanUseRealtimeDiagnosticsUi =
    activeCanViewDiagnostics &&
    Boolean(activeHost?.realtimeEnabled && activeHost.realtimeHubPath);
  const activeCatalogScope = activeSystem
    ? catalogScopeBySystemId[activeSystem.id] ?? null
    : null;
  const activeOverviewScope = activeSystem
    ? overviewScopeBySystemId[activeSystem.id] ?? null
    : null;
  const connection = useMemo<WorkableConnection | null>(
    () =>
      activeHost?.apiUrl
        ? {
            apiUrl: activeHost.apiUrl,
            realtimeHubPath: activeHost.realtimeEnabled
              ? activeHost.realtimeHubPath ?? null
              : null,
            systemName: activeSystem?.systemName,
          }
        : null,
    [activeHost, activeSystem]
  );
  const hydratedConnection = hasMounted ? connection : null;
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
  const diagnosticsRealtimeEnabled =
    Boolean(hydratedConnection?.realtimeHubPath) &&
    activeCanUseRealtimeDiagnosticsUi;
  const diagnosticsAlertTargets = useMemo(
    () => createDiagnosticsAlertTargets(consoleState.hosts),
    [consoleState.hosts]
  );
  const diagnosticsAlertSources = useMemo<DiagnosticsAlertSource[]>(
    () => diagnosticsAlertTargets.map((target) => ({
      ...(diagnosticsAlertsByTargetId[target.id] ?? {
        connectionState: "connecting",
        enabled: true,
        loading: true,
      }),
      target,
    })),
    [diagnosticsAlertTargets, diagnosticsAlertsByTargetId]
  );
  const activeDiagnosticsAlertTargetId = activeHost && activeSystem
    ? createDiagnosticsAlertTargetId(
        activeHost.apiUrl,
        activeHost.realtimeHubPath ?? null,
        activeSystem.systemName
      )
    : null;
  const captureRealtimePayloads = realtimePayloadOpen && realtimePayloadCaptureEnabled;
  const diagnosticsTray = useWorkableRealtimeView<WorkComponentQueryResult>(
    hydratedConnection,
    "diagnostics",
    diagnosticsTrayRequest,
    diagnosticsRealtimeEnabled && systemNotificationOpen,
    captureRealtimePayloads,
    realtimePayloadMaxMessages,
    "diagnostics:tray"
  );
  const readModelDiagnosticsDetail = useWorkableRealtimeView<WorkComponentQueryResult>(
    hydratedConnection,
    "diagnostics",
    readModelDiagnosticsDetailRequest,
    diagnosticsRealtimeEnabled && systemNotificationOpen && readModelDiagnosticsExpanded,
    captureRealtimePayloads,
    realtimePayloadMaxMessages,
    "diagnostics:read-model"
  );
  const retentionDiagnosticsDetail = useWorkableRealtimeView<WorkComponentQueryResult>(
    hydratedConnection,
    "diagnostics",
    retentionDiagnosticsDetailRequest,
    diagnosticsRealtimeEnabled && systemNotificationOpen && retentionDiagnosticsExpanded,
    captureRealtimePayloads,
    realtimePayloadMaxMessages,
    "diagnostics:retention"
  );
  const concurrencyDiagnosticsDetail = useWorkableRealtimeView<WorkComponentQueryResult>(
    hydratedConnection,
    "diagnostics",
    concurrencyDiagnosticsDetailRequest,
    diagnosticsRealtimeEnabled && systemNotificationOpen && concurrencyDiagnosticsExpanded,
    captureRealtimePayloads,
    realtimePayloadMaxMessages,
    "diagnostics:concurrency"
  );
  const durabilityDiagnosticsDetail = useWorkableRealtimeView<WorkComponentQueryResult>(
    hydratedConnection,
    "diagnostics",
    durabilityDiagnosticsDetailRequest,
    diagnosticsRealtimeEnabled && systemNotificationOpen && durabilityDiagnosticsExpanded,
    captureRealtimePayloads,
    realtimePayloadMaxMessages,
    "diagnostics:durability"
  );
  const idempotencyDiagnosticsDetail = useWorkableRealtimeView<WorkComponentQueryResult>(
    hydratedConnection,
    "diagnostics",
    idempotencyDiagnosticsDetailRequest,
    diagnosticsRealtimeEnabled && systemNotificationOpen && idempotencyDiagnosticsExpanded,
    captureRealtimePayloads,
    realtimePayloadMaxMessages,
    "diagnostics:idempotency"
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
  const captureRealtimeEvents =
    realtimePayloadOpen &&
    realtimePayloadActiveTab === "events";
  const realtimeEvents = useWorkableRealtimeEvents(
    hydratedConnection,
    eventViewerCriteria,
    Boolean(hydratedConnection?.realtimeHubPath) &&
      activeCanUseRealtimeEvents &&
      captureRealtimeEvents &&
      selectedEventViewerEventTypes.length > 0,
    captureRealtimeEvents,
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
  const upsertSystemIssueNotification = useCallback((
    notification: Omit<SystemNotification, "onDismiss"> | null
  ) => {
    if (!notification) {
      return;
    }

    setSystemIssueNotifications((current) => {
      const existing = current[notification.id];
      if (existing &&
        existing.title === notification.title &&
        existing.description === notification.description &&
        existing.tone === notification.tone) {
        return current;
      }

      return {
        ...current,
        [notification.id]: notification,
      };
    });
  }, []);
  const clearSystemIssueNotification = useCallback((notificationId: string) => {
    setSystemIssueNotifications((current) => {
      if (!(notificationId in current)) {
        return current;
      }

      const next = { ...current };
      delete next[notificationId];
      return next;
    });
  }, []);
  const extraSystemNotifications = useMemo<SystemNotification[]>(
    () => Object.values(systemIssueNotifications).map((notification) => ({
      ...notification,
      onDismiss: () => clearSystemIssueNotification(notification.id),
    })),
    [clearSystemIssueNotification, systemIssueNotifications]
  );

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
          const matchesTargetScope =
            host.apiUrl === target.apiUrl &&
            (system.systemName ?? "") === targetSystemName;

          if (!matchesTargetScope || system.state === state) {
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
    targetId: string,
    snapshot: DiagnosticsAlertSnapshot | null
  ) => {
    const target = diagnosticsAlertTargets.find((candidate) => candidate.id === targetId);
    const systemDiagnostics = getWorkComponentData<WorkSystemDiagnosticsCompactComponent>(
      snapshot?.data,
      "systemDiagnostics"
    );
    if (systemDiagnostics?.systemState) {
      if (target) {
        updateSystemStateFromDiagnosticsTarget(target, systemDiagnostics.systemState);
      }
    }

    setDiagnosticsAlertsByTargetId((current) => {
      if (!snapshot) {
        if (!(targetId in current)) {
          return current;
        }

        const next = { ...current };
        delete next[targetId];
        return next;
      }

      if (diagnosticsAlertSnapshotsEqual(current[targetId], snapshot)) {
        return current;
      }

      return {
        ...current,
        [targetId]: snapshot,
      };
    });
  }, [diagnosticsAlertTargets, updateSystemStateFromDiagnosticsTarget]);

  useEffect(() => {
    queueMicrotask(() => {
      const loaded = loadConsoleStorage();
      restoredHostsRef.current = loaded.hosts;
      setConsoleState(loaded);
      setView(loaded.view);
      setVisibleView(loaded.view);
      setMountedViews(new Set([loaded.view]));
      setPendingView(null);
      setHasMounted(true);
    });
  }, []);

  useEffect(() => {
    const hostsToRevalidate = restoredHostsRef.current ?? [];
    if (!hasMounted || hostsToRevalidate.length === 0) {
      return;
    }

    let canceled = false;

    void (async () => {
      const discoveries = await Promise.all(
        hostsToRevalidate.map(async (host) => {
          try {
            const result = await discoverHost(host.apiUrl);
            return {
              hostId: host.id,
              host: reconcileStoredHostWithDiscovery(host, result),
            };
          } catch {
            return null;
          }
        })
      );

      if (canceled) {
        return;
      }

      let resetNavigation = false;
      setConsoleState((current) => {
        const updates = new Map(
          discoveries
            .filter((discovery): discovery is {
              hostId: string;
              host: WorkableHostConnection;
            } => discovery !== null)
            .map((discovery) => [discovery.hostId, discovery])
        );

        if (updates.size === 0) {
          return current;
        }

        const hosts = current.hosts.map((host) => {
          const update = updates.get(host.id);
          return update ? update.host : host;
        });
        const availableSystemIds = new Set(
          hosts.flatMap((host) => host.systems.map((system) => system.id))
        );
        const nextActiveSystemId =
          current.activeSystemId && availableSystemIds.has(current.activeSystemId)
            ? current.activeSystemId
            : getFirstAvailableSystemId(hosts);
        const expandedSystemIds = current.expandedSystemIds.filter((id) =>
          availableSystemIds.has(id)
        );

        resetNavigation = nextActiveSystemId !== current.activeSystemId;

        return {
          ...current,
          activeSystemId: nextActiveSystemId,
          expandedHostIds: current.expandedHostIds.filter((id) =>
            hosts.some((host) => host.id === id)
          ),
          expandedSystemIds,
          hosts,
        };
      });

      if (resetNavigation) {
        setSelectedDefinitionId(null);
        setSelectedDefinitionName(null);
        setSelectedWorkerId(null);
        setNavigationHistory([]);
        setView("overview");
        setVisibleView("overview");
        setPendingView("overview");
        setMountedViews(new Set(["overview"]));
      }
    })();

    return () => {
      canceled = true;
    };
  }, [hasMounted]);

  useEffect(() => {
    if (!hasMounted) {
      return;
    }

    if (typeof window !== "undefined") {
      window.localStorage.setItem(STORAGE_KEY, JSON.stringify(consoleState));
    }
  }, [consoleState, hasMounted]);

  useEffect(() => {
    if (!hydratedConnection || !realtimePayloadOpen) {
      return;
    }

    let canceled = false;
    queueMicrotask(() => {
      if (!canceled) {
        setEventViewerDefinitionsLoading(true);
        setEventViewerDefinitionError(undefined);
      }
    });
    workableFetch<WorkDefinition[]>(hydratedConnection, "definitions")
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
  }, [hydratedConnection, realtimePayloadOpen]);

  const currentNavigation = useCallback(
    (): NavigationEntry => ({
      catalogScope: cloneOverviewScope(
        catalogScopeBySystemId[consoleState.activeSystemId] ?? null
      ),
      definitionId: selectedDefinitionId,
      definitionName: selectedDefinitionName,
      iterationSequence: selectedIterationSequence,
      iterationCategoryFilter,
      iterationDefinitionFilter,
      iterationKeyTypeFilter,
      iterationStatusFilter,
      iterationWorkerId: selectedIterationWorkerId,
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
      selectedDefinitionName,
      selectedIterationSequence,
      selectedIterationWorkerId,
      selectedWorkerId,
      workerCategoryFilter,
      view,
      workerDefinitionFilter,
      workerStateFilter,
    ]
  );

  const pushCurrentNavigation = useCallback((clearForward = true) => {
    const entry = currentNavigation();
    if (clearForward) {
      setForwardNavigation([]);
    }
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
  const toggleRealtimePayloadOpen = useCallback(() => {
    setRealtimePayloadOpen((current) => !current);
  }, []);
  const defaultHeaderCapabilities = useMemo<ConsoleHeaderCapabilities | null>(
    () =>
      hydratedConnection
        ? {
            realtime: {
              connectionState: "disabled",
              enabled: false,
              title: "Nothing on this screen uses realtime.",
              menuItems: [
                {
                  active: realtimePayloadOpen,
                  icon: <Rows4 className="size-4" />,
                  id: "global-realtime-payloads",
                  label: "Realtime payloads",
                  onSelect: toggleRealtimePayloadOpen,
                },
              ],
            },
            refresh: {
              ariaLabel: headerRefreshTitle(view),
              onRefresh: () => refreshView(view),
              title: headerRefreshTitle(view),
            },
          }
        : null,
    [hydratedConnection, realtimePayloadOpen, refreshView, toggleRealtimePayloadOpen, view]
  );

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
    if (visibleView !== "worker" && visibleView !== "iteration") {
      viewScrollPositions.current[visibleView] = getWindowScrollTop();
    }
  }, [visibleView]);

  const openWorker = useCallback((workerId: string, trackHistory = true) => {
    rememberCurrentViewScroll();
    if (trackHistory) {
      pushCurrentNavigation();
    }
    setSelectedDefinitionId(null);
    setSelectedDefinitionName(null);
    setSelectedIterationWorkerId(null);
    setSelectedIterationSequence(null);
    setSelectedWorkerId(workerId);
    setVisibleView("worker");
    setPendingView(null);
    setView("worker");
    refreshView("worker");
  }, [pushCurrentNavigation, refreshView, rememberCurrentViewScroll]);

  const openIteration = (workerId: string, sequence: number, trackHistory = true) => {
    rememberCurrentViewScroll();
    if (trackHistory) {
      pushCurrentNavigation();
    }
    setSelectedDefinitionId(null);
    setSelectedDefinitionName(null);
    setSelectedWorkerId(null);
    setSelectedIterationWorkerId(workerId);
    setSelectedIterationSequence(sequence);
    setVisibleView("iteration");
    setPendingView(null);
    setView("iteration");
    refreshView("iteration");
  };

  const openDefinition = (
    definitionId: string,
    options?: {
      definitionName?: string;
      systemId?: string;
    }
  ) => {
    const systemId = options?.systemId ?? activeSystem?.id ?? "";
    rememberCurrentViewScroll();
    pushCurrentNavigation();
    setSelectedWorkerId(null);
    setSelectedIterationWorkerId(null);
    setSelectedIterationSequence(null);
    setSelectedDefinitionId(definitionId);
    setSelectedDefinitionName(options?.definitionName ?? null);
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
        definitionName: nextView === "definition" ? selectedDefinitionName : null,
        iterationSequence: null,
        iterationWorkerId: null,
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

    if (nextView !== "worker" && nextView !== "iteration") {
      setSelectedWorkerId(null);
      setSelectedIterationWorkerId(null);
      setSelectedIterationSequence(null);
      if (nextView !== "definition") {
        setSelectedDefinitionId(null);
        setSelectedDefinitionName(null);
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
    setSelectedIterationSequence(entry.iterationSequence);
    setSelectedIterationWorkerId(entry.iterationWorkerId);
    setIterationStatusFilter(entry.iterationStatusFilter);
    setKeyTypeFilter(entry.keyTypeFilter);
    setSelectedDefinitionId(entry.definitionId);
    setSelectedDefinitionName(entry.definitionName);
    setSelectedWorkerId(entry.workerId);
    setWorkerCategoryFilter(entry.workerCategoryFilter);
    setWorkerDefinitionFilter(entry.workerDefinitionFilter);
    setWorkerStateFilter(entry.workerStateFilter);
    setConsoleState((current) => ({
      ...current,
      activeSystemId: entry.systemId,
      view: isServerView(entry.view) ? entry.view : current.view,
    }));
    if (entry.view !== "worker" && entry.view !== "iteration") {
      setMountedViews((current) => new Set([...current, entry.view]));
    }
    setVisibleView(entry.view);
    setPendingView(null);
    setView(entry.view);
  }, []);

  const navigateBack = useCallback(() => {
    const previous = navigationHistory.at(-1);
    if (!previous) {
      return;
    }

    const currentEntry = currentNavigation();
    restoreNavigation(previous);
    if (previous.view === "workers" || previous.view === "iterations") {
      refreshView(previous.view);
    }
    setForwardNavigation((current) =>
      navigationEntriesEqual(current.at(-1), currentEntry)
        ? current
        : [...current, currentEntry].slice(-20)
    );
    setNavigationHistory((current) => current.slice(0, -1));
  }, [currentNavigation, navigationHistory, refreshView, restoreNavigation]);
  const navigateForward = useCallback(() => {
    const next = forwardNavigation.at(-1);
    if (!next) {
      return;
    }

    const currentEntry = currentNavigation();
    restoreNavigation(next);
    if (next.view === "workers" || next.view === "iterations") {
      refreshView(next.view);
    }
    setNavigationHistory((current) =>
      navigationEntriesEqual(current.at(-1), currentEntry)
        ? current
        : [...current, currentEntry].slice(-20)
    );
    setForwardNavigation((current) => current.slice(0, -1));
  }, [currentNavigation, forwardNavigation, refreshView, restoreNavigation]);
  const breadcrumbParent = useMemo(() => {
    const previous = navigationHistory.at(-1);
    if (view === "definition" && previous?.view === "worker" && previous.workerId) {
      return {
        label: previous.workerId,
        onSelect: navigateBack,
      };
    }

    if (view === "worker") {
      const workerParent = [...navigationHistory]
        .reverse()
        .find((entry) =>
          entry.view === "definitions" ||
          entry.view === "definition" ||
          entry.view === "workers" ||
          entry.view === "iterations"
        );

      if (workerParent?.view === "definitions") {
        return {
          label: navTitle("definitions"),
          onSelect: navigateBack,
        };
      }

      if (workerParent?.view === "definition") {
        return {
          label: workerParent.definitionName ?? workerParent.definitionId ?? navTitle("definition"),
          onSelect: navigateBack,
        };
      }

      if (workerParent?.view === "workers" || workerParent?.view === "iterations") {
        return {
          label: navTitle(workerParent.view),
          onSelect: navigateBack,
        };
      }
    }

    if (view === "iteration") {
      const iterationParent = [...navigationHistory]
        .reverse()
        .find((entry) =>
          entry.view === "worker" ||
          entry.view === "iterations" ||
          entry.view === "definitions" ||
          entry.view === "definition"
        );

      if (selectedIterationWorkerId) {
        return {
          label: selectedIterationWorkerId,
          onSelect: () => {
            if (iterationParent?.view === "worker" && iterationParent.workerId === selectedIterationWorkerId) {
              navigateBack();
              return;
            }

            openWorker(selectedIterationWorkerId);
          },
        };
      }
    }

    return null;
  }, [navigateBack, navigationHistory, openWorker, selectedIterationWorkerId, view]);
  const autoOpenScopedDefinitionInCatalog = useMemo(() => {
    const previous = navigationHistory.at(-1);
    return previous?.view !== "worker";
  }, [navigationHistory]);

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
    if (visibleView === "worker" || visibleView === "iteration") {
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
    if (visibleView === "worker" || visibleView === "iteration") {
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

  const signOut = async () => {
    if (isSigningOut) {
      return;
    }

    setIsSigningOut(true);
    try {
      await fetch("/api/auth/logout", {
        method: "POST",
      });
    } finally {
      router.replace("/login");
      router.refresh();
      setIsSigningOut(false);
    }
  };

  return (
    <SidebarProvider scrollMode={usesPanelOwnedScroll ? "panel" : "browser"}>
      <DiagnosticsAlertSubscriptions
        captureEnabled={captureRealtimePayloads}
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
        <SidebarFooter>
          <div className="p-2">
            <Button
              className="w-full justify-start"
              disabled={isSigningOut}
              onClick={() => void signOut()}
              variant="outline"
            >
              {isSigningOut ? <Loader2 className="size-4 animate-spin" /> : <LogOut />}
              <span>{isSigningOut ? "Signing out..." : "Sign out"}</span>
            </Button>
          </div>
        </SidebarFooter>
      </Sidebar>
      <SidebarInset>
        <main className="flex min-h-0 flex-1 flex-col bg-background">
          <div className="relative mx-auto flex min-h-0 w-full max-w-7xl flex-1 flex-col p-4 md:p-6">
              {!hydratedConnection && (
                <EmptyServerState
                  description={
                    consoleState.hosts.length > 0
                      ? "Your saved servers are still configured, but the current user cannot Connect to any currently discovered Workable systems."
                      : undefined
                  }
                  onAddServer={() => setServerDialog({ mode: "add" })}
                  title={consoleState.hosts.length > 0 ? "No accessible systems" : undefined}
                />
              )}
              {hydratedConnection && (
                <ConsolePageRealtimeViewProvider>
                  <ConsoleHeaderCapabilitiesProvider defaultCapabilities={defaultHeaderCapabilities}>
                    <ConsoleViewport scrollMode={usesPanelOwnedScroll ? "panel" : "browser"}>
                      {activeHost && activeSystem && (
                        <ConsoleNavigationHeader
                          breadcrumbParent={breadcrumbParent}
                          canGoBack={navigationHistory.length > 0}
                          canGoForward={forwardNavigation.length > 0}
                          definitionId={selectedDefinitionId}
                          definitionName={selectedDefinitionName}
                          host={activeHost}
                          iterationSequence={selectedIterationSequence}
                          onBack={navigateBack}
                          onForward={navigateForward}
                          onOpenView={openView}
                          system={activeSystem}
                          systemNotifications={(
                            <div className="flex items-center gap-1">
                              <SystemNotificationTray
                                acknowledgedRejectedWorkCounts={acknowledgedRejectedWorkCounts}
                                activeDiagnosticsAlertTargetId={activeDiagnosticsAlertTargetId}
                                activeSystemDiagnosticsAvailable={activeCanUseRealtimeDiagnosticsUi}
                                alertSources={diagnosticsAlertSources}
                                concurrencyDetailDiagnostics={concurrencyDiagnosticsDetail}
                                concurrencyExpanded={concurrencyDiagnosticsExpanded}
                                durabilityDetailDiagnostics={durabilityDiagnosticsDetail}
                                durabilityExpanded={durabilityDiagnosticsExpanded}
                                extraNotifications={extraSystemNotifications}
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
                            </div>
                          )}
                          view={view}
                          workerId={selectedWorkerId}
                        />
                      )}
                      <ConsoleViewportContent scrollMode={usesPanelOwnedScroll ? "panel" : "browser"}>
                        <ErrorPanel errors={[lifecycleError]} />
                        {mountedViews.has("overview") && (
                          <ConsoleViewMount active={visibleView === "overview"}>
                            <OverviewView
                              access={activeSystem?.access}
                              connection={hydratedConnection}
                              hiddenPanelIds={consoleState.overviewHiddenPanels}
                              hiddenThroughputSeries={consoleState.overviewHiddenThroughputSeries}
                              isVisible={visibleView === "overview"}
                              onConnectionError={handleOverviewConnectionError}
                              onActiveRealtimeConnectionCountChange={ignoreRealtimeConnectionCountChange}
                              onStateLoaded={handleOverviewStateLoaded}
                              onOpenCatalog={() => openView("definitions")}
                              onOpenIterations={openIterations}
                              onOpenKeyType={openIterationsByKeyType}
                              onReady={markOverviewReady}
                              onPanelShapeChange={setOverviewPanelShape}
                              onPanelVisibilityChange={setOverviewPanelVisible}
                              onResetUi={resetOverviewUiToDefaults}
                              onThroughputSeriesToggle={toggleOverviewThroughputSeries}
                              panelShapes={consoleState.overviewPanelShapes}
                              realtimePayloadCaptureEnabled={realtimePayloadCaptureEnabled}
                              realtimePayloadMaxMessages={realtimePayloadMaxMessages}
                              realtimePayloadOpen={realtimePayloadOpen}
                              onRealtimePayloadOpenChange={setRealtimePayloadOpen}
                              onViewIterationsByStatus={openIterationsFiltered}
                              onViewWorkersByState={openWorkersFiltered}
                              overviewScope={activeOverviewScope}
                              refreshToken={refreshTokens.overview}
                              onOpenWorker={openWorker}
                              renderControls={() => (
                                <OverviewCatalogFilter
                                  connection={hydratedConnection}
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
                              )}
                            />
                          </ConsoleViewMount>
                        )}
                        {mountedViews.has("definitions") && (
                          <ConsoleViewMount active={visibleView === "definitions"}>
                            <DefinitionsView
                              autoOpenScopedDefinition={autoOpenScopedDefinitionInCatalog}
                              catalogScope={activeCatalogScope}
                              connection={hydratedConnection}
                              onCatalogScopeChange={(scope) => {
                                if (activeSystem) {
                                  openCatalogScope(activeSystem.id, scope);
                                }
                              }}
                              onOpenDefinition={(definitionId, definitionName) =>
                                openDefinition(definitionId, {
                                  definitionName,
                                  systemId: activeSystem?.id ?? "",
                                })
                              }
                              onOpenWorker={openWorker}
                              onReady={markDefinitionsReady}
                              refreshToken={refreshTokens.definitions}
                            />
                          </ConsoleViewMount>
                        )}
                        {mountedViews.has("definition") && selectedDefinitionId && (
                          <ConsoleViewMount active={visibleView === "definition"}>
                            <DefinitionView
                              connection={hydratedConnection}
                              definitionId={selectedDefinitionId}
                              onDefinitionResolved={setSelectedDefinitionName}
                              onOpenWorker={openWorker}
                              onReady={markDefinitionReady}
                              refreshToken={refreshTokens.definition}
                            />
                          </ConsoleViewMount>
                        )}
                        {mountedViews.has("workers") && (
                          <ConsoleViewMount
                            active={visibleView === "workers"}
                            fill
                            scrollMode="panel"
                          >
                            <WorkersView
                              categoryFilter={workerCategoryFilter}
                              connection={hydratedConnection}
                              filterControl={{
                                activeCount: getQueryFilterActiveCount(
                                  createQueryCatalogScope(workerCategoryFilter, workerDefinitionFilter),
                                  workerStateFilter,
                                  keyTypeFilter
                                ),
                                content: (
                                  <QueryFilterPanelContent
                                    allFacetLabel="All states"
                                    catalogScope={createQueryCatalogScope(workerCategoryFilter, workerDefinitionFilter)}
                                    connection={hydratedConnection}
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
                                ),
                                contentClassName: "w-[26rem] p-0",
                                label: "Filter workers",
                              }}
                              isLoadingTarget={visibleView === "workers" || pendingView === "workers"}
                              isVisible={visibleView === "workers"}
                              onOpenWorker={openWorker}
                              onReady={markWorkersReady}
                              definitionFilter={workerDefinitionFilter}
                              keyTypeFilter={keyTypeFilter}
                              stateFilter={workerStateFilter}
                              refreshToken={refreshTokens.workers}
                            />
                          </ConsoleViewMount>
                        )}
                        {mountedViews.has("iterations") && (
                          <ConsoleViewMount
                            active={visibleView === "iterations"}
                            fill
                            scrollMode="panel"
                          >
                            <IterationsView
                              categoryFilter={iterationCategoryFilter}
                              connection={hydratedConnection}
                              definitionFilter={iterationDefinitionFilter}
                              filterControl={{
                                activeCount: getQueryFilterActiveCount(
                                  createQueryCatalogScope(iterationCategoryFilter, iterationDefinitionFilter),
                                  iterationStatusFilter,
                                  iterationKeyTypeFilter
                                ),
                                content: (
                                  <QueryFilterPanelContent
                                    allFacetLabel="All statuses"
                                    catalogScope={createQueryCatalogScope(iterationCategoryFilter, iterationDefinitionFilter)}
                                    connection={hydratedConnection}
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
                                ),
                                contentClassName: "w-[26rem] p-0",
                                label: "Filter iterations",
                              }}
                              isLoadingTarget={visibleView === "iterations" || pendingView === "iterations"}
                              isVisible={visibleView === "iterations"}
                              keyTypeFilter={iterationKeyTypeFilter}
                              onOpenIteration={openIteration}
                              onReady={markIterationsReady}
                              refreshToken={refreshTokens.iterations}
                              statusFilter={iterationStatusFilter}
                            />
                          </ConsoleViewMount>
                        )}
                        <DelayedLoadingOverlay
                          active={!!pendingView && view !== "worker" && view !== "iteration"}
                          label={`Loading ${pendingView ? navTitle(pendingView) : "view"}`}
                        />
                        {view === "worker" && selectedWorkerId && (
                          <ConsoleViewMount active={true}>
                            <WorkerConsoleView
                              clearSystemNotification={clearSystemIssueNotification}
                              connection={hydratedConnection}
                              key={selectedWorkerId}
                              onActiveRealtimeConnectionCountChange={ignoreRealtimeConnectionCountChange}
                              onOpenDefinitionCatalog={(definitionName, category) => openCatalogScope(
                                activeSystemId,
                                {
                                  category: normalizeCategoryFilter(category ?? "") || undefined,
                                  definitionName: definitionName.trim() || undefined,
                                }
                              )}
                              onNavigateBack={navigateBack}
                              onOpenIteration={openIteration}
                              onOpenWorker={openWorker}
                              onRealtimePayloadOpenChange={setRealtimePayloadOpen}
                              refreshToken={refreshTokens.worker}
                              realtimePayloadCaptureEnabled={realtimePayloadCaptureEnabled}
                              realtimePayloadMaxMessages={realtimePayloadMaxMessages}
                              realtimePayloadOpen={realtimePayloadOpen}
                              reportSystemNotification={upsertSystemIssueNotification}
                              workerId={selectedWorkerId}
                            />
                          </ConsoleViewMount>
                        )}
                        {view === "iteration" && selectedIterationWorkerId && selectedIterationSequence !== null && (
                          <ConsoleViewMount active={true}>
                            <IterationConsoleView
                              connection={hydratedConnection}
                              key={`${selectedIterationWorkerId}:${selectedIterationSequence}`}
                              onNavigateBack={navigateBack}
                              onOpenDefinition={(definitionId, definitionName) =>
                                openDefinition(definitionId, {
                                  definitionName: definitionName ?? undefined,
                                  systemId: activeSystem?.id ?? "",
                                })
                              }
                              refreshToken={refreshTokens.iteration}
                              sequence={selectedIterationSequence}
                              workerId={selectedIterationWorkerId}
                            />
                          </ConsoleViewMount>
                        )}
                        <RealtimePayloadWindowHost
                          eventTabContent={(
                            <RealtimeEventsTabPanel
                              definitionError={eventViewerDefinitionError}
                              definitions={eventViewerDefinitions}
                              definitionsLoading={eventViewerDefinitionsLoading}
                              error={realtimeEvents.error}
                              eventTypes={eventViewerEventTypes}
                              maxMessages={eventViewerMaxMessages}
                              messages={realtimeEventCapture.messages}
                              onAddKey={addEventViewerKey}
                              onClearMessages={clearConsoleRealtimeEventCapture}
                              onDefinitionToggle={toggleEventViewerDefinition}
                              onEventTypeToggle={toggleEventViewerEventType}
                              onMaxMessagesChange={setEventViewerMaxMessages}
                              onRemoveKey={removeEventViewerKey}
                              realtimeStats={realtimeStats}
                              selectedDefinitionIds={selectedEventViewerDefinitionIds}
                              selectedEventTypes={selectedEventViewerEventTypes}
                              selectedKeys={selectedEventViewerKeys}
                            />
                          )}
                          maxMessages={realtimePayloadMaxMessages}
                          onActiveTabChange={setRealtimePayloadActiveTab}
                          onMaxMessagesChange={setRealtimePayloadMaxMessages}
                          onOpenChange={setRealtimePayloadOpen}
                          open={realtimePayloadOpen}
                          realtimeStats={realtimeStats}
                          activeTab={realtimePayloadActiveTab}
                        />
                      </ConsoleViewportContent>
                    </ConsoleViewport>
                  </ConsoleHeaderCapabilitiesProvider>
                </ConsolePageRealtimeViewProvider>
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

function RealtimePayloadWindowHost({
  activeTab,
  eventTabContent,
  maxMessages,
  onActiveTabChange,
  onMaxMessagesChange,
  onOpenChange,
  open,
  realtimeStats,
}: {
  activeTab: RealtimePayloadWindowTab;
  eventTabContent: ReactNode;
  maxMessages: number;
  onActiveTabChange: (tab: RealtimePayloadWindowTab) => void;
  onMaxMessagesChange: (maxMessages: number) => void;
  onOpenChange: (open: boolean) => void;
  open: boolean;
  realtimeStats: ReturnType<typeof useConsoleRealtimeStats>;
}) {
  const realtimePayloadCapture = useConsoleRealtimePayloadCapture();

  return (
    <RealtimePayloadWindow
      activeTab={activeTab}
      eventTabContent={eventTabContent}
      maxMessages={maxMessages}
      messages={realtimePayloadCapture.messages}
      onActiveTabChange={onActiveTabChange}
      onClearMessages={clearConsoleRealtimePayloadCapture}
      onMaxMessagesChange={onMaxMessagesChange}
      onOpenChange={onOpenChange}
      open={open}
      realtimeStats={realtimeStats}
    />
  );
}

type SystemDiagnosticsViewState = RealtimeViewLoadable<WorkComponentQueryResult>;

type DiagnosticsAlertTarget = {
  id: string;
  apiUrl: string;
  displayName: string;
  hostId: string;
  hostName: string;
  realtimeHubPath: string;
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
  onSnapshot: (targetId: string, snapshot: DiagnosticsAlertSnapshot | null) => void;
  request: unknown;
  targets: DiagnosticsAlertTarget[];
}) {
  return (
    <>
      {targets.map((target) => (
        <DiagnosticsAlertSubscription
          captureEnabled={captureEnabled}
          enabled={enabled}
          key={target.id}
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
  onSnapshot: (targetId: string, snapshot: DiagnosticsAlertSnapshot | null) => void;
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
    `diagnostics:alerts:${target.id}`
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
    onSnapshot(target.id, snapshot);
  }, [
    diagnostics.connectionState,
    diagnostics.data,
    diagnostics.enabled,
    diagnostics.error,
    diagnostics.loading,
    diagnostics.refreshing,
    onSnapshot,
    target.id,
  ]);

  useEffect(
    () => () => {
      lastSnapshotRef.current = null;
      onSnapshot(target.id, null);
    },
    [onSnapshot, target.id]
  );

  return null;
}

function RealtimeEventsTabPanel({
  definitionError,
  definitions,
  definitionsLoading,
  error,
  eventTypes,
  maxMessages,
  messages,
  onAddKey,
  onClearMessages,
  onDefinitionToggle,
  onEventTypeToggle,
  onMaxMessagesChange,
  onRemoveKey,
  realtimeStats,
  selectedDefinitionIds,
  selectedEventTypes,
  selectedKeys,
}: {
  definitionError?: string;
  definitions: WorkDefinition[];
  definitionsLoading: boolean;
  error?: string;
  eventTypes: readonly string[];
  maxMessages: number;
  messages: RealtimeEventMessage[];
  onAddKey: (key: WorkableRealtimeEventKeyCriteria) => void;
  onClearMessages: () => void;
  onDefinitionToggle: (definitionId: string) => void;
  onEventTypeToggle: (eventType: string) => void;
  onMaxMessagesChange: (maxMessages: number) => void;
  onRemoveKey: (key: WorkableRealtimeEventKeyCriteria) => void;
  realtimeStats: ReturnType<typeof useConsoleRealtimeStats>;
  selectedDefinitionIds: string[];
  selectedEventTypes: string[];
  selectedKeys: WorkableRealtimeEventKeyCriteria[];
}) {
  const [catalogPath, setCatalogPath] = useState("");
  const [eventTableHeight, setEventTableHeight] = useState(208);
  const [filtersCollapsed, setFiltersCollapsed] = useState(false);
  const [messagesCollapsed, setMessagesCollapsed] = useState(false);
  const [searchText, setSearchText] = useState("");
  const [selectedEventIndex, setSelectedEventIndex] = useState(0);
  const [selectedMessageId, setSelectedMessageId] = useState<string | null>(null);
  const [tablePaused, setTablePaused] = useState(false);
  const [pausedMessages, setPausedMessages] = useState<RealtimeEventMessage[] | null>(null);
  const [keyKind, setKeyKind] = useState<WorkKeyKind | "Any">("Any");
  const [keyType, setKeyType] = useState("");
  const [keyValue, setKeyValue] = useState("");
  const eventRowRefs = useRef<Array<HTMLButtonElement | null>>([]);
  const eventTableResizeRef = useRef<{
    startHeight: number;
    startY: number;
  } | null>(null);
  const deferredSearchText = useDeferredValue(searchText);
  const normalizedSearchText = deferredSearchText.trim().toLowerCase();
  const tableBaseMessages = tablePaused && pausedMessages ? pausedMessages : messages;
  const filteredMessages = useMemo(
    () =>
      tableBaseMessages.filter((message) =>
        !normalizedSearchText ||
        getRealtimeEventSearchText(message).includes(normalizedSearchText)
      ),
    [normalizedSearchText, tableBaseMessages]
  );
  const newMessageCount = useMemo(() => {
    if (!tablePaused || !pausedMessages) {
      return 0;
    }

    const pausedIds = new Set(pausedMessages.map((message) => message.id));
    return messages.filter((message) => !pausedIds.has(message.id)).length;
  }, [messages, pausedMessages, tablePaused]);
  const selectedMessage =
    filteredMessages.find((message) => message.id === selectedMessageId) ??
    filteredMessages[0];
  const selectedEvent = selectedMessage?.events[Math.min(selectedEventIndex, selectedMessage.events.length - 1)];
  const selectedEventIndexInBounds = selectedEvent
    ? Math.min(selectedEventIndex, (selectedMessage?.events.length ?? 1) - 1)
    : 0;
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
    const row = eventRowRefs.current[selectedEventIndexInBounds];
    row?.scrollIntoView({ block: "nearest" });
    if (document.activeElement && eventRowRefs.current.includes(document.activeElement as HTMLButtonElement)) {
      row?.focus();
    }
  }, [selectedEventIndexInBounds, selectedMessage?.id]);

  const toggleTablePaused = () => {
    if (tablePaused) {
      setPausedMessages(null);
      setTablePaused(false);
      return;
    }

    setPausedMessages(messages);
    setTablePaused(true);
  };

  const showNewMessages = () => {
    setPausedMessages(messages);
  };

  const clearMessages = () => {
    setSelectedMessageId(null);
    setSelectedEventIndex(0);
    setTablePaused(false);
    setPausedMessages(null);
    onClearMessages();
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
    <div className="grid h-full min-h-0 grid-rows-[auto_minmax(0,1fr)] gap-3 overflow-hidden">
      <div className="grid gap-2 rounded-md border bg-muted/30 px-2 py-2">
        <div className="flex min-w-0 flex-wrap items-center gap-1">
          <input
            className="h-7 min-w-48 flex-1 rounded-md border bg-background px-2 text-foreground text-xs"
            onChange={(event) => setSearchText(event.currentTarget.value)}
            placeholder="Filter events"
            value={searchText}
          />
          <RealtimeStatsMenu realtimeStats={realtimeStats} />
          <Button
            className="h-7 px-2 text-xs"
            onClick={toggleTablePaused}
            size="sm"
            variant={tablePaused ? "secondary" : "ghost"}
          >
            {tablePaused ? <Play className="size-3.5" /> : <Pause className="size-3.5" />}
            {tablePaused ? "Resume" : "Pause"}
          </Button>
          {tablePaused && (
            <Button
              className="h-7 px-2 text-xs"
              disabled={newMessageCount === 0}
              onClick={showNewMessages}
              size="sm"
              variant={newMessageCount > 0 ? "secondary" : "ghost"}
            >
              Show {newMessageCount.toLocaleString()} new
            </Button>
          )}
          <label className="flex h-7 items-center gap-1.5 rounded-md border bg-background px-2 text-xs">
            <span className="text-muted-foreground">Max</span>
            <input
              className="w-14 bg-transparent font-mono text-foreground outline-none"
              max={1000}
              min={1}
              onChange={(event) =>
                onMaxMessagesChange(normalizeEventViewerMaxMessages(event.currentTarget.value))
              }
              type="number"
              value={maxMessages}
            />
          </label>
          <Button
            className="h-7 px-2 text-xs"
            disabled={messages.length === 0}
            onClick={clearMessages}
            size="sm"
            variant="ghost"
          >
            Clear
          </Button>
        </div>
        {error && (
          <div className="rounded-md border border-red-500/30 bg-red-500/10 px-2 py-1.5 text-red-200 text-xs">
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
          <div className="grid gap-2 border-b bg-muted/30 px-2 py-2">
            <div className="flex items-center justify-between gap-2">
              {!messagesCollapsed && (
                <div className="font-medium text-muted-foreground text-xs">Batches</div>
              )}
              <Button
                aria-label={messagesCollapsed ? "Show events" : "Collapse events"}
                className="ml-auto size-7"
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
            {!messagesCollapsed && (
              <div className="grid grid-cols-[6.5rem_minmax(8rem,1fr)_minmax(10rem,1.4fr)_minmax(8rem,1fr)_5rem] gap-3 font-medium text-muted-foreground text-xs">
                <span>Time</span>
                <span>Event types</span>
                <span>Definitions</span>
                <span>Workers</span>
                <span className="text-right">Size</span>
              </div>
            )}
          </div>
          {messagesCollapsed ? (
            <div className="flex min-h-0 items-start justify-center overflow-hidden py-2">
              <div className="font-mono text-muted-foreground text-xs [writing-mode:vertical-rl]">
                {filteredMessages.length.toLocaleString()} events
              </div>
            </div>
          ) : (
            <div className="min-h-0 overflow-auto p-2">
              {filteredMessages.length === 0 ? (
                <div className="p-3 text-muted-foreground text-sm">
                  {messages.length > 0 && normalizedSearchText
                    ? "No events match the current filter."
                    : selectedEventTypes.length === 0
                    ? "Select one or more event types to start capture."
                    : "Waiting for realtime events."}
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
                      onClick={() => {
                        setSelectedMessageId(message.id);
                        setSelectedEventIndex(0);
                      }}
                      type="button"
                    >
                      <span className="grid grid-cols-[6.5rem_minmax(8rem,1fr)_minmax(10rem,1.4fr)_minmax(8rem,1fr)_5rem] items-center gap-3">
                        <span className="font-mono">{formatEventViewerTime(message.receivedAt)}</span>
                        <span className="truncate text-muted-foreground">
                          {formatEventBatchTypeSummary(message.eventTypes)}
                        </span>
                        <span className="truncate text-muted-foreground">
                          {formatEventBatchDefinitionSummary(message.events)}
                        </span>
                        <span className="truncate font-mono text-muted-foreground">
                          {formatEventBatchWorkerSummary(message.events)}
                        </span>
                        <span className="font-mono text-right text-muted-foreground">
                          {formatEventByteCount(message.bytes, message.bytesEstimated)}
                        </span>
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
                      {workEvent.workDefinitionId?.value ?? "-"}
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
    .map((workEvent) => workEvent.workDefinitionId?.value)
    .filter((definitionId): definitionId is string => Boolean(definitionId)))];

  if (definitionIds.length === 0) {
    return "No definition";
  }

  return definitionIds.length === 1
    ? definitionIds[0]
    : `${definitionIds.length} definitions`;
}

function formatEventBatchWorkerSummary(events: WorkableRealtimeEvent[]) {
  const workerIds = [...new Set(events
    .map((workEvent) => workEvent.workerId?.value)
    .filter((workerId): workerId is string => Boolean(workerId)))];

  if (workerIds.length === 0) {
    return "System";
  }

  return workerIds.length === 1
    ? workerIds[0]
    : `${workerIds.length} workers`;
}

function getRealtimeEventSearchText(message: RealtimeEventMessage) {
  return [
    message.batchId,
    message.batchSize ? String(message.batchSize) : null,
    ...message.eventTypes,
    ...message.events.flatMap((workEvent) => [
      workEvent.eventType,
      workEvent.workerId?.value ?? null,
      workEvent.workDefinitionId?.value ?? null,
      workEvent.subjectId?.type ?? null,
      workEvent.subjectId?.value ?? null,
    ]),
    JSON.stringify(message.value),
  ]
    .filter((value): value is string => Boolean(value))
    .join(" ")
    .toLowerCase();
}

function formatEventByteCount(bytes: number, estimated?: boolean) {
  return `${estimated ? ">=" : ""}${bytes.toLocaleString()}b`;
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
  onDismiss?: () => void;
  rejectedWorkCount?: number;
  sourceId?: string;
  tone: "critical" | "warning";
  title: string;
};

function SystemNotificationTray({
  acknowledgedRejectedWorkCounts,
  activeDiagnosticsAlertTargetId,
  activeSystemDiagnosticsAvailable,
  alertSources,
  concurrencyDetailDiagnostics,
  concurrencyExpanded,
  durabilityDetailDiagnostics,
  durabilityExpanded,
  extraNotifications = [],
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
  activeDiagnosticsAlertTargetId: string | null;
  activeSystemDiagnosticsAvailable: boolean;
  alertSources: DiagnosticsAlertSource[];
  concurrencyDetailDiagnostics: SystemDiagnosticsViewState;
  concurrencyExpanded: boolean;
  durabilityDetailDiagnostics: SystemDiagnosticsViewState;
  durabilityExpanded: boolean;
  extraNotifications?: SystemNotification[];
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
  const activeAlertSource = activeDiagnosticsAlertTargetId
    ? alertSources.find((source) => source.target.id === activeDiagnosticsAlertTargetId)
    : undefined;
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
  const notifications = [
    ...extraNotifications,
    ...alertSources.flatMap((source) =>
    createSystemNotifications(
      getWorkComponentData<WorkSystemDiagnosticsCompactComponent>(source.data, "systemDiagnostics"),
      getWorkComponentData<WorkQueueDiagnosticsCompactComponent>(source.data, "queueDiagnostics"),
      acknowledgedRejectedWorkCounts[source.target.id] ?? 0,
      getWorkComponentData<WorkReadModelDiagnosticsCompactComponent>(source.data, "readModelDiagnostics"),
      getWorkComponentData<WorkRetentionDiagnosticsCompactComponent>(source.data, "retentionDiagnostics"),
      getWorkComponentData<WorkConcurrencyDiagnosticsCompactComponent>(source.data, "concurrencyDiagnostics"),
      getWorkComponentData<WorkDurabilityDiagnosticsCompactComponent>(source.data, "durabilityDiagnostics"),
      source.error,
      source.target
    )
    ),
  ];
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
            <div className="truncate text-muted-foreground text-xs">{alertSubscriptionText}</div>
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
                    <div className="min-w-0 flex-1">
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
                    {notification.onDismiss ? (
                      <Button
                        aria-label="Dismiss notification"
                        className="h-6 w-6 shrink-0 self-start rounded-sm p-0 text-current/80 hover:bg-black/10 hover:text-current dark:hover:bg-white/10"
                        onClick={() => notification.onDismiss?.()}
                        size="icon"
                        type="button"
                        variant="ghost"
                      >
                        <X className="size-3.5" />
                      </Button>
                    ) : null}
                  </div>
                </div>
              ))
            ) : (
              <div className="rounded-md border border-border bg-muted/20 px-3 py-2 text-muted-foreground text-sm">
                No system notifications.
              </div>
            )}
          </div>
          {activeSystemDiagnosticsAvailable ? (
            <>
              <div className="border-b bg-muted/10 px-3 py-2">
                <div className="font-medium text-sm">Diagnostics for system: {systemName}</div>
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
            </>
          ) : (
            <div className="border-t bg-muted/10 px-3 py-3">
              <div className="font-medium text-sm">Detailed diagnostics unavailable</div>
              <div className="text-muted-foreground text-xs">
                Notifications still include systems you can access, but the active system does not
                expose detailed diagnostics for this user.
              </div>
            </div>
          )}
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
      id: `${source?.id ?? "active"}:system-stopping`,
      tone: "warning",
      title: `${sourcePrefix}System is shutting down`,
    });
  }

  if (error) {
    notifications.push({
      description: error,
      id: `${source?.id ?? "active"}:diagnostics-unavailable`,
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
      id: `${source?.id ?? "active"}:queue-rejections`,
      rejectedWorkCount: queue.alertableRejectedWorkCount,
      sourceId: source?.id,
      tone: "critical",
      title: `${sourcePrefix}Work is being rejected`,
    });
  }

  if (readModel?.hasProjectorFailure) {
    notifications.push({
      description: `${readModel.projectorFailureType ?? "Projector failure"}${readModel.projectorFailureMessage ? `: ${readModel.projectorFailureMessage}` : ""}`,
      id: `${source?.id ?? "active"}:read-model-failure`,
      tone: "critical",
      title: `${sourcePrefix}Read model projector failed`,
    });
  }

  if (readModel?.isReadModelBehind) {
    notifications.push({
      description: `${formatNumber(readModel.pendingUpdateCount)} update${readModel.pendingUpdateCount === 1 ? "" : "s"} waiting to be projected${sourceSuffix}.`,
      id: `${source?.id ?? "active"}:read-model-lag`,
      tone: readModel.pendingUpdateCount >= readModel.readModelLagWarningThreshold * 10
        ? "critical"
        : "warning",
      title: `${sourcePrefix}Read model is behind`,
    });
  }

  if (retention?.hasSchedulerFailure) {
    notifications.push({
      description: `${retention.schedulerFailureType ?? "Retention scheduler failure"}${retention.schedulerFailureMessage ? `: ${retention.schedulerFailureMessage}` : ""}`,
      id: `${source?.id ?? "active"}:retention-failure`,
      tone: "critical",
      title: `${sourcePrefix}Retention scheduler failed`,
    });
  }

  if (retention?.isRetentionBehind) {
    notifications.push({
      description: `Oldest due purge is overdue by ${formatDuration(retention.oldestDuePurgeAge)}${sourceSuffix}.`,
      id: `${source?.id ?? "active"}:retention-lag`,
      tone: parseDurationSeconds(retention.oldestDuePurgeAge) >= retention.retentionLagWarningSeconds * 10
        ? "critical"
        : "warning",
      title: `${sourcePrefix}Retention is behind`,
    });
  }

  if (concurrency?.isConcurrencyBehind) {
    notifications.push({
      description: `${formatNumber(concurrency.deferredStartCount)} deferred worker${concurrency.deferredStartCount === 1 ? "" : "s"} waiting, oldest deferred for ${formatDuration(concurrency.oldestDeferredStartAge)}${sourceSuffix}.`,
      id: `${source?.id ?? "active"}:concurrency-lag`,
      tone: parseDurationSeconds(concurrency.oldestDeferredStartAge) >= concurrency.concurrencyLagWarningSeconds * 10
        ? "critical"
        : "warning",
      title: `${sourcePrefix}Concurrency is backed up`,
    });
  }

  if (durability?.hasReaderFailure) {
    notifications.push({
      description: `${durability.readerFailureType ?? "Durable reader failure"}${durability.readerFailureMessage ? `: ${durability.readerFailureMessage}` : ""}`,
      id: `${source?.id ?? "active"}:durability-reader-failure`,
      tone: "critical",
      title: `${sourcePrefix}Durable reader failed`,
    });
  }

  if (durability?.hasLeaseRenewalFailure) {
    notifications.push({
      description: `${durability.leaseRenewalFailureType ?? "Lease renewal failure"}${durability.leaseRenewalFailureMessage ? `: ${durability.leaseRenewalFailureMessage}` : ""}`,
      id: `${source?.id ?? "active"}:durability-renewal-failure`,
      tone: "critical",
      title: `${sourcePrefix}Durable lease renewal failed`,
    });
  }

  if (durability?.hasCleanupFailure) {
    notifications.push({
      description: `${durability.cleanupFailureType ?? "Cleanup failure"}${durability.cleanupFailureMessage ? `: ${durability.cleanupFailureMessage}` : ""}`,
      id: `${source?.id ?? "active"}:durability-cleanup-failure`,
      tone: "critical",
      title: `${sourcePrefix}Durable cleanup failed`,
    });
  }

  if (durability?.isAcceptedWorkerMaterializationBehind) {
    notifications.push({
      description: `${formatNumber(durability.acceptedWaiterCount)} accepted durable worker${durability.acceptedWaiterCount === 1 ? "" : "s"} waiting to materialize, oldest wait ${formatDuration(durability.oldestAcceptedWaiterAge)}${sourceSuffix}.`,
      id: `${source?.id ?? "active"}:durability-waiters`,
      tone: parseDurationSeconds(durability.oldestAcceptedWaiterAge) >= durability.acceptedWorkerWarningSeconds * 10
        ? "critical"
        : "warning",
      title: `${sourcePrefix}Durable worker materialization is behind`,
    });
  }

  if (durability?.isCleanupBehind) {
    notifications.push({
      description: `${formatNumber(durability.pendingCleanupCount)} durable cleanup item${durability.pendingCleanupCount === 1 ? "" : "s"} pending, oldest waiting ${formatDuration(durability.oldestPendingCleanupAge)}${sourceSuffix}.`,
      id: `${source?.id ?? "active"}:durability-cleanup-lag`,
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

function loadConsoleStorage(): ConsoleStorage {
  const fallback = createDefaultConsoleStorage();

  if (typeof window === "undefined") {
    return fallback;
  }

  const stored = window.localStorage.getItem(STORAGE_KEY);
  if (stored) {
    try {
      const parsed = JSON.parse(stored) as Partial<ConsoleStorage> & {
        overviewPanelShapes?: unknown;
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
            overviewPanelShapes: normalizeOverviewPanelShapes(parsed.overviewPanelShapes),
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
          : getFirstAvailableSystemId(hosts);

        return {
          activeSystemId,
          expandedHostIds: parsed.expandedHostIds?.filter((id) =>
            hosts.some((host) => host.id === id)
          ) ?? [hosts[0].id],
          expandedSystemIds: parsed.expandedSystemIds?.filter((id) => systemIds.has(id)) ?? (
            activeSystemId ? [activeSystemId] : []
          ),
          hosts,
          overviewHiddenPanels: normalizeOverviewHiddenPanels(
            parsed.overviewHiddenPanels,
            parsed.overviewThroughputHidden
          ),
          overviewPanelShapes: normalizeOverviewPanelShapes(parsed.overviewPanelShapes),
          overviewHiddenThroughputSeries: normalizeThroughputSeriesIds(
            parsed.overviewHiddenThroughputSeries
          ),
          overviewThroughputHidden: parsed.overviewThroughputHidden ?? false,
          view: isServerView(parsed.view) ? parsed.view : "overview",
        };
      }
    } catch {
      window.localStorage.removeItem(STORAGE_KEY);
    }
  }

  return fallback;
}

function createDefaultConsoleStorage(): ConsoleStorage {
  return {
    activeSystemId: "",
    expandedHostIds: [],
    expandedSystemIds: [],
    hosts: [],
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
  value: unknown
): OverviewPanelShapeMap {
  const shapes = createDefaultOverviewPanelShapes();

  if (value && typeof value === "object" && !Array.isArray(value)) {
    const requested = value as Partial<Record<OverviewPanelId, unknown>>;
    for (const panelId of overviewPanelIds) {
      shapes[panelId] = normalizeOverviewPanelShape(panelId, requested[panelId]);
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
    (
      value === "compact" ||
      capabilities.supportedShapes.includes(value as WorkComponentShape)
    )
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
  const isDefaultLocalSampleHost = hostId === "local-sample-host";
  const systems = Array.isArray(host.systems)
    ? host.systems.map((system) => normalizeStoredSystem(hostId, system))
    : [createDefaultSystem(hostId)];
  const legacyRealtimeSource = systems.find((system) => {
    const candidate = system as WorkableSystemConnection & {
      realtimeEnabled?: boolean;
      realtimeHubPath?: string | null;
      realtimeTransport?: string | null;
    };
    return Boolean(candidate.realtimeEnabled && candidate.realtimeHubPath);
  }) as (WorkableSystemConnection & {
    realtimeEnabled?: boolean;
    realtimeHubPath?: string | null;
    realtimeTransport?: string | null;
  }) | undefined;

  return {
    id: hostId,
    name: host.name || "Workable host",
    apiUrl: host.apiUrl || DEFAULT_WORKABLE_API_URL,
    realtimeEnabled: isDefaultLocalSampleHost
      ? true
      : Boolean(host.realtimeEnabled ?? legacyRealtimeSource?.realtimeEnabled),
    realtimeHubPath: isDefaultLocalSampleHost
      ? host.realtimeHubPath ?? legacyRealtimeSource?.realtimeHubPath ?? "/workable/realtime"
      : host.realtimeHubPath ?? legacyRealtimeSource?.realtimeHubPath ?? null,
    realtimeTransport: isDefaultLocalSampleHost
      ? host.realtimeTransport ?? legacyRealtimeSource?.realtimeTransport ?? "signalr"
      : host.realtimeTransport ?? legacyRealtimeSource?.realtimeTransport ?? null,
    systems,
  };
}

function normalizeStoredSystem(
  hostId: string,
  system: WorkableSystemConnection
): WorkableSystemConnection {
  return {
    id: system.id || createServerId(),
    hostId,
    name: system.name || "Default",
    systemName: normalizeOptional(system.systemName),
    access: system.access ?? (hostId === "local-sample-host" ? createFullAccessSummary() : undefined),
    persistentCoordinationAvailable: Boolean(system.persistentCoordinationAvailable),
    state: system.state ?? null,
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

  const fallbackHost = state.hosts.find((host) => host.systems.length > 0);
  if (!fallbackHost) {
    return null;
  }

  return { host: fallbackHost, system: fallbackHost.systems[0] };
}

function getFirstAvailableSystemId(hosts: WorkableHostConnection[]) {
  return hosts.find((host) => host.systems.length > 0)?.systems[0]?.id ?? "";
}

function createDiagnosticsAlertTargets(hosts: WorkableHostConnection[]): DiagnosticsAlertTarget[] {
  const targetsById = new Map<string, DiagnosticsAlertTarget>();

  for (const host of hosts) {
    if (!host.realtimeEnabled || !host.realtimeHubPath) {
      continue;
    }

    for (const system of host.systems) {
      if (
        system.access?.canViewDiagnostics !== true
      ) {
        continue;
      }

      const id = createDiagnosticsAlertTargetId(
        host.apiUrl,
        host.realtimeHubPath,
        system.systemName
      );
      if (targetsById.has(id)) {
        continue;
      }

      targetsById.set(id, {
        id,
        apiUrl: host.apiUrl,
        displayName: `${system.name} @ ${host.name}`,
        hostId: host.id,
        hostName: host.name,
        realtimeHubPath: host.realtimeHubPath,
        systemName: system.systemName,
      });
    }
  }

  return [...targetsById.values()];
}

function createDiagnosticsAlertTargetId(
  apiUrl: string,
  realtimeHubPath: string | null | undefined,
  systemName: string | undefined
) {
  return `${apiUrl}\n${realtimeHubPath ?? ""}\n${systemName ?? ""}`;
}

function isServerView(value: unknown): value is ServerView {
  return (
    value === "overview" ||
    value === "definitions" ||
    value === "definition" ||
    value === "workers" ||
    value === "iterations"
  );
}

function getViewReadinessKey(systemId: string, view: View) {
  return `${systemId}:${view}`;
}

function createDefaultSystem(hostId: string): WorkableSystemConnection {
  return {
    id: "local-sample-default",
    hostId,
    name: "Default",
    access: createFullAccessSummary(),
    persistentCoordinationAvailable: false,
    state: null,
  };
}

function createServerId() {
  if (typeof crypto !== "undefined" && "randomUUID" in crypto) {
    return crypto.randomUUID();
  }

  return `server-${Date.now().toString(36)}`;
}

function createFullAccessSummary(): WorkSystemAccessSummary {
  return {
    canConnect: true,
    isSystemAdministrator: true,
    isWorkAdministrator: true,
    canViewDiagnostics: true,
    canControlSystem: true,
    canReadAllWork: true,
    canOperateAllWork: true,
    totalDefinitionCount: 0,
    readableDefinitionCount: 0,
    operableDefinitionCount: 0,
  };
}

function normalizeOptional(value?: string | null) {
  const trimmed = value?.trim();
  return trimmed ? trimmed : undefined;
}

function navTitle(view: View) {
  if (view === "worker") {
    return "Worker Console";
  }
  if (view === "iteration") {
    return "Iteration";
  }
  if (view === "definition") {
    return "Definition";
  }

  return navItems.find((item) => item.id === view)?.label ?? "Overview";
}

function headerRefreshTitle(view: View) {
  switch (view) {
    case "definitions":
      return "Refresh catalog";
    case "definition":
      return "Refresh definition";
    case "iterations":
      return "Refresh iterations";
    case "worker":
      return "Refresh worker";
    case "iteration":
      return "Refresh iteration";
    case "workers":
      return "Refresh workers";
    default:
      return "Refresh overview";
  }
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

function navigationEntriesEqual(left: NavigationEntry | undefined, right: NavigationEntry) {
  return (
    left?.systemId === right.systemId &&
    overviewScopesEqual(left.catalogScope, right.catalogScope) &&
    left.definitionId === right.definitionId &&
    left.definitionName === right.definitionName &&
    left.iterationSequence === right.iterationSequence &&
    left.iterationCategoryFilter === right.iterationCategoryFilter &&
    left.iterationDefinitionFilter === right.iterationDefinitionFilter &&
    left.iterationKeyTypeFilter === right.iterationKeyTypeFilter &&
    left.iterationStatusFilter.length === right.iterationStatusFilter.length &&
    left.iterationStatusFilter.every(
      (status, index) => status === right.iterationStatusFilter[index]
    ) &&
    left.iterationWorkerId === right.iterationWorkerId &&
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
