"use client";

import Image from "next/image";
import {
  Activity,
  ArrowLeft,
  Ban,
  Boxes,
  Braces,
  CheckCircle2,
  ChevronRight,
  CircleAlert,
  CircleDot,
  Clock3,
  ListFilter,
  Hourglass,
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
  ShieldAlert,
  Square,
  Trash2,
  Workflow,
} from "lucide-react";
import type { Dispatch, SetStateAction } from "react";
import { useCallback, useEffect, useMemo, useRef, useState } from "react";
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
  DropdownMenuCheckboxItem,
  DropdownMenuContent,
  DropdownMenuItem,
  DropdownMenuSeparator,
  DropdownMenuTrigger,
} from "@/components/ui/dropdown-menu";
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
  type WorkCompletionStatus,
  type WorkIterationKeyTypeQueryResult,
  type WorkIterationKeyTypeFacet,
  type WorkKeyTypeQueryResult,
  type WorkSystemFailedWorkersOverview,
  type WorkSystemLifecycleResult,
  type WorkSystemOverview,
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

type View = "overview" | "definitions" | "workers" | "iterations" | "worker";
type ServerView = Exclude<View, "worker">;

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
  view: ServerView;
};

type PendingDelete =
  | { kind: "host"; host: WorkableHostConnection }
  | { kind: "system"; host: WorkableHostConnection; system: WorkableSystemConnection };

type PendingStopSystem = {
  system: WorkableSystemConnection;
};

type NavigationEntry = {
  iterationKeyTypeFilter: string;
  iterationStatusFilter: WorkCompletionStatus[];
  keyTypeFilter: string;
  systemId: string;
  view: View;
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
  workers: 0,
  iterations: 0,
  worker: 0,
};

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
  const [selectedWorkerId, setSelectedWorkerId] = useState<string | null>(null);
  const [keyTypeFilter, setKeyTypeFilter] = useState("");
  const [workerStateFilter, setWorkerStateFilter] = useState<WorkerState[]>([]);
  const [iterationKeyTypeFilter, setIterationKeyTypeFilter] = useState("");
  const [iterationStatusFilter, setIterationStatusFilter] = useState<WorkCompletionStatus[]>([]);
  const [navigationHistory, setNavigationHistory] = useState<NavigationEntry[]>([]);
  const readyViews = useRef<Set<string>>(new Set());
  const activeLocation = findSystemLocation(consoleState, consoleState.activeSystemId);
  const activeHost = activeLocation?.host;
  const activeSystem = activeLocation?.system;
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
      iterationKeyTypeFilter,
      iterationStatusFilter,
      keyTypeFilter,
      systemId: consoleState.activeSystemId,
      view,
      workerId: selectedWorkerId,
      workerStateFilter,
    }),
    [
      consoleState.activeSystemId,
      iterationKeyTypeFilter,
      iterationStatusFilter,
      keyTypeFilter,
      selectedWorkerId,
      view,
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

  const openWorker = (workerId: string, trackHistory = true) => {
    if (trackHistory) {
      pushCurrentNavigation();
    }
    setSelectedWorkerId(workerId);
    setVisibleView("worker");
    setPendingView(null);
    setView("worker");
    refreshView("worker");
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
        workerId: null,
        iterationKeyTypeFilter,
        iterationStatusFilter,
        keyTypeFilter,
        workerStateFilter,
      })
    ) {
      pushCurrentNavigation();
    }

    if (nextView !== "worker") {
      setSelectedWorkerId(null);
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
    setKeyTypeFilter("");
    setWorkerStateFilter(states);
    openView("workers", activeSystem?.id ?? "", false);
  };

  const openIterations = () => {
    pushCurrentNavigation();
    setIterationKeyTypeFilter("");
    setIterationStatusFilter([]);
    openView("iterations", activeSystem?.id ?? "", false);
  };

  const openIterationsByKeyType = (keyType: string) => {
    pushCurrentNavigation();
    setIterationKeyTypeFilter(keyType);
    setIterationStatusFilter([]);
    openView("iterations", activeSystem?.id ?? "", false);
  };

  const openIterationsFiltered = (statuses: WorkCompletionStatus[]) => {
    pushCurrentNavigation();
    setIterationKeyTypeFilter("");
    setIterationStatusFilter(statuses);
    openView("iterations", activeSystem?.id ?? "", false);
  };

  const openMenuView = (nextView: View, systemId: string) => {
    if (nextView === "workers") {
      setKeyTypeFilter("");
      setWorkerStateFilter([]);
    }
    if (nextView === "iterations") {
      setIterationKeyTypeFilter("");
      setIterationStatusFilter([]);
    }

    openView(nextView, systemId);
  };

  const goBack = () => {
    const previous = navigationHistory.at(-1);
    if (!previous) {
      return;
    }

    setNavigationHistory((current) => current.slice(0, -1));
    setIterationKeyTypeFilter(previous.iterationKeyTypeFilter);
    setIterationStatusFilter(previous.iterationStatusFilter);
    setKeyTypeFilter(previous.keyTypeFilter);
    setWorkerStateFilter(previous.workerStateFilter);
    if (previous.view === "worker" && previous.workerId) {
      setConsoleState((current) => ({
        ...current,
        activeSystemId: previous.systemId,
        view: "workers",
      }));
      setSelectedWorkerId(previous.workerId);
      setVisibleView("worker");
      setPendingView(null);
      setView("worker");
      refreshView("worker");
      return;
    }

    openView(previous.view, previous.systemId, false);
  };

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
            <SidebarGroupLabel>Servers</SidebarGroupLabel>
            <SidebarGroupAction
              onClick={() => setServerDialog({ mode: "add" })}
              title="Add server"
            >
              <Plus />
              <span className="sr-only">Add server</span>
            </SidebarGroupAction>
            <SidebarGroupContent>
              <ServerTree
                activeSystemId={activeSystem?.id ?? ""}
                expandedHostIds={consoleState.expandedHostIds}
                expandedSystemIds={consoleState.expandedSystemIds}
                hosts={consoleState.hosts}
                lifecycleActionSystemId={lifecycleActionSystemId}
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
        <header className="flex h-14 shrink-0 items-center gap-3 border-b px-4">
          <SidebarTrigger />
          <Separator className="h-6" orientation="vertical" />
          <Button
            aria-label="Back"
            disabled={navigationHistory.length === 0}
            onClick={goBack}
            size="icon-sm"
            variant="ghost"
          >
            <ArrowLeft className="size-4" />
          </Button>
          <div className="min-w-0 flex-1">
            <ConsoleBreadcrumb
              host={activeHost}
              onOpenView={openView}
              system={activeSystem}
              view={view}
              workerParentView={getWorkerParentView(navigationHistory)}
              workerId={selectedWorkerId ?? undefined}
            />
            <div className="truncate text-muted-foreground text-xs">
              {activeHost ? formatHostSubtitle(activeHost) : "No Workable system selected"}
            </div>
          </div>
          <Button
            onClick={() => refreshView(view)}
            size="sm"
            variant="outline"
          >
            <RefreshCw className="size-4" />
            Refresh
          </Button>
        </header>
        <main className="min-h-0 flex-1 overflow-hidden bg-background">
          <ScrollArea className="h-[calc(100vh-3.5rem)]">
            <div className="relative mx-auto w-full max-w-7xl p-4 md:p-6">
              {!connection && (
                <EmptyServerState onAddServer={() => setServerDialog({ mode: "add" })} />
              )}
              {connection && (
                <>
                  <ErrorPanel errors={[lifecycleError]} />
                  {mountedViews.has("overview") && (
                    <div className={visibleView === "overview" ? undefined : "hidden"}>
                      <OverviewView
                        connection={connection}
                        onConnectionError={handleOverviewConnectionError}
                        onStateLoaded={handleOverviewStateLoaded}
                        onOpenCatalog={() => openView("definitions")}
                        onOpenIterations={openIterations}
                        onOpenKeyType={openIterationsByKeyType}
                        onReady={() => markViewReady("overview")}
                        onViewIterationsByStatus={openIterationsFiltered}
                        onViewWorkersByState={openWorkersFiltered}
                        refreshToken={refreshTokens.overview}
                        onOpenWorker={openWorker}
                      />
                    </div>
                  )}
                  {mountedViews.has("definitions") && (
                    <div className={visibleView === "definitions" ? undefined : "hidden"}>
                      <DefinitionsView
                        connection={connection}
                        onOpenWorker={openWorker}
                        onReady={() => markViewReady("definitions")}
                        refreshToken={refreshTokens.definitions}
                      />
                    </div>
                  )}
                  {mountedViews.has("workers") && (
                    <div className={visibleView === "workers" ? undefined : "hidden"}>
                      <WorkersView
                        connection={connection}
                        onOpenWorker={openWorker}
                        onReady={() => markViewReady("workers")}
                        keyTypeFilter={keyTypeFilter}
                        onKeyTypeFilterChange={setKeyTypeFilter}
                        stateFilter={workerStateFilter}
                        onStateFilterChange={setWorkerStateFilter}
                        refreshToken={refreshTokens.workers}
                      />
                    </div>
                  )}
                  {mountedViews.has("iterations") && (
                    <div className={visibleView === "iterations" ? undefined : "hidden"}>
                      <IterationsView
                        connection={connection}
                        keyTypeFilter={iterationKeyTypeFilter}
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
                <WorkerConsoleView
                  backLabel={`Back to ${navTitle(getWorkerParentView(navigationHistory))}`}
                  connection={connection}
                  onBack={() => openView(getWorkerParentView(navigationHistory))}
                  refreshToken={refreshTokens.worker}
                  workerId={selectedWorkerId}
                />
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

function ConsoleBreadcrumb({
  host,
  onOpenView,
  system,
  view,
  workerParentView = "workers",
  workerId,
}: {
  host?: WorkableHostConnection;
  onOpenView: (view: View, systemId?: string) => void;
  system?: WorkableSystemConnection;
  view: View;
  workerParentView?: ServerView;
  workerId?: string;
}) {
  if (!host || !system) {
    return (
      <Breadcrumb>
        <BreadcrumbList className="flex-nowrap overflow-hidden">
          <BreadcrumbItem>
            <BreadcrumbPage className="truncate">Workable</BreadcrumbPage>
          </BreadcrumbItem>
        </BreadcrumbList>
      </Breadcrumb>
    );
  }

  const openSystemOverview = () => onOpenView("overview", system.id);
  const workerLabel = workerId ? shortId(workerId) : "Worker";
  const workerParentLabel = navTitle(workerParentView);

  return (
    <Breadcrumb>
      <BreadcrumbList className="flex-nowrap overflow-hidden">
        <BreadcrumbItem className="min-w-0">
          <BreadcrumbLink asChild>
            <button
              className="max-w-40 cursor-pointer truncate text-left"
              onClick={openSystemOverview}
              type="button"
            >
              {host.name}
            </button>
          </BreadcrumbLink>
        </BreadcrumbItem>
        <BreadcrumbSeparator />
        <BreadcrumbItem className="min-w-0">
          <BreadcrumbLink asChild>
            <button
              className="max-w-48 cursor-pointer truncate text-left"
              onClick={openSystemOverview}
              type="button"
            >
              {system.name}
            </button>
          </BreadcrumbLink>
        </BreadcrumbItem>
        <BreadcrumbSeparator />
        {view === "worker" ? (
          <>
            <BreadcrumbItem>
              <BreadcrumbLink asChild>
                <button
                  className="cursor-pointer"
                  onClick={() => onOpenView(workerParentView, system.id)}
                  type="button"
                >
                  {workerParentLabel}
                </button>
              </BreadcrumbLink>
            </BreadcrumbItem>
            <BreadcrumbSeparator />
            <BreadcrumbItem className="min-w-0">
              <BreadcrumbPage className="truncate font-mono">{workerLabel}</BreadcrumbPage>
            </BreadcrumbItem>
          </>
        ) : (
          <BreadcrumbItem className="min-w-0">
            <BreadcrumbPage className="truncate">{navTitle(view)}</BreadcrumbPage>
          </BreadcrumbItem>
        )}
      </BreadcrumbList>
    </Breadcrumb>
  );
}

function ServerTree({
  activeSystemId,
  expandedHostIds,
  expandedSystemIds,
  hosts,
  lifecycleActionSystemId,
  onAddServer,
  onEditHost,
  onLifecycleAction,
  onOpenView,
  onRemoveHost,
  onRemoveSystem,
  onToggleHost,
  onToggleSystem,
  view,
}: {
  activeSystemId: string;
  expandedHostIds: string[];
  expandedSystemIds: string[];
  hosts: WorkableHostConnection[];
  lifecycleActionSystemId: string | null;
  onAddServer: () => void;
  onEditHost: (host: WorkableHostConnection) => void;
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
  return (
    <SidebarMenu>
      {hosts.map((host) => {
        const isHostExpanded = expandedHostIds.includes(host.id);
        const isActiveHost = host.systems.some((system) => system.id === activeSystemId);

        return (
          <SidebarMenuItem key={host.id}>
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
            <SidebarMenuAction
              className="right-7"
              onClick={(event) => {
                event.stopPropagation();
                onEditHost(host);
              }}
              showOnHover
              title="Edit host"
            >
              <Pencil />
              <span className="sr-only">Edit host</span>
            </SidebarMenuAction>
            <SidebarMenuAction
              onClick={(event) => {
                event.stopPropagation();
                onRemoveHost(host);
              }}
              showOnHover
              title="Remove host"
            >
              <Trash2 />
              <span className="sr-only">Remove host</span>
            </SidebarMenuAction>
            {isHostExpanded && (
              <SidebarMenuSub>
                {host.systems.map((system) => {
                  const isActiveSystem = system.id === activeSystemId;
                  const isSystemExpanded = expandedSystemIds.includes(system.id);
                  const lifecycleAction = getSystemLifecycleAction(system.state);

                  return (
                    <SidebarMenuSubItem key={system.id}>
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
                          <span>{system.name}</span>
                          <SystemStateBadge state={system.state} />
                          {system.realtimeEnabled && (
                            <Radio className="text-emerald-300" />
                          )}
                        </button>
                      </SidebarMenuSubButton>
                      {(lifecycleAction || lifecycleActionSystemId === system.id) && (
                        <button
                          className="absolute right-7 top-1 flex size-5 items-center justify-center rounded-md text-sidebar-foreground opacity-0 transition-opacity hover:bg-sidebar-accent hover:text-sidebar-accent-foreground group-focus-within/menu-sub-item:opacity-100 group-hover/menu-sub-item:opacity-100 disabled:cursor-wait disabled:opacity-60"
                          disabled={lifecycleActionSystemId === system.id || !lifecycleAction}
                          onClick={(event) => {
                            event.stopPropagation();
                            if (lifecycleAction) {
                              onLifecycleAction(system, lifecycleAction);
                            }
                          }}
                          title={getSystemLifecycleActionLabel(system.state)}
                          type="button"
                        >
                          {lifecycleActionSystemId === system.id ? (
                            <Loader2 className="size-3.5 animate-spin" />
                          ) : lifecycleAction === "stop" ? (
                            <Square className="size-3.5" />
                          ) : (
                            <Play className="size-3.5" />
                          )}
                          <span className="sr-only">{getSystemLifecycleActionLabel(system.state)}</span>
                        </button>
                      )}
                      <button
                        className="absolute right-1 top-1 flex size-5 items-center justify-center rounded-md text-sidebar-foreground opacity-0 transition-opacity hover:bg-sidebar-accent hover:text-sidebar-accent-foreground group-focus-within/menu-sub-item:opacity-100 group-hover/menu-sub-item:opacity-100"
                        onClick={(event) => {
                          event.stopPropagation();
                          onRemoveSystem(host, system);
                        }}
                        title="Remove system"
                        type="button"
                      >
                        <Trash2 className="size-3.5" />
                        <span className="sr-only">Remove system</span>
                      </button>
                      {isSystemExpanded && (
                        <SidebarMenuSub className="mx-2">
                          {navItems.map((item) => (
                            <SidebarMenuSubItem key={`${system.id}:${item.id}`}>
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
                            </SidebarMenuSubItem>
                          ))}
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
    <Tooltip>
      <TooltipTrigger asChild>
        <span className={`ml-auto size-2 rounded-full ${systemStateDotClass(state)}`} />
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
      const systems = result.systems ?? [];
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
  }, [apiUrl]);

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
      systems: selected.map((system) => createStoredSystem(hostId, system, realtimeSystemIds)),
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
    <Tooltip>
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

function OverviewView({
  connection,
  onConnectionError,
  onOpenCatalog,
  onOpenIterations,
  onOpenKeyType,
  onReady,
  onOpenWorker,
  onStateLoaded,
  onViewIterationsByStatus,
  onViewWorkersByState,
  refreshToken,
}: {
  connection: WorkableConnection;
  onConnectionError: () => void;
  onOpenCatalog: () => void;
  onOpenIterations: () => void;
  onOpenKeyType: (keyType: string) => void;
  onReady: () => void;
  onOpenWorker: (workerId: string) => void;
  onStateLoaded: (state: string) => void;
  onViewIterationsByStatus: (statuses: WorkCompletionStatus[]) => void;
  onViewWorkersByState: (states: WorkerState[]) => void;
  refreshToken: number;
}) {
  const [actionError, setActionError] = useState<string>();
  const [actionWorkerId, setActionWorkerId] = useState<string | null>(null);
  const [failedWorkersSlice, setFailedWorkersSlice] = useState<{
    data: WorkSystemFailedWorkersOverview;
    key: string;
  } | null>(null);
  const failedWorkersKey = `${connection.apiUrl}:${connection.systemName ?? ""}:${refreshToken}`;
  const overview = useWorkableResource<WorkSystemOverview>(
    connection,
    "overview",
    refreshToken
  );
  const isReady = !overview.loading;
  const data = overview.data;
  const activeFailedWorkersSlice = failedWorkersSlice?.key === failedWorkersKey
    ? failedWorkersSlice.data
    : undefined;
  const activeWorkerCount = activeFailedWorkersSlice?.activeWorkerCount ?? data?.activeWorkerCount ?? 0;
  const finalWorkerCount = activeFailedWorkersSlice?.finalWorkerCount ?? data?.finalWorkerCount ?? 0;
  const failedWorkerCount = activeFailedWorkersSlice?.failedWorkerCount ?? data?.failedWorkerCount ?? 0;
  const workerCountByState = activeFailedWorkersSlice?.workerCountByState ?? data?.workerCountByState ?? {};
  const failedWorkers = activeFailedWorkersSlice?.failedWorkers ?? data?.failedWorkers ?? [];

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
      const failedWorkersOverview = await workableFetch<WorkSystemFailedWorkersOverview>(
        connection,
        "overview/failed-workers"
      );
      setFailedWorkersSlice({ data: failedWorkersOverview, key: failedWorkersKey });
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
    if (data?.systemState) {
      onStateLoaded(data.systemState);
    }
  }, [data?.systemState, onStateLoaded]);

  useEffect(() => {
    if (overview.error) {
      onConnectionError();
    }
  }, [overview.error, onConnectionError]);

  return (
    <div className="space-y-6">
      <ErrorPanel errors={[overview.error, actionError]} />
      <section className="space-y-4 rounded-lg border bg-card p-4">
        <WorkerStateStrip
          counts={workerCountByState}
          loading={overview.loading}
          onSelectState={(state) => onViewWorkersByState([state])}
        />
        <div className="grid gap-3 md:grid-cols-2 xl:grid-cols-4">
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
            value={data?.definitionCount ?? 0}
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
      </section>
      <OverviewWorkerList
        emptyText="No failed workers."
        loading={overview.loading && failedWorkers.length === 0}
        onWorkerAction={executeWorkerAction}
        onOpenWorker={onOpenWorker}
        onViewState={() => onViewWorkersByState(failedWorkerStates)}
        pendingActionWorkerId={actionWorkerId}
        state="Failed"
        title="Recent Failed Workers"
        workers={failedWorkers}
      />
      <section className="space-y-4 rounded-lg border bg-card p-4">
        <IterationStatusStrip
          counts={data?.iterationCountByStatus ?? {}}
          loading={overview.loading}
          onSelectStatus={(status) => onViewIterationsByStatus([status])}
        />
      <TopKeyTypePanel
        keys={data?.commonKeyTypes ?? []}
        loading={overview.loading}
        onShowMore={onOpenIterations}
        onSelectKeyType={onOpenKeyType}
      />
      </section>
      <div className="grid gap-4 xl:grid-cols-2">
        <OverviewIterationList
          emptyText="No failed iterations."
          loading={overview.loading}
          onOpenWorker={onOpenWorker}
          onViewState={() => onViewIterationsByStatus(["Failed"])}
          status="Failed"
          title="Recent Failed Iterations"
          iterations={data?.failedIterations ?? []}
        />
        <OverviewIterationList
          emptyText="No completed iterations."
          loading={overview.loading}
          onOpenWorker={onOpenWorker}
          onViewState={() => onViewIterationsByStatus(["Completed"])}
          status="Completed"
          title="Recent Completed Iterations"
          iterations={data?.completedIterations ?? []}
        />
      </div>
    </div>
  );
}

function OverviewWorkerList({
  emptyText,
  loading,
  onOpenWorker,
  onViewState,
  onWorkerAction,
  pendingActionWorkerId,
  state,
  title,
  workers,
}: {
  emptyText: string;
  loading: boolean;
  onOpenWorker: (workerId: string) => void;
  onViewState: () => void;
  onWorkerAction: (worker: WorkerOverviewItem, action: WorkAction) => Promise<void>;
  pendingActionWorkerId: string | null;
  state: WorkerState;
  title: string;
  workers: WorkerOverviewItem[];
}) {
  return (
    <Card>
      <CardHeader>
        <div>
          <button
            className="-mx-2 -my-1 inline-flex cursor-pointer items-center gap-1 rounded-md border border-transparent px-2 py-1 font-semibold text-base transition-colors hover:border-primary/60 hover:bg-accent/40 hover:text-primary focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring focus-visible:ring-offset-2 focus-visible:ring-offset-background"
            onClick={onViewState}
            type="button"
          >
            {title}
            <Badge className={`justify-center ${stateTone(state)}`} variant="outline">
              {state}
            </Badge>
            <ChevronRight className="size-4" />
          </button>
        </div>
      </CardHeader>
      <CardContent>
        <WorkerTable
          emptyText={emptyText}
          hideState
          loading={loading}
          onAction={onWorkerAction}
          onSelect={(worker) => onOpenWorker(worker.id.value)}
          pendingActionWorkerId={pendingActionWorkerId}
          workers={workers}
        />
      </CardContent>
    </Card>
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
      <StatusStripSection
        description="Workers grouped by current state, with summary links for active, final, failed, and catalog counts."
        title="Workers"
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
      title="Workers"
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
}: {
  counts: Partial<Record<WorkCompletionStatus, number>>;
  loading: boolean;
  onSelectStatus: (status: WorkCompletionStatus) => void;
}) {
  if (loading) {
    return (
      <StatusStripSection
        description="Worker iterations grouped by status, with common relationship types for quick filtering."
        title="Worker iterations"
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
      title="Worker iterations"
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
      <div className="flex items-center gap-1.5">
        <h2 className="font-medium text-sm">{title}</h2>
        <Tooltip>
          <TooltipTrigger asChild>
            <button
              aria-label={`${title}: ${description}`}
              className="inline-flex size-4 items-center justify-center rounded-full text-muted-foreground transition-colors hover:text-foreground focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring focus-visible:ring-offset-2 focus-visible:ring-offset-background"
              type="button"
            >
              <Info className="size-3.5" />
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
            <Tooltip key={key.type}>
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
            <Tooltip>
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
  onOpenWorker,
  onViewState,
  status,
  title,
  iterations,
}: {
  emptyText: string;
  loading: boolean;
  onOpenWorker: (workerId: string) => void;
  onViewState: () => void;
  status: WorkCompletionStatus;
  title: string;
  iterations: WorkerIterationOverviewItem[];
}) {
  return (
    <Card>
      <CardHeader>
        <div>
          <button
            className="-mx-2 -my-1 inline-flex cursor-pointer items-center gap-1 rounded-md border border-transparent px-2 py-1 font-semibold text-base transition-colors hover:border-primary/60 hover:bg-accent/40 hover:text-primary focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring focus-visible:ring-offset-2 focus-visible:ring-offset-background"
            onClick={onViewState}
            type="button"
          >
            {title}
            <Badge className={`justify-center ${completionTone(status)}`} variant="outline">
              {status}
            </Badge>
            <ChevronRight className="size-4" />
          </button>
        </div>
      </CardHeader>
      <CardContent>
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
      </CardContent>
    </Card>
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
  connection,
  onOpenWorker,
  onReady,
  refreshToken,
}: {
  connection: WorkableConnection;
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
  const isReady = !definitions.loading;

  useEffect(() => {
    if (isReady) {
      onReady();
    }
  }, [isReady, onReady]);

  const filtered = useMemo(() => {
    const query = search.trim().toLowerCase();
    if (!query) {
      return definitions.data ?? [];
    }
    return (definitions.data ?? []).filter((definition) =>
      [definition.name, definition.category, definition.description]
        .filter(Boolean)
        .some((value) => String(value).toLowerCase().includes(query))
    );
  }, [definitions.data, search]);

  return (
    <div className="space-y-6">
      <ErrorPanel errors={[definitions.error]} />
      <Card>
        <CardHeader className="gap-4 md:flex-row md:items-center md:justify-start">
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
                <button
                  className="rounded-lg border bg-card p-4 text-left transition-colors hover:bg-accent"
                  key={definition.id.value}
                  onClick={() => setQueueDefinition(definition)}
                  type="button"
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
                </button>
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

function WorkersView({
  connection,
  keyTypeFilter,
  onOpenWorker,
  onKeyTypeFilterChange,
  onReady,
  onStateFilterChange,
  refreshToken,
  stateFilter,
}: {
  connection: WorkableConnection;
  keyTypeFilter: string;
  onOpenWorker: (workerId: string) => void;
  onKeyTypeFilterChange: (keyType: string) => void;
  onReady: () => void;
  onStateFilterChange: (states: WorkerState[]) => void;
  refreshToken: number;
  stateFilter: WorkerState[];
}) {
  const [definitionName, setDefinitionName] = useState("");
  const query = useMemo(
    () => ({
      definitionName: definitionName.trim() || undefined,
      keyType: keyTypeFilter.trim() || undefined,
      states: stateFilter.length === 0 ? undefined : stateFilter,
    }),
    [definitionName, keyTypeFilter, stateFilter]
  );
  const workers = useWorkerQuery(connection, query, refreshToken);
  const isReady = !workers.loading;

  useEffect(() => {
    if (isReady) {
      onReady();
    }
  }, [isReady, onReady]);

  return (
    <div className="space-y-6">
      <ErrorPanel errors={[workers.error]} />
      <Card>
        <CardHeader className="gap-4 lg:flex-row lg:items-center lg:justify-start">
          <div className="grid gap-3 md:grid-cols-[minmax(0,18rem)_minmax(0,18rem)_14rem]">
            <Input
              onChange={(event) => setDefinitionName(event.target.value)}
              placeholder="Definition name"
              value={definitionName}
            />
            <Input
              onChange={(event) => onKeyTypeFilterChange(event.target.value)}
              placeholder="Key type"
              value={keyTypeFilter}
            />
            <WorkerStateFilterDropdown
              onChange={onStateFilterChange}
              value={stateFilter}
            />
          </div>
        </CardHeader>
        <CardContent>
          <WorkerTable
            loading={workers.loading}
            onSelect={(worker) => onOpenWorker(worker.id.value)}
            workers={workers.data?.workers ?? []}
          />
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
  connection,
  keyTypeFilter,
  onKeyTypeFilterChange,
  onOpenWorker,
  onReady,
  onStatusFilterChange,
  refreshToken,
  statusFilter,
}: {
  connection: WorkableConnection;
  keyTypeFilter: string;
  onKeyTypeFilterChange: (keyType: string) => void;
  onOpenWorker: (workerId: string) => void;
  onReady: () => void;
  onStatusFilterChange: (statuses: WorkCompletionStatus[]) => void;
  refreshToken: number;
  statusFilter: WorkCompletionStatus[];
}) {
  const [definitionName, setDefinitionName] = useState("");
  const query = useMemo(
    () => ({
      definitionName: definitionName.trim() || undefined,
      keyType: keyTypeFilter.trim() || undefined,
      statuses: statusFilter.length === 0 ? undefined : statusFilter,
    }),
    [definitionName, keyTypeFilter, statusFilter]
  );
  const iterations = useIterationQuery(connection, query, refreshToken);
  const isReady = !iterations.loading;

  useEffect(() => {
    if (isReady) {
      onReady();
    }
  }, [isReady, onReady]);

  return (
    <div className="space-y-6">
      <ErrorPanel errors={[iterations.error]} />
      <Card>
        <CardHeader className="gap-4 lg:flex-row lg:items-center lg:justify-start">
          <div className="grid gap-3 md:grid-cols-[minmax(0,18rem)_minmax(0,18rem)_14rem]">
            <Input
              onChange={(event) => setDefinitionName(event.target.value)}
              placeholder="Definition name"
              value={definitionName}
            />
            <Input
              onChange={(event) => onKeyTypeFilterChange(event.target.value)}
              placeholder="Key type"
              value={keyTypeFilter}
            />
            <IterationStatusFilterDropdown
              onChange={onStatusFilterChange}
              value={statusFilter}
            />
          </div>
        </CardHeader>
        <CardContent>
          <IterationTable
            iterations={iterations.data?.iterations ?? []}
            loading={iterations.loading}
            onSelect={(iteration) => onOpenWorker(iteration.workerId.value)}
          />
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

function WorkerStateFilterDropdown({
  onChange,
  value,
}: {
  onChange: (states: WorkerState[]) => void;
  value: WorkerState[];
}) {
  const selected = new Set(value);
  const label =
    value.length === 0
      ? "All states"
      : value.length === 1
        ? value[0]
        : `${value.length} states`;

  const setStateEnabled = (state: WorkerState, enabled: boolean) => {
    const next = new Set(selected);
    if (enabled) {
      next.add(state);
    } else {
      next.delete(state);
    }
    onChange(states.filter((item) => next.has(item)));
  };

  return (
    <DropdownMenu>
      <DropdownMenuTrigger asChild>
        <Button className="justify-between" type="button" variant="outline">
          <span className="flex items-center gap-2">
            <ListFilter className="size-4" />
            {label}
          </span>
          {value.length > 0 && (
            <Badge variant="secondary">{value.length}</Badge>
          )}
        </Button>
      </DropdownMenuTrigger>
      <DropdownMenuContent align="end" className="w-56">
        <DropdownMenuCheckboxItem
          checked={value.length === 0}
          onCheckedChange={() => onChange([])}
          onSelect={(event) => event.preventDefault()}
        >
          All states
        </DropdownMenuCheckboxItem>
        <DropdownMenuSeparator />
        {states.map((state) => (
          <DropdownMenuCheckboxItem
            checked={selected.has(state)}
            key={state}
            onCheckedChange={(checked) => setStateEnabled(state, checked === true)}
            onSelect={(event) => event.preventDefault()}
          >
            {state}
          </DropdownMenuCheckboxItem>
        ))}
      </DropdownMenuContent>
    </DropdownMenu>
  );
}

function IterationStatusFilterDropdown({
  onChange,
  value,
}: {
  onChange: (statuses: WorkCompletionStatus[]) => void;
  value: WorkCompletionStatus[];
}) {
  const selected = new Set(value);
  const label =
    value.length === 0
      ? "All statuses"
      : value.length === 1
        ? value[0]
        : `${value.length} statuses`;

  const setStatusEnabled = (status: WorkCompletionStatus, enabled: boolean) => {
    const next = new Set(selected);
    if (enabled) {
      next.add(status);
    } else {
      next.delete(status);
    }
    onChange(iterationStatuses.filter((item) => next.has(item)));
  };

  return (
    <DropdownMenu>
      <DropdownMenuTrigger asChild>
        <Button className="justify-between" type="button" variant="outline">
          <span className="flex items-center gap-2">
            <ListFilter className="size-4" />
            {label}
          </span>
          {value.length > 0 && (
            <Badge variant="secondary">{value.length}</Badge>
          )}
        </Button>
      </DropdownMenuTrigger>
      <DropdownMenuContent align="end" className="w-56">
        <DropdownMenuCheckboxItem
          checked={value.length === 0}
          onCheckedChange={() => onChange([])}
          onSelect={(event) => event.preventDefault()}
        >
          All statuses
        </DropdownMenuCheckboxItem>
        <DropdownMenuSeparator />
        {iterationStatuses.map((status) => (
          <DropdownMenuCheckboxItem
            checked={selected.has(status)}
            key={status}
            onCheckedChange={(checked) => setStatusEnabled(status, checked === true)}
            onSelect={(event) => event.preventDefault()}
          >
            {status}
          </DropdownMenuCheckboxItem>
        ))}
      </DropdownMenuContent>
    </DropdownMenu>
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
      <DialogContent className="max-h-[88vh] overflow-hidden p-0 sm:max-w-5xl">
        <DialogHeader>
          <DialogTitle className="px-4 pt-4">Queue Work</DialogTitle>
          <DialogDescription className="space-y-2 px-4">
            <span className="block font-mono">{definition?.name}</span>
          </DialogDescription>
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
              <div className="grid gap-2">
                <Label>Request JSON</Label>
                <Textarea
                  className="h-[calc(54vh-2rem)] min-h-0 resize-none overflow-y-auto font-mono"
                  onChange={(event) => setManualRequestJson(event.target.value)}
                  value={manualRequestJson}
                />
              </div>
            </TabsContent>
          </Tabs>
          <div className="-mx-4 flex justify-end gap-2 border-t bg-muted/30 px-4 py-3">
            <Button disabled={isQueueing} onClick={() => onOpenChange(false)} variant="outline">
              Close
            </Button>
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
        servers?: LegacyWorkableServerConnection[];
      };

      if (Array.isArray(parsed.hosts) && parsed.hosts.length > 0) {
        const hosts = parsed.hosts.map(normalizeStoredHost);
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
    view: "overview",
  };
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
  realtimeSystemIds: Set<string>
): WorkableSystemConnection {
  const key = getSystemStorageKey(system);
  const realtimeSupported = system.capabilities.realtime.enabled;

  return {
    id: `${hostId}-${key || "default"}`,
    hostId,
    name: getSystemDisplayName(system),
    systemName: normalizeOptional(system.name),
    realtimeEnabled: realtimeSupported && realtimeSystemIds.has(key),
    realtimeSupported,
    realtimeTransport: system.capabilities.realtime.transport ?? null,
    state: system.state,
  };
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

function formatHostSubtitle(host: WorkableHostConnection) {
  return host.apiUrl;
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
  value: number;
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
          <Tooltip>
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
      <pre className="max-h-64 overflow-auto rounded-lg border bg-muted/30 p-3 font-mono text-xs">
        {JSON.stringify(value ?? null, null, 2)}
      </pre>
    </div>
  );
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

  return navItems.find((item) => item.id === view)?.label ?? "Overview";
}

function getWorkerParentView(history: NavigationEntry[]): ServerView {
  const previous = history.at(-1);
  return previous && isServerView(previous.view) ? previous.view : "workers";
}

function shortId(value: string) {
  return value.length > 12 ? `${value.slice(0, 8)}...` : value;
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

function getSystemLifecycleActionLabel(state?: string | null) {
  const action = getSystemLifecycleAction(state);
  if (action === "start") {
    return "Start system";
  }
  if (action === "stop") {
    return "Stop system";
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
    left.iterationKeyTypeFilter === right.iterationKeyTypeFilter &&
    left.iterationStatusFilter.length === right.iterationStatusFilter.length &&
    left.iterationStatusFilter.every(
      (status, index) => status === right.iterationStatusFilter[index]
    ) &&
    left.keyTypeFilter === right.keyTypeFilter &&
    left.view === right.view &&
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

function useWorkerQuery(
  connection: WorkableConnection,
  query: { definitionName?: string; keyType?: string; states?: WorkerState[] },
  refreshToken: number
): Loadable<WorkerQueryResult> {
  const [state, setState] = useState<Loadable<WorkerQueryResult>>({
    loading: true,
  });
  const key = JSON.stringify(query);

  useEffect(() => {
    let canceled = false;
    const parsedQuery = JSON.parse(key) as {
      definitionName?: string;
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
            })
          : await workableFetch<WorkerQueryResult>(connection, "workers/query", {
              method: "POST",
              body: JSON.stringify({
                definitionName: parsedQuery.definitionName,
                states: parsedQuery.states,
                skip: 0,
                take: 50,
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
  }, [connection, key, refreshToken]);

  return state;
}

function useIterationQuery(
  connection: WorkableConnection,
  query: {
    definitionName?: string;
    keyType?: string;
    statuses?: WorkCompletionStatus[];
  },
  refreshToken: number
): Loadable<WorkerIterationQueryResult> {
  const [state, setState] = useState<Loadable<WorkerIterationQueryResult>>({
    loading: true,
  });
  const key = JSON.stringify(query);

  useEffect(() => {
    let canceled = false;
    const parsedQuery = JSON.parse(key) as {
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
            })
          : await workableFetch<WorkerIterationQueryResult>(connection, "iterations/query", {
              method: "POST",
              body: JSON.stringify({
                definitionName: parsedQuery.definitionName,
                statuses: parsedQuery.statuses,
                sort: "CompletedAt",
                direction: "Descending",
                skip: 0,
                take: 50,
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
  }, [connection, key, refreshToken]);

  return state;
}

async function queryWorkersByKeyType(
  connection: WorkableConnection,
  query: { definitionName?: string; keyType: string; states?: WorkerState[] }
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
        take: 50,
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

      workersById.set(worker.id.value, worker);
    }
  }
  const workers = [...workersById.values()].sort(
    (left, right) =>
      new Date(right.updatedAt).getTime() - new Date(left.updatedAt).getTime()
  );

  return {
    workers: workers.slice(0, 50),
    totalCount: workers.length,
    skip: 0,
    take: 50,
  };
}

async function queryIterationsByKeyType(
  connection: WorkableConnection,
  query: {
    definitionName?: string;
    keyType: string;
    statuses?: WorkCompletionStatus[];
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
        take: 50,
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

      iterationsById.set(`${iteration.workerId.value}:${iteration.sequence}`, iteration);
    }
  }
  const iterations = [...iterationsById.values()].sort(
    (left, right) =>
      new Date(right.completedAt).getTime() - new Date(left.completedAt).getTime()
  );

  return {
    iterations: iterations.slice(0, 50),
    totalCount: iterations.length,
    skip: 0,
    take: 50,
  };
}
