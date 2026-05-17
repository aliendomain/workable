"use client";

import Image from "next/image";
import {
  Activity,
  Bell,
  Boxes,
  ChevronRight,
  CircleAlert,
  Clock3,
  Loader2,
  Plus,
  RefreshCw,
  RotateCcw,
  Settings,
  Workflow,
} from "lucide-react";
import { Fragment, type ReactNode, useCallback, useEffect, useMemo, useRef, useState } from "react";
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
  useWorkableRealtimeView,
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
  type WorkReadModelDiagnosticsCompactComponent,
  type WorkReadModelDiagnosticsDetailedComponent,
  type WorkSystemReadModelDiagnostics,
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
  "Canceling",
  "Failed",
  "Canceled",
  "Completed",
];

const iterationStatuses: WorkCompletionStatus[] = ["Executing", "Completed", "Failed", "Canceled", "Paused"];

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
  const [readModelDiagnosticsExpanded, setReadModelDiagnosticsExpanded] = useState(false);
  const [realtimePayloadCaptureEnabled, setRealtimePayloadCaptureEnabled] = useState(true);
  const [realtimePayloadMaxMessages, setRealtimePayloadMaxMessages] = useState(100);
  const [realtimePayloadOpen, setRealtimePayloadOpen] = useState(false);
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
  const activeApiUrl = activeHost?.apiUrl;
  const activeSystemName = activeSystem?.systemName;
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
            realtimeHubPath: activeSystem?.realtimeEnabled
              ? activeSystem.realtimeHubPath
              : null,
            systemName: activeSystemName,
          }
        : null,
    [activeApiUrl, activeSystem, activeSystemName]
  );
  const diagnosticsAlertRequest = useMemo(
    () => ({
      components: [
        {
          id: "readModelDiagnostics",
          options: {
            publishMode: "alertChanges",
            warningThreshold: readModelLagWarningThreshold,
          },
          shape: "compact",
          type: "readModelDiagnostics",
        },
      ],
    }),
    []
  );
  const diagnosticsTrayRequest = useMemo(
    () => ({
      components: [
        {
          id: "readModelDiagnostics",
          options: {
            publishMode: "continuous",
            warningThreshold: readModelLagWarningThreshold,
          },
          shape: "compact",
          type: "readModelDiagnostics",
        },
      ],
    }),
    []
  );
  const diagnosticsDetailRequest = useMemo(
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
  const diagnosticsRealtimeEnabled = Boolean(connection?.realtimeHubPath);
  const captureRealtimePayloads = realtimePayloadOpen && realtimePayloadCaptureEnabled;
  const diagnosticsAlert = useWorkableRealtimeView<WorkComponentQueryResult>(
    connection,
    "diagnostics",
    diagnosticsAlertRequest,
    diagnosticsRealtimeEnabled,
    captureRealtimePayloads,
    realtimePayloadMaxMessages,
    "diagnostics:alerts"
  );
  const diagnosticsTray = useWorkableRealtimeView<WorkComponentQueryResult>(
    connection,
    "diagnostics",
    diagnosticsTrayRequest,
    diagnosticsRealtimeEnabled && systemNotificationOpen,
    captureRealtimePayloads,
    realtimePayloadMaxMessages,
    "diagnostics:tray"
  );
  const diagnosticsDetail = useWorkableRealtimeView<WorkComponentQueryResult>(
    connection,
    "diagnostics",
    diagnosticsDetailRequest,
    diagnosticsRealtimeEnabled && systemNotificationOpen && readModelDiagnosticsExpanded,
    captureRealtimePayloads,
    realtimePayloadMaxMessages,
    "diagnostics:read-model"
  );
  const diagnosticsRealtimeMessages = useMemo(
    () => [
      ...diagnosticsAlert.messages,
      ...diagnosticsTray.messages,
      ...diagnosticsDetail.messages,
    ],
    [diagnosticsAlert.messages, diagnosticsDetail.messages, diagnosticsTray.messages]
  );
  const clearDiagnosticsAlertMessages = diagnosticsAlert.clearMessages;
  const clearDiagnosticsTrayMessages = diagnosticsTray.clearMessages;
  const clearDiagnosticsDetailMessages = diagnosticsDetail.clearMessages;
  const clearDiagnosticsRealtimeMessages = useCallback(() => {
    clearDiagnosticsAlertMessages();
    clearDiagnosticsTrayMessages();
    clearDiagnosticsDetailMessages();
  }, [clearDiagnosticsAlertMessages, clearDiagnosticsDetailMessages, clearDiagnosticsTrayMessages]);
  const handleSystemNotificationOpenChange = useCallback((open: boolean) => {
    setSystemNotificationOpen(open);
    if (!open) {
      setReadModelDiagnosticsExpanded(false);
    }
  }, []);

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

  const markViewReady = (readyView: ServerView) => {
    if (!activeSystem) {
      return;
    }

    readyViews.current.add(getViewReadinessKey(activeSystem.id, readyView));
    if (pendingView === readyView) {
      setVisibleView(readyView);
      setPendingView(null);
    }
  };

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
    if (activeSystem) {
      updateSystemState(activeSystem.id, state);
    }
  }, [activeSystem, updateSystemState]);

  const handleOverviewConnectionError = useCallback(() => {
    setLifecycleError(undefined);
    if (activeSystem) {
      updateSystemState(activeSystem.id, null);
    }
  }, [activeSystem, updateSystemState]);

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
                        <SystemNotificationTray
                          alertDiagnostics={diagnosticsAlert}
                          detailDiagnostics={diagnosticsDetail}
                          onOpenChange={handleSystemNotificationOpenChange}
                          onReadModelExpandedChange={setReadModelDiagnosticsExpanded}
                          open={systemNotificationOpen}
                          readModelExpanded={readModelDiagnosticsExpanded}
                          systemName={activeSystem.name}
                          trayDiagnostics={diagnosticsTray}
                        />
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
                        onReady={() => markViewReady("overview")}
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
                        renderToolbar={({ loading, realtimePayloadControl, refreshing }) => (
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
                              realtimePayloadControl={realtimePayloadControl}
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
                        onReady={() => markViewReady("definitions")}
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
                        onReady={() => markViewReady("definition")}
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
                        onReady={() => markViewReady("workers")}
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
                        onReady={() => markViewReady("iterations")}
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

type SystemNotification = {
  description: string;
  id: string;
  tone: "critical" | "warning";
  title: string;
};

function SystemNotificationTray({
  alertDiagnostics,
  detailDiagnostics,
  onOpenChange,
  onReadModelExpandedChange,
  open,
  readModelExpanded,
  systemName,
  trayDiagnostics,
}: {
  alertDiagnostics: SystemDiagnosticsViewState;
  detailDiagnostics: SystemDiagnosticsViewState;
  onOpenChange: (open: boolean) => void;
  onReadModelExpandedChange: (expanded: boolean) => void;
  open: boolean;
  readModelExpanded: boolean;
  systemName: string;
  trayDiagnostics: SystemDiagnosticsViewState;
}) {
  const alertCompact = getWorkComponentData<WorkReadModelDiagnosticsCompactComponent>(
    alertDiagnostics.data,
    "readModelDiagnostics"
  );
  const trayCompact = getWorkComponentData<WorkReadModelDiagnosticsCompactComponent>(
    trayDiagnostics.data,
    "readModelDiagnostics"
  );
  const detailed = getWorkComponentData<WorkReadModelDiagnosticsDetailedComponent>(
    detailDiagnostics.data,
    "readModelDiagnostics"
  );
  const detailCompact = createCompactDiagnosticsFromDetailed(detailed);
  const visibleCompact = readModelExpanded
    ? detailCompact ?? trayCompact
    : trayCompact;
  const compact = open
    ? visibleCompact ?? alertCompact
    : alertCompact;
  const diagnosticsError = alertDiagnostics.error || (
    readModelExpanded ? detailDiagnostics.error : open ? trayDiagnostics.error : undefined
  );
  const notifications = createSystemNotifications(compact, diagnosticsError);
  const hasNotifications = notifications.length > 0;
  const busy = alertDiagnostics.loading || alertDiagnostics.refreshing ||
    (open && !readModelExpanded && (trayDiagnostics.loading || trayDiagnostics.refreshing)) ||
    (readModelExpanded && (detailDiagnostics.loading || detailDiagnostics.refreshing));
  const connectionState = alertDiagnostics.enabled
    ? alertDiagnostics.connectionState
    : "disabled";
  const detailLastUpdatedAt = detailDiagnostics.data?.generatedAt
    ? new Date(detailDiagnostics.data.generatedAt)
    : undefined;

  return (
    <Popover onOpenChange={onOpenChange} open={open}>
      <Tooltip delayDuration={500} disableHoverableContent>
        <TooltipTrigger asChild>
          <PopoverTrigger asChild>
            <Button
              aria-label="System notifications"
              className={`relative ${hasNotifications ? "text-amber-400 hover:text-amber-300" : "text-muted-foreground hover:text-foreground"} hover:bg-transparent dark:hover:bg-transparent`}
              size="icon-sm"
              variant="ghost"
            >
              {hasNotifications ? (
                <CircleAlert className="size-4" />
              ) : (
                <Bell className="size-4" />
              )}
              {hasNotifications && (
                <span className="absolute right-0.5 top-0.5 flex min-w-3 translate-x-1/4 -translate-y-1/4 items-center justify-center rounded-full border border-background bg-amber-400 px-0.5 text-[9px] font-semibold leading-3 text-black">
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
              {systemName} - {connectionState}
            </div>
          </div>
          {busy && <Loader2 className="size-4 shrink-0 animate-spin text-muted-foreground" />}
        </div>
        <div className="max-h-[70vh] overflow-auto">
          <div className="space-y-2 border-b p-3">
            {alertDiagnostics.loading && !compact ? (
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
            compact={compact}
            expanded={readModelExpanded}
            lastUpdatedAt={detailLastUpdatedAt}
            loading={detailDiagnostics.loading && !detailed}
            onExpandedChange={onReadModelExpandedChange}
            readModel={detailed?.readModel}
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
          tone={compact?.isReadModelBehind ? "warning" : undefined}
          value={formatNumber(readModel?.pendingUpdateCount)}
        />
        <DiagnosticsMetric
          label="Last batch"
          value={formatNumber(readModel?.lastBatchSize)}
        />
        <DiagnosticsMetric
          label="Enqueued"
          value={formatNumber(readModel?.enqueuedSequence)}
        />
        <DiagnosticsMetric
          label="Applied"
          value={formatNumber(readModel?.appliedSequence)}
        />
        <DiagnosticsMetric
          label="Snapshots"
          value={formatNumber(readModel?.publishedSnapshotCount)}
        />
        <DiagnosticsMetric
          label="Last projection"
          value={formatDuration(readModel?.lastProjectionDuration)}
        />
      </div>
      <div className="rounded-md border border-border px-3 py-2 text-xs">
        <div className="flex items-center justify-between gap-3">
          <span className="text-muted-foreground">Last projected</span>
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

function DiagnosticsMetric({
  label,
  tone,
  value,
}: {
  label: string;
  tone?: "warning";
  value: string;
}) {
  return (
    <div className={`rounded-md border px-3 py-2 ${tone === "warning" ? "border-amber-500/30 bg-amber-500/10" : "border-border"}`}>
      <div className="text-muted-foreground">{label}</div>
      <div className="truncate font-mono text-foreground">{value}</div>
    </div>
  );
}

function createSystemNotifications(
  diagnostics?: WorkReadModelDiagnosticsCompactComponent,
  error?: string
): SystemNotification[] {
  const notifications: SystemNotification[] = [];

  if (error) {
    notifications.push({
      description: error,
      id: "diagnostics-unavailable",
      tone: "warning",
      title: "Diagnostics unavailable",
    });
  }

  if (diagnostics?.hasProjectorFailure) {
    notifications.push({
      description: `${diagnostics.projectorFailureType ?? "Projector failure"}${diagnostics.projectorFailureMessage ? `: ${diagnostics.projectorFailureMessage}` : ""}`,
      id: "read-model-failure",
      tone: "critical",
      title: "Read model projector failed",
    });
  }

  if (diagnostics?.isReadModelBehind) {
    notifications.push({
      description: `${formatNumber(diagnostics.pendingUpdateCount)} update${diagnostics.pendingUpdateCount === 1 ? "" : "s"} waiting to be projected.`,
      id: "read-model-lag",
      tone: diagnostics.pendingUpdateCount >= diagnostics.readModelLagWarningThreshold * 10
        ? "critical"
        : "warning",
      title: "Read model is behind",
    });
  }

  return notifications;
}

function createCompactDiagnosticsFromDetailed(
  detailed?: WorkReadModelDiagnosticsDetailedComponent
): WorkReadModelDiagnosticsCompactComponent | undefined {
  if (!detailed) {
    return undefined;
  }

  return {
    hasProjectorFailure: detailed.readModel.hasProjectorFailure,
    isReadModelBehind: detailed.isReadModelBehind,
    pendingUpdateCount: detailed.readModel.pendingUpdateCount,
    projectorFailureMessage: detailed.readModel.projectorFailureMessage,
    projectorFailureType: detailed.readModel.projectorFailureType,
    readModelLagWarningThreshold: detailed.readModelLagWarningThreshold,
  };
}

function getWorkComponentData<T>(result: WorkComponentQueryResult | undefined, id: string) {
  const component = result?.components?.[id];
  return component?.status?.toLowerCase() === "ok" ? component.data as T : undefined;
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

function OverviewPanelSettings({
  hiddenPanelIds,
  onPanelVisibilityChange,
  realtimePayloadControl,
  onResetUi,
}: {
  hiddenPanelIds: OverviewPanelId[];
  onPanelVisibilityChange: (panelId: OverviewPanelId, visible: boolean) => void;
  realtimePayloadControl?: ReactNode;
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
          {realtimePayloadControl}
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
  return {
    id: system.id || createServerId(),
    hostId,
    name: system.name || "Default",
    systemName: normalizeOptional(system.systemName),
    realtimeEnabled: Boolean(system.realtimeEnabled && system.realtimeSupported),
    realtimeHubPath: system.realtimeHubPath ?? null,
    realtimeSupported: Boolean(system.realtimeSupported),
    realtimeTransport: system.realtimeTransport ?? null,
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
    realtimeEnabled: false,
    realtimeHubPath: null,
    realtimeSupported: false,
    realtimeTransport: null,
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
