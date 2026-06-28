import {
  overviewPanelIds,
  overviewPanelShapeCapabilities,
  type OverviewPanelId,
  type OverviewPanelShapeMap,
} from "@/components/features/console/overview-panels";
import type {
  OverviewScope,
  ServerView,
  View,
  WorkableHostConnection,
  WorkableSystemConnection,
} from "@/components/features/console/types";
import { normalizeOverviewScope, overviewScopesEqual } from "@/components/workable/console/catalog-path";
import type { WorkerConsoleViewUiStateSnapshot } from "@/components/workable/console/detail-screens";
import type { WorkflowRunConsoleViewUiStateSnapshot } from "@/components/workable/console/workflow-run-screen";
import {
  DEFAULT_WORKABLE_API_URL,
  createDefaultWorkableHttpSystemCapabilities,
  normalizeWorkableHttpSystemCapabilities,
  type WorkCompletionStatus,
  type WorkComponentShape,
  type WorkKeyKind,
  type WorkSystemAccessSummary,
  type WorkerState,
} from "@/lib/workable";

export const STORAGE_KEY = "workable-console.state.v1";
export const throughputSeriesIds = ["started", "completed", "failed", "canceled"] as const;

export type ThroughputSeriesId = (typeof throughputSeriesIds)[number];

export type ConsoleStorage = {
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

export type NavigationEntry = {
  catalogScope: OverviewScope | null;
  iterationSequence: number | null;
  iterationCategoryFilter: string;
  iterationDefinitionFilter: string;
  iterationKeyKindFilter: WorkKeyKind | "Any";
  iterationKeyTypeFilter: string;
  iterationKeyValueFilter: string;
  iterationStatusFilter: WorkCompletionStatus[];
  overviewScope: OverviewScope | null;
  definitionName: string | null;
  workerCategoryFilter: string;
  workerDefinitionFilter: string;
  keyKindFilter: WorkKeyKind | "Any";
  keyTypeFilter: string;
  keyValueFilter: string;
  systemId: string;
  view: View;
  definitionId: string | null;
  iterationWorkerId: string | null;
  workerId: string | null;
  workflowRunId: string | null;
  workerUiState: WorkerConsoleViewUiStateSnapshot | null;
  workflowRunUiState: WorkflowRunConsoleViewUiStateSnapshot | null;
  workerStateFilter: WorkerState[];
};

export type DiagnosticsAlertTarget = {
  id: string;
  apiUrl: string;
  displayName: string;
  hostId: string;
  hostName: string;
  realtimeHubPath: string;
  systemName?: string;
};

export function loadConsoleStorage(): ConsoleStorage {
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

export function createDefaultConsoleStorage(): ConsoleStorage {
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

export function createDefaultOverviewPanelShapes(): OverviewPanelShapeMap {
  return Object.fromEntries(
    overviewPanelIds.map((panelId) => [
      panelId,
      overviewPanelShapeCapabilities[panelId].defaultShape,
    ])
  ) as OverviewPanelShapeMap;
}

export function normalizeOverviewPanelShapes(
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

export function normalizeOverviewPanelShape(
  panelId: OverviewPanelId,
  value: unknown
): WorkComponentShape {
  const capabilities = overviewPanelShapeCapabilities[panelId];
  return typeof value === "string" &&
    capabilities.supportedShapes.includes(value as WorkComponentShape)
    ? value as WorkComponentShape
    : capabilities.defaultShape;
}

export function normalizeOverviewHiddenPanels(
  value: unknown,
  legacyThroughputHidden = false
): OverviewPanelId[] {
  const requested = new Set(normalizeOverviewPanelIds(value));

  if (legacyThroughputHidden) {
    requested.add("throughput");
  }

  return overviewPanelIds.filter((id) => requested.has(id));
}

export function normalizeThroughputSeriesIds(value: unknown): ThroughputSeriesId[] {
  if (!Array.isArray(value)) {
    return [];
  }

  const requested = new Set(value.filter(isThroughputSeriesId));
  const hidden = throughputSeriesIds.filter((id) => requested.has(id));
  return hidden.length >= throughputSeriesIds.length ? hidden.slice(1) : hidden;
}

export function isThroughputSeriesId(value: unknown): value is ThroughputSeriesId {
  return typeof value === "string" &&
    throughputSeriesIds.includes(value as ThroughputSeriesId);
}

export function normalizeOverviewPanelIds(value: unknown): OverviewPanelId[] {
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

export function normalizeStoredHost(host: WorkableHostConnection): WorkableHostConnection {
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

export function normalizeStoredSystem(
  hostId: string,
  system: WorkableSystemConnection
): WorkableSystemConnection {
  const legacySystem = system as WorkableSystemConnection & {
    persistentCoordinationAvailable?: boolean;
    sqlProfilingAvailable?: boolean;
  };

  return {
    id: system.id || createServerId(),
    hostId,
    name: system.name || "Default",
    systemName: normalizeOptional(system.systemName),
    access: system.access ?? (hostId === "local-sample-host" ? createFullAccessSummary() : undefined),
    capabilities: normalizeWorkableHttpSystemCapabilities(
      legacySystem.capabilities ?? {
        persistentCoordinationAvailable: legacySystem.persistentCoordinationAvailable,
        sqlProfilingAvailable: legacySystem.sqlProfilingAvailable,
      }
    ),
    state: system.state ?? null,
  };
}

export function findSystemLocation(
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

export function getFirstAvailableSystemId(hosts: WorkableHostConnection[]) {
  return hosts.find((host) => host.systems.length > 0)?.systems[0]?.id ?? "";
}

export function createDiagnosticsAlertTargets(hosts: WorkableHostConnection[]): DiagnosticsAlertTarget[] {
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

export function createDiagnosticsAlertTargetId(
  apiUrl: string,
  realtimeHubPath: string | null | undefined,
  systemName: string | undefined
) {
  return `${apiUrl}\n${realtimeHubPath ?? ""}\n${systemName ?? ""}`;
}

export function isServerView(value: unknown): value is ServerView {
  return (
    value === "overview" ||
    value === "definitions" ||
    value === "definition" ||
    value === "workers" ||
    value === "iterations"
  );
}

export function getViewReadinessKey(systemId: string, view: View) {
  return `${systemId}:${view}`;
}

export function createDefaultSystem(hostId: string): WorkableSystemConnection {
  return {
    id: "local-sample-default",
    hostId,
    name: "Default",
    access: createFullAccessSummary(),
    capabilities: createDefaultWorkableHttpSystemCapabilities(),
    state: null,
  };
}

export function createServerId() {
  if (typeof crypto !== "undefined" && "randomUUID" in crypto) {
    return crypto.randomUUID();
  }

  return `server-${Date.now().toString(36)}`;
}

export function createFullAccessSummary(): WorkSystemAccessSummary {
  return {
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

export function normalizeOptional(value?: string | null) {
  const trimmed = value?.trim();
  return trimmed ? trimmed : undefined;
}

export function navTitle(view: View) {
  switch (view) {
    case "workflowRun":
      return "Workflow Run";
    case "worker":
      return "Worker Console";
    case "iteration":
      return "Iteration";
    case "definition":
      return "Definition";
    case "definitions":
      return "Catalog";
    case "workers":
      return "Workers";
    case "iterations":
      return "Iterations";
    default:
      return "Overview";
  }
}

export function headerRefreshTitle(view: View) {
  switch (view) {
    case "workflowRun":
      return "Refresh workflow run";
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

export function cloneOverviewScope(scope: OverviewScope | null): OverviewScope | null {
  return normalizeOverviewScope(scope);
}

export function getWindowScrollTop() {
  return document.scrollingElement?.scrollTop ?? window.scrollY;
}

export function getDocumentScrollHeight() {
  return Math.max(
    document.body.scrollHeight,
    document.documentElement.scrollHeight
  );
}

export function navigationEntriesEqual(left: NavigationEntry | undefined, right: NavigationEntry) {
  return (
    left?.systemId === right.systemId &&
    overviewScopesEqual(left.catalogScope, right.catalogScope) &&
    left.definitionId === right.definitionId &&
    left.definitionName === right.definitionName &&
    left.iterationSequence === right.iterationSequence &&
    left.iterationCategoryFilter === right.iterationCategoryFilter &&
    left.iterationDefinitionFilter === right.iterationDefinitionFilter &&
    left.iterationKeyKindFilter === right.iterationKeyKindFilter &&
    left.iterationKeyTypeFilter === right.iterationKeyTypeFilter &&
    left.iterationKeyValueFilter === right.iterationKeyValueFilter &&
    left.iterationStatusFilter.length === right.iterationStatusFilter.length &&
    left.iterationStatusFilter.every(
      (status, index) => status === right.iterationStatusFilter[index]
    ) &&
    left.iterationWorkerId === right.iterationWorkerId &&
    left.keyKindFilter === right.keyKindFilter &&
    left.keyTypeFilter === right.keyTypeFilter &&
    left.keyValueFilter === right.keyValueFilter &&
    overviewScopesEqual(left.overviewScope, right.overviewScope) &&
    left.view === right.view &&
    left.workerCategoryFilter === right.workerCategoryFilter &&
    left.workerDefinitionFilter === right.workerDefinitionFilter &&
    left.workerId === right.workerId &&
    left.workflowRunId === right.workflowRunId &&
    left.workflowRunUiState?.runId === right.workflowRunUiState?.runId &&
    left.workflowRunUiState?.selectedStepName === right.workflowRunUiState?.selectedStepName &&
    left.workerStateFilter.length === right.workerStateFilter.length &&
    left.workerStateFilter.every((state, index) => state === right.workerStateFilter[index])
  );
}
