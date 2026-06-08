"use client";

import {
  Activity,
  Boxes,
  ChevronLeft,
  ChevronRight,
  Clock3,
  FileCode2,
  Folder,
  Loader2,
  Pencil,
  Play,
  Plus,
  Search,
  Send,
  Server,
  Square,
  Trash2,
  Workflow,
} from "lucide-react";
import { Fragment, type ReactNode, useEffect, useMemo, useState } from "react";
import { ConsoleEmptyState } from "@/components/features/console/empty-state";
import { ConsoleHeaderCapabilityControls } from "@/components/features/console/header-capability-controls";
import {
  consoleBreadcrumbCurrentClassName,
  consoleBreadcrumbDefinitionClassName,
  consoleBreadcrumbLinkClassName,
  consoleBreadcrumbTextClassName,
} from "@/components/features/console/console-primitives";
import { useResolvedConsoleHeaderCapabilities } from "@/components/features/console/header-capabilities";
import type {
  OverviewScope,
  PendingDelete,
  PendingStopSystem,
  ServerView,
  View,
  WorkableHostConnection,
  WorkableSystemConnection,
} from "@/components/features/console/types";
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
import {
  DefinitionCatalogBrowser,
  defaultCatalogBrowserBackButtonClassName,
  defaultCatalogBrowserHeaderClassName,
  defaultCatalogBrowserTitleClassName,
} from "@/components/workable/console/catalog-browser";
import { normalizeCategoryFilter } from "@/components/workable/console/catalog-browser-data";
import { QueueDialog } from "@/components/workable/console/detail-screens";
import { ErrorBanner } from "@/components/workable/console/feedback-panel";
import { semanticDotToneClass, semanticToneForStateName } from "@/lib/ui/state-tones";
import {
  type WorkDefinition,
  type WorkableConnection,
  type QueueRequestSchemaDescriptor,
  type WorkSystemAccessSummary,
} from "@/lib/workable";

export {
  ServerDialog,
  createUnknownAccessSummary,
  createWorkableApiUrlCandidates,
  discoverHost,
  formatCompactCount,
  formatHostEndpoint,
  formatWorkableApiUrl,
  getSystemAccessBadges,
  getSystemDisplayName,
  getSystemSecondaryText,
  getSystemStorageKey,
  getWorkAccessBadge,
  isWorkableHostResponse,
  normalizeOptional,
  reconcileStoredHostWithDiscovery,
  stripTrailingPathSegment,
} from "@/components/workable/console/server-dialog";
const navItems: Array<{ id: ServerView; label: string; icon: typeof Activity }> = [
  { id: "overview", label: "Overview", icon: Activity },
  { id: "definitions", label: "Catalog", icon: Boxes },
  { id: "workers", label: "Workers", icon: Workflow },
  { id: "iterations", label: "Iterations", icon: Clock3 },
];
export const catalogExplorerShellClassName =
  "relative z-10 -ml-11 mr-0 mb-2 mt-1 w-[calc(var(--sidebar-width)-2rem)] overflow-hidden rounded-md border border-sidebar-border bg-sidebar group-data-[collapsible=icon]:hidden";
export const catalogExplorerBodyClassName =
  "workable-grid-scrollbar max-h-72 overflow-y-auto";

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
  onOpenDefinition: (
    definitionName: string,
    options?: {
      definitionName?: string;
      systemId?: string;
    }
  ) => void;
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
                  const canControlSystem = system.access?.canControlSystem === true;
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
                          </button>
                        </SidebarMenuSubButton>
                        {canControlSystem && (lifecycleAction || lifecycleActionSystemId === system.id) && (
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
  onOpenDefinition: (
    definitionName: string,
    options?: {
      definitionName?: string;
      systemId?: string;
    }
  ) => void;
  onOpenWorker: (workerId: string) => void;
  system: WorkableSystemConnection;
}) {
  const canQueueDefinitions = canOperateSystemWork(system.access);
  const connection = useMemo<WorkableConnection>(
    () => ({
      apiUrl: host.apiUrl,
      systemName: system.systemName,
    }),
    [host.apiUrl, system.systemName]
  );
  const [path, setPath] = useState(activeOverviewCategory);
  const [queueDialogData, setQueueDialogData] = useState<{
    definition: WorkDefinition;
    queueRequestSchema: QueueRequestSchemaDescriptor;
  } | null>(null);
  const [queueDefinitionLoadingId, setQueueDefinitionLoadingId] = useState<string | null>(null);
  const [queueDefinitionError, setQueueDefinitionError] = useState<string>();

  return (
    <>
      <div className={catalogExplorerShellClassName}>
        {queueDefinitionError && (
          <div className="border-sidebar-border border-b">
            <ErrorBanner
              message={queueDefinitionError}
              title="Queue configuration unavailable"
            />
          </div>
        )}
        <DefinitionCatalogBrowser
          backButtonClassName={defaultCatalogBrowserBackButtonClassName(
            "size-5 hover:bg-sidebar-accent hover:text-sidebar-accent-foreground"
          )}
          backIconClassName="size-3.5"
          bodyClassName={catalogExplorerBodyClassName}
          connection={connection}
          emptyState={(
            <div className="px-2 py-2 text-sidebar-foreground/60 text-xs">
              No catalog entries.
            </div>
          )}
          headerClassName={defaultCatalogBrowserHeaderClassName(
            "h-8 border-sidebar-border px-1.5 text-sidebar-foreground/80 text-xs"
          )}
          loadingState={Array.from({ length: 4 }).map((_, index) => (
            <Skeleton className="mx-2 my-1 h-7" key={index} />
          ))}
          onNavigate={setPath}
          path={path}
          renderCategory={(category) => (
            <div className="flex h-7 min-w-0 items-center text-sidebar-foreground text-sm hover:bg-sidebar-accent hover:text-sidebar-accent-foreground">
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
          )}
          renderDefinition={(definition, catalog) => (
            <div
              className={
                definition.name === activeDefinitionName
                  ? "flex h-7 min-w-0 items-center bg-sidebar-accent text-sidebar-accent-foreground text-sm"
                  : "flex h-7 min-w-0 items-center text-sidebar-foreground text-sm hover:bg-sidebar-accent hover:text-sidebar-accent-foreground"
              }
            >
              <button
                className="flex h-full min-w-0 flex-1 items-center gap-2 px-2 text-left"
                onClick={() => onOpenDefinition(definition.name, {
                  definitionName: definition.name,
                  systemId: system.id,
                })}
                type="button"
              >
                <FileCode2 className="size-4 shrink-0 text-sidebar-accent-foreground" />
                <span className="min-w-0 flex-1 truncate font-mono">{definition.name}</span>
              </button>
              {canQueueDefinitions && (
                <Tooltip delayDuration={500} disableHoverableContent>
                  <TooltipTrigger asChild>
                    <button
                      aria-label={`Queue ${definition.name}`}
                      className="mr-1 flex size-5 shrink-0 items-center justify-center rounded-md text-sidebar-foreground/70 hover:bg-sidebar-accent hover:text-sidebar-accent-foreground"
                      disabled={queueDefinitionLoadingId === definition.name}
                      onClick={async () => {
                        setQueueDialogData(null);
                        setQueueDefinitionError(undefined);
                        setQueueDefinitionLoadingId(definition.name);

                        try {
                          const info = await catalog.loadDefinitionInfo(definition.name);
                          setQueueDialogData({
                            definition: info.definition,
                            queueRequestSchema: info.queueRequestSchema,
                          });
                        } catch (error) {
                          setQueueDefinitionError(
                            error instanceof Error ? error.message : "Definition could not be loaded."
                          );
                        } finally {
                          setQueueDefinitionLoadingId(null);
                        }
                      }}
                      type="button"
                    >
                      {queueDefinitionLoadingId === definition.name ? (
                        <Loader2 className="size-3.5 animate-spin" />
                      ) : (
                        <Send className="size-3.5" />
                      )}
                    </button>
                  </TooltipTrigger>
                  <TooltipContent side="right" sideOffset={6}>
                    Queue {definition.name}
                  </TooltipContent>
                </Tooltip>
              )}
            </div>
          )}
          renderError={(error) => (
            <div className="border-sidebar-border border-b">
              <ErrorBanner
                message={error}
                title="Catalog unavailable"
              />
            </div>
          )}
          titleClassName={defaultCatalogBrowserTitleClassName("text-xs font-normal")}
          rootLabel="Catalog"
        />
      </div>
      <QueueDialog
        connection={connection}
        definition={queueDialogData?.definition ?? null}
        fetchQueueSchemaWhenNeeded={false}
        onQueuedWorker={onOpenWorker}
        preloadedQueueSchemaDescriptor={queueDialogData?.queueRequestSchema ?? null}
        onOpenChange={(open) => {
          if (!open) {
            setQueueDialogData(null);
            setQueueDefinitionLoadingId(null);
            setQueueDefinitionError(undefined);
          }
        }}
      />
    </>
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
      <ConsoleEmptyState className="max-w-md text-foreground" padding="spacious">
        <div className="mx-auto flex size-10 items-center justify-center rounded-md bg-muted">
          <Server className="size-5 text-muted-foreground" />
        </div>
        <h1 className="mt-4 font-semibold text-xl">{title}</h1>
        <p className="mt-2 text-muted-foreground text-sm">{description}</p>
        <Button className="mt-4" onClick={onAddServer}>
          <Plus className="size-4" />
          Add server
        </Button>
      </ConsoleEmptyState>
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
  const { description, title } = getDeleteTargetDialogText(target);

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

export function getDeleteTargetDialogText(target: PendingDelete | null) {
  return {
    title: target?.kind === "host"
      ? `Remove ${target.host.name}?`
      : target
        ? `Remove ${target.system.name}?`
        : "Remove item?",
    description: target?.kind === "host"
      ? "This removes the server group and every Workable system saved under it from this browser."
      : target?.host.systems.length === 1
        ? `This removes ${target.system.name}. Because it is the last system under ${target.host.name}, the server group will be removed too.`
        : "This removes only this Workable system from the sidebar.",
  };
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
          <AlertDialogTitle>{getStopSystemDialogTitle(target)}</AlertDialogTitle>
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

export function getStopSystemDialogTitle(target: PendingStopSystem | null) {
  return `Stop ${target?.system.name ?? "system"}?`;
}

export function ConsoleNavigationHeader({
  breadcrumbParent,
  canGoBack,
  canGoForward,
  definitionId,
  definitionName,
  host,
  iterationSequence,
  onBack,
  onForward,
  onOpenView,
  system,
  systemNotifications,
  view,
  workerId,
}: {
  breadcrumbParent?: {
    label: string;
    onSelect: () => void;
  } | null;
  canGoBack: boolean;
  canGoForward: boolean;
  definitionId: string | null;
  definitionName: string | null;
  host: WorkableHostConnection;
  iterationSequence: number | null;
  onBack: () => void;
  onForward: () => void;
  onOpenView: (view: View, systemId?: string, trackHistory?: boolean) => void;
  system: WorkableSystemConnection;
  systemNotifications?: ReactNode;
  view: View;
  workerId: string | null;
}) {
  const headerCapabilities = useResolvedConsoleHeaderCapabilities();
  const canOpenOverview = view !== "overview";
  const currentLabel =
    view === "definition" && definitionId
      ? definitionName ?? definitionId
      : view === "iteration" && iterationSequence !== null
        ? `#${iterationSequence}`
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
      <Tooltip delayDuration={500} disableHoverableContent>
        <TooltipTrigger asChild>
          <Button
            aria-label="Go forward"
            className="shrink-0"
            disabled={!canGoForward}
            onClick={onForward}
            size="icon-sm"
            variant="ghost"
          >
            <ChevronRight className="size-4" />
          </Button>
        </TooltipTrigger>
        <TooltipContent side="bottom" sideOffset={6}>
          Go forward
        </TooltipContent>
      </Tooltip>
      <div className="min-w-0 flex-1 overflow-x-auto">
        <Breadcrumb>
          <BreadcrumbList className={`flex-nowrap whitespace-nowrap ${consoleBreadcrumbTextClassName}`}>
            <BreadcrumbItem className="min-w-0 shrink-0">
              <BreadcrumbPage className="max-w-48 truncate text-muted-foreground">
                {host.name}
              </BreadcrumbPage>
            </BreadcrumbItem>
            <BreadcrumbSeparator className="shrink-0" />
            <BreadcrumbItem className="min-w-0 shrink-0">
              {canOpenOverview ? (
                <BreadcrumbLink asChild className={consoleBreadcrumbLinkClassName}>
                  <button onClick={() => onOpenView("overview", system.id)} type="button">
                    {system.name}
                  </button>
                </BreadcrumbLink>
              ) : (
                <BreadcrumbPage className={consoleBreadcrumbCurrentClassName}>
                  {system.name}
                </BreadcrumbPage>
              )}
            </BreadcrumbItem>
            <BreadcrumbSeparator className="shrink-0" />
            <BreadcrumbItem className="min-w-0 shrink-0">
              {breadcrumbParent ? (
                <BreadcrumbLink asChild className={consoleBreadcrumbDefinitionClassName}>
                  <button onClick={breadcrumbParent.onSelect} type="button">
                    {breadcrumbParent.label}
                  </button>
                </BreadcrumbLink>
              ) : (
                <BreadcrumbPage
                  className={`${view === "worker" || view === "definition" ? consoleBreadcrumbDefinitionClassName : consoleBreadcrumbCurrentClassName} text-foreground`}
                >
                  {currentLabel}
                </BreadcrumbPage>
              )}
            </BreadcrumbItem>
            {breadcrumbParent && (
              <>
                <BreadcrumbSeparator className="shrink-0" />
                <BreadcrumbItem className="min-w-0 shrink-0">
                  <BreadcrumbPage className={`${consoleBreadcrumbDefinitionClassName} font-semibold text-foreground`}>
                    {currentLabel}
                  </BreadcrumbPage>
                </BreadcrumbItem>
              </>
            )}
          </BreadcrumbList>
        </Breadcrumb>
      </div>
      {(headerCapabilities || systemNotifications) && (
        <div className="ml-auto flex shrink-0 items-center gap-1">
          <ConsoleHeaderCapabilityControls capabilities={headerCapabilities} />
          {systemNotifications}
        </div>
      )}
    </div>
  );
}

function canOperateSystemWork(access?: WorkSystemAccessSummary) {
  return access?.canOperateAllWork === true || (access?.operableDefinitionCount ?? 0) > 0;
}

export function navTitle(view: View) {
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

export function getSystemLifecycleAction(state?: string | null): "start" | "stop" | null {
  const normalized = state?.toLowerCase();
  if (normalized === "created" || normalized === "stopped") {
    return "start";
  }
  if (normalized === "started") {
    return "stop";
  }
  return null;
}

export function getSystemLifecycleActionLabel(
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

export function systemStateDotClass(state?: string | null) {
  return semanticDotToneClass(semanticToneForStateName(state));
}

