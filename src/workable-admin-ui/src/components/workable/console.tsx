"use client";

import Image from "next/image";
import {
  Activity,
  ArrowDown,
  ArrowUp,
  Ban,
  Boxes,
  Braces,
  CheckCircle2,
  ChevronDown,
  ChevronLeft,
  ChevronRight,
  ChevronsLeft,
  CircleAlert,
  CircleDot,
  Clock3,
  Equal,
  FileCode2,
  Folder,
  ListFilter,
  Hourglass,
  Home,
  Info,
  Loader2,
  MoreHorizontal,
  Pause,
  Pencil,
  Play,
  Plus,
  Radio,
  RefreshCw,
  Search,
  Send,
  Server,
  Settings,
  ShieldAlert,
  Square,
  Trash2,
  Workflow,
  X,
} from "lucide-react";
import type { Dispatch, ReactNode, SetStateAction } from "react";
import { Fragment, useCallback, useEffect, useLayoutEffect, useMemo, useRef, useState } from "react";
import { Alert, AlertDescription, AlertTitle } from "@/components/ui/alert";
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
import { Badge } from "@/components/ui/badge";
import {
  Breadcrumb,
  BreadcrumbItem,
  BreadcrumbLink,
  BreadcrumbList,
  BreadcrumbPage,
  BreadcrumbSeparator,
} from "@/components/ui/breadcrumb";
import { Button } from "@/components/ui/button";
import {
  Card,
  CardContent,
  CardDescription,
  CardHeader,
  CardTitle,
} from "@/components/ui/card";
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogHeader,
  DialogTitle,
} from "@/components/ui/dialog";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import {
  ScrollArea,
  ScrollBar,
} from "@/components/ui/scroll-area";
import {
  DropdownMenu,
  DropdownMenuContent,
  DropdownMenuItem,
  DropdownMenuTrigger,
} from "@/components/ui/dropdown-menu";
import {
  Popover,
  PopoverContent,
  PopoverTrigger,
} from "@/components/ui/popover";
import { Separator } from "@/components/ui/separator";
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
  SidebarMenu,
  SidebarMenuAction,
  SidebarMenuButton,
  SidebarMenuItem,
  SidebarMenuSub,
  SidebarMenuSubButton,
  SidebarMenuSubItem,
  SidebarProvider,
  SidebarTrigger,
} from "@/components/ui/sidebar";
import { Skeleton } from "@/components/ui/skeleton";
import {
  Table,
  TableBody,
  TableCell,
  TableHead,
  TableHeader,
  TableRow,
} from "@/components/ui/table";
import {
  Tabs,
  TabsContent,
  TabsList,
  TabsTrigger,
} from "@/components/ui/tabs";
import { Textarea } from "@/components/ui/textarea";
import {
  Tooltip,
  TooltipContent,
  TooltipTrigger,
} from "@/components/ui/tooltip";
import {
  SchemaForm,
  SchemaPathField,
  SchemaPresetButton,
  compactJson,
  createDefaultValue,
  parseJsonSchema,
} from "@/components/workable/schema-form";
import {
  DEFAULT_WORKABLE_API_URL,
  formatDateTime,
  stateTone,
  WorkableApiError,
  workableFetch,
  type QueueWorkRequest,
  type QueueRequestSchemaDescriptor,
  type WorkerOptions,
  type WorkAction,
  type WorkConfiguration,
  type WorkDefinition,
  type WorkDefinitionReconfigurationOutcome,
  type WorkCompletionStatus,
  type WorkComponentQueryResult,
  type WorkComponentRequest,
  type WorkComponentResult,
  type WorkComponentShape,
  type WorkIterationKeyTypeQueryResult,
  type WorkIterationKeyTypeFacet,
  type WorkInfo,
  type WorkOverviewThroughputComponent,
  type WorkKeyTypeQueryResult,
  type WorkOverviewCatalogCategoryItem,
  type WorkOverviewDefinitionItem,
  type WorkSystemFailedWorkersOverview,
  type WorkSystemLifecycleResult,
  type WorkSystemOverview,
  type WorkSystemThroughput,
  type WorkThroughputBucket,
  type WorkThroughputLiveSummary,
  type WorkTypedValue,
  type WorkableConnection,
  type WorkableHttpSystemInfo,
  type WorkableHttpSystems,
  type WorkerIterationOverviewItem,
  type WorkerIterationQueryResult,
  type WorkerOverviewItem,
  type WorkerQueryResult,
  type WorkerSnapshot,
  type WorkerState,
} from "@/lib/workable";

const STORAGE_KEY = "workable-console.state.v1";
const LEGACY_CONNECTION_STORAGE_KEY = "workable-console.connection";

type View = "overview" | "definitions" | "definition" | "workers" | "iterations" | "worker";
type ServerView = Exclude<View, "worker">;
type ThroughputMode = "completion" | "execution";
type ThroughputMetric = {
  description: string;
  icon?: typeof Activity;
  iconClass?: string;
  id: string;
  label: string;
  pulseClass?: string;
  value: string;
  valueClass?: string;
  widthClass?: string;
};

type WorkOverviewSystemComponent = Pick<WorkSystemOverview, "systemName" | "systemState">;
type WorkOverviewCatalogComponent = Pick<
  WorkSystemOverview,
  "catalogCategories" | "catalogDefinitions"
>;
type WorkOverviewWorkersComponent = Pick<
  WorkSystemOverview,
  | "activeWorkerCount"
  | "definitionCount"
  | "failedWorkerCount"
  | "finalWorkerCount"
  | "oldestQueuedAt"
  | "workerCountByState"
>;
type WorkOverviewRelationshipsComponent = Pick<
  WorkSystemOverview,
  | "canceledIterationCount"
  | "commonKeyTypes"
  | "completedIterationCount"
  | "currentIterationCount"
  | "failedIterationCount"
  | "iterationCountByStatus"
>;

function overviewComponent(
  id: string,
  type: string = id,
  shape: WorkComponentShape = "detailed",
  options?: unknown
): WorkComponentRequest {
  return options === undefined ? { id, shape, type } : { id, options, shape, type };
}

const overviewPanelIds = [
  "workers",
  "failedWorkers",
  "throughput",
  "relationships",
  "failedIterations",
  "completedIterations",
] as const;
type OverviewPanelId = (typeof overviewPanelIds)[number];

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
  overviewCollapsedPanels: OverviewPanelId[];
  overviewHiddenPanels: OverviewPanelId[];
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

type Loadable<T> = {
  data?: T;
  error?: string;
  loading: boolean;
  refreshing?: boolean;
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

const failedWorkerStates: WorkerState[] = ["Failed"];
const finalWorkerStates: WorkerState[] = ["Canceled", "Completed"];
const activeWorkerStates: WorkerState[] = states.filter(
  (state) => !failedWorkerStates.includes(state) && !finalWorkerStates.includes(state)
);
const overviewWorkerStates: WorkerState[] = states.filter(
  (state) => state !== "Pausing" && state !== "Canceling"
);
const iterationStatuses: WorkCompletionStatus[] = ["Executing", "Completed", "Failed", "Canceled", "Paused"];
const throughputWindows = [
  { label: "60s", seconds: 60, bucketSeconds: 1 },
  { label: "5m", seconds: 300, bucketSeconds: 5 },
  { label: "15m", seconds: 900, bucketSeconds: 15 },
  { label: "1h", seconds: 3600, bucketSeconds: 60 },
];

const navItems: Array<{ id: ServerView; label: string; icon: typeof Activity }> = [
  { id: "overview", label: "Overview", icon: Activity },
  { id: "definitions", label: "Catalog", icon: Boxes },
  { id: "workers", label: "Workers", icon: Workflow },
  { id: "iterations", label: "Iterations", icon: Clock3 },
];

const clickableTileClass =
  "border-primary/35 ring-1 ring-primary/10 transition-colors hover:border-primary/70 hover:bg-accent/40 hover:ring-primary/30";
const subtleClickableTileClass =
  "transition-colors hover:border-primary/60 hover:bg-accent/40";
const initialRefreshTokens: Record<View, number> = {
  overview: 0,
  definitions: 0,
  definition: 0,
  workers: 0,
  iterations: 0,
  worker: 0,
};
const viewContentOffsetClass = "pt-2";
const defaultQueryTake = 4;
const maxQueryTake = 100;
const minQueryTake = 4;
const queryTableHeaderHeight = 41;
const queryTableRowHeight = 56;
const queryViewportSafetyPadding = 24;

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
  const readyViews = useRef<Set<string>>(new Set());
  const activeLocation = findSystemLocation(consoleState, consoleState.activeSystemId);
  const activeHost = activeLocation?.host;
  const activeSystem = activeLocation?.system;
  const activeCatalogScope = activeSystem
    ? catalogScopeBySystemId[activeSystem.id] ?? null
    : null;
  const activeOverviewScope = activeSystem
    ? overviewScopeBySystemId[activeSystem.id] ?? null
    : null;
  const connection = useMemo<WorkableConnection | null>(
    () =>
      activeHost && activeSystem
        ? {
            apiUrl: activeHost.apiUrl,
            systemName: activeSystem.systemName,
          }
        : null,
    [activeHost, activeSystem]
  );

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
      const next = { ...current };
      if (scope) {
        next[systemId] = { ...scope };
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
      const next = { ...current };
      if (scope) {
        next[systemId] = { ...scope };
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

  const setOverviewPanelCollapsed = useCallback((
    panelId: OverviewPanelId,
    collapsed: boolean
  ) => {
    setConsoleState((current) => {
      const panels = new Set(current.overviewCollapsedPanels ?? []);
      if (collapsed) {
        panels.add(panelId);
      } else {
        panels.delete(panelId);
      }

      return {
        ...current,
        overviewCollapsedPanels: overviewPanelIds.filter((id) => panels.has(id)),
      };
    });
  }, []);

  const openWorker = (workerId: string, trackHistory = true) => {
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

  const openWorkersFiltered = (states: WorkerState[]) => {
    pushCurrentNavigation();
    setWorkerCategoryFilter("");
    setWorkerDefinitionFilter("");
    setKeyTypeFilter("");
    setWorkerStateFilter(states);
    openView("workers", activeSystem?.id ?? "", false);
  };

  const openIterations = () => {
    pushCurrentNavigation();
    setIterationCategoryFilter("");
    setIterationDefinitionFilter("");
    setIterationKeyTypeFilter("");
    setIterationStatusFilter([]);
    openView("iterations", activeSystem?.id ?? "", false);
  };

  const openIterationsByKeyType = (keyType: string) => {
    pushCurrentNavigation();
    setIterationCategoryFilter("");
    setIterationDefinitionFilter("");
    setIterationKeyTypeFilter(keyType);
    setIterationStatusFilter([]);
    openView("iterations", activeSystem?.id ?? "", false);
  };

  const openIterationsFiltered = (statuses: WorkCompletionStatus[]) => {
    pushCurrentNavigation();
    setIterationCategoryFilter("");
    setIterationDefinitionFilter("");
    setIterationKeyTypeFilter("");
    setIterationStatusFilter(statuses);
    openView("iterations", activeSystem?.id ?? "", false);
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
        <main className="min-h-0 flex-1 overflow-hidden bg-background">
          <ScrollArea className="h-screen">
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
                      view={view}
                      workerId={selectedWorkerId}
                    />
                  )}
                  <ErrorPanel errors={[lifecycleError]} />
                  {mountedViews.has("overview") && (
                    <div className={visibleView === "overview" ? viewContentOffsetClass : "hidden"}>
                      <OverviewView
                        collapsedPanelIds={consoleState.overviewCollapsedPanels}
                        connection={connection}
                        hiddenPanelIds={consoleState.overviewHiddenPanels}
                        isVisible={visibleView === "overview"}
                        onConnectionError={handleOverviewConnectionError}
                        onStateLoaded={handleOverviewStateLoaded}
                        onOpenCatalog={() => openView("definitions")}
                        onClearOverviewScope={() => {
                          if (activeSystem) {
                            openCategoryOverview(activeSystem.id, "");
                          }
                        }}
                        onSelectOverviewCategory={(category) => {
                          if (activeSystem) {
                            openCategoryOverview(activeSystem.id, category);
                          }
                        }}
                        onSelectOverviewDefinition={(definitionName, category) => {
                          if (activeSystem) {
                            openDefinitionOverview(
                              activeSystem.id,
                              definitionName,
                              category
                            );
                          }
                        }}
                        onOpenIterations={openIterations}
                        onOpenKeyType={openIterationsByKeyType}
                        onPanelCollapsedChange={setOverviewPanelCollapsed}
                        onReady={() => markViewReady("overview")}
                        onRefresh={() => refreshView("overview")}
                        onPanelVisibilityChange={setOverviewPanelVisible}
                        onViewIterationsByStatus={openIterationsFiltered}
                        onViewWorkersByState={openWorkersFiltered}
                        overviewScope={activeOverviewScope}
                        refreshToken={refreshTokens.overview}
                        onOpenWorker={openWorker}
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
                        onOpenWorker={openWorker}
                        onReady={() => markViewReady("workers")}
                        definitionFilter={workerDefinitionFilter}
                        onCategoryFilterChange={setWorkerCategoryFilter}
                        onDefinitionFilterChange={setWorkerDefinitionFilter}
                        keyTypeFilter={keyTypeFilter}
                        onKeyTypeFilterChange={setKeyTypeFilter}
                        stateFilter={workerStateFilter}
                        onStateFilterChange={setWorkerStateFilter}
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
                        keyTypeFilter={iterationKeyTypeFilter}
                        onCategoryFilterChange={setIterationCategoryFilter}
                        onDefinitionFilterChange={setIterationDefinitionFilter}
                        onKeyTypeFilterChange={setIterationKeyTypeFilter}
                        onOpenWorker={openWorker}
                        onReady={() => markViewReady("iterations")}
                        onStatusFilterChange={setIterationStatusFilter}
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
            <ScrollBar orientation="vertical" />
          </ScrollArea>
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

function ServerTree({
  activeSystemId,
  catalogScopeBySystemId,
  expandedHostIds,
  expandedSystemIds,
  hosts,
  lifecycleActionSystemId,
  onAddServer,
  onEditHost,
  onOpenCatalogScope,
  onOpenDefinition,
  onOpenWorker,
  onLifecycleAction,
  onOpenView,
  onRemoveHost,
  onRemoveSystem,
  onToggleHost,
  onToggleSystem,
  view,
}: {
  activeSystemId: string;
  catalogScopeBySystemId: Record<string, OverviewScope | undefined>;
  expandedHostIds: string[];
  expandedSystemIds: string[];
  hosts: WorkableHostConnection[];
  lifecycleActionSystemId: string | null;
  onAddServer: () => void;
  onEditHost: (host: WorkableHostConnection) => void;
  onOpenCatalogScope: (systemId: string, scope: OverviewScope | null) => void;
  onOpenDefinition: (definitionId: string, systemId?: string) => void;
  onOpenWorker: (workerId: string) => void;
  onLifecycleAction: (system: WorkableSystemConnection, action: "start" | "stop") => void;
  onOpenView: (view: View, systemId: string) => void;
  onRemoveHost: (host: WorkableHostConnection) => void;
  onRemoveSystem: (
    host: WorkableHostConnection,
    system: WorkableSystemConnection
  ) => void;
  onToggleHost: (hostId: string) => void;
  onToggleSystem: (systemId: string) => void;
  view: View;
}) {
  const [openCatalogSystemIds, setOpenCatalogSystemIds] = useState<string[]>([]);

  const toggleCatalog = (systemId: string) => {
    setOpenCatalogSystemIds((current) =>
      current.includes(systemId)
        ? current.filter((id) => id !== systemId)
        : [...current, systemId]
    );
  };

  return (
    <SidebarMenu>
      {hosts.map((host) => {
        const isHostExpanded = expandedHostIds.includes(host.id);
        const isActiveHost = host.systems.some((system) => system.id === activeSystemId);

        return (
          <SidebarMenuItem key={host.id}>
            <div className="group/host-row relative">
              <SidebarMenuButton
                className="pr-14"
                isActive={isActiveHost}
                onClick={() => onToggleHost(host.id)}
                tooltip={host.name}
              >
                <ChevronRight
                  className={isHostExpanded ? "rotate-90 transition-transform" : "transition-transform"}
                />
                <Server />
                <span>{host.name}</span>
              </SidebarMenuButton>
              <Tooltip delayDuration={500} disableHoverableContent>
                <TooltipTrigger asChild>
                  <SidebarMenuAction
                    className="pointer-events-none right-7 opacity-0 group-hover/host-row:pointer-events-auto group-hover/host-row:opacity-100"
                    onClick={(event) => {
                      event.stopPropagation();
                      onEditHost(host);
                    }}
                  >
                    <Pencil />
                    <span className="sr-only">{`Update '${host.name}' server settings`}</span>
                  </SidebarMenuAction>
                </TooltipTrigger>
                <TooltipContent side="right" sideOffset={6}>
                  {`Update '${host.name}' server settings`}
                </TooltipContent>
              </Tooltip>
              <Tooltip delayDuration={500} disableHoverableContent>
                <TooltipTrigger asChild>
                  <SidebarMenuAction
                    className="pointer-events-none opacity-0 group-hover/host-row:pointer-events-auto group-hover/host-row:opacity-100"
                    onClick={(event) => {
                      event.stopPropagation();
                      onRemoveHost(host);
                    }}
                  >
                    <Trash2 />
                    <span className="sr-only">Remove this server from your explorer</span>
                  </SidebarMenuAction>
                </TooltipTrigger>
                <TooltipContent side="right" sideOffset={6}>
                  Remove this server from your explorer
                </TooltipContent>
              </Tooltip>
            </div>
            {isHostExpanded && (
              <SidebarMenuSub>
                {host.systems.map((system) => {
                  const isActiveSystem = system.id === activeSystemId;
                  const isSystemExpanded = expandedSystemIds.includes(system.id);
                  const lifecycleAction = getSystemLifecycleAction(system.state);
                  const lifecycleActionLabel = getSystemLifecycleActionLabel(
                    system.state,
                    system,
                    host
                  );

                  return (
                    <SidebarMenuSubItem key={system.id}>
                      <div className="group/system-row relative">
                        <SidebarMenuSubButton
                          asChild
                          className="pr-14"
                          isActive={isActiveSystem}
                        >
                          <button
                            onClick={() => {
                              onToggleSystem(system.id);
                              if (!isActiveSystem) {
                                onOpenView("overview", system.id);
                              }
                            }}
                            type="button"
                          >
                            <ChevronRight
                              className={
                                isSystemExpanded
                                  ? "rotate-90 transition-transform"
                                  : "transition-transform"
                              }
                            />
                            <span className="min-w-0 truncate">{system.name}</span>
                            <SystemStateBadge state={system.state} />
                            {system.realtimeEnabled && (
                              <Radio className="text-emerald-300" />
                            )}
                          </button>
                        </SidebarMenuSubButton>
                        {(lifecycleAction || lifecycleActionSystemId === system.id) && (
                          <Tooltip delayDuration={500} disableHoverableContent>
                            <TooltipTrigger asChild>
                              <button
                                className="pointer-events-none absolute right-7 top-1 flex size-5 items-center justify-center rounded-md text-sidebar-foreground opacity-0 transition-opacity hover:bg-sidebar-accent hover:text-sidebar-accent-foreground group-hover/system-row:pointer-events-auto group-hover/system-row:opacity-100 disabled:cursor-wait disabled:opacity-60"
                                disabled={lifecycleActionSystemId === system.id || !lifecycleAction}
                                onClick={(event) => {
                                  event.stopPropagation();
                                  if (lifecycleAction) {
                                    onLifecycleAction(system, lifecycleAction);
                                  }
                                }}
                                type="button"
                              >
                                {lifecycleActionSystemId === system.id ? (
                                  <Loader2 className="size-3.5 animate-spin" />
                                ) : lifecycleAction === "stop" ? (
                                  <Square className="size-3.5" />
                                ) : (
                                  <Play className="size-3.5" />
                                )}
                                <span className="sr-only">{lifecycleActionLabel}</span>
                              </button>
                            </TooltipTrigger>
                            <TooltipContent
                              className="max-w-80 whitespace-normal break-words text-left"
                              side="right"
                              sideOffset={6}
                            >
                              {lifecycleActionLabel}
                            </TooltipContent>
                          </Tooltip>
                        )}
                        <Tooltip delayDuration={500} disableHoverableContent>
                          <TooltipTrigger asChild>
                            <button
                              className="pointer-events-none absolute right-1 top-1 flex size-5 items-center justify-center rounded-md text-sidebar-foreground opacity-0 transition-opacity hover:bg-sidebar-accent hover:text-sidebar-accent-foreground group-hover/system-row:pointer-events-auto group-hover/system-row:opacity-100"
                              onClick={(event) => {
                                event.stopPropagation();
                                onRemoveSystem(host, system);
                              }}
                              type="button"
                            >
                              <Trash2 className="size-3.5" />
                              <span className="sr-only">Remove this system from your explorer</span>
                            </button>
                          </TooltipTrigger>
                          <TooltipContent side="right" sideOffset={6}>
                            Remove this system from your explorer
                          </TooltipContent>
                        </Tooltip>
                      </div>
                      {isSystemExpanded && (
                        <SidebarMenuSub className="ml-2 mr-0 pr-0">
                          {navItems.map((item) => {
                            const isCatalog = item.id === "definitions";
                            const isCatalogOpen = openCatalogSystemIds.includes(system.id);

                            return (
                              <Fragment key={`${system.id}:${item.id}`}>
                                <SidebarMenuSubItem>
                                  {isCatalog ? (
                                    <SidebarMenuSubButton
                                      asChild
                                      className="gap-1 pr-2"
                                      isActive={isActiveSystem && view === item.id}
                                    >
                                      <div>
                                        <button
                                          className="flex h-full min-w-0 items-center gap-2 text-left"
                                          onClick={() => onOpenView(item.id, system.id)}
                                          type="button"
                                        >
                                          <item.icon className="size-4 shrink-0 text-sidebar-accent-foreground" />
                                          <span>{item.label}</span>
                                        </button>
                                        <Tooltip delayDuration={500} disableHoverableContent>
                                          <TooltipTrigger asChild>
                                            <button
                                              aria-label={
                                                isCatalogOpen
                                                  ? "Close catalog explorer"
                                                  : "Explore worker categories and definitions"
                                              }
                                              aria-pressed={isCatalogOpen}
                                              className={
                                                isCatalogOpen
                                                  ? "flex size-5 shrink-0 items-center justify-center rounded-md bg-sidebar-accent text-sidebar-accent-foreground"
                                                  : "flex size-5 shrink-0 items-center justify-center rounded-md text-sidebar-foreground transition-colors hover:bg-sidebar-accent hover:text-sidebar-accent-foreground"
                                              }
                                              onClick={(event) => {
                                                event.stopPropagation();
                                                toggleCatalog(system.id);
                                              }}
                                              type="button"
                                            >
                                              <Search className="size-3.5" />
                                            </button>
                                          </TooltipTrigger>
                                          <TooltipContent side="right" sideOffset={6}>
                                            {isCatalogOpen
                                              ? "Close catalog explorer"
                                              : "Explore worker categories and definitions"}
                                          </TooltipContent>
                                        </Tooltip>
                                      </div>
                                    </SidebarMenuSubButton>
                                  ) : (
                                    <SidebarMenuSubButton
                                      asChild
                                      isActive={isActiveSystem && view === item.id}
                                    >
                                      <button
                                        onClick={() => onOpenView(item.id, system.id)}
                                        type="button"
                                      >
                                        <item.icon />
                                        <span>{item.label}</span>
                                      </button>
                                    </SidebarMenuSubButton>
                                  )}
                                </SidebarMenuSubItem>
                                {isCatalog && isCatalogOpen && (
                                  <SidebarMenuSubItem>
                                    <CatalogExplorer
                                      activeDefinitionName={
                                        isActiveSystem
                                          ? catalogScopeBySystemId[system.id]?.definitionName ?? ""
                                          : ""
                                      }
                                      activeOverviewCategory={
                                        isActiveSystem
                                          ? catalogScopeBySystemId[system.id]?.category ?? ""
                                          : ""
                                      }
                                      host={host}
                                      onOpenCatalogScope={onOpenCatalogScope}
                                      onOpenDefinition={onOpenDefinition}
                                      onOpenWorker={onOpenWorker}
                                      system={system}
                                    />
                                  </SidebarMenuSubItem>
                                )}
                              </Fragment>
                            );
                          })}
                        </SidebarMenuSub>
                      )}
                    </SidebarMenuSubItem>
                  );
                })}
              </SidebarMenuSub>
            )}
          </SidebarMenuItem>
        );
      })}
      <SidebarMenuItem>
        <SidebarMenuButton onClick={onAddServer} variant="outline">
          <Plus />
          <span>Add server</span>
        </SidebarMenuButton>
      </SidebarMenuItem>
    </SidebarMenu>
  );
}

type DefinitionCatalogLevel = {
  categories: WorkOverviewCatalogCategoryItem[];
  definitions: WorkDefinition[];
};

function CatalogExplorer({
  activeDefinitionName,
  activeOverviewCategory,
  host,
  onOpenCatalogScope,
  onOpenDefinition,
  onOpenWorker,
  system,
}: {
  activeDefinitionName: string;
  activeOverviewCategory: string;
  host: WorkableHostConnection;
  onOpenCatalogScope: (systemId: string, scope: OverviewScope | null) => void;
  onOpenDefinition: (definitionId: string, systemId?: string) => void;
  onOpenWorker: (workerId: string) => void;
  system: WorkableSystemConnection;
}) {
  const connection = useMemo<WorkableConnection>(
    () => ({
      apiUrl: host.apiUrl,
      systemName: system.systemName,
    }),
    [host.apiUrl, system.systemName]
  );
  const [path, setPath] = useState(activeOverviewCategory);
  const catalogLevel = useWorkableResource<DefinitionCatalogLevel>(
    connection,
    createDefinitionCatalogLevelPath(path),
    0
  );
  const [queueDefinition, setQueueDefinition] = useState<WorkDefinition | null>(null);
  const categories = catalogLevel.data?.categories ?? [];
  const definitions = catalogLevel.data?.definitions ?? [];
  const pathSegments = splitCatalogPath(path);
  const currentLabel = pathSegments.at(-1) ?? "All categories";
  const canGoBack = pathSegments.length > 0;

  const goBack = () => {
    const nextPath = pathSegments.slice(0, -1).join(":");
    setPath(nextPath);
  };
  return (
    <div className="relative z-10 -ml-11 mr-0 mt-1 w-[calc(var(--sidebar-width)-2rem)] overflow-hidden rounded-md border border-sidebar-border bg-sidebar group-data-[collapsible=icon]:hidden">
      <div className="flex h-8 min-w-0 items-center gap-1 border-sidebar-border border-b px-1.5 text-sidebar-foreground/80 text-xs">
        <button
          aria-label={canGoBack ? "Back to parent category" : "Catalog root"}
          className="flex size-5 shrink-0 items-center justify-center rounded-md hover:bg-sidebar-accent hover:text-sidebar-accent-foreground disabled:pointer-events-none disabled:opacity-40"
          disabled={!canGoBack}
          onClick={goBack}
          type="button"
        >
          {canGoBack ? <ChevronLeft className="size-3.5" /> : <Home className="size-3.5" />}
        </button>
        <span className="min-w-0 flex-1 truncate">{currentLabel}</span>
      </div>
      <div className="py-1">
        {catalogLevel.loading ? (
          Array.from({ length: 4 }).map((_, index) => (
            <Skeleton className="mx-2 my-1 h-7" key={index} />
          ))
        ) : (
          <>
            {categories.map((category) => (
              <div
                className="flex h-7 min-w-0 items-center text-sidebar-foreground text-sm hover:bg-sidebar-accent hover:text-sidebar-accent-foreground"
                key={category.path}
              >
                <button
                  className="flex h-full min-w-0 flex-1 items-center gap-2 px-2 text-left"
                  onClick={() => {
                    setPath(category.path);
                  }}
                  type="button"
                >
                  <Folder className="size-4 shrink-0 text-sidebar-accent-foreground" />
                  <span className="min-w-0 flex-1 truncate">{category.label}</span>
                  <span className="shrink-0 text-sidebar-foreground/60 text-xs tabular-nums">
                    {category.count}
                  </span>
                </button>
                <Tooltip delayDuration={500} disableHoverableContent>
                  <TooltipTrigger asChild>
                    <button
                      aria-label={`Open Catalog filtered to ${category.label}`}
                      className="mr-1 flex size-5 shrink-0 items-center justify-center rounded-md text-sidebar-foreground/70 hover:bg-sidebar-accent hover:text-sidebar-accent-foreground"
                      onClick={() => onOpenCatalogScope(system.id, {
                        category: normalizeCategoryFilter(category.path),
                        includeSubcategories: true,
                      })}
                      type="button"
                    >
                      <Boxes className="size-3.5" />
                    </button>
                  </TooltipTrigger>
                  <TooltipContent side="right" sideOffset={6}>
                    Open Catalog filtered to {category.label}
                  </TooltipContent>
                </Tooltip>
              </div>
            ))}
            {definitions.map((definition) => (
              <div
                className={
                  definition.name === activeDefinitionName
                    ? "flex h-7 min-w-0 items-center bg-sidebar-accent text-sidebar-accent-foreground text-sm"
                    : "flex h-7 min-w-0 items-center text-sidebar-foreground text-sm hover:bg-sidebar-accent hover:text-sidebar-accent-foreground"
                }
                key={definition.id.value}
              >
                <button
                  className="flex h-full min-w-0 flex-1 items-center gap-2 px-2 text-left"
                  onClick={() => onOpenDefinition(definition.id.value, system.id)}
                  type="button"
                >
                  <FileCode2 className="size-4 shrink-0 text-sidebar-accent-foreground" />
                  <span className="min-w-0 flex-1 truncate font-mono">{definition.name}</span>
                </button>
                <Tooltip delayDuration={500} disableHoverableContent>
                  <TooltipTrigger asChild>
                    <button
                      aria-label={`Queue ${definition.name}`}
                      className="mr-1 flex size-5 shrink-0 items-center justify-center rounded-md text-sidebar-foreground/70 hover:bg-sidebar-accent hover:text-sidebar-accent-foreground"
                      onClick={() => setQueueDefinition(definition)}
                      type="button"
                    >
                      <Send className="size-3.5" />
                    </button>
                  </TooltipTrigger>
                  <TooltipContent side="right" sideOffset={6}>
                    Queue {definition.name}
                  </TooltipContent>
                </Tooltip>
              </div>
            ))}
            {categories.length === 0 && definitions.length === 0 && (
              <div className="px-2 py-2 text-sidebar-foreground/60 text-xs">
                No catalog entries.
              </div>
            )}
          </>
        )}
      </div>
      <QueueDialog
        connection={connection}
        definition={queueDefinition}
        onQueuedWorker={onOpenWorker}
        onOpenChange={(open) => !open && setQueueDefinition(null)}
      />
    </div>
  );
}

function EmptyServerState({ onAddServer }: { onAddServer: () => void }) {
  return (
    <div className="flex min-h-[calc(100vh-8rem)] items-center justify-center">
      <div className="max-w-md rounded-lg border border-dashed p-8 text-center">
        <div className="mx-auto flex size-10 items-center justify-center rounded-md bg-muted">
          <Server className="size-5 text-muted-foreground" />
        </div>
        <h1 className="mt-4 font-semibold text-xl">No servers</h1>
        <p className="mt-2 text-muted-foreground text-sm">
          Add a Workable HTTP host to discover its systems.
        </p>
        <Button className="mt-4" onClick={onAddServer}>
          <Plus className="size-4" />
          Add server
        </Button>
      </div>
    </div>
  );
}

function SystemStateBadge({ state }: { state?: string | null }) {
  const label = state || "State unknown. Open Overview or refresh to connect.";
  return (
    <Tooltip delayDuration={500} disableHoverableContent>
      <TooltipTrigger asChild>
        <span className={`size-2 shrink-0 rounded-full ${systemStateDotClass(state)}`} />
      </TooltipTrigger>
      <TooltipContent side="right" sideOffset={6}>
        {label}
      </TooltipContent>
    </Tooltip>
  );
}

function DelayedLoadingOverlay({
  active,
  delay = 100,
  label,
}: {
  active: boolean;
  delay?: number;
  label: string;
}) {
  const [visible, setVisible] = useState(false);

  useEffect(() => {
    if (!active) {
      queueMicrotask(() => setVisible(false));
      return;
    }

    const timeoutId = window.setTimeout(() => setVisible(true), delay);
    return () => window.clearTimeout(timeoutId);
  }, [active, delay]);

  if (!visible) {
    return null;
  }

  return (
    <div className="absolute inset-0 z-20 flex items-center justify-center rounded-lg bg-background/55 backdrop-blur-sm">
      <div className="flex items-center gap-2 rounded-md border bg-popover px-3 py-2 text-popover-foreground shadow-sm">
        <Loader2 className="size-4 animate-spin" />
        <span className="font-medium text-sm">{label}</span>
      </div>
    </div>
  );
}

function DeleteTargetDialog({
  onConfirm,
  onOpenChange,
  target,
}: {
  onConfirm: () => void;
  onOpenChange: (open: boolean) => void;
  target: PendingDelete | null;
}) {
  const title =
    target?.kind === "host"
      ? `Remove ${target.host.name}?`
      : target
        ? `Remove ${target.system.name}?`
        : "Remove item?";
  const description =
    target?.kind === "host"
      ? "This removes the server group and every Workable system saved under it from this browser."
      : target?.host.systems.length === 1
        ? `This removes ${target.system.name}. Because it is the last system under ${target.host.name}, the server group will be removed too.`
        : "This removes only this Workable system from the sidebar.";

  return (
    <AlertDialog onOpenChange={onOpenChange} open={!!target}>
      <AlertDialogContent>
        <AlertDialogHeader>
          <AlertDialogTitle>{title}</AlertDialogTitle>
          <AlertDialogDescription>{description}</AlertDialogDescription>
        </AlertDialogHeader>
        <AlertDialogFooter>
          <AlertDialogCancel>Cancel</AlertDialogCancel>
          <AlertDialogAction onClick={onConfirm} variant="destructive">
            Remove
          </AlertDialogAction>
        </AlertDialogFooter>
      </AlertDialogContent>
    </AlertDialog>
  );
}

function StopSystemDialog({
  onConfirm,
  onOpenChange,
  target,
}: {
  onConfirm: () => void;
  onOpenChange: (open: boolean) => void;
  target: PendingStopSystem | null;
}) {
  return (
    <AlertDialog onOpenChange={onOpenChange} open={!!target}>
      <AlertDialogContent>
        <AlertDialogHeader>
          <AlertDialogTitle>Stop {target?.system.name ?? "system"}?</AlertDialogTitle>
          <AlertDialogDescription>
            This stops the Workable system and may affect queued or running workers.
          </AlertDialogDescription>
        </AlertDialogHeader>
        <AlertDialogFooter>
          <AlertDialogCancel>Cancel</AlertDialogCancel>
          <AlertDialogAction onClick={onConfirm} variant="destructive">
            Stop
          </AlertDialogAction>
        </AlertDialogFooter>
      </AlertDialogContent>
    </AlertDialog>
  );
}

function ServerDialog({
  mode,
  open,
  onOpenChange,
  onSave,
  host,
}: {
  mode: "add" | "edit";
  open: boolean;
  onOpenChange: (open: boolean) => void;
  onSave: (host: WorkableHostConnection) => void;
  host?: WorkableHostConnection;
}) {
  const [name, setName] = useState(host?.name ?? "");
  const [apiUrl, setApiUrl] = useState(host?.apiUrl ?? "");
  const [discovered, setDiscovered] = useState<WorkableHttpSystemInfo[]>(
    () => host?.systems.map(createDiscoveredSystemFromStored) ?? []
  );
  const [selectedSystemIds, setSelectedSystemIds] = useState<Set<string>>(
    () => new Set(host?.systems.map((system) => system.systemName ?? "") ?? [])
  );
  const [realtimeSystemIds, setRealtimeSystemIds] = useState<Set<string>>(
    () => new Set(host?.systems.filter((system) => system.realtimeEnabled).map((system) => system.systemName ?? ""))
  );
  const [isLoadingSystems, setIsLoadingSystems] = useState(false);
  const [systemsError, setSystemsError] = useState<string | undefined>();

  const fetchSystems = useCallback(async () => {
    if (!apiUrl.trim()) {
      return;
    }

    setIsLoadingSystems(true);
    setSystemsError(undefined);

    try {
      const result = await discoverSystems(apiUrl);
      const systems = mergeDiscoveredSystemsWithStored(result.systems ?? [], host?.systems);
      setApiUrl(result.apiUrl);
      setDiscovered(systems);

      setSelectedSystemIds((current) => {
        if (current.size > 0) {
          return current;
        }

        return new Set(systems.map(getSystemStorageKey));
      });
      setRealtimeSystemIds((current) => {
        const next = new Set<string>();
        for (const system of systems) {
          const key = getSystemStorageKey(system);
          if (current.has(key) && system.capabilities.realtime.enabled) {
            next.add(key);
          }
        }
        return next;
      });
    } catch (caught) {
      setDiscovered([]);
      setSystemsError(
        caught instanceof Error ? caught.message : "Unable to load Workable systems."
      );
    } finally {
      setIsLoadingSystems(false);
    }
  }, [apiUrl, host]);

  useEffect(() => {
    if (!open || mode !== "edit" || !host?.apiUrl) {
      return;
    }

    const timeoutId = window.setTimeout(() => {
      void fetchSystems();
    }, 0);

    return () => window.clearTimeout(timeoutId);
  }, [fetchSystems, host?.apiUrl, mode, open]);

  const save = () => {
    const selected = discovered.filter((system) =>
      selectedSystemIds.has(getSystemStorageKey(system))
    );
    const hasSelectedDiscoveredSystem = selected.length > 0;

    if (!hasSelectedDiscoveredSystem) {
      setSystemsError("Select at least one Workable system.");
      return;
    }

    const hostId = host?.id ?? createServerId();
    onSave({
      id: hostId,
      name: name.trim() || "Workable host",
      apiUrl: apiUrl.trim(),
      systems: selected.map((system) =>
        createStoredSystem(
          hostId,
          system,
          realtimeSystemIds,
          findStoredSystemByKey(host, system)
        )
      ),
    });
    onOpenChange(false);
  };

  const toggleSelectedSystem = (system: WorkableHttpSystemInfo, checked: boolean) => {
    const key = getSystemStorageKey(system);
    setSelectedSystemIds((current) => {
      const next = new Set(current);
      if (checked) {
        next.add(key);
      } else {
        next.delete(key);
      }
      return next;
    });
  };

  const toggleRealtimeSystem = (system: WorkableHttpSystemInfo, checked: boolean) => {
    const key = getSystemStorageKey(system);
    setRealtimeSystemIds((current) => {
      const next = new Set(current);
      if (checked && system.capabilities.realtime.enabled) {
        next.add(key);
      } else {
        next.delete(key);
      }
      return next;
    });
  };

  return (
    <Dialog onOpenChange={onOpenChange} open={open}>
      <DialogContent className="sm:max-w-2xl">
        <DialogHeader>
          <DialogTitle>{mode === "add" ? "Add server" : "Edit server"}</DialogTitle>
          <DialogDescription>
            Discover Workable systems exposed by a host and add selected systems to the tree.
          </DialogDescription>
        </DialogHeader>
        <div className="space-y-4">
          <div className="grid gap-2">
            <Label>Host name</Label>
            <Input
              onChange={(event) => setName(event.target.value)}
              value={name}
            />
          </div>
          <div className="grid gap-2">
            <Label>HTTP API URL</Label>
            <div className="flex gap-2">
              <Input
                onChange={(event) => {
                  setApiUrl(event.target.value);
                  setDiscovered([]);
                  setSelectedSystemIds(new Set());
                  setRealtimeSystemIds(new Set());
                  setSystemsError(undefined);
                }}
                value={apiUrl}
              />
              <Button
                disabled={isLoadingSystems || !apiUrl.trim()}
                onClick={() => void fetchSystems()}
                type="button"
                variant="outline"
              >
                {isLoadingSystems ? (
                  <Loader2 className="size-4 animate-spin" />
                ) : (
                  <RefreshCw className="size-4" />
                )}
                Load systems
              </Button>
            </div>
          </div>
          {systemsError && (
            <Alert variant="destructive">
              <ShieldAlert className="size-4" />
              <AlertTitle>Discovery failed</AlertTitle>
              <AlertDescription>{systemsError}</AlertDescription>
            </Alert>
          )}
          <div className="rounded-lg border">
            <div className="grid grid-cols-[1fr_7rem] border-b px-3 py-2 font-medium text-muted-foreground text-xs">
              <span>System</span>
              <span>Real time</span>
            </div>
            <div className="max-h-72 overflow-y-auto">
              {isLoadingSystems ? (
                <div className="p-3">
                  <StackedSkeleton count={3} />
                </div>
              ) : discovered.length === 0 ? (
                <div className="p-6 text-center text-muted-foreground text-sm">
                  Enter a URL and load systems.
                </div>
              ) : (
                discovered.map((system) => {
                  const key = getSystemStorageKey(system);
                  const realtimeAvailable = system.capabilities.realtime.enabled;

                  return (
                    <div
                      className="grid grid-cols-[1fr_7rem] items-center gap-3 border-b px-3 py-3 last:border-b-0"
                      key={key}
                    >
                      <label className="flex min-w-0 items-start gap-3">
                        <input
                          checked={selectedSystemIds.has(key)}
                          className="mt-0.5 size-4 rounded border"
                          onChange={(event) => toggleSelectedSystem(system, event.target.checked)}
                          type="checkbox"
                        />
                        <span className="min-w-0">
                          <span className="block truncate font-medium text-sm">
                            {getSystemDisplayName(system)}
                          </span>
                          <span className="block text-muted-foreground text-xs">
                            {system.isDefault ? "Default system" : system.state}
                          </span>
                        </span>
                      </label>
                      <RealtimeCheckbox
                        checked={realtimeAvailable && realtimeSystemIds.has(key)}
                        disabled={!realtimeAvailable}
                        onChange={(checked) => toggleRealtimeSystem(system, checked)}
                      />
                    </div>
                  );
                })
              )}
            </div>
          </div>
          <div className="flex justify-end gap-2">
            <Button onClick={() => onOpenChange(false)} variant="outline">
              Cancel
            </Button>
            <Button
              disabled={
                !apiUrl.trim() ||
                isLoadingSystems ||
                discovered.length === 0 ||
                !discovered.some((system) => selectedSystemIds.has(getSystemStorageKey(system)))
              }
              onClick={save}
            >
              Save
            </Button>
          </div>
        </div>
      </DialogContent>
    </Dialog>
  );
}

function RealtimeCheckbox({
  checked,
  disabled,
  onChange,
}: {
  checked: boolean;
  disabled: boolean;
  onChange: (checked: boolean) => void;
}) {
  const checkbox = (
    <input
      checked={checked}
      className="size-4 rounded border disabled:opacity-50"
      disabled={disabled}
      onChange={(event) => onChange(event.target.checked)}
      type="checkbox"
    />
  );

  if (!disabled) {
    return <label className="flex items-center justify-center">{checkbox}</label>;
  }

  return (
    <Tooltip delayDuration={500} disableHoverableContent>
      <TooltipTrigger asChild>
        <span className="flex cursor-not-allowed items-center justify-center">
          {checkbox}
        </span>
      </TooltipTrigger>
      <TooltipContent
        className="max-w-64 whitespace-normal text-left"
        side="top"
        sideOffset={6}
      >
        Real-time not available because SignalR is not configured on the server.
      </TooltipContent>
    </Tooltip>
  );
}

function ConsoleNavigationHeader({
  canGoBack,
  definitionId,
  host,
  onBack,
  onOpenView,
  system,
  view,
  workerId,
}: {
  canGoBack: boolean;
  definitionId: string | null;
  host: WorkableHostConnection;
  onBack: () => void;
  onOpenView: (view: View, systemId?: string, trackHistory?: boolean) => void;
  system: WorkableSystemConnection;
  view: View;
  workerId: string | null;
}) {
  const canOpenOverview = view !== "overview";
  const currentLabel =
    view === "definition" && definitionId
      ? definitionId
      : view === "worker" && workerId
        ? workerId
        : navTitle(view);

  return (
    <div className="mb-3 flex min-h-8 min-w-0 items-center gap-2">
      <SidebarTrigger className="-ml-1" />
      <Separator className="h-5" orientation="vertical" />
      <Tooltip delayDuration={500} disableHoverableContent>
        <TooltipTrigger asChild>
          <Button
            aria-label="Go back"
            className="shrink-0"
            disabled={!canGoBack}
            onClick={onBack}
            size="icon-sm"
            variant="ghost"
          >
            <ChevronLeft className="size-4" />
          </Button>
        </TooltipTrigger>
        <TooltipContent side="bottom" sideOffset={6}>
          Go back
        </TooltipContent>
      </Tooltip>
      <div className="min-w-0 overflow-x-auto">
        <Breadcrumb>
          <BreadcrumbList className="flex-nowrap whitespace-nowrap">
            <BreadcrumbItem className="min-w-0 shrink-0">
              <BreadcrumbPage className="max-w-48 truncate text-muted-foreground">
                {host.name}
              </BreadcrumbPage>
            </BreadcrumbItem>
            <BreadcrumbSeparator className="shrink-0" />
            <BreadcrumbItem className="min-w-0 shrink-0">
              {canOpenOverview ? (
                <BreadcrumbLink asChild className="max-w-56 truncate">
                  <button onClick={() => onOpenView("overview", system.id)} type="button">
                    {system.name}
                  </button>
                </BreadcrumbLink>
              ) : (
                <BreadcrumbPage className="max-w-56 truncate">
                  {system.name}
                </BreadcrumbPage>
              )}
            </BreadcrumbItem>
            <BreadcrumbSeparator className="shrink-0" />
            <BreadcrumbItem className="min-w-0 shrink-0">
              <BreadcrumbPage className={`${view === "worker" || view === "definition" ? "font-mono" : ""} max-w-80 truncate font-semibold text-foreground`}>
                {currentLabel}
              </BreadcrumbPage>
            </BreadcrumbItem>
          </BreadcrumbList>
        </Breadcrumb>
      </div>
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
    <div className="min-w-0 overflow-x-auto text-xs">
      <Breadcrumb>
        <BreadcrumbList className="flex-nowrap whitespace-nowrap text-xs">
          <BreadcrumbItem className="shrink-0">
            {scope ? (
              <BreadcrumbLink asChild>
                <button onClick={onClear} type="button">
                  All categories
                </button>
              </BreadcrumbLink>
            ) : (
              <BreadcrumbPage>All categories</BreadcrumbPage>
            )}
          </BreadcrumbItem>
          {categoryCrumbs.map((crumb, index) => {
            const isCurrentCategory = !hasDefinition && index === categoryCrumbs.length - 1;

            return (
              <Fragment key={crumb.path}>
                <BreadcrumbSeparator className="shrink-0" />
                <BreadcrumbItem className="min-w-0 shrink-0">
                  {isCurrentCategory ? (
                    <BreadcrumbPage className="max-w-56 truncate">
                      {crumb.label}
                    </BreadcrumbPage>
                  ) : (
                    <BreadcrumbLink asChild className="max-w-56 truncate">
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
                  <BreadcrumbLink asChild className="max-w-80 truncate font-mono">
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
                  <BreadcrumbPage className="max-w-80 truncate font-mono text-foreground">
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

function ViewActionLane({ children }: { children?: ReactNode }) {
  return (
    <div
      aria-hidden={children ? undefined : true}
      className="-mb-2 flex min-h-9 min-w-0 -translate-y-2 items-center justify-end gap-1"
    >
      {children}
    </div>
  );
}

function OverviewCatalogFilter({
  connection,
  loading,
  onClear,
  onSelectCategory,
  onSelectDefinition,
  refreshToken,
  scope,
  tooltipLabel = "Filter overview by category and definition",
}: {
  connection: WorkableConnection;
  loading: boolean;
  onClear: () => void;
  onSelectCategory: (category: string) => void;
  onSelectDefinition: (definitionName: string, category: string) => void;
  refreshToken: number;
  scope: OverviewScope | null;
  tooltipLabel?: string;
}) {
  const [open, setOpen] = useState(false);
  const [tooltipOpen, setTooltipOpen] = useState(false);
  const tooltipOpenTimer = useRef<ReturnType<typeof setTimeout> | null>(null);
  const [path, setPath] = useState(scope?.category ?? "");
  const activeFilterCount = scope ? 1 : 0;
  const scopeLabel = formatOverviewScopeLabel(scope);
  const filterTooltip = scopeLabel
    ? `Filtered by catalog: ${scopeLabel}`
    : tooltipLabel;
  const catalogRequest = useMemo(
    () => ({
      components: [overviewComponent("catalog")],
      scope: createOverviewComponentScope(scope),
    }),
    [scope]
  );
  const catalog = useWorkablePostResource<WorkComponentQueryResult>(
    connection,
    open ? "components/query" : null,
    catalogRequest,
    refreshToken
  );
  const catalogComponent = getWorkComponentData<WorkOverviewCatalogComponent>(
    open ? catalog.data : undefined,
    "catalog"
  );

  const closeTooltip = useCallback(() => {
    if (tooltipOpenTimer.current) {
      clearTimeout(tooltipOpenTimer.current);
      tooltipOpenTimer.current = null;
    }
    setTooltipOpen(false);
  }, []);

  const scheduleTooltip = useCallback(() => {
    closeTooltip();
    if (open) {
      return;
    }

    tooltipOpenTimer.current = setTimeout(() => {
      setTooltipOpen(true);
      tooltipOpenTimer.current = null;
    }, 500);
  }, [closeTooltip, open]);

  useEffect(() => () => {
    if (tooltipOpenTimer.current) {
      clearTimeout(tooltipOpenTimer.current);
    }
  }, []);


  const handleOpenChange = (nextOpen: boolean) => {
    closeTooltip();
    if (nextOpen) {
      setPath(scope?.category ?? "");
    }
    setOpen(nextOpen);
  };

  const clearAll = () => {
    closeTooltip();
    setPath("");
    onClear();
  };

  return (
    <Popover open={open} onOpenChange={handleOpenChange}>
      <Tooltip
        disableHoverableContent
        open={tooltipOpen}
      >
        <TooltipTrigger asChild>
          <PopoverTrigger asChild>
            <Button
              aria-label="Filter overview"
              className="relative text-muted-foreground hover:bg-transparent hover:text-foreground aria-expanded:bg-transparent aria-expanded:text-foreground dark:hover:bg-transparent"
              onBlur={closeTooltip}
              onClick={closeTooltip}
              onFocus={closeTooltip}
              onPointerEnter={scheduleTooltip}
              onPointerLeave={closeTooltip}
              size="icon-sm"
              variant="ghost"
            >
              <ListFilter className="size-4" />
              {activeFilterCount > 0 && (
                <span className="-right-0.5 -top-0.5 absolute flex size-4 items-center justify-center rounded-full bg-primary font-medium text-[10px] text-primary-foreground">
                  {activeFilterCount}
                </span>
              )}
            </Button>
          </PopoverTrigger>
        </TooltipTrigger>
        <TooltipContent
          className="max-w-80 whitespace-normal text-left"
          side="bottom"
          sideOffset={6}
        >
          {filterTooltip}
        </TooltipContent>
      </Tooltip>
      <PopoverContent align="end" className="w-[26rem] p-0">
        <div className="flex h-10 items-center justify-between border-b px-3">
          <span className="font-medium text-sm">Filters</span>
          <Button onClick={clearAll} size="sm" variant="ghost">
            Clear
          </Button>
        </div>
        <ScrollArea className="max-h-[70vh]">
          <div className="p-3">
            <div className="overflow-hidden rounded-lg border">
              <div className="border-b px-3 py-2 font-medium text-muted-foreground text-xs">
                Catalog
              </div>
              <CatalogFilterPanel
                categories={catalogComponent?.catalogCategories ?? []}
                definitions={catalogComponent?.catalogDefinitions ?? []}
                loading={loading || catalog.loading || !!catalog.refreshing}
                onClear={clearAll}
                onClose={() => setOpen(false)}
                onSelectCategory={(category) => {
                  closeTooltip();
                  onSelectCategory(category);
                }}
                onSelectDefinition={(definitionName, category) => {
                  closeTooltip();
                  onSelectDefinition(definitionName, category);
                }}
                path={path}
                scope={scope}
                setPath={setPath}
              />
            </div>
          </div>
        </ScrollArea>
      </PopoverContent>
    </Popover>
  );
}

function CatalogFilterPanel({
  categories,
  definitions,
  loading,
  onClear,
  onClose,
  onSelectCategory,
  onSelectDefinition,
  path,
  scope,
  setPath,
}: {
  categories: WorkOverviewCatalogCategoryItem[];
  definitions: WorkOverviewDefinitionItem[];
  loading: boolean;
  onClear: () => void;
  onClose?: () => void;
  onSelectCategory: (category: string) => void;
  onSelectDefinition: (definitionName: string, category: string) => void;
  path: string;
  scope: OverviewScope | null;
  setPath: (path: string) => void;
}) {
  const pathSegments = splitCatalogPath(path);
  const currentLabel = pathSegments.at(-1) ?? "All categories";
  const canGoBack = pathSegments.length > 0;

  const selectCategory = (category: string) => {
    setPath(category);
    onSelectCategory(category);
  };

  const clear = () => {
    setPath("");
    onClear();
  };

  const goBack = () => {
    selectCategory(pathSegments.slice(0, -1).join(":"));
  };

  return (
    <>
      <div className="flex h-10 min-w-0 items-center gap-1 border-b px-2">
        <button
          aria-label={canGoBack ? "Back to parent category" : "Catalog root"}
          className="flex size-7 shrink-0 items-center justify-center rounded-md hover:bg-accent hover:text-accent-foreground disabled:pointer-events-none disabled:opacity-40"
          disabled={!canGoBack}
          onClick={goBack}
          type="button"
        >
          {canGoBack ? <ChevronLeft className="size-4" /> : <Home className="size-4" />}
        </button>
        <span className="min-w-0 flex-1 truncate font-medium text-sm">
          {currentLabel}
        </span>
        <Button onClick={clear} size="sm" variant="ghost">
          All
        </Button>
      </div>
      <ScrollArea className="max-h-80">
        <div className="py-1">
          {loading ? (
            Array.from({ length: 5 }).map((_, index) => (
              <Skeleton className="mx-2 my-1 h-8" key={index} />
            ))
          ) : (
            <>
              {categories.map((category) => {
                const isActive =
                  !scope?.definitionName &&
                  normalizeCategoryFilter(scope?.category ?? "") ===
                    normalizeCategoryFilter(category.path);

                return (
                  <button
                    className={
                      isActive
                        ? "flex h-8 w-full min-w-0 items-center gap-2 bg-accent px-2 text-left text-accent-foreground text-sm"
                        : "flex h-8 w-full min-w-0 items-center gap-2 px-2 text-left text-sm hover:bg-accent hover:text-accent-foreground"
                    }
                    key={category.path}
                    onClick={() => selectCategory(category.path)}
                    type="button"
                  >
                    <Folder className="size-4 shrink-0 text-muted-foreground" />
                    <span className="min-w-0 flex-1 truncate">{category.label}</span>
                    <span className="shrink-0 text-muted-foreground text-xs tabular-nums">
                      {category.count}
                    </span>
                  </button>
                );
              })}
              {definitions.map((definition) => {
                const isActive = definition.name === scope?.definitionName;

                return (
                  <button
                    className={
                      isActive
                        ? "flex h-8 w-full min-w-0 items-center gap-2 bg-accent px-2 text-left text-accent-foreground text-sm"
                        : "flex h-8 w-full min-w-0 items-center gap-2 px-2 text-left text-sm hover:bg-accent hover:text-accent-foreground"
                    }
                    key={definition.id.value}
                    onClick={() => {
                      onSelectDefinition(
                        definition.name,
                        definition.category ?? path
                      );
                      onClose?.();
                    }}
                    type="button"
                  >
                    <FileCode2 className="size-4 shrink-0 text-muted-foreground" />
                    <span className="min-w-0 flex-1 truncate font-mono">
                      {definition.name}
                    </span>
                  </button>
                );
              })}
              {categories.length === 0 && definitions.length === 0 && (
                <div className="px-3 py-3 text-muted-foreground text-sm">
                  No catalog entries.
                </div>
              )}
            </>
          )}
        </div>
      </ScrollArea>
    </>
  );
}

function QueryFacetPanel<TValue extends string>({
  allLabel,
  onChange,
  options,
  value,
}: {
  allLabel: string;
  onChange: (value: TValue[]) => void;
  options: TValue[];
  value: TValue[];
}) {
  const selected = new Set(value);
  const selectedLabel =
    value.length === 0
      ? allLabel
      : value.length === 1
        ? value[0]
        : `${value.length} selected`;

  const setEnabled = (option: TValue, enabled: boolean) => {
    const next = new Set(selected);
    if (enabled) {
      next.add(option);
    } else {
      next.delete(option);
    }
    onChange(options.filter((item) => next.has(item)));
  };

  return (
    <div>
      <div className="flex h-10 items-center justify-between border-b px-3">
        <span className="truncate font-medium text-sm">{selectedLabel}</span>
        <Button onClick={() => onChange([])} size="sm" variant="ghost">
          All
        </Button>
      </div>
      <div className="py-1">
        {options.map((option) => {
          const isSelected = selected.has(option);

          return (
            <button
              className={
                isSelected
                  ? "flex h-8 w-full items-center gap-2 bg-accent px-3 text-accent-foreground text-sm"
                  : "flex h-8 w-full items-center gap-2 px-3 text-sm hover:bg-accent hover:text-accent-foreground"
              }
              key={option}
              onClick={() => setEnabled(option, !isSelected)}
              type="button"
            >
              {isSelected ? (
                <CheckCircle2 className="size-4 shrink-0 text-primary" />
              ) : (
                <Square className="size-4 shrink-0 text-muted-foreground" />
              )}
              <span>{option}</span>
            </button>
          );
        })}
      </div>
    </div>
  );
}

function QueryFilterPopover<TValue extends string>({
  allFacetLabel,
  catalogScope,
  connection,
  facetLabel,
  facetOptions,
  facetValue,
  keyTypeFilter,
  onClearCatalog,
  onFacetChange,
  onKeyTypeFilterChange,
  onSelectCategory,
  onSelectDefinition,
  refreshToken,
}: {
  allFacetLabel: string;
  catalogScope: OverviewScope | null;
  connection: WorkableConnection;
  facetLabel: string;
  facetOptions: TValue[];
  facetValue: TValue[];
  keyTypeFilter: string;
  onClearCatalog: () => void;
  onFacetChange: (value: TValue[]) => void;
  onKeyTypeFilterChange: (keyType: string) => void;
  onSelectCategory: (category: string) => void;
  onSelectDefinition: (definitionName: string, category: string) => void;
  refreshToken: number;
}) {
  const [open, setOpen] = useState(false);
  const [path, setPath] = useState(catalogScope?.category ?? "");
  const catalogRequest = useMemo(
    () => ({
      components: [{ id: "catalog", type: "catalog" }],
      scope: createOverviewComponentScope(catalogScope),
    }),
    [catalogScope]
  );
  const catalog = useWorkablePostResource<WorkComponentQueryResult>(
    connection,
    open ? "components/query" : null,
    catalogRequest,
    refreshToken
  );
  const catalogComponent = getWorkComponentData<WorkOverviewCatalogComponent>(
    open ? catalog.data : undefined,
    "catalog"
  );
  const activeFilterCount =
    (catalogScope ? 1 : 0) +
    (keyTypeFilter.trim() ? 1 : 0) +
    (facetValue.length > 0 ? 1 : 0);
  const filterDescriptions = createQueryFilterDescriptions(
    catalogScope,
    facetLabel,
    facetValue,
    keyTypeFilter
  );
  const filterTooltip =
    filterDescriptions.length > 0
      ? `Filtered by ${filterDescriptions.join("; ")}`
      : "Filter query";

  const handleOpenChange = (nextOpen: boolean) => {
    if (nextOpen) {
      setPath(catalogScope?.category ?? "");
    }
    setOpen(nextOpen);
  };

  const clearAll = () => {
    onClearCatalog();
    onKeyTypeFilterChange("");
    onFacetChange([]);
    setPath("");
  };

  return (
    <Popover open={open} onOpenChange={handleOpenChange}>
      <Tooltip delayDuration={500} disableHoverableContent>
        <TooltipTrigger asChild>
          <PopoverTrigger asChild>
            <Button
              aria-label="Filter query"
              className="relative text-muted-foreground hover:bg-transparent hover:text-foreground aria-expanded:bg-transparent aria-expanded:text-foreground dark:hover:bg-transparent"
              size="icon-sm"
              variant="ghost"
            >
              <ListFilter className="size-4" />
              {activeFilterCount > 0 && (
                <span className="-right-0.5 -top-0.5 absolute flex size-4 items-center justify-center rounded-full bg-primary font-medium text-[10px] text-primary-foreground">
                  {activeFilterCount}
                </span>
              )}
            </Button>
          </PopoverTrigger>
        </TooltipTrigger>
        <TooltipContent
          className="max-w-80 whitespace-normal text-left"
          side="bottom"
          sideOffset={6}
        >
          {filterTooltip}
        </TooltipContent>
      </Tooltip>
      <PopoverContent align="end" className="w-[26rem] p-0">
        <div className="flex h-10 items-center justify-between border-b px-3">
          <span className="font-medium text-sm">Filters</span>
          <Button onClick={clearAll} size="sm" variant="ghost">
            Clear
          </Button>
        </div>
        <ScrollArea className="max-h-[70vh]">
          <div className="grid gap-3 p-3">
            <div className="overflow-hidden rounded-lg border">
              <div className="border-b px-3 py-2 font-medium text-muted-foreground text-xs">
                Catalog
              </div>
              <CatalogFilterPanel
                categories={catalogComponent?.catalogCategories ?? []}
                definitions={catalogComponent?.catalogDefinitions ?? []}
                loading={catalog.loading || !!catalog.refreshing}
                onClear={onClearCatalog}
                onSelectCategory={onSelectCategory}
                onSelectDefinition={(definitionName, category) => {
                  onSelectDefinition(definitionName, category);
                  setOpen(false);
                }}
                path={path}
                scope={catalogScope}
                setPath={setPath}
              />
            </div>
            <div className="rounded-lg border">
              <div className="border-b px-3 py-2 font-medium text-muted-foreground text-xs">
                {facetLabel}
              </div>
              <QueryFacetPanel
                allLabel={allFacetLabel}
                onChange={onFacetChange}
                options={facetOptions}
                value={facetValue}
              />
            </div>
            <div className="grid gap-2 rounded-lg border p-3">
              <Label className="text-muted-foreground text-xs">Key type</Label>
              <Input
                className="h-8"
                onChange={(event) => onKeyTypeFilterChange(event.target.value)}
                placeholder="Any key type"
                value={keyTypeFilter}
              />
            </div>
          </div>
        </ScrollArea>
      </PopoverContent>
    </Popover>
  );
}

function createQueryFilterDescriptions<TValue extends string>(
  catalogScope: OverviewScope | null,
  facetLabel: string,
  facetValue: TValue[],
  keyTypeFilter: string
) {
  const descriptions: string[] = [];
  const catalogLabel = formatOverviewScopeLabel(catalogScope);
  if (catalogLabel) {
    descriptions.push(`catalog: ${catalogLabel}`);
  }
  if (facetValue.length > 0) {
    descriptions.push(`${facetLabel.toLowerCase()}: ${formatFilterValues(facetValue)}`);
  }
  if (keyTypeFilter.trim()) {
    descriptions.push(`key type: ${keyTypeFilter.trim()}`);
  }

  return descriptions;
}

function formatFilterValues(values: readonly string[]) {
  const visible = values.slice(0, 3);
  const suffix = values.length > visible.length
    ? `, +${values.length - visible.length} more`
    : "";
  return `${visible.join(", ")}${suffix}`;
}

function QueryPaginationControls({
  skip,
  take,
  totalCount,
  onFirst,
  onNext,
  onPrevious,
}: {
  skip: number;
  take: number;
  totalCount?: number;
  onFirst: () => void;
  onNext: () => void;
  onPrevious: () => void;
}) {
  const count = totalCount ?? 0;
  const firstRecord = count === 0 ? 0 : Math.min(skip + 1, count);
  const lastRecord = count === 0 ? 0 : Math.min(skip + take, count);
  const canPrevious = skip > 0;
  const canNext = skip + take < count;

  return (
    <div className="ml-1 flex items-center gap-1 text-muted-foreground text-xs">
      <span className="min-w-24 text-right tabular-nums">
        {firstRecord}-{lastRecord} of {count}
      </span>
      <Button
        aria-label="First page"
        disabled={!canPrevious}
        onClick={onFirst}
        size="icon-sm"
        variant="ghost"
      >
        <ChevronsLeft className="size-4" />
      </Button>
      <Button
        aria-label="Previous page"
        disabled={!canPrevious}
        onClick={onPrevious}
        size="icon-sm"
        variant="ghost"
      >
        <ChevronLeft className="size-4" />
      </Button>
      <Button
        aria-label="Next page"
        disabled={!canNext}
        onClick={onNext}
        size="icon-sm"
        variant="ghost"
      >
        <ChevronRight className="size-4" />
      </Button>
    </div>
  );
}

function OverviewView({
  collapsedPanelIds,
  connection,
  hiddenPanelIds,
  isVisible,
  onClearOverviewScope,
  onConnectionError,
  onOpenCatalog,
  onOpenIterations,
  onOpenKeyType,
  onPanelCollapsedChange,
  onReady,
  onOpenWorker,
  onPanelVisibilityChange,
  onRefresh,
  onStateLoaded,
  onSelectOverviewCategory,
  onSelectOverviewDefinition,
  onViewIterationsByStatus,
  onViewWorkersByState,
  overviewScope,
  refreshToken,
}: {
  collapsedPanelIds: OverviewPanelId[];
  connection: WorkableConnection;
  hiddenPanelIds: OverviewPanelId[];
  isVisible: boolean;
  onClearOverviewScope: () => void;
  onConnectionError: () => void;
  onOpenCatalog: () => void;
  onOpenIterations: () => void;
  onOpenKeyType: (keyType: string) => void;
  onPanelCollapsedChange: (panelId: OverviewPanelId, collapsed: boolean) => void;
  onReady: () => void;
  onOpenWorker: (workerId: string) => void;
  onPanelVisibilityChange: (panelId: OverviewPanelId, visible: boolean) => void;
  onRefresh: () => void;
  onStateLoaded: (state: string) => void;
  onSelectOverviewCategory: (category: string) => void;
  onSelectOverviewDefinition: (definitionName: string, category: string) => void;
  onViewIterationsByStatus: (statuses: WorkCompletionStatus[]) => void;
  onViewWorkersByState: (states: WorkerState[]) => void;
  overviewScope: OverviewScope | null;
  refreshToken: number;
}) {
  const [actionError, setActionError] = useState<string>();
  const [actionWorkerId, setActionWorkerId] = useState<string | null>(null);
  const [failedWorkersSlice, setFailedWorkersSlice] = useState<{
    data: WorkSystemFailedWorkersOverview;
    key: string;
  } | null>(null);
  const [throughputMode, setThroughputMode] = useState<ThroughputMode>("completion");
  const [throughputWindowSeconds, setThroughputWindowSeconds] = useState(60);
  const throughputWindow =
    throughputWindows.find((window) => window.seconds === throughputWindowSeconds) ??
    throughputWindows[0];
  const isPanelVisible = useCallback(
    (panelId: OverviewPanelId) => !hiddenPanelIds.includes(panelId),
    [hiddenPanelIds]
  );
  const isPanelCollapsed = useCallback(
    (panelId: OverviewPanelId) => collapsedPanelIds.includes(panelId),
    [collapsedPanelIds]
  );
  const shouldFetchPanel = useCallback(
    (panelId: OverviewPanelId) =>
      isVisible && isPanelVisible(panelId) && !isPanelCollapsed(panelId),
    [isPanelCollapsed, isPanelVisible, isVisible]
  );
  const throughputCollapsed = isPanelCollapsed("throughput");
  const overviewComponents = useMemo(() => {
    const components: WorkComponentRequest[] = [
      overviewComponent("system"),
    ];

    if (shouldFetchPanel("workers")) {
      components.push(overviewComponent("workers"));
    }
    if (shouldFetchPanel("failedWorkers")) {
      components.push(overviewComponent("failedWorkers"));
    }
    if (shouldFetchPanel("relationships")) {
      components.push(overviewComponent("relationships"));
    }
    if (shouldFetchPanel("failedIterations")) {
      components.push(overviewComponent("failedIterations", "failedIterations", "standard"));
    }
    if (shouldFetchPanel("completedIterations")) {
      components.push(overviewComponent("completedIterations", "completedIterations", "standard"));
    }
    if (shouldFetchPanel("throughput")) {
      components.push(overviewComponent(
        "throughput",
        "throughput",
        "detailed",
        {
          bucketSeconds: throughputWindow.bucketSeconds,
          windowSeconds: throughputWindow.seconds,
        }
      ));
    }

    return components;
  }, [shouldFetchPanel, throughputWindow.bucketSeconds, throughputWindow.seconds]);
  const overviewRequest = useMemo(
    () => ({
      components: overviewComponents,
      scope: createOverviewComponentScope(overviewScope),
    }),
    [overviewComponents, overviewScope]
  );
  const failedWorkersRefreshRequest = useMemo(
    () => ({
      components: [
        overviewComponent("workers"),
        overviewComponent("failedWorkers"),
      ],
      scope: createOverviewComponentScope(overviewScope),
    }),
    [overviewScope]
  );
  const failedWorkersKey = `${connection.apiUrl}:${connection.systemName ?? ""}:${JSON.stringify(failedWorkersRefreshRequest)}:${refreshToken}`;
  const overview = useWorkablePostResource<WorkComponentQueryResult>(
    connection,
    isVisible ? "views/overview" : null,
    overviewRequest,
    refreshToken
  );
  const isReady = !overview.loading;
  const systemComponent = getWorkComponentData<WorkOverviewSystemComponent>(
    overview.data,
    "system"
  );
  const workersComponent = getWorkComponentData<WorkOverviewWorkersComponent>(
    overview.data,
    "workers"
  );
  const failedWorkersComponent = getWorkComponentData<WorkerOverviewItem[]>(
    overview.data,
    "failedWorkers"
  );
  const relationshipsComponent = getWorkComponentData<WorkOverviewRelationshipsComponent>(
    overview.data,
    "relationships"
  );
  const failedIterationsComponent = getWorkComponentData<WorkerIterationOverviewItem[]>(
    overview.data,
    "failedIterations"
  );
  const completedIterationsComponent = getWorkComponentData<WorkerIterationOverviewItem[]>(
    overview.data,
    "completedIterations"
  );
  const throughputComponent = getWorkComponentData<WorkOverviewThroughputComponent>(
    overview.data,
    "throughput"
  );
  const throughputData = throughputComponent?.throughput;
  const activeFailedWorkersSlice = failedWorkersSlice?.key === failedWorkersKey
    ? failedWorkersSlice.data
    : undefined;
  const hasOverviewFilters = !!overviewScope;
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
  const componentErrors = getWorkComponentErrors(overview.data);
  const showFailedIterations = isPanelVisible("failedIterations");
  const showCompletedIterations = isPanelVisible("completedIterations");
  const failedIterationsCollapsed = isPanelCollapsed("failedIterations");
  const completedIterationsCollapsed = isPanelCollapsed("completedIterations");
  const iterationListsCanShareRow =
    showFailedIterations &&
    showCompletedIterations &&
    !failedIterationsCollapsed &&
    !completedIterationsCollapsed;

  const executeWorkerAction = async (worker: WorkerOverviewItem, action: WorkAction) => {
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
      const refreshedFailedWorkers = getWorkComponentData<WorkerOverviewItem[]>(
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
    if (systemComponent?.systemState) {
      onStateLoaded(systemComponent.systemState);
    }
  }, [systemComponent?.systemState, onStateLoaded]);

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
          actionError,
          ...componentErrors,
        ]}
      />
      <ViewActionLane>
        <OverviewCatalogFilter
          connection={connection}
          loading={overview.loading || !!overview.refreshing}
          onClear={onClearOverviewScope}
          onSelectCategory={onSelectOverviewCategory}
          onSelectDefinition={onSelectOverviewDefinition}
          refreshToken={refreshToken}
          scope={overviewScope}
        />
        {hasOverviewFilters && (
          <Tooltip delayDuration={500} disableHoverableContent>
            <TooltipTrigger asChild>
              <Button
                aria-label="Clear filters"
                className="text-muted-foreground hover:bg-transparent hover:text-foreground dark:hover:bg-transparent"
                onClick={onClearOverviewScope}
                size="icon-sm"
                variant="ghost"
              >
                <X className="size-4" />
              </Button>
            </TooltipTrigger>
            <TooltipContent side="bottom" sideOffset={6}>
              Clear filters
            </TooltipContent>
          </Tooltip>
        )}
        <Tooltip delayDuration={500} disableHoverableContent>
          <TooltipTrigger asChild>
            <Button
              aria-label="Refresh overview"
              className="text-muted-foreground hover:bg-transparent hover:text-foreground dark:hover:bg-transparent"
              onClick={onRefresh}
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
          hiddenPanelIds={hiddenPanelIds}
          onPanelVisibilityChange={onPanelVisibilityChange}
        />
      </ViewActionLane>
      {isPanelVisible("workers") && (
        <OverviewPanelShell
          collapsed={isPanelCollapsed("workers")}
          onCollapsedChange={(collapsed) => onPanelCollapsedChange("workers", collapsed)}
          description="Worker states and current worker totals."
          title="Workers"
        >
          <WorkerStateStrip
            counts={workerCountByState}
            loading={overview.loading}
            onSelectState={(state) => onViewWorkersByState([state])}
            title="Worker states"
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
        </OverviewPanelShell>
      )}
      {isPanelVisible("failedWorkers") && (
        <OverviewWorkerList
          emptyText="No failed workers."
          loading={overview.loading && failedWorkers.length === 0}
          collapsed={isPanelCollapsed("failedWorkers")}
          onCollapsedChange={(collapsed) => onPanelCollapsedChange("failedWorkers", collapsed)}
          onWorkerAction={executeWorkerAction}
          onOpenWorker={onOpenWorker}
          onViewState={() => onViewWorkersByState(failedWorkerStates)}
          pendingActionWorkerId={actionWorkerId}
          state="Failed"
          title="Recent Failed Workers"
          workers={failedWorkers}
        />
      )}
      {isPanelVisible("throughput") && (
        <ThroughputChartPanel
          collapsed={throughputCollapsed}
          loading={overview.loading && !throughputData}
          mode={throughputMode}
          onCollapsedChange={(collapsed) => onPanelCollapsedChange("throughput", collapsed)}
          onModeChange={setThroughputMode}
          onWindowChange={setThroughputWindowSeconds}
          throughput={throughputData}
          windowSeconds={throughputWindow.seconds}
        />
      )}
      {isPanelVisible("relationships") && (
        <OverviewPanelShell
          collapsed={isPanelCollapsed("relationships")}
          onCollapsedChange={(collapsed) => onPanelCollapsedChange("relationships", collapsed)}
          description="Iteration statuses and common relationship types."
          title="Relationships"
        >
          <IterationStatusStrip
            counts={relationshipsComponent?.iterationCountByStatus ?? {}}
            loading={overview.loading}
            onSelectStatus={(status) => onViewIterationsByStatus([status])}
            title="Iteration statuses"
          />
          <TopKeyTypePanel
            keys={relationshipsComponent?.commonKeyTypes ?? []}
            loading={overview.loading}
            onShowMore={onOpenIterations}
            onSelectKeyType={onOpenKeyType}
          />
        </OverviewPanelShell>
      )}
      <div className={`grid gap-4 ${iterationListsCanShareRow ? "xl:grid-cols-2" : ""}`}>
        {showFailedIterations && (
          <OverviewIterationList
            emptyText="No failed iterations."
            loading={overview.loading}
            collapsed={failedIterationsCollapsed}
            onCollapsedChange={(collapsed) => onPanelCollapsedChange("failedIterations", collapsed)}
            onOpenWorker={onOpenWorker}
            onViewState={() => onViewIterationsByStatus(["Failed"])}
            status="Failed"
            title="Recent Failed Iterations"
            iterations={failedIterationsComponent ?? []}
          />
        )}
        {showCompletedIterations && (
          <OverviewIterationList
            emptyText="No completed iterations."
            loading={overview.loading}
            collapsed={completedIterationsCollapsed}
            onCollapsedChange={(collapsed) => onPanelCollapsedChange("completedIterations", collapsed)}
            onOpenWorker={onOpenWorker}
            onViewState={() => onViewIterationsByStatus(["Completed"])}
            status="Completed"
            title="Recent Completed Iterations"
            iterations={completedIterationsComponent ?? []}
          />
        )}
      </div>
    </div>
  );
}

function OverviewPanelShell({
  actions,
  children,
  collapsed = false,
  contentClassName,
  description,
  onCollapsedChange,
  title,
}: {
  actions?: ReactNode;
  children: ReactNode;
  collapsed?: boolean;
  contentClassName?: string;
  description?: string;
  onCollapsedChange?: (collapsed: boolean) => void;
  title: ReactNode;
}) {
  return (
    <section className="rounded-xl bg-card p-4 ring-1 ring-foreground/10">
      <div className="flex items-center justify-between gap-3">
        <div className="flex min-w-0 items-center gap-2">
          {onCollapsedChange && (
            <Button
              aria-label={collapsed ? "Expand panel" : "Collapse panel"}
              className="-ml-1 size-7 shrink-0 text-muted-foreground"
              onClick={() => onCollapsedChange(!collapsed)}
              size="icon-sm"
              variant="ghost"
            >
              <ChevronDown className={`size-4 transition-transform ${collapsed ? "-rotate-90" : ""}`} />
            </Button>
          )}
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
        {actions && !collapsed && (
          <div className="flex shrink-0 flex-wrap items-center justify-end gap-2">
            {actions}
          </div>
        )}
      </div>
      {!collapsed && (
        <div className={contentClassName ?? "mt-4 space-y-4"}>
          {children}
        </div>
      )}
    </section>
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
    description: "Iteration statuses and common relationship types.",
    id: "relationships",
    label: "Relationships",
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

function OverviewPanelSettings({
  hiddenPanelIds,
  onPanelVisibilityChange,
}: {
  hiddenPanelIds: OverviewPanelId[];
  onPanelVisibilityChange: (panelId: OverviewPanelId, visible: boolean) => void;
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
      </PopoverContent>
    </Popover>
  );
}

function OverviewWorkerList({
  collapsed,
  emptyText,
  loading,
  onCollapsedChange,
  onOpenWorker,
  onViewState,
  onWorkerAction,
  pendingActionWorkerId,
  state,
  title,
  workers,
}: {
  collapsed?: boolean;
  emptyText: string;
  loading: boolean;
  onCollapsedChange?: (collapsed: boolean) => void;
  onOpenWorker: (workerId: string) => void;
  onViewState: () => void;
  onWorkerAction: (worker: WorkerOverviewItem, action: WorkAction) => Promise<void>;
  pendingActionWorkerId: string | null;
  state: WorkerState;
  title: string;
  workers: WorkerOverviewItem[];
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
      collapsed={collapsed}
      onCollapsedChange={onCollapsedChange}
      title={
        <>
          {title}
          <Badge className={`justify-center ${stateTone(state)}`} variant="outline">
            {state}
          </Badge>
        </>
      }
    >
      <WorkerTable
        emptyText={emptyText}
        hideState
        loading={loading}
        onAction={onWorkerAction}
        onSelect={(worker) => onOpenWorker(worker.id.value)}
        pendingActionWorkerId={pendingActionWorkerId}
        workers={workers}
      />
    </OverviewPanelShell>
  );
}

function WorkerStateStrip({
  counts,
  loading,
  onSelectState,
  title = "Workers",
}: {
  counts: Partial<Record<WorkerState, number>>;
  loading: boolean;
  onSelectState: (state: WorkerState) => void;
  title?: string;
}) {
  if (loading) {
    return (
      <StatusStripSection
        description="Workers grouped by current state, with summary links for active, final, failed, and catalog counts."
        title={title}
      >
        <div className="flex gap-2 overflow-x-auto pb-1">
          {overviewWorkerStates.map((state) => (
            <Skeleton className="h-8 min-w-28 flex-1 rounded-full" key={state} />
          ))}
        </div>
      </StatusStripSection>
    );
  }

  return (
    <StatusStripSection
      description="Workers grouped by current state, with summary links for active, final, failed, and catalog counts."
      title={title}
    >
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
    </StatusStripSection>
  );
}

function IterationStatusStrip({
  counts,
  loading,
  onSelectStatus,
  title = "Worker iterations",
}: {
  counts: Partial<Record<WorkCompletionStatus, number>>;
  loading: boolean;
  onSelectStatus: (status: WorkCompletionStatus) => void;
  title?: string;
}) {
  if (loading) {
    return (
      <StatusStripSection
        description="Worker iterations grouped by status, with common relationship types for quick filtering."
        title={title}
      >
        <div className="flex gap-2 overflow-x-auto pb-1">
          {iterationStatuses.map((status) => (
            <Skeleton className="h-8 min-w-28 flex-1 rounded-full" key={status} />
          ))}
        </div>
      </StatusStripSection>
    );
  }

  return (
    <StatusStripSection
      description="Worker iterations grouped by status, with common relationship types for quick filtering."
      title={title}
    >
      <div className="flex gap-2 overflow-x-auto pb-1">
        {iterationStatuses.map((status) => (
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
    </StatusStripSection>
  );
}

function StatusStripSection({
  children,
  description,
  title,
}: {
  children: React.ReactNode;
  description: string;
  title: string;
}) {
  return (
    <section className="space-y-2">
      <div className="flex items-center">
        <Tooltip delayDuration={500} disableHoverableContent>
          <TooltipTrigger asChild>
            <button
              aria-label={`${title}: ${description}`}
              className="group inline-flex min-w-0 items-center gap-1.5 rounded-sm text-left focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring focus-visible:ring-offset-2 focus-visible:ring-offset-background"
              type="button"
            >
              <span className="font-medium text-foreground text-sm">{title}</span>
              <Info className="size-3.5 shrink-0 text-muted-foreground transition-colors group-hover:text-foreground" />
            </button>
          </TooltipTrigger>
          <TooltipContent className="max-w-64 whitespace-normal text-left" side="top" sideOffset={6}>
            {description}
          </TooltipContent>
        </Tooltip>
      </div>
      {children}
    </section>
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
  collapsed,
  emptyText,
  loading,
  onCollapsedChange,
  onOpenWorker,
  onViewState,
  status,
  title,
  iterations,
}: {
  collapsed?: boolean;
  emptyText: string;
  loading: boolean;
  onCollapsedChange?: (collapsed: boolean) => void;
  onOpenWorker: (workerId: string) => void;
  onViewState: () => void;
  status: WorkCompletionStatus;
  title: string;
  iterations: WorkerIterationOverviewItem[];
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
      collapsed={collapsed}
      onCollapsedChange={onCollapsedChange}
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
      ) : (
        <div className="space-y-2">
          {iterations.map((iteration) => (
            <button
              className="grid w-full cursor-pointer gap-3 rounded-lg border bg-card p-3 text-left transition-colors hover:bg-accent md:grid-cols-[minmax(0,1fr)_12rem_7rem]"
              key={`${iteration.workerId.value}-${iteration.sequence}`}
              onClick={() => onOpenWorker(iteration.workerId.value)}
              type="button"
            >
              <div className="min-w-0">
                <div className="truncate font-mono text-xs">{iteration.definitionName}</div>
                <div className="mt-1 text-muted-foreground text-xs">
                  {iteration.category ?? "Uncategorized"}
                </div>
              </div>
              <OverviewWorkerMeta
                label={status === "Failed" ? "Failed" : "Completed"}
                value={formatRelativeTime(iteration.completedAt)}
              />
              <OverviewWorkerMeta
                label="Execution"
                value={<DurationValue duration={formatExecutionDuration(iteration.executionDuration)} />}
              />
            </button>
          ))}
        </div>
      )}
    </OverviewPanelShell>
  );
}

function OverviewWorkerMeta({ label, value }: { label: string; value: React.ReactNode }) {
  return (
    <div className="min-w-0">
      <div className="text-muted-foreground text-[11px]">{label}</div>
      <div className="truncate text-xs">{value}</div>
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

function DefinitionsView({
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
  onOpenDefinition: (definitionId: string) => void;
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
      onOpenDefinition(filtered[0].id.value);
    }
  }, [catalogScope?.definitionName, definitions.loading, filtered, onOpenDefinition]);

  return (
    <div className="space-y-6">
      <ErrorPanel errors={[definitions.error]} />
      <ViewActionLane />
      <Card>
        <CardHeader className="gap-4 md:flex-row md:items-center md:justify-between">
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
          <div className="relative w-full md:w-80">
            <Search className="absolute left-3 top-2.5 size-4 text-muted-foreground" />
            <Input
              className="pl-9"
              onChange={(event) => setSearch(event.target.value)}
              placeholder="Search catalog"
              value={search}
            />
          </div>
        </CardHeader>
        <CardContent>
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
                      onClick={() => onOpenDefinition(definition.id.value)}
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
        </CardContent>
      </Card>
      <QueueDialog
        connection={connection}
        definition={queueDefinition}
        onQueuedWorker={onOpenWorker}
        onOpenChange={(open) => !open && setQueueDefinition(null)}
      />
    </div>
  );
}

function DefinitionView({
  connection,
  definitionId,
  onOpenWorker,
  onReady,
  refreshToken,
}: {
  connection: WorkableConnection;
  definitionId: string;
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
    <div className="space-y-6">
      <ErrorPanel errors={[info.error, saveError]} />
      <ViewActionLane />
      {info.loading ? (
        <StackedSkeleton count={6} />
      ) : !definition ? (
        <div className="rounded-lg border border-dashed p-8 text-center text-muted-foreground text-sm">
          Definition not found.
        </div>
      ) : (
        <>
          {saveStatus && (
            <Alert>
              <CheckCircle2 className="size-4" />
              <AlertTitle>Configuration saved</AlertTitle>
              <AlertDescription>{saveStatus}</AlertDescription>
            </Alert>
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
    </div>
  );
}

function WorkersView({
  categoryFilter,
  connection,
  definitionFilter,
  keyTypeFilter,
  onCategoryFilterChange,
  onDefinitionFilterChange,
  onOpenWorker,
  onKeyTypeFilterChange,
  onReady,
  onStateFilterChange,
  refreshToken,
  stateFilter,
}: {
  categoryFilter: string;
  connection: WorkableConnection;
  definitionFilter: string;
  keyTypeFilter: string;
  onOpenWorker: (workerId: string) => void;
  onCategoryFilterChange: (category: string) => void;
  onDefinitionFilterChange: (definitionName: string) => void;
  onKeyTypeFilterChange: (keyType: string) => void;
  onReady: () => void;
  onStateFilterChange: (states: WorkerState[]) => void;
  refreshToken: number;
  stateFilter: WorkerState[];
}) {
  const [pageState, setPageState] = useState({
    index: 0,
    queryKey: "",
    take: defaultQueryTake,
  });
  const catalogScope = useMemo<OverviewScope | null>(() => {
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
  }, [categoryFilter, definitionFilter]);
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
  const { queryTake, queryTableRef } = useViewportQueryTake();
  const queryKey = JSON.stringify(query);
  const pageIndex =
    pageState.queryKey === queryKey && pageState.take === queryTake
      ? pageState.index
      : 0;
  const skip = pageIndex * queryTake;
  const workers = useWorkerQuery(connection, query, refreshToken, queryTake, skip);
  const isReady = !workers.loading;
  const hasFilters = !!catalogScope || !!keyTypeFilter.trim() || stateFilter.length > 0;
  const clearFilters = () => {
    onCategoryFilterChange("");
    onDefinitionFilterChange("");
    onKeyTypeFilterChange("");
    onStateFilterChange([]);
  };

  useEffect(() => {
    if (isReady) {
      onReady();
    }
  }, [isReady, onReady]);

  return (
    <div className="space-y-6">
      <ErrorPanel errors={[workers.error]} />
      <ViewActionLane>
        <QueryFilterPopover
          allFacetLabel="All states"
          catalogScope={catalogScope}
          connection={connection}
          facetLabel="Worker states"
          facetOptions={states}
          facetValue={stateFilter}
          keyTypeFilter={keyTypeFilter}
          onClearCatalog={() => {
            onCategoryFilterChange("");
            onDefinitionFilterChange("");
          }}
          onFacetChange={onStateFilterChange}
          onKeyTypeFilterChange={onKeyTypeFilterChange}
          onSelectCategory={(category) => {
            onCategoryFilterChange(category);
            onDefinitionFilterChange("");
          }}
          onSelectDefinition={(definitionName, category) => {
            onCategoryFilterChange(category);
            onDefinitionFilterChange(definitionName);
          }}
          refreshToken={refreshToken}
        />
        {hasFilters && (
          <Tooltip delayDuration={500} disableHoverableContent>
            <TooltipTrigger asChild>
              <Button
                aria-label="Clear filters"
                className="text-muted-foreground hover:bg-transparent hover:text-foreground dark:hover:bg-transparent"
                onClick={clearFilters}
                size="icon-sm"
                variant="ghost"
              >
                <X className="size-4" />
              </Button>
            </TooltipTrigger>
            <TooltipContent side="bottom" sideOffset={6}>
              Clear filters
            </TooltipContent>
          </Tooltip>
        )}
        <QueryPaginationControls
          skip={skip}
          take={queryTake}
          totalCount={workers.data?.totalCount}
          onFirst={() => setPageState({
            index: 0,
            queryKey,
            take: queryTake,
          })}
          onNext={() => setPageState({
            index: pageIndex + 1,
            queryKey,
            take: queryTake,
          })}
          onPrevious={() => setPageState({
            index: Math.max(0, pageIndex - 1),
            queryKey,
            take: queryTake,
          })}
        />
      </ViewActionLane>
      <Card>
        <CardContent className="pt-0">
          <div ref={queryTableRef}>
            <WorkerTable
              loading={workers.loading}
              onSelect={(worker) => onOpenWorker(worker.id.value)}
              workers={workers.data?.workers ?? []}
            />
          </div>
        </CardContent>
      </Card>
    </div>
  );
}

function WorkerTable({
  compact,
  emptyText = "No workers matched the current query.",
  hideState = false,
  loading,
  onAction,
  onSelect,
  pendingActionWorkerId,
  workers,
}: {
  compact?: boolean;
  emptyText?: string;
  hideState?: boolean;
  loading: boolean;
  onAction?: (worker: WorkerOverviewItem, action: WorkAction) => Promise<void>;
  onSelect?: (worker: WorkerOverviewItem) => void;
  pendingActionWorkerId?: string | null;
  workers: WorkerOverviewItem[];
}) {
  const hasActions = Boolean(onAction);

  if (loading) {
    return <StackedSkeleton count={compact ? 5 : 8} />;
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
            {!compact && <TableHead>Subject</TableHead>}
            <TableHead>Updated</TableHead>
            <TableHead>Duration</TableHead>
            {hasActions && <TableHead className="w-12" />}
          </TableRow>
        </TableHeader>
        <TableBody>
          {workers.map((worker) => (
            <TableRow
              className={onSelect ? "cursor-pointer" : undefined}
              key={worker.id.value}
              onClick={(event) => {
                const target = event.target;
                if (
                  target instanceof Element &&
                  target.closest("[data-worker-row-action]")
                ) {
                  return;
                }

                onSelect?.(worker);
              }}
            >
              <TableCell>
                <div className="font-mono text-xs">{worker.definitionName}</div>
                <div className="font-mono text-muted-foreground text-xs">
                  {worker.id.value.slice(0, 8)}
                </div>
              </TableCell>
              {!hideState && (
                <TableCell>
                  <Badge className={stateTone(worker.state)} variant="outline">
                    {worker.state}
                  </Badge>
                </TableCell>
              )}
              {!compact && (
                <TableCell className="font-mono text-muted-foreground text-xs">
                  {worker.subjectId
                    ? `${worker.subjectId.type}:${worker.subjectId.value}`
                    : "-"}
                </TableCell>
              )}
              <TableCell className="text-muted-foreground text-xs">
                {formatRelativeTime(worker.updatedAt)}
              </TableCell>
              <TableCell>
                <DurationValue
                  className="font-mono text-xs"
                  duration={formatWorkerDuration(worker)}
                  muted
                />
              </TableCell>
              {hasActions && (
                <TableCell data-worker-row-action>
                  <WorkerRowActionMenu
                    disabled={pendingActionWorkerId === worker.id.value}
                    onAction={(action) => onAction?.(worker, action)}
                    worker={worker}
                  />
                </TableCell>
              )}
            </TableRow>
          ))}
        </TableBody>
      </Table>
    </div>
  );
}

function WorkerRowActionMenu({
  disabled,
  onAction,
  worker,
}: {
  disabled: boolean;
  onAction: (action: WorkAction) => Promise<void> | void;
  worker: WorkerOverviewItem;
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
          onPointerDown={(event) => event.stopPropagation()}
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

function getWorkerRowActions(worker: WorkerOverviewItem): WorkAction[] {
  if (worker.state === "Failed" || worker.state === "Paused" || worker.state === "Queued") {
    return ["Start", "Cancel"];
  }

  if (worker.state === "Running" || worker.state === "Waiting" || worker.state === "Retrying") {
    return ["Cancel"];
  }

  return [];
}

function IterationsView({
  categoryFilter,
  connection,
  definitionFilter,
  keyTypeFilter,
  onCategoryFilterChange,
  onDefinitionFilterChange,
  onKeyTypeFilterChange,
  onOpenWorker,
  onReady,
  onStatusFilterChange,
  refreshToken,
  statusFilter,
}: {
  categoryFilter: string;
  connection: WorkableConnection;
  definitionFilter: string;
  keyTypeFilter: string;
  onCategoryFilterChange: (category: string) => void;
  onDefinitionFilterChange: (definitionName: string) => void;
  onKeyTypeFilterChange: (keyType: string) => void;
  onOpenWorker: (workerId: string) => void;
  onReady: () => void;
  onStatusFilterChange: (statuses: WorkCompletionStatus[]) => void;
  refreshToken: number;
  statusFilter: WorkCompletionStatus[];
}) {
  const [pageState, setPageState] = useState({
    index: 0,
    queryKey: "",
    take: defaultQueryTake,
  });
  const catalogScope = useMemo<OverviewScope | null>(() => {
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
  }, [categoryFilter, definitionFilter]);
  const query = useMemo(
    () => ({
      category: normalizeCategoryFilter(categoryFilter) || undefined,
      definitionName: definitionFilter.trim() || undefined,
      keyType: keyTypeFilter.trim() || undefined,
      statuses: statusFilter.length === 0 ? undefined : statusFilter,
    }),
    [categoryFilter, definitionFilter, keyTypeFilter, statusFilter]
  );
  const { queryTake, queryTableRef } = useViewportQueryTake();
  const queryKey = JSON.stringify(query);
  const pageIndex =
    pageState.queryKey === queryKey && pageState.take === queryTake
      ? pageState.index
      : 0;
  const skip = pageIndex * queryTake;
  const iterations = useIterationQuery(connection, query, refreshToken, queryTake, skip);
  const isReady = !iterations.loading;
  const hasFilters = !!catalogScope || !!keyTypeFilter.trim() || statusFilter.length > 0;
  const clearFilters = () => {
    onCategoryFilterChange("");
    onDefinitionFilterChange("");
    onKeyTypeFilterChange("");
    onStatusFilterChange([]);
  };

  useEffect(() => {
    if (isReady) {
      onReady();
    }
  }, [isReady, onReady]);

  return (
    <div className="space-y-6">
      <ErrorPanel errors={[iterations.error]} />
      <ViewActionLane>
        <QueryFilterPopover
          allFacetLabel="All statuses"
          catalogScope={catalogScope}
          connection={connection}
          facetLabel="Iteration statuses"
          facetOptions={iterationStatuses}
          facetValue={statusFilter}
          keyTypeFilter={keyTypeFilter}
          onClearCatalog={() => {
            onCategoryFilterChange("");
            onDefinitionFilterChange("");
          }}
          onFacetChange={onStatusFilterChange}
          onKeyTypeFilterChange={onKeyTypeFilterChange}
          onSelectCategory={(category) => {
            onCategoryFilterChange(category);
            onDefinitionFilterChange("");
          }}
          onSelectDefinition={(definitionName, category) => {
            onCategoryFilterChange(category);
            onDefinitionFilterChange(definitionName);
          }}
          refreshToken={refreshToken}
        />
        {hasFilters && (
          <Tooltip delayDuration={500} disableHoverableContent>
            <TooltipTrigger asChild>
              <Button
                aria-label="Clear filters"
                className="text-muted-foreground hover:bg-transparent hover:text-foreground dark:hover:bg-transparent"
                onClick={clearFilters}
                size="icon-sm"
                variant="ghost"
              >
                <X className="size-4" />
              </Button>
            </TooltipTrigger>
            <TooltipContent side="bottom" sideOffset={6}>
              Clear filters
            </TooltipContent>
          </Tooltip>
        )}
        <QueryPaginationControls
          skip={skip}
          take={queryTake}
          totalCount={iterations.data?.totalCount}
          onFirst={() => setPageState({
            index: 0,
            queryKey,
            take: queryTake,
          })}
          onNext={() => setPageState({
            index: pageIndex + 1,
            queryKey,
            take: queryTake,
          })}
          onPrevious={() => setPageState({
            index: Math.max(0, pageIndex - 1),
            queryKey,
            take: queryTake,
          })}
        />
      </ViewActionLane>
      <Card>
        <CardContent className="pt-0">
          <div ref={queryTableRef}>
            <IterationTable
              iterations={iterations.data?.iterations ?? []}
              loading={iterations.loading}
              onSelect={(iteration) => onOpenWorker(iteration.workerId.value)}
            />
          </div>
        </CardContent>
      </Card>
    </div>
  );
}

function IterationTable({
  iterations,
  loading,
  onSelect,
}: {
  iterations: WorkerIterationOverviewItem[];
  loading: boolean;
  onSelect: (iteration: WorkerIterationOverviewItem) => void;
}) {
  if (loading) {
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
      <Table>
        <TableHeader>
          <TableRow>
            <TableHead>Definition</TableHead>
            <TableHead>Status</TableHead>
            <TableHead>Worker state</TableHead>
            <TableHead>Subject</TableHead>
            <TableHead>Completed</TableHead>
            <TableHead>Duration</TableHead>
          </TableRow>
        </TableHeader>
        <TableBody>
          {iterations.map((iteration) => (
            <TableRow
              className="cursor-pointer"
              key={`${iteration.workerId.value}:${iteration.sequence}`}
              onClick={() => onSelect(iteration)}
            >
              <TableCell>
                <div className="font-mono text-xs">{iteration.definitionName}</div>
                <div className="font-mono text-muted-foreground text-xs">
                  {iteration.workerId.value.slice(0, 8)} / iteration {iteration.sequence}
                </div>
              </TableCell>
              <TableCell>
                <Badge className={completionTone(iteration.status)} variant="outline">
                  {iteration.status}
                </Badge>
              </TableCell>
              <TableCell>
                <Badge className={stateTone(iteration.workerState)} variant="outline">
                  {iteration.workerState}
                </Badge>
              </TableCell>
              <TableCell className="font-mono text-muted-foreground text-xs">
                {formatTypedValue(iteration.subjectId)}
              </TableCell>
              <TableCell className="text-muted-foreground text-xs">
                {formatRelativeTime(iteration.completedAt)}
              </TableCell>
              <TableCell className="font-mono text-muted-foreground text-xs">
                <DurationValue duration={formatExecutionDuration(iteration.executionDuration)} />
              </TableCell>
            </TableRow>
          ))}
        </TableBody>
      </Table>
    </div>
  );
}

function WorkerConsoleView({
  backLabel,
  connection,
  onBack,
  refreshToken,
  workerId,
}: {
  backLabel: string;
  connection: WorkableConnection;
  onBack: () => void;
  refreshToken: number;
  workerId: string;
}) {
  const [actionMessage, setActionMessage] = useState<string | undefined>();
  const [actionRefreshToken, setActionRefreshToken] = useState(0);
  const snapshot = useWorkableResource<WorkerSnapshot>(
    connection,
    `workers/${workerId}`,
    refreshToken + actionRefreshToken
  );

  const executeAction = async (action: WorkAction) => {
    const current = snapshot.data;
    if (!current) {
      return;
    }

    const result = await workableFetch<{ status: string; messages?: { text: string }[] }>(
      connection,
      `workers/${current.id.value}/actions/${action.toLowerCase()}`,
      {
        method: "POST",
        body: JSON.stringify({ revision: current.revision }),
      }
    );
    setActionMessage(
      result.messages?.map((message) => message.text).join(" ") ||
        `${action} returned ${result.status}.`
    );
    setActionRefreshToken((value) => value + 1);
  };

  return (
    <div className="space-y-6">
      <ViewActionLane />
      <div className="flex flex-wrap items-start justify-between gap-4">
        <PageHeading
          description={workerId}
          title={snapshot.data?.definitionName ?? "Worker"}
        />
        <Button onClick={onBack} size="sm" variant="outline">
          {backLabel}
        </Button>
      </div>
      {snapshot.loading && <StackedSkeleton count={8} />}
      {snapshot.error && (
        <Alert variant="destructive">
          <ShieldAlert className="size-4" />
          <AlertTitle>Unable to load worker</AlertTitle>
          <AlertDescription>{snapshot.error}</AlertDescription>
        </Alert>
      )}
      {snapshot.data && (
        <>
          <div className="grid gap-4 md:grid-cols-2 xl:grid-cols-4">
            <Card>
              <CardHeader className="pb-2">
                <CardDescription>State</CardDescription>
              </CardHeader>
              <CardContent>
                <Badge className={stateTone(snapshot.data.state)} variant="outline">
                  {snapshot.data.state}
                </Badge>
              </CardContent>
            </Card>
            <Card>
              <CardHeader className="pb-2">
                <CardDescription>Revision</CardDescription>
              </CardHeader>
              <CardContent className="font-mono text-2xl">
                {snapshot.data.revision}
              </CardContent>
            </Card>
            <Card>
              <CardHeader className="pb-2">
                <CardDescription>State Sequence</CardDescription>
              </CardHeader>
              <CardContent className="font-mono text-2xl">
                {snapshot.data.stateSequence}
              </CardContent>
            </Card>
            <Card>
              <CardHeader className="pb-2">
                <CardDescription>Updated</CardDescription>
              </CardHeader>
              <CardContent className="text-sm">
                {formatDateTime(snapshot.data.updatedAt)}
              </CardContent>
            </Card>
          </div>

          <Card>
            <CardHeader className="gap-4 lg:flex-row lg:items-center lg:justify-between">
              <div>
                <CardTitle>Worker Console</CardTitle>
                <CardDescription>
                  Versioned actions are sent with the current snapshot revision.
                </CardDescription>
              </div>
              <div className="flex flex-wrap gap-2">
                <WorkerActionButton action="Start" icon={Play} onAction={executeAction} />
                <WorkerActionButton action="Pause" icon={Pause} onAction={executeAction} />
                <WorkerActionButton action="Cancel" icon={Ban} onAction={executeAction} />
                <WorkerActionButton action="Push" icon={Clock3} onAction={executeAction} />
                <WorkerActionButton action="Purge" icon={Trash2} onAction={executeAction} />
              </div>
            </CardHeader>
            <CardContent className="grid gap-4 md:grid-cols-2 xl:grid-cols-3">
              <MetadataItem label="Worker ID" value={snapshot.data.id.value} />
              <MetadataItem label="Definition" value={snapshot.data.definitionName} />
              <MetadataItem label="Definition ID" value={snapshot.data.definitionId.value} />
              <MetadataItem label="Category" value={snapshot.data.definitionCategory ?? "-"} />
              <MetadataItem label="Created" value={formatDateTime(snapshot.data.createdAt)} />
              <MetadataItem label="Updated" value={formatDateTime(snapshot.data.updatedAt)} />
              <MetadataItem
                label="Subject"
                value={formatTypedValue(snapshot.data.subjectId)}
              />
              <MetadataItem
                label="Concurrency Key"
                value={formatTypedValue(snapshot.data.concurrencyKey)}
              />
              <MetadataItem
                label="Identifiers"
                value={snapshot.data.identifiers?.map(formatTypedValue).join(", ") || "-"}
              />
            </CardContent>
          </Card>

          {actionMessage && (
            <Alert>
              <CircleDot className="size-4" />
              <AlertTitle>Action result</AlertTitle>
              <AlertDescription>{actionMessage}</AlertDescription>
            </Alert>
          )}

          <div className="grid gap-4 xl:grid-cols-2">
            <SnapshotBlock label="Input" value={snapshot.data.input} />
            <SnapshotBlock label="Output" value={snapshot.data.output} />
            <SnapshotBlock label="Messages" value={snapshot.data.messages} />
            <SnapshotBlock label="Iterations" value={snapshot.data.iterations} />
            <SnapshotBlock label="Logs" value={snapshot.data.logs} />
            <SnapshotBlock label="Action History" value={snapshot.data.actionHistory} />
            <SnapshotBlock label="Profile" value={snapshot.data.profile} />
            <SnapshotBlock label="Version" value={snapshot.data.version} />
          </div>
        </>
      )}
    </div>
  );
}

function QueueDialog({
  connection,
  definition,
  onQueuedWorker,
  onOpenChange,
}: {
  connection: WorkableConnection;
  definition: WorkDefinition | null;
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
    const nextValue = createDefaultValue(inputSchema);
    const nextRequest = createDefaultQueueRequest(definition);
    queueMicrotask(() => {
      setActiveTab(inputSchema ? "input" : "manual");
      setFormValue(nextValue);
      setManualRequestJson(compactJson({
        completion: "ReturnAfterAccepted",
        input: nextValue,
      }));
      setQueueRequest(nextRequest);
      setIsQueueing(false);
      setStatus(undefined);
      setError(undefined);
    });
  }, [definition, inputSchema]);

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

  const queue = async () => {
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

      if (completionMode !== "WaitForCompletion" && result.workerId?.value) {
        onOpenChange(false);
        onQueuedWorker(result.workerId.value);
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
        className="max-h-[88vh] overflow-hidden p-0 sm:max-w-5xl"
        onInteractOutside={(event) => event.preventDefault()}
      >
        <DialogHeader>
          <DialogTitle className="flex min-w-0 flex-wrap items-baseline gap-x-2 gap-y-1 px-4 pt-4">
            <span>Configure input, behavior, and runtime options for</span>
            <span
              className="min-w-0 truncate font-mono text-sm font-semibold text-sky-300"
              title={definition?.name}
            >
              {definition?.name ?? "worker"}
            </span>
          </DialogTitle>
        </DialogHeader>
        <div className="min-h-0 space-y-4 px-4">
          {error && (
            <Alert variant="destructive">
              <ShieldAlert className="size-4" />
              <AlertTitle>Queue failed</AlertTitle>
              <AlertDescription>{error}</AlertDescription>
            </Alert>
          )}
          {status && (
            <Alert>
              <CheckCircle2 className="size-4" />
              <AlertTitle>Queue accepted</AlertTitle>
              <AlertDescription>{status}</AlertDescription>
            </Alert>
          )}
          {isWaitingForCompletion && (
            <Alert>
              <Hourglass className="size-4 animate-pulse" />
              <AlertTitle>Waiting for completion</AlertTitle>
              <AlertDescription>
                The worker is executing. This dialog will update when the HTTP request returns.
              </AlertDescription>
            </Alert>
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
            className="min-h-[62vh]"
            value={activeTab}
          >
            <div className="flex flex-wrap items-center justify-between gap-3 border-b pb-3">
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
            <TabsContent className="mt-4 h-[54vh] overflow-y-auto pr-2" value="input">
              <SchemaForm
                onChange={updateFormValue}
                schema={inputSchema}
                value={formValue}
              />
            </TabsContent>
            <TabsContent className="mt-4 h-[54vh] overflow-y-auto pr-2" value="config">
              <QueueConfigurationTabs
                descriptor={queueSchemaDescriptor}
                onRequestChange={setQueueRequest}
                request={queueRequest}
                schema={queueRequestSchema}
              />
            </TabsContent>
            <TabsContent className="mt-4 h-[54vh] overflow-y-auto pr-2" value="manual">
              <JsonTextEditor
                label="Request JSON"
                onChange={setManualRequestJson}
                value={manualRequestJson}
              />
            </TabsContent>
          </Tabs>
          <div className="-mx-4 flex items-center justify-between gap-3 border-t bg-muted/30 px-4 py-3">
            <div className="min-w-0 text-sm">
              <span className="text-muted-foreground">Queue a worker for </span>
              <span className="font-mono font-semibold text-sky-300">
                {definition?.name ?? "definition"}
              </span>
            </div>
            <Button disabled={isQueueing} onClick={() => void queue()}>
              {isQueueing ? (
                <Loader2 className="size-4 animate-spin" />
              ) : (
                <Send className="size-4" />
              )}
              {isWaitingForCompletion ? "Waiting" : "Queue"}
            </Button>
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
    <Tabs className="min-h-full" defaultValue={firstTab}>
      <TabsList className="flex h-auto w-full flex-wrap justify-start">
        {descriptor.tabs.map((tab) => (
          <TabsTrigger key={tab.id} value={tab.id}>
            {tab.label}
          </TabsTrigger>
        ))}
      </TabsList>

      {descriptor.tabs.map((tab) => (
        <TabsContent className="mt-4 space-y-4" key={tab.id} value={tab.id}>
          <ConfigTabHeader description={tab.description} title={tab.label} />
          <div className="grid max-w-5xl gap-4 md:grid-cols-2">
            {tab.fields.map((field) => (
              <SchemaPathField
                description={field.description}
                key={`${tab.id}:${field.path}`}
                label={field.label}
                onChange={(next) => onRequestChange(next as QueueWorkRequest)}
                path={field.path}
                schema={schema}
                value={request}
              />
            ))}
          </div>
        </TabsContent>
      ))}
    </Tabs>
  );
}

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
            overviewCollapsedPanels: normalizeOverviewPanelIds(parsed.overviewCollapsedPanels),
            overviewHiddenPanels: normalizeOverviewHiddenPanels(
              parsed.overviewHiddenPanels,
              parsed.overviewThroughputHidden
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
          overviewCollapsedPanels: normalizeOverviewPanelIds(parsed.overviewCollapsedPanels),
          overviewHiddenPanels: normalizeOverviewHiddenPanels(
            parsed.overviewHiddenPanels,
            parsed.overviewThroughputHidden
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
          overviewCollapsedPanels: [],
          overviewHiddenPanels: [],
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
      overviewCollapsedPanels: [],
      overviewHiddenPanels: [],
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
    overviewCollapsedPanels: [],
    overviewHiddenPanels: [],
    overviewThroughputHidden: false,
    view: "overview",
  };
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
    realtimeSupported: false,
    realtimeTransport: null,
    state: null,
  };
}

function createStoredSystem(
  hostId: string,
  system: WorkableHttpSystemInfo,
  realtimeSystemIds: Set<string>,
  existingSystem?: WorkableSystemConnection
): WorkableSystemConnection {
  const key = getSystemStorageKey(system);
  const realtimeSupported = system.capabilities.realtime.enabled;

  return {
    id: existingSystem?.id ?? `${hostId}-${key || "default"}`,
    hostId,
    name: getSystemDisplayName(system),
    systemName: normalizeOptional(system.name),
    realtimeEnabled: realtimeSupported && realtimeSystemIds.has(key),
    realtimeSupported,
    realtimeTransport: system.capabilities.realtime.transport ?? null,
    state: system.state,
  };
}

function mergeDiscoveredSystemsWithStored(
  discovered: WorkableHttpSystemInfo[],
  storedSystems?: WorkableSystemConnection[]
) {
  if (!storedSystems?.length) {
    return discovered;
  }

  const merged = [...discovered];
  const discoveredKeys = new Set(discovered.map(getSystemStorageKey));
  for (const storedSystem of storedSystems) {
    const storedDiscovery = createDiscoveredSystemFromStored(storedSystem);
    const key = getSystemStorageKey(storedDiscovery);
    if (!discoveredKeys.has(key)) {
      merged.push(storedDiscovery);
      discoveredKeys.add(key);
    }
  }

  return merged;
}

function findStoredSystemByKey(
  host: WorkableHostConnection | undefined,
  system: WorkableHttpSystemInfo
) {
  const key = getSystemStorageKey(system);
  return host?.systems.find(
    (storedSystem) => getSystemStorageKey(createDiscoveredSystemFromStored(storedSystem)) === key
  );
}

async function discoverSystems(apiUrl: string): Promise<WorkableHttpSystems & { apiUrl: string }> {
  const candidates = createWorkableApiUrlCandidates(apiUrl);
  let lastError: unknown;

  for (const candidate of candidates) {
    try {
      const result = await workableFetch<WorkableHttpSystems>(
        {
          apiUrl: candidate,
        },
        "systems"
      );

      return {
        ...result,
        apiUrl: candidate,
      };
    } catch (caught) {
      lastError = caught;
    }
  }

  const attempted = candidates.map(formatSystemsEndpoint).join(", ");
  const detail =
    lastError instanceof Error && lastError.message !== "fetch failed"
      ? ` ${lastError.message}`
      : "";

  throw new Error(
    `Unable to reach the Workable API.${detail} Tried ${attempted}. Check that the protocol and port match the server.`
  );
}

function createWorkableApiUrlCandidates(value: string) {
  const trimmed = value.trim().replace(/\/+$/, "");
  if (!trimmed) {
    return [];
  }

  try {
    const entered = new URL(trimmed);
    const candidates: string[] = [];
    const addCandidate = (url: URL) => {
      const candidate = formatWorkableApiUrl(url);
      if (!candidates.includes(candidate)) {
        candidates.push(candidate);
      }
    };

    const systemsBase = stripTrailingPathSegment(entered, "systems");
    addCandidate(systemsBase);

    const path = systemsBase.pathname.replace(/\/+$/, "");
    if (!path.toLowerCase().endsWith("/workable")) {
      const workableBase = new URL(systemsBase.toString());
      workableBase.pathname = `${path}/workable`.replace(/^\/?/, "/");
      addCandidate(workableBase);
    }

    return candidates;
  } catch {
    return [trimmed];
  }
}

function stripTrailingPathSegment(url: URL, segment: string) {
  const next = new URL(url.toString());
  const path = next.pathname.replace(/\/+$/, "");

  if (path.toLowerCase().endsWith(`/${segment.toLowerCase()}`)) {
    next.pathname = path.slice(0, -(segment.length + 1)) || "/";
  }

  return next;
}

function formatWorkableApiUrl(url: URL) {
  const path = url.pathname === "/" ? "" : url.pathname.replace(/\/+$/, "");
  return `${url.origin}${path}${url.search}`;
}

function formatSystemsEndpoint(apiUrl: string) {
  const normalized = apiUrl.replace(/\/+$/, "");
  return `${normalized}/systems`;
}

function createDiscoveredSystemFromStored(
  system: WorkableSystemConnection
): WorkableHttpSystemInfo {
  return {
    id: { value: system.id },
    name: system.systemName ?? null,
    state: system.state ?? "Unknown",
    isDefault: !system.systemName,
    capabilities: {
      realtime: {
        enabled: Boolean(system.realtimeSupported),
        transport: system.realtimeTransport,
      },
    },
  };
}

function getSystemStorageKey(system: WorkableHttpSystemInfo) {
  return system.name?.trim() ?? "";
}

function getSystemDisplayName(system: WorkableHttpSystemInfo) {
  return normalizeOptional(system.name) ?? "Default";
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

function PageHeading({
  description,
  title,
}: {
  description: string;
  title: string;
}) {
  return (
    <div>
      <h1 className="font-semibold text-2xl tracking-normal">{title}</h1>
      <p className="mt-1 max-w-3xl text-muted-foreground text-sm">
        {description}
      </p>
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

function ThroughputChartPanel({
  collapsed,
  loading,
  mode,
  onCollapsedChange,
  onModeChange,
  onWindowChange,
  throughput,
  windowSeconds,
}: {
  collapsed?: boolean;
  loading: boolean;
  mode: ThroughputMode;
  onCollapsedChange?: (collapsed: boolean) => void;
  onModeChange: (mode: ThroughputMode) => void;
  onWindowChange: (seconds: number) => void;
  throughput?: WorkSystemThroughput;
  windowSeconds: number;
}) {
  const chartLabel = mode === "execution" ? "Execution time" : "Throughput";
  const chartDescription = mode === "execution"
    ? "Average execution time for settled iterations, scoped to the current overview filter."
    : "Started, completed, failed, and canceled iteration rates, scoped to the current overview filter.";
  return (
    <OverviewPanelShell
      actions={
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
      }
      collapsed={collapsed}
      onCollapsedChange={onCollapsedChange}
      description={chartDescription}
      title={chartLabel}
    >
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
              key={mode}
              mode={mode}
              throughput={throughput}
              windowSeconds={windowSeconds}
            />
          )}
        </TabsContent>
      </Tabs>
    </OverviewPanelShell>
  );
}

function ThroughputAreaChart({
  mode,
  throughput,
  windowSeconds,
}: {
  mode: ThroughputMode;
  throughput?: WorkSystemThroughput;
  windowSeconds: number;
}) {
  const buckets = getSettledThroughputBuckets(throughput);
  const bucketSeconds = throughput?.bucketSeconds ??
    throughputWindows.find((window) => window.seconds === windowSeconds)?.bucketSeconds ??
    1;
  const series = createThroughputSeries(mode, buckets, bucketSeconds);
  const maxValue = getNiceChartMax(Math.max(0, ...series.flatMap((item) => item.values)), mode);
  const yTicks = createYAxisTicks(maxValue);
  const xTicks = createTimeAxisTicks(throughput, buckets);
  const metrics = createThroughputMetrics(
    mode,
    throughput,
    bucketSeconds,
    windowSeconds
  );
  const lineSeries = mode === "completion" && series.length > 1
    ? [...series.slice(1), series[0]]
    : series;

  return (
    <div className="space-y-3">
      <div className="flex flex-wrap items-center justify-between gap-3">
        <div className="flex flex-wrap items-center gap-3">
          {series.map((item) => (
            <div className="flex items-center gap-1.5 text-xs" key={item.label}>
              <span className={`size-2 rounded-full ${item.legendClass}`} />
              <span className="text-muted-foreground">{item.label}</span>
            </div>
          ))}
        </div>
        <div className="flex flex-wrap items-center justify-end gap-1.5">
          {metrics.map((metric) => {
            const Icon = metric.icon;
            return (
              <Tooltip delayDuration={500} disableHoverableContent key={metric.id}>
                <TooltipTrigger asChild>
                  <div
                    className={`flex items-center justify-center gap-1.5 whitespace-nowrap rounded-full border border-foreground/10 bg-background/70 px-2.5 py-1 shadow-sm ${metric.widthClass ?? "min-w-24"}`}
                    tabIndex={0}
                  >
                    {metric.pulseClass && <span className={`size-2 rounded-full ${metric.pulseClass}`} />}
                    {Icon && <Icon className={`size-3.5 ${metric.iconClass ?? "text-muted-foreground"}`} />}
                    {metric.label && <span className="text-muted-foreground text-[11px]">{metric.label}</span>}
                    <span className={`font-mono font-semibold text-xs ${metric.valueClass ?? ""}`}>{metric.value}</span>
                  </div>
                </TooltipTrigger>
                <TooltipContent className="max-w-64 whitespace-normal text-left" side="top" sideOffset={6}>
                  {metric.description}
                </TooltipContent>
              </Tooltip>
            );
          })}
        </div>
      </div>
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
    </div>
  );
}

function getSettledThroughputBuckets(throughput?: WorkSystemThroughput) {
  if (!throughput) {
    return [];
  }

  return throughput.buckets.slice(0, -1);
}

function createThroughputSeries(
  mode: ThroughputMode,
  buckets: WorkThroughputBucket[],
  bucketSeconds: number
) {
  const normalizedBucketSeconds = Math.max(1, bucketSeconds);
  if (mode === "execution") {
    return [
      {
        color: "#a78bfa",
        gradientId: "execution-throughput",
        label: "Avg execution ms",
        legendClass: "bg-violet-400",
        values: buckets.map((bucket) => Math.round(bucket.averageExecutionMilliseconds)),
      },
    ];
  }

  return [
      {
        color: "#38bdf8",
        gradientId: "started-throughput",
        label: "Started",
        legendClass: "bg-sky-400",
        strokeDasharray: "6 5",
        strokeWidth: "3",
        values: buckets.map((bucket) => bucket.started / normalizedBucketSeconds),
      },
      {
        color: "#34d399",
        gradientId: "completed-throughput",
        label: "Completed",
        legendClass: "bg-emerald-400",
        values: buckets.map((bucket) => bucket.completed / normalizedBucketSeconds),
      },
      {
        color: "#f87171",
        gradientId: "failed-throughput",
        label: "Failed",
        legendClass: "bg-red-400",
        values: buckets.map((bucket) => bucket.failed / normalizedBucketSeconds),
      },
      {
        color: "#fbbf24",
        gradientId: "canceled-throughput",
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
  chartBucketSeconds: number,
  chartWindowSeconds: number
): ThroughputMetric[] {
  const buckets = getSettledThroughputBuckets(chartThroughput);
  const bucketLabel = formatThroughputBucketLabel(chartBucketSeconds);
  const totalDescription = `Total settled iterations in the selected ${formatThroughputWindowLabel(chartWindowSeconds)} chart window. This includes completed, failed, and canceled iterations.`;
  if (!chartThroughput || buckets.length === 0) {
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
      ...(mode === "completion"
        ? [
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
          ]
        : []),
      {
        description: mode === "execution"
          ? `Average execution time across settled iterations in the selected ${formatThroughputWindowLabel(chartWindowSeconds)} chart window.`
          : totalDescription,
        id: mode === "execution" ? "execution-average" : "total",
        label: mode === "execution" ? "Avg" : "Total",
        value: "-",
        widthClass: mode === "execution" ? "min-w-20" : "min-w-20",
      },
      ...(mode === "completion"
        ? [
            {
              description: `Average execution time across settled iterations in the selected ${formatThroughputWindowLabel(chartWindowSeconds)} chart window.`,
              id: "window-average",
              label: "Avg",
              value: "-",
              widthClass: "min-w-20",
            },
          ]
        : []),
    ];
  }

  const windowAverageExecution = createWindowExecutionAverageMilliseconds(buckets);
  if (mode === "execution") {
    const current = buckets.at(-1)?.averageExecutionMilliseconds ?? 0;
    return [
      {
        description: `Average execution time in the latest ${bucketLabel} bucket.`,
        id: "execution-latest",
        label: "Latest",
        pulseClass: "bg-violet-400 shadow-[0_0_14px_rgba(167,139,250,0.75)]",
        value: formatMilliseconds(current),
        widthClass: "min-w-24",
      },
      {
        description: `Average execution time across settled iterations in the selected ${formatThroughputWindowLabel(chartWindowSeconds)} chart window.`,
        id: "execution-average",
        label: "Avg",
        value: formatMilliseconds(windowAverageExecution),
        widthClass: "min-w-20",
      },
    ];
  }

  const liveSummary = chartThroughput.liveSummary;
  const latestStartedRate = liveSummary.startedPerSecond;
  const latestCompletedRate = liveSummary.completedPerSecond;
  const latestFailedRate = liveSummary.failedPerSecond;
  const latestCanceledRate = liveSummary.canceledPerSecond;
  const settledTotal = buckets.reduce((sum, bucket) =>
    sum + bucket.completed + bucket.failed + bucket.canceled, 0);
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
      description: `Average execution time across settled iterations in the selected ${formatThroughputWindowLabel(chartWindowSeconds)} chart window.`,
      id: "window-average",
      label: "Avg",
      value: formatMilliseconds(windowAverageExecution),
      widthClass: "min-w-20",
    },
  ];
}

function createWindowExecutionAverageMilliseconds(buckets: WorkThroughputBucket[]) {
  const totals = buckets.reduce(
    (current, bucket) => {
      const settledCount = bucket.completed + bucket.failed + bucket.canceled;
      return {
        count: current.count + settledCount,
        milliseconds: current.milliseconds + bucket.averageExecutionMilliseconds * settledCount,
      };
    },
    { count: 0, milliseconds: 0 }
  );

  return totals.count === 0 ? 0 : totals.milliseconds / totals.count;
}

function createExecutionPressureMetric(summary: WorkThroughputLiveSummary): ThroughputMetric {
  const deltaPerSecond = summary.inFlightDeltaPerSecond;
  if (deltaPerSecond > 0) {
    return {
      description: `Live execution pressure is increasing. Over the last ${summary.windowSeconds} seconds, iterations started ${formatRate(deltaPerSecond)} per second faster than they settled.`,
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
      description: `Live execution pressure is decreasing. Over the last ${summary.windowSeconds} seconds, iterations settled ${formatRate(absoluteDeltaPerSecond)} per second faster than they started.`,
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
    description: `Live execution pressure is balanced. Over the last ${summary.windowSeconds} seconds, starts and settled outcomes matched.`,
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

function formatThroughputBucketLabel(seconds: number) {
  if (seconds === 60) {
    return "1-minute";
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
  if (!throughput || buckets.length === 0) {
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
    return `${(value / 1000).toFixed(value >= 10_000 ? 0 : 1)}s`;
  }

  return `${Math.round(value)}ms`;
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

function WorkerActionButton({
  action,
  icon: Icon,
  onAction,
}: {
  action: WorkAction;
  icon: typeof Play;
  onAction: (action: WorkAction) => Promise<void>;
}) {
  return (
    <Button onClick={() => void onAction(action)} size="sm" variant="outline">
      <Icon className="size-4" />
      {action}
    </Button>
  );
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

function MetadataItem({ label, value }: { label: string; value: string }) {
  return (
    <div className="min-w-0 rounded-md border bg-muted/20 p-3">
      <div className="text-muted-foreground text-xs">{label}</div>
      <div className="mt-1 break-words font-mono text-sm">{value}</div>
    </div>
  );
}

function formatTypedValue(value?: WorkTypedValue | null) {
  return value ? `${value.type}:${value.value}` : "-";
}

function ErrorPanel({ errors }: { errors: Array<string | undefined> }) {
  const error = [...new Set(errors.filter(Boolean))].join(" ");
  if (!error) {
    return null;
  }

  return (
    <Alert variant="destructive">
      <ShieldAlert className="size-4" />
      <AlertTitle>Connection issue</AlertTitle>
      <AlertDescription>{error}</AlertDescription>
    </Alert>
  );
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
  return scope ? { ...scope } : null;
}

function overviewScopesEqual(
  left: OverviewScope | null,
  right: OverviewScope | null
) {
  return (
    left?.category === right?.category &&
    left?.definitionName === right?.definitionName &&
    left?.includeSubcategories === right?.includeSubcategories
  );
}

function createOverviewComponentScope(scope: OverviewScope | null) {
  const category = normalizeCategoryFilter(scope?.category ?? "");
  const definitionName = scope?.definitionName?.trim() ?? "";
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

function createDefinitionCatalogLevelPath(category: string) {
  const query = new URLSearchParams({ level: "true" });
  const normalizedCategory = normalizeCategoryFilter(category);
  if (normalizedCategory) {
    query.set("category", normalizedCategory);
  }

  return `definitions?${query.toString()}`;
}

function splitCategoryPath(category?: string | null) {
  return (category?.trim() || "General")
    .split(":")
    .map((segment) => segment.trim())
    .filter(Boolean);
}

function splitCatalogPath(path: string) {
  return path.trim()
    ? path
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

function normalizeCategoryFilter(path: string) {
  return splitCatalogPath(path).join(":");
}

function formatOverviewScopeLabel(scope: OverviewScope | null) {
  if (!scope) {
    return "";
  }

  const categoryLabel = splitCatalogPath(scope.category ?? "").join(" / ");
  if (scope.definitionName) {
    return categoryLabel
      ? `${categoryLabel} / ${scope.definitionName}`
      : scope.definitionName;
  }

  return categoryLabel;
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

function workerMatchesCategory(
  item: { category?: string | null },
  category?: string,
  includeSubcategories = true
) {
  const scopeSegments = splitCatalogPath(category ?? "");
  if (scopeSegments.length === 0) {
    return true;
  }

  const categorySegments = splitCategoryPath(item.category);
  return includeSubcategories
    ? startsWithCategoryPath(categorySegments, scopeSegments)
    : categorySegments.length === scopeSegments.length &&
        startsWithCategoryPath(categorySegments, scopeSegments);
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

function getWorkerParentView(history: NavigationEntry[]): ServerView {
  const previous = history.at(-1);
  return previous && isServerView(previous.view) ? previous.view : "workers";
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

function formatWorkerDuration(worker: WorkerOverviewItem): DurationDisplay {
  if (worker.nextRunAt) {
    return { isWarning: false, text: "∞" };
  }

  if (worker.totalExecutionDuration) {
    return formatExecutionDuration(worker.totalExecutionDuration);
  }

  const createdAt = Date.parse(worker.createdAt);
  const updatedAt = Date.parse(worker.updatedAt);
  if (!Number.isFinite(createdAt) || !Number.isFinite(updatedAt)) {
    return { isWarning: false, text: "-" };
  }

  return formatDurationSeconds(Math.max(0, (updatedAt - createdAt) / 1000));
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

function getSystemLifecycleAction(state?: string | null): "start" | "stop" | null {
  const normalized = state?.toLowerCase();
  if (normalized === "created" || normalized === "stopped") {
    return "start";
  }
  if (normalized === "started") {
    return "stop";
  }
  return null;
}

function getSystemLifecycleActionLabel(
  state: string | null | undefined,
  system: WorkableSystemConnection,
  host: WorkableHostConnection
) {
  const action = getSystemLifecycleAction(state);
  if (action === "start") {
    return `Start the workable system '${system.name}' at ${host.apiUrl}`;
  }
  if (action === "stop") {
    return `Stop the workable system '${system.name}' at ${host.apiUrl}`;
  }
  return "Lifecycle action unavailable";
}

function systemStateDotClass(state?: string | null) {
  switch (state) {
    case "Started":
      return "bg-emerald-400";
    case "Starting":
    case "Stopping":
      return "bg-amber-300";
    case "Stopped":
    case "Created":
      return "bg-zinc-500";
    default:
      return "bg-muted-foreground/45";
  }
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

function createDefaultQueueRequest(definition: WorkDefinition | null): QueueWorkRequest {
  return {
    completion: "ReturnAfterAccepted",
    options: createEffectiveConfigurationOptions(definition),
  };
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
  idempotency: {
    isEnabled: false,
    conflictPolicy: "RejectDuplicates",
  },
  recurrence: {
    isEnabled: false,
    interval: "00:00:00",
    continueAfterFailure: true,
    circuitBreakerFailureThreshold: 3,
    maximumSuccessfulIterations: 25,
    maximumFailedIterations: 5,
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
    purgeInterval: "00:05:00",
  },
  concurrency: {
    isEnabled: false,
    maximumCapacity: 0,
    scope: "PerDefinition",
    blockingMode: "WhileExecutingPausedOrFailed",
    limitReachedBehavior: "Ignore",
    overrideBehavior: "Flexible",
  },
};

function useWorkableResource<T>(
  connection: WorkableConnection,
  path: string | null,
  refreshToken: number
): Loadable<T> {
  const [state, setState] = useState<Loadable<T>>({ loading: !!path });

  useEffect(() => {
    if (!path) {
      queueMicrotask(() => setState({ loading: false }));
      return;
    }

    let canceled = false;
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

    workableFetch<T>(connection, path)
      .then((data) => {
        if (!canceled) {
          setState({ data, loading: false, refreshing: false });
        }
      })
      .catch((error) => {
        if (!canceled) {
          setState((current) => ({
            data: current.data,
            error: error instanceof Error ? error.message : "Request failed.",
            loading: false,
            refreshing: false,
          }));
        }
      });

    return () => {
      canceled = true;
    };
  }, [connection, path, refreshToken]);

  return state;
}

function useWorkablePostResource<T>(
  connection: WorkableConnection,
  path: string | null,
  body: unknown,
  refreshToken: number
): Loadable<T> {
  const [state, setState] = useState<Loadable<T>>({ loading: !!path });
  const bodyKey = JSON.stringify(body);

  useEffect(() => {
    if (!path) {
      queueMicrotask(() => setState({ loading: false }));
      return;
    }

    let canceled = false;
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

    workableFetch<T>(connection, path, {
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
          setState((current) => ({
            data: current.data,
            error: error instanceof Error ? error.message : "Request failed.",
            loading: false,
            refreshing: false,
          }));
        }
      });

    return () => {
      canceled = true;
    };
  }, [bodyKey, connection, path, refreshToken]);

  return state;
}

function useViewportQueryTake() {
  const queryTableRef = useRef<HTMLDivElement>(null);
  const [queryTake, setQueryTake] = useState(defaultQueryTake);

  useLayoutEffect(() => {
    if (typeof window === "undefined") {
      return;
    }

    let frameId = 0;
    const updateTake = () => {
      frameId = 0;
      const root = queryTableRef.current;
      const tableTop = root?.getBoundingClientRect().top ?? 0;
      const scrollViewport = root?.closest("[data-slot='scroll-area-viewport']");
      const viewportBottom =
        scrollViewport?.getBoundingClientRect().bottom ?? window.innerHeight;
      const pageContainer = root?.closest("[data-view-content]");
      const pageBottomPadding = pageContainer
        ? Number.parseFloat(window.getComputedStyle(pageContainer).paddingBottom) || 0
        : 0;
      const headerHeight =
        root?.querySelector("thead")?.getBoundingClientRect().height ??
        queryTableHeaderHeight;
      const tableRows = root
        ? [...root.querySelectorAll("tbody tr")].slice(0, 5)
        : [];
      const measuredRowHeight = tableRows.length > 0
        ? tableRows.reduce(
            (total, row) => total + row.getBoundingClientRect().height,
            0
          ) / tableRows.length
        : queryTableRowHeight;
      const availableHeight = Math.max(
        0,
        viewportBottom - tableTop - pageBottomPadding - queryViewportSafetyPadding
      );
      const rows = Math.floor(
        (availableHeight - headerHeight) / Math.max(1, measuredRowHeight)
      );
      const nextTake = Math.min(maxQueryTake, Math.max(minQueryTake, rows));

      setQueryTake((current) => (current === nextTake ? current : nextTake));
    };

    const scheduleUpdate = () => {
      if (frameId) {
        window.cancelAnimationFrame(frameId);
      }
      frameId = window.requestAnimationFrame(updateTake);
    };

    scheduleUpdate();
    window.addEventListener("resize", scheduleUpdate);

    const observer = typeof ResizeObserver !== "undefined"
      ? new ResizeObserver(scheduleUpdate)
      : null;
    if (queryTableRef.current) {
      observer?.observe(queryTableRef.current);
    }
    observer?.observe(document.body);

    return () => {
      window.removeEventListener("resize", scheduleUpdate);
      if (frameId) {
        window.cancelAnimationFrame(frameId);
      }
      observer?.disconnect();
    };
  }, []);

  return { queryTake, queryTableRef };
}

function useWorkerQuery(
  connection: WorkableConnection,
  query: {
    category?: string;
    definitionName?: string;
    includeSubcategories?: boolean;
    keyType?: string;
    states?: WorkerState[];
  },
  refreshToken: number,
  take: number,
  skip: number
): Loadable<WorkerQueryResult> {
  const [state, setState] = useState<Loadable<WorkerQueryResult>>({
    loading: true,
  });
  const key = JSON.stringify(query);
  const boundedTake = Math.min(maxQueryTake, Math.max(minQueryTake, Math.trunc(take)));

  useEffect(() => {
    let canceled = false;
    const parsedQuery = JSON.parse(key) as {
      category?: string;
      definitionName?: string;
      includeSubcategories?: boolean;
      keyType?: string;
      states?: WorkerState[];
    };

    const load = async () => {
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

      try {
        const data = parsedQuery.keyType !== undefined
          ? await queryWorkersByKeyType(connection, {
              ...parsedQuery,
              keyType: parsedQuery.keyType,
              skip,
              take: boundedTake,
            })
          : await workableFetch<WorkerQueryResult>(connection, "workers/query", {
              method: "POST",
              body: JSON.stringify({
                category: parsedQuery.category,
                definitionName: parsedQuery.definitionName,
                includeSubcategories: parsedQuery.includeSubcategories,
                states: parsedQuery.states,
                skip,
                take: boundedTake,
              }),
            });

        if (!canceled) {
          setState({ data, loading: false, refreshing: false });
        }
      } catch (error) {
        if (!canceled) {
          const detail = error instanceof Error ? error.message : "Request failed.";
          setState((current) => ({
            data: current.data,
            error: `Worker query failed. ${detail}`,
            loading: false,
            refreshing: false,
          }));
        }
      }
    };

    void load();

    return () => {
      canceled = true;
    };
  }, [boundedTake, connection, key, refreshToken, skip]);

  return state;
}

function useIterationQuery(
  connection: WorkableConnection,
  query: {
    category?: string;
    definitionName?: string;
    keyType?: string;
    statuses?: WorkCompletionStatus[];
  },
  refreshToken: number,
  take: number,
  skip: number
): Loadable<WorkerIterationQueryResult> {
  const [state, setState] = useState<Loadable<WorkerIterationQueryResult>>({
    loading: true,
  });
  const key = JSON.stringify(query);
  const boundedTake = Math.min(maxQueryTake, Math.max(minQueryTake, Math.trunc(take)));

  useEffect(() => {
    let canceled = false;
    const parsedQuery = JSON.parse(key) as {
      category?: string;
      definitionName?: string;
      keyType?: string;
      statuses?: WorkCompletionStatus[];
    };

    const load = async () => {
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

      try {
        const data = parsedQuery.keyType !== undefined
          ? await queryIterationsByKeyType(connection, {
              ...parsedQuery,
              keyType: parsedQuery.keyType,
              skip,
              take: boundedTake,
            })
          : await workableFetch<WorkerIterationQueryResult>(connection, "iterations/query", {
              method: "POST",
              body: JSON.stringify({
                category: parsedQuery.category,
                definitionName: parsedQuery.definitionName,
                statuses: parsedQuery.statuses,
                sort: "CompletedAt",
                direction: "Descending",
                skip,
                take: boundedTake,
              }),
            });

        if (!canceled) {
          setState({ data, loading: false, refreshing: false });
        }
      } catch (error) {
        if (!canceled) {
          const detail = error instanceof Error ? error.message : "Request failed.";
          setState((current) => ({
            data: current.data,
            error: `Iteration query failed. ${detail}`,
            loading: false,
            refreshing: false,
          }));
        }
      }
    };

    void load();

    return () => {
      canceled = true;
    };
  }, [boundedTake, connection, key, refreshToken, skip]);

  return state;
}

async function queryWorkersByKeyType(
  connection: WorkableConnection,
  query: {
    category?: string;
    definitionName?: string;
    includeSubcategories?: boolean;
    keyType: string;
    states?: WorkerState[];
    skip: number;
    take: number;
  }
): Promise<WorkerQueryResult> {
  const result = await workableFetch<WorkKeyTypeQueryResult>(
    connection,
    "work-keys/types/query",
    {
      method: "POST",
      body: JSON.stringify({
        type: query.keyType,
        states: query.states,
        skip: 0,
        take: query.take,
      }),
    }
  );
  const workersById = new Map<string, WorkerOverviewItem>();
  for (const keyType of result.types) {
    for (const worker of keyType.workers) {
      if (
        query.definitionName &&
        !worker.definitionName.toLowerCase().includes(query.definitionName.toLowerCase())
      ) {
        continue;
      }
      if (!workerMatchesCategory(worker, query.category, query.includeSubcategories ?? true)) {
        continue;
      }

      workersById.set(worker.id.value, worker);
    }
  }
  const workers = [...workersById.values()].sort(
    (left, right) =>
      new Date(right.updatedAt).getTime() - new Date(left.updatedAt).getTime()
  );

  return {
    workers: workers.slice(query.skip, query.skip + query.take),
    totalCount: workers.length,
    skip: query.skip,
    take: query.take,
  };
}

async function queryIterationsByKeyType(
  connection: WorkableConnection,
  query: {
    category?: string;
    definitionName?: string;
    keyType: string;
    statuses?: WorkCompletionStatus[];
    skip: number;
    take: number;
  }
): Promise<WorkerIterationQueryResult> {
  const result = await workableFetch<WorkIterationKeyTypeQueryResult>(
    connection,
    "work-iteration-keys/types/query",
    {
      method: "POST",
      body: JSON.stringify({
        type: query.keyType,
        statuses: query.statuses,
        skip: 0,
        take: query.take,
      }),
    }
  );
  const iterationsById = new Map<string, WorkerIterationOverviewItem>();
  for (const keyType of result.types) {
    for (const iteration of keyType.iterations) {
      if (
        query.definitionName &&
        !iteration.definitionName.toLowerCase().includes(query.definitionName.toLowerCase())
      ) {
        continue;
      }
      if (!workerMatchesCategory(iteration, query.category, true)) {
        continue;
      }

      iterationsById.set(`${iteration.workerId.value}:${iteration.sequence}`, iteration);
    }
  }
  const iterations = [...iterationsById.values()].sort(
    (left, right) =>
      new Date(right.completedAt).getTime() - new Date(left.completedAt).getTime()
  );

  return {
    iterations: iterations.slice(query.skip, query.skip + query.take),
    totalCount: iterations.length,
    skip: query.skip,
    take: query.take,
  };
}
