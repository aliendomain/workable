import assert from "node:assert/strict";
import test from "node:test";
import {
  applySystemNotificationDismissals,
  cloneOverviewScope,
  clampEventTableHeight,
  createCompactConcurrencyDiagnosticsFromDetailed,
  createCompactDurabilityDiagnosticsFromDetailed,
  createCompactIdempotencyDiagnosticsFromDetailed,
  createCompactReadModelDiagnosticsFromDetailed,
  createCompactRetentionDiagnosticsFromDetailed,
  createDefaultConsoleStorage,
  createDefaultOverviewPanelShapes,
  createDefaultSystem,
  createDiagnosticsAlertTargetId,
  createDiagnosticsAlertTargets,
  createFullAccessSummary,
  createSystemNotifications,
  diagnosticsAlertSnapshotsEqual,
  findSystemLocation,
  formatEventBatchDefinitionSummary,
  formatEventBatchTypeSummary,
  formatEventBatchWorkerSummary,
  formatEventByteCount,
  getFirstAvailableSystemId,
  getRealtimeEventSearchText,
  getViewReadinessKey,
  headerRefreshTitle,
  isServerView,
  isThroughputSeriesId,
  navTitle,
  navigationEntriesEqual,
  normalizeEventViewerMaxMessages,
  normalizeOptional,
  normalizeOverviewHiddenPanels,
  normalizeOverviewPanelIds,
  normalizeOverviewPanelShape,
  normalizeOverviewPanelShapes,
  normalizeStoredHost,
  normalizeStoredSystem,
  normalizeThroughputSeriesIds,
  pruneDismissedSystemNotificationKeys,
  shouldClearDefinitionCatalogCacheForDiagnosticsTransition,
  systemNotificationDismissalKey,
  eventTypeTone,
  type ConsoleStorage,
  type NavigationEntry,
} from "@/components/workable/console.tsx";
import type {
  WorkableHostConnection,
  WorkableSystemConnection,
} from "@/components/features/console/types";
import { semanticBadgeToneClass } from "@/lib/ui/state-tones";
import type { WorkableRealtimeEvent } from "@/lib/workable";

function system(overrides: Partial<WorkableSystemConnection> = {}): WorkableSystemConnection {
  return {
    hostId: "host-1",
    id: "system-1",
    name: "Default",
    capabilities: {
      persistentCoordinationAvailable: false,
      sqlProfilingAvailable: false,
    },
    state: "Started",
    ...overrides,
  };
}

function host(overrides: Partial<WorkableHostConnection> = {}): WorkableHostConnection {
  return {
    apiUrl: "https://workable.test",
    id: "host-1",
    name: "Workable",
    realtimeEnabled: true,
    realtimeHubPath: "/hub",
    systems: [system()],
    ...overrides,
  };
}

function navigationEntry(overrides: Partial<NavigationEntry> = {}): NavigationEntry {
  return {
    catalogScope: null,
    definitionId: null,
    definitionName: null,
    iterationCategoryFilter: "",
    iterationDefinitionFilter: "",
    iterationKeyKindFilter: "Any",
    iterationKeyTypeFilter: "",
    iterationKeyValueFilter: "",
    iterationSequence: null,
    iterationStatusFilter: [],
    iterationWorkerId: null,
    keyKindFilter: "Any",
    keyTypeFilter: "",
    keyValueFilter: "",
    overviewScope: null,
    systemId: "system-1",
    view: "overview",
    workerCategoryFilter: "",
    workerDefinitionFilter: "",
    workerId: null,
    workflowRunId: null,
    workerStateFilter: [],
    workerUiState: null,
    ...overrides,
  };
}

function event(overrides: Partial<WorkableRealtimeEvent> = {}): WorkableRealtimeEvent {
  return {
    eventType: "worker.completed",
    sentAt: "2026-05-30T10:00:00.000Z",
    workerId: { value: "worker-1" },
    workDefinitionName: "definition-1",
    ...overrides,
  } as WorkableRealtimeEvent;
}

test("realtime event helpers summarize batches, search text, byte counts, limits, and tones", () => {
  assert.equal(formatEventBatchTypeSummary([]), "No event types");
  assert.equal(formatEventBatchTypeSummary(["worker.completed"]), "worker.completed");
  assert.equal(
    formatEventBatchTypeSummary(["a", "b", "c", "d"]),
    "4 types: a, b, c, ..."
  );
  assert.equal(formatEventBatchDefinitionSummary([]), "No definition");
  assert.equal(formatEventBatchDefinitionSummary([event(), event({ workerId: { value: "worker-2" } })]), "definition-1");
  assert.equal(
    formatEventBatchDefinitionSummary([
      event({ workDefinitionName: "definition-1" }),
      event({ workDefinitionName: "definition-2" }),
    ]),
    "2 definitions"
  );
  assert.equal(formatEventBatchWorkerSummary([]), "System");
  assert.equal(formatEventBatchWorkerSummary([event()]), "worker-1");
  assert.equal(formatEventBatchWorkerSummary([event(), event({ workerId: { value: "worker-2" } })]), "2 workers");
  assert.equal(formatEventByteCount(1200), "1,200b");
  assert.equal(formatEventByteCount(1200, true), ">=1,200b");
  assert.equal(clampEventTableHeight(40), 96);
  assert.equal(clampEventTableHeight(600), 520);
  assert.equal(normalizeEventViewerMaxMessages("bad"), 100);
  assert.equal(normalizeEventViewerMaxMessages("0"), 1);
  assert.equal(normalizeEventViewerMaxMessages("2500"), 1000);
  assert.equal(eventTypeTone("worker.failed"), semanticBadgeToneClass("danger"));
  assert.equal(eventTypeTone("worker.completed"), semanticBadgeToneClass("success"));
  assert.equal(eventTypeTone("worker.waiting"), semanticBadgeToneClass("info"));
  assert.equal(eventTypeTone("worker.cancel.requested"), semanticBadgeToneClass("warning"));

  const search = getRealtimeEventSearchText({
    batchId: "batch-1",
    batchSize: 2,
    bytes: 10,
    events: [event({ subjectId: { type: "Order", value: "100" } })],
    eventTypes: ["worker.completed"],
    id: "message-1",
    receivedAt: Date.now(),
    value: { ok: true } as never,
  });
  assert.equal(search.includes("batch-1 2 worker.completed"), true);
  assert.equal(search.includes("order 100"), true);
});

test("console storage normalization preserves valid UI state and falls back for invalid persisted values", () => {
  const defaults = createDefaultConsoleStorage();
  assert.equal(defaults.view, "overview");
  assert.deepEqual(defaults.hosts, []);
  assert.deepEqual(Object.keys(createDefaultOverviewPanelShapes()).sort(), [
    "completedIterations",
    "failedIterations",
    "failedWorkers",
    "iterations",
    "throughput",
    "workers",
  ]);
  assert.equal(normalizeOverviewPanelShape("throughput", "compact"), "compact");
  assert.equal(normalizeOverviewPanelShape("workers", "unsupported"), "standard");
  assert.equal(normalizeOverviewPanelShapes({ throughput: "compact", workers: "bad" }).throughput, "compact");
  assert.deepEqual(normalizeOverviewHiddenPanels(["throughput", "workers", "bad"]), ["workers", "throughput"]);
  assert.deepEqual(normalizeOverviewHiddenPanels([], true), ["throughput"]);
  assert.deepEqual(normalizeOverviewPanelIds(["workers", "bad", "throughput"]), ["workers", "throughput"]);
  assert.equal(isThroughputSeriesId("failed"), true);
  assert.equal(isThroughputSeriesId("other"), false);
  assert.deepEqual(
    normalizeThroughputSeriesIds(["started", "completed", "failed", "canceled", "other"]),
    ["completed", "failed", "canceled"]
  );
});

test("stored host and system helpers normalize legacy realtime metadata, access, and lookup behavior", () => {
  const normalizedLocal = normalizeStoredHost({
    apiUrl: "",
    id: "local-sample-host",
    name: "",
    realtimeEnabled: false,
    systems: [
      {
        ...system({ id: "", name: "", systemName: "  Ops  " }),
        realtimeEnabled: true,
        realtimeHubPath: "/legacy",
        realtimeTransport: "legacy",
      } as WorkableSystemConnection & {
        realtimeEnabled: boolean;
        realtimeHubPath: string;
        realtimeTransport: string;
      },
    ],
  });
  assert.equal(normalizedLocal.name, "Workable host");
  assert.equal(normalizedLocal.apiUrl.length > 0, true);
  assert.equal(normalizedLocal.realtimeEnabled, true);
  assert.equal(normalizedLocal.realtimeHubPath, "/workable/realtime");
  assert.equal(normalizedLocal.realtimeTransport, "signalr");
  assert.equal(normalizedLocal.systems[0].name, "Default");
  assert.equal(normalizedLocal.systems[0].systemName, "Ops");
  assert.equal(normalizedLocal.systems[0].access?.canControlSystem, true);

  const normalizedSystem = normalizeStoredSystem("host-2", {
    hostId: "old",
    id: "system-2",
    name: "",
    capabilities: {
      persistentCoordinationAvailable: true,
      sqlProfilingAvailable: false,
    },
    state: undefined,
  });
  assert.equal(normalizedSystem.hostId, "host-2");
  assert.equal(normalizedSystem.name, "Default");
  assert.equal(normalizedSystem.state, null);
  assert.equal(normalizedSystem.capabilities.persistentCoordinationAvailable, true);
  assert.equal(createDefaultSystem("host-3").hostId, "host-3");
  assert.equal(createFullAccessSummary().canOperateAllWork, true);
  assert.equal(normalizeOptional("  Ops "), "Ops");
  assert.equal(normalizeOptional(" "), undefined);

  const storage: ConsoleStorage = {
    ...createDefaultConsoleStorage(),
    hosts: [
      host({ id: "empty", systems: [] }),
      host({ id: "host-1", systems: [system({ id: "system-1" })] }),
    ],
  };
  assert.equal(findSystemLocation(storage, "system-1")?.host.id, "host-1");
  assert.equal(findSystemLocation(storage, "missing")?.system.id, "system-1");
  assert.equal(getFirstAvailableSystemId(storage.hosts), "system-1");
  assert.equal(getViewReadinessKey("system-1", "workers"), "system-1:workers");
  assert.equal(isServerView("workers"), true);
  assert.equal(isServerView("worker"), false);
  assert.equal(navTitle("worker"), "Worker Console");
  assert.equal(navTitle("workflowRun"), "Workflow Run");
  assert.equal(headerRefreshTitle("definition"), "Refresh definition");
  assert.equal(headerRefreshTitle("workflowRun"), "Refresh workflow run");
  assert.deepEqual(cloneOverviewScope({ category: " Ops ", includeSubcategories: true }), {
    category: "Ops",
    definitionName: undefined,
    includeSubcategories: true,
  });
});

test("diagnostics target and notification helpers deduplicate targets and describe alert branches", () => {
  const access = {
    ...createFullAccessSummary(),
    canViewDiagnostics: true,
  };
  const targets = createDiagnosticsAlertTargets([
    host({
      systems: [
        system({ access, id: "system-1", name: "Default", systemName: undefined }),
        system({ access, id: "system-2", name: "Default copy", systemName: undefined }),
        system({ access: { ...access, canViewDiagnostics: false }, id: "system-3", name: "Hidden" }),
      ],
    }),
  ]);
  assert.deepEqual(targets.map((target) => target.id), [
    createDiagnosticsAlertTargetId("https://workable.test", "/hub", undefined),
  ]);

  const notifications = createSystemNotifications(
    { isShuttingDown: true } as never,
    {
      alertableRejectedWorkCount: 3,
      hasAlertableRejectedWork: true,
      lastAlertableRejectedMessage: "No capacity",
    } as never,
    1,
    {
      hasProjectorFailure: true,
      isReadModelBehind: true,
      pendingUpdateCount: 101,
      projectorFailureMessage: "boom",
      projectorFailureType: "ProjectionException",
      readModelLagWarningThreshold: 10,
    } as never,
    {
      hasSchedulerFailure: true,
      isRetentionBehind: true,
      oldestDuePurgeAge: "00:20:00",
      retentionLagWarningSeconds: 60,
      schedulerFailureType: "SchedulerException",
    } as never,
    {
      concurrencyLagWarningSeconds: 30,
      deferredStartCount: 2,
      isConcurrencyBehind: true,
      oldestDeferredStartAge: "00:10:00",
    } as never,
    {
      acceptedWaiterCount: 2,
      acceptedWorkerWarningSeconds: 30,
      cleanupFailureType: "CleanupException",
      cleanupWarningSeconds: 30,
      hasCleanupFailure: true,
      hasLeaseRenewalFailure: true,
      hasReaderFailure: true,
      isAcceptedWorkerMaterializationBehind: true,
      isCleanupBehind: true,
      leaseRenewalFailureType: "LeaseException",
      oldestAcceptedWaiterAge: "00:10:00",
      oldestPendingCleanupAge: "00:10:00",
      pendingCleanupCount: 4,
      readerFailureType: "ReaderException",
    } as never,
    "Realtime disconnected",
    targets[0]
  );
  assert.deepEqual(
    notifications.map((notification) => notification.title),
    [
      "Default @ Workable: System is shutting down",
      "Default @ Workable: Diagnostics unavailable",
      "Default @ Workable: Work is being rejected",
      "Default @ Workable: Read model projector failed",
      "Default @ Workable: Read model is behind",
      "Default @ Workable: Retention scheduler failed",
      "Default @ Workable: Retention is behind",
      "Default @ Workable: Concurrency is backed up",
      "Default @ Workable: Durable reader failed",
      "Default @ Workable: Durable lease renewal failed",
      "Default @ Workable: Durable cleanup failed",
      "Default @ Workable: Durable worker materialization is behind",
      "Default @ Workable: Durable cleanup is behind",
    ]
  );
  assert.equal(notifications[0]?.dismissible, true);
});

test("dismissed shutdown notifications reappear after that shutdown warning clears", () => {
  const notifications = createSystemNotifications(
    { isShuttingDown: true } as never,
    undefined,
    0,
    undefined,
    undefined,
    undefined,
    undefined,
    undefined,
    {
      apiUrl: "https://workable.test",
      displayName: "Default @ Workable",
      id: "target-default",
      systemName: "Default",
    }
  );
  const dismissalKey = systemNotificationDismissalKey(notifications[0]!);

  assert.deepEqual(
    applySystemNotificationDismissals(notifications, new Set([dismissalKey]), () => undefined),
    []
  );
  assert.equal(
    pruneDismissedSystemNotificationKeys(new Set([dismissalKey]), notifications).has(dismissalKey),
    true
  );
  assert.equal(
    pruneDismissedSystemNotificationKeys(new Set([dismissalKey]), []).has(dismissalKey),
    false
  );
});

test("compact diagnostics and alert snapshot helpers preserve fallbacks and transition checks", () => {
  assert.deepEqual(createCompactReadModelDiagnosticsFromDetailed({
    isReadModelBehind: true,
    pendingUpdateCount: 0,
    readModel: {
      hasProjectorFailure: true,
      pendingUpdateCount: 5,
      projectorFailureMessage: "boom",
    },
    readModelLagWarningThreshold: 10,
  } as never), {
    hasProjectorFailure: true,
    isReadModelBehind: true,
    pendingUpdateCount: 5,
    projectorFailureMessage: "boom",
    projectorFailureType: undefined,
    readModelLagWarningThreshold: 10,
  });
  assert.equal(createCompactReadModelDiagnosticsFromDetailed(undefined), undefined);
  assert.equal(createCompactRetentionDiagnosticsFromDetailed({ isRetentionBehind: false, retentionLagWarningSeconds: 60 } as never)?.oldestDuePurgeAge, "00:00:00");
  assert.equal(createCompactConcurrencyDiagnosticsFromDetailed({ concurrencyLagWarningSeconds: 30, isConcurrencyBehind: false } as never)?.oldestDeferredStartAge, "00:00:00");
  assert.equal(createCompactDurabilityDiagnosticsFromDetailed({ acceptedWorkerWarningSeconds: 30, cleanupWarningSeconds: 30, isAcceptedWorkerMaterializationBehind: false, isCleanupBehind: false } as never)?.pendingCleanupCount, 0);
  assert.deepEqual(createCompactIdempotencyDiagnosticsFromDetailed({ idempotency: { duplicateRejectionCount: 2 } } as never), {
    duplicateRejectionCount: 2,
    lastDuplicateRejectedStorage: undefined,
  });

  const previous = {
    connectionState: "connected",
    data: {
      components: {
        systemDiagnostics: {
          data: { isShuttingDown: false, systemState: "Stopped" },
          status: "ok",
        },
      },
      generatedAt: "2026-05-30T10:00:00.000Z",
    },
    enabled: true,
    loading: false,
  };
  const next = {
    ...previous,
    data: {
      components: {
        systemDiagnostics: {
          data: { isShuttingDown: true, systemState: "Stopping" },
          status: "ok",
        },
      },
      generatedAt: "2026-05-30T10:00:01.000Z",
    },
  };
  assert.equal(shouldClearDefinitionCatalogCacheForDiagnosticsTransition(previous, next), true);
  assert.equal(diagnosticsAlertSnapshotsEqual(previous, previous), true);
  assert.equal(diagnosticsAlertSnapshotsEqual(previous, { ...previous }), true);
  assert.equal(diagnosticsAlertSnapshotsEqual(previous, { ...previous, loading: true }), false);
});

test("navigation entries compare every persisted selection field", () => {
  const left = navigationEntry({
    catalogScope: { category: "Ops" },
    overviewScope: { category: "Ops" },
  });
  const right = navigationEntry({
    catalogScope: { category: " Ops " },
    overviewScope: { category: "Ops" },
  });
  assert.equal(navigationEntriesEqual(left, right), true);
  assert.equal(navigationEntriesEqual(undefined, left), false);
  assert.equal(navigationEntriesEqual(left, { ...right, workerId: "worker-1" }), false);
  assert.equal(navigationEntriesEqual(left, { ...right, workflowRunId: "run-1" }), false);
  assert.equal(navigationEntriesEqual(left, { ...right, iterationStatusFilter: ["Failed"] }), false);
});
