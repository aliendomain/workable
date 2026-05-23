"use client";

import {
  Activity,
  Boxes,
  ChevronLeft,
  ChevronRight,
  Clock3,
  FileCode2,
  Folder,
  Home,
  Loader2,
  Pencil,
  Play,
  Plus,
  Radio,
  RefreshCw,
  Search,
  Send,
  Server,
  Square,
  Trash2,
  Workflow,
} from "lucide-react";
import { Fragment, type ReactNode, useCallback, useEffect, useMemo, useState } from "react";
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
import { Dialog, DialogContent, DialogDescription, DialogHeader, DialogTitle } from "@/components/ui/dialog";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { Separator } from "@/components/ui/separator";
import {
  SidebarMenu,
  SidebarMenuAction,
  SidebarMenuButton,
  SidebarMenuSub,
  SidebarMenuSubButton,
  SidebarMenuSubItem,
  SidebarMenuItem,
  SidebarTrigger,
} from "@/components/ui/sidebar";
import { Skeleton } from "@/components/ui/skeleton";
import { Tooltip, TooltipContent, TooltipTrigger } from "@/components/ui/tooltip";
import { QueueDialog } from "@/components/workable/console/detail-screens";
import { ErrorBanner } from "@/components/workable/console/feedback-panel";
import {
  workableFetch,
  WorkableApiError,
  type WorkDefinition,
  type WorkOverviewCatalogCategoryItem,
  type WorkableConnection,
  type WorkSystemAccessSummary,
  type WorkableHttpSystemInfo,
  type WorkableHttpSystems,
} from "@/lib/workable";

type View = "overview" | "definitions" | "definition" | "workers" | "iterations" | "worker";
type ServerView = Exclude<View, "worker">;
type OverviewScope = {
  category?: string;
  definitionName?: string;
  includeSubcategories?: boolean;
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
  access?: WorkSystemAccessSummary;
  realtimeEnabled: boolean;
  realtimeFeatures?: string[] | null;
  realtimeHubPath?: string | null;
  realtimeSupported?: boolean;
  realtimeTransport?: string | null;
  state?: string | null;
};
type PendingDelete =
  | { kind: "host"; host: WorkableHostConnection }
  | { kind: "system"; host: WorkableHostConnection; system: WorkableSystemConnection };
type PendingStopSystem = {
  system: WorkableSystemConnection;
};
type Loadable<T> = {
  data?: T;
  error?: string;
  loading: boolean;
  refreshing?: boolean;
};
const navItems: Array<{ id: ServerView; label: string; icon: typeof Activity }> = [
  { id: "overview", label: "Overview", icon: Activity },
  { id: "definitions", label: "Catalog", icon: Boxes },
  { id: "workers", label: "Workers", icon: Workflow },
  { id: "iterations", label: "Iterations", icon: Clock3 },
];
export function ServerTree({
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
                {host.systems.length === 0 && (
                  <SidebarMenuSubItem>
                    <div className="px-3 py-2 text-muted-foreground text-xs">
                      No Workable systems are currently available for this user on that host.
                    </div>
                  </SidebarMenuSubItem>
                )}
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

export function EmptyServerState({
  description = "Add a Workable HTTP host to discover its systems.",
  onAddServer,
  title = "No servers",
}: {
  description?: string;
  onAddServer: () => void;
  title?: string;
}) {
  return (
    <div className="flex min-h-[calc(100vh-8rem)] items-center justify-center">
      <div className="max-w-md rounded-lg border border-dashed p-8 text-center">
        <div className="mx-auto flex size-10 items-center justify-center rounded-md bg-muted">
          <Server className="size-5 text-muted-foreground" />
        </div>
        <h1 className="mt-4 font-semibold text-xl">{title}</h1>
        <p className="mt-2 text-muted-foreground text-sm">{description}</p>
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

export function DelayedLoadingOverlay({
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

export function DeleteTargetDialog({
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

export function StopSystemDialog({
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

export function ServerDialog({
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
  const [hasLoadedSystems, setHasLoadedSystems] = useState(false);
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
      setHasLoadedSystems(true);
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
      setHasLoadedSystems(false);
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
                  setHasLoadedSystems(false);
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
            <ErrorBanner key={systemsError} message={systemsError} title="Discovery failed" />
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
                  {hasLoadedSystems && !systemsError
                    ? "Connected to the host, but this signed-in user does not have Connect permission for any Workable systems exposed there."
                    : "Enter a URL and load systems."}
                </div>
              ) : (
                discovered.map((system) => {
                  const key = getSystemStorageKey(system);
                  const realtimeAvailable = system.capabilities.realtime.enabled;
                  const accessBadges = getSystemAccessBadges(system.access);

                  return (
                    <div
                      className="grid grid-cols-[1fr_7rem] items-start gap-3 border-b px-3 py-3 last:border-b-0"
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
                          {getSystemSecondaryText(system) && (
                            <span className="block text-muted-foreground text-xs">
                              {getSystemSecondaryText(system)}
                            </span>
                          )}
                          <span className="mt-2 flex flex-wrap items-center gap-1.5">
                            <span className="mr-1 text-[11px] font-medium uppercase tracking-[0.12em] text-muted-foreground/80">
                              Permissions
                            </span>
                            {accessBadges.map((badge) => (
                              <span
                                className="rounded-full border border-border/70 bg-muted/40 px-2 py-0.5 text-[11px] text-muted-foreground"
                                key={badge}
                              >
                                {badge}
                              </span>
                            ))}
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
    return <label className="flex justify-center pt-0.5">{checkbox}</label>;
  }

  return (
    <Tooltip delayDuration={500} disableHoverableContent>
      <TooltipTrigger asChild>
        <span className="flex cursor-not-allowed justify-center pt-0.5">
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

export function ConsoleNavigationHeader({
  canGoBack,
  definitionId,
  host,
  onBack,
  onOpenView,
  system,
  systemNotifications,
  view,
  workerId,
}: {
  canGoBack: boolean;
  definitionId: string | null;
  host: WorkableHostConnection;
  onBack: () => void;
  onOpenView: (view: View, systemId?: string, trackHistory?: boolean) => void;
  system: WorkableSystemConnection;
  systemNotifications?: ReactNode;
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
      <div className="min-w-0 flex-1 overflow-x-auto">
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
      {systemNotifications && (
        <div className="ml-auto flex shrink-0 items-center">
          {systemNotifications}
        </div>
      )}
    </div>
  );
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
    access: system.access,
    realtimeEnabled: realtimeSupported && realtimeSystemIds.has(key),
    realtimeFeatures: normalizeRealtimeFeatures(system.capabilities.realtime.features),
    realtimeHubPath: system.capabilities.realtime.hubPath ?? null,
    realtimeSupported,
    realtimeTransport: system.capabilities.realtime.transport ?? null,
    state: system.state,
  };
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

function getSystemAccessBadges(access: WorkSystemAccessSummary) {
  const badges = ["Connect"];

  if (access.isSystemAdministrator) {
    badges.push("System admin");
  }

  if (access.isWorkAdministrator) {
    badges.push("Work admin");
  }

  if (access.canViewDiagnostics) {
    badges.push("Diagnostics");
  }

  if (access.canControlSystem) {
    badges.push("Control system");
  }

  const readBadge = getWorkAccessBadge(
    "Read",
    access.readableDefinitionCount,
    access.totalDefinitionCount,
    access.canReadAllWork
  );
  if (readBadge) {
    badges.push(readBadge);
  }

  const operateBadge = getWorkAccessBadge(
    "Operate",
    access.operableDefinitionCount,
    access.totalDefinitionCount,
    access.canOperateAllWork
  );
  if (operateBadge) {
    badges.push(operateBadge);
  }

  if (access.readableDefinitionCount === 0 && access.operableDefinitionCount === 0) {
    badges.push("No work access");
  }

  return badges;
}

function createUnknownAccessSummary(): WorkSystemAccessSummary {
  return {
    canConnect: true,
    isSystemAdministrator: false,
    isWorkAdministrator: false,
    canViewDiagnostics: false,
    canControlSystem: false,
    canReadAllWork: false,
    canOperateAllWork: false,
    totalDefinitionCount: 0,
    readableDefinitionCount: 0,
    operableDefinitionCount: 0,
  };
}

function normalizeRealtimeFeatures(features?: string[] | null) {
  if (!Array.isArray(features)) {
    return null;
  }

  const normalized = [...new Set(
    features
      .filter((feature) => typeof feature === "string" && feature.trim())
      .map((feature) => feature.trim())
  )];

  return normalized.length > 0 ? normalized : null;
}

function getWorkAccessBadge(
  label: "Read" | "Operate",
  count: number,
  total: number,
  allAccess: boolean
) {
  if (total === 0 || count === 0) {
    return null;
  }

  if (allAccess || count >= total) {
    return `${label} all work`;
  }

  return `${label} ${formatCompactCount(count)}/${formatCompactCount(total)} defs`;
}

function formatCompactCount(value: number) {
  return new Intl.NumberFormat("en-US", { notation: value >= 1000 ? "compact" : "standard" }).format(value);
}

export async function discoverSystems(apiUrl: string): Promise<WorkableHttpSystems & { apiUrl: string }> {
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
      if (!isWorkableSystemsResponse(result)) {
        continue;
      }

      return {
        ...result,
        apiUrl: candidate,
      };
    } catch (caught) {
      lastError = caught;
    }
  }

  const attempted = candidates.map(formatSystemsEndpoint).join(", ");
  if (lastError instanceof WorkableApiError) {
    if (lastError.status === 401) {
      throw new Error(
        "This Workable host requires authentication before its systems can be discovered. Sign in and try again."
      );
    }

    if (lastError.status === 403) {
      throw new Error(
        "This user cannot discover systems on that host. Workable Connect access is required to add the server."
      );
    }

    if (lastError.status === 404) {
      throw new Error(
        `No Workable systems endpoint was found at that address. Make sure the URL points to the Workable HTTP API root, usually ending in /workable. Tried ${attempted}.`
      );
    }
  }

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

function isWorkableSystemsResponse(value: unknown): value is WorkableHttpSystems {
  return Boolean(
    value &&
      typeof value === "object" &&
      Array.isArray((value as Partial<WorkableHttpSystems>).systems)
  );
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
        features: normalizeRealtimeFeatures(system.realtimeFeatures),
        hubPath: system.realtimeHubPath,
        transport: system.realtimeTransport,
      },
      persistentCoordinationAvailable: false,
    },
    access: system.access ?? createUnknownAccessSummary(),
  };
}

export function reconcileStoredSystemsWithDiscovery(
  host: WorkableHostConnection,
  systems: WorkableHttpSystemInfo[]
): WorkableSystemConnection[] {
  const realtimeSystemIds = new Set(
    host.systems
      .filter((system) => system.realtimeEnabled)
      .map((system) => system.systemName ?? "")
  );

  return host.systems.flatMap((storedSystem) => {
    const discoveredSystem = systems.find(
      (system) =>
        getSystemStorageKey(system) ===
        getSystemStorageKey(createDiscoveredSystemFromStored(storedSystem))
    );

    return discoveredSystem
      ? [createStoredSystem(host.id, discoveredSystem, realtimeSystemIds, storedSystem)]
      : [];
  });
}

function getSystemStorageKey(system: WorkableHttpSystemInfo) {
  return system.name?.trim() ?? "";
}

function getSystemDisplayName(system: WorkableHttpSystemInfo) {
  return normalizeOptional(system.name) ?? "Default";
}

function getSystemSecondaryText(system: WorkableHttpSystemInfo) {
  return system.isDefault ? "Default system" : null;
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

function createDefinitionCatalogLevelPath(category: string) {
  const query = new URLSearchParams({ level: "true" });
  const normalizedCategory = normalizeCategoryFilter(category);
  if (normalizedCategory) {
    query.set("category", normalizedCategory);
  }

  return `definitions?${query.toString()}`;
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

function useWorkableResource<T>(
  connection: WorkableConnection,
  path: string | null,
  refreshToken: number
): Loadable<T> {
  const [state, setState] = useState<Loadable<T>>({ loading: !!path });
  const apiUrl = connection.apiUrl;
  const systemName = connection.systemName;

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
  }, [apiUrl, systemName, path, refreshToken]);

  return state;
}
