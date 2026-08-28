import assert from "node:assert/strict";
import test from "node:test";
import { createElement } from "react";
import {
  applyQueueConfigurationRules,
  capWorkerLogEntries,
  classifyStackTraceLine,
  cloneConfiguration,
  cloneJsonValue,
  cloneQueueWorkRequest,
  cloneTypedValue,
  cloneTypedValues,
  cloneWorkerOptions,
  compareLogLikeEntries,
  compareWorkerLogEntries,
  configurationValuesEqual,
  createCollapsedStackCounts,
  createConfigurationFieldSections,
  createCopiedWorkerQueueRequest,
  createDefaultQueueRequest,
  createDefaultWorkerHiddenPanels,
  createDefinitionConfigurationDescriptor,
  createEffectiveConfigurationOptions,
  createIterationDurationGraphScale,
  createIterationFocusedHiddenPanels,
  createIterationOverviewActivityQuery,
  createIterationOverviewPath,
  createQueueDialogRequest,
  createSelectedLogLevelsForFocus,
  createSelectedTimelineFiltersForFocus,
  createStackTraceDisplayEntries,
  createWorkerConfigurationDescriptor,
  createWorkerConfigurationDifferences,
  createWorkerConfigurationRequest,
  createWorkerFocusedHiddenPanels,
  createWorkerOverviewActivityQuery,
  createWorkerOverviewLogsPath,
  createWorkerOverviewPath,
  createWorkerOverviewTimelinePath,
  IterationConsoleView,
  createWorkerReconfiguration,
  defaultWorkConfiguration,
  definitionMatchesCatalogScope,
  descriptionForFieldSection,
  detailCompletionTone,
  filterWorkerOverviewLogEntriesBySelectedLevels,
  findTabBasePath,
  formatCollapsedStackEntry,
  formatCompactTimelineRelativeTime,
  formatConfigurationValue,
  formatDurationLabel,
  formatElapsedSince,
  formatFutureRelativeTime,
  formatMessageSeverity,
  formatMillisecondsCompact,
  formatWorkerTimelineBadgeLabel,
  getAvailableWorkerActions,
  getFieldSectionId,
  getIterationDurationGraphBarHeight,
  getIterationDurationMilliseconds,
  getPreferredWorkerOverviewRecentIteration,
  getQueueConfigurationFieldConstraint,
  getStackTraceLines,
  getValueAtPath,
  getWorkerLogServerPageWindowAdvanceCount,
  getWorkerLogStreamCardClassName,
  getWorkerLogStreamViewportClassName,
  humanizePathSegment,
  isDefaultWorkerLogQuery,
  isDefaultWorkerTimelineQuery,
  isFinalIterationStatus,
  isPersistentConcurrencyActive,
  isPlainObject,
  labelForFieldSection,
  mapTimelineFilterKindToServerCategory,
  mergeWorkerOverviewItemsById,
  mergeWorkerOverviewRealtimeEntries,
  mergeWorkerOverviewRecentIterations,
  messageSeverityFilterTone,
  messageSeverityTone,
  normalizeMessageSeverity,
  normalizeMessageSeverityLabel,
  normalizeSelectedLogLevelsForRequest,
  normalizeSelectedTimelineFiltersForRequest,
  normalizeVisibleWorkerTimelineItems,
  parseDurationMilliseconds,
  parseJsonText,
  parseOptionalObjectJson,
  parseQueueJson,
  parseSchemaJsonValue,
  retainLatestWorkerTimelineRealtimeItems,
  restoreBaseCursorPaginationAfterCompaction,
  serializeWorkerLogQuery,
  serializeWorkerTimelineQuery,
  shouldForgetPagedIterationMessages,
  shouldForgetPagedWorkerTimelineItems,
  shouldForgetPushedWorkerTimelineItems,
  shouldRetainVisibleWorkerWaitingTile,
  sortWorkerLogEntries,
  sortWorkerOverviewLogEntries,
  splitCategoryPath,
  stackFrameFilterTone,
  stackFrameKindLabel,
  stackTraceLineTone,
  startsWithCategoryPath,
  stopAutomaticCursorPaginationAfterError,
  stripInvocationConfiguration,
  summarizeWorkerLogEntries,
  updateSelectedLogLevels,
  updateSelectedTimelineFilters,
  workerActionToneClassName,
  workerStatusTextTone,
} from "@/components/workable/console/detail-screens";
import { renderDom } from "@/test/dom";
import {
  semanticBadgeToneClass,
  semanticTextToneClass,
} from "@/lib/ui/state-tones";
import type {
  QueueRequestSchemaDescriptor,
  QueueWorkRequest,
  WorkConfiguration,
  WorkDefinition,
  WorkProfileSnapshot,
  WorkWorkerIterationOverviewComponent,
  WorkWorkerOverviewLogEntry,
  WorkWorkerOverviewRecentIteration,
  WorkWorkerOverviewTimelineItem,
  WorkableConnection,
  WorkerIterationSnapshot,
  WorkerLogEntry,
} from "@/lib/workable";

const connection: WorkableConnection = {
  apiUrl: "https://console.example.com/workable",
  systemName: "Ops",
};

function descriptor(): QueueRequestSchemaDescriptor {
  return {
    schema: { jsonSchema: "{}" },
    tabs: [
      {
        description: "Coordination",
        fields: [
          {
            description: "Enabled",
            label: "Enabled",
            path: "options.configuration.coordination.isEnabled",
          },
          {
            description: "Concurrency enabled",
            label: "Concurrency enabled",
            path: "options.configuration.coordination.concurrency.isEnabled",
          },
          {
            description: "Durability enabled",
            label: "Durability enabled",
            path: "options.configuration.coordination.durability.isEnabled",
          },
          {
            description: "Input",
            label: "Input",
            path: "input",
          },
        ],
        id: "coordination",
        label: "Coordination",
      },
      {
        description: "Queue",
        fields: [
          {
            description: "Completion",
            label: "Completion",
            path: "completion",
          },
          {
            description: "Profiling",
            label: "Profiling",
            path: "options.profilingEnabled",
          },
        ],
        id: "queue",
        label: "Queue",
      },
    ],
  };
}

function definition(overrides: Partial<WorkDefinition> = {}): WorkDefinition {
  return {
    id: { value: "definition-1" },
    name: "ImportOrders",
    revision: 1,
    ...overrides,
  };
}

function persistentConcurrencyConfiguration(): WorkConfiguration {
  const configuration = cloneConfiguration(defaultWorkConfiguration);
  configuration.coordination.storage = "Persistent";
  configuration.coordination.concurrency.isEnabled = true;
  return configuration;
}

function overviewLog(overrides: Partial<WorkWorkerOverviewLogEntry>): WorkWorkerOverviewLogEntry {
  return {
    category: "Worker",
    eventId: 1,
    id: "log-1",
    level: "Information",
    message: "hello",
    occurredAt: "2026-05-30T10:00:00.000Z",
    ...overrides,
  };
}

function overviewTimelineItem(overrides: Partial<WorkWorkerOverviewTimelineItem>): WorkWorkerOverviewTimelineItem {
  return {
    at: "2026-05-30T10:00:00.000Z",
    category: "SystemEvent",
    id: "timeline-1",
    kind: "StateChange",
    ...overrides,
  };
}

function workerLog(overrides: Partial<WorkerLogEntry>): WorkerLogEntry {
  return {
    category: "Worker",
    id: "log-1",
    level: "Information",
    message: "hello",
    occurredAt: "2026-05-30T10:00:00.000Z",
    ...overrides,
  };
}

function recentIteration(overrides: Partial<WorkWorkerOverviewRecentIteration>): WorkWorkerOverviewRecentIteration {
  return {
    attemptCount: 1,
    startedAt: "2026-05-30T10:00:00.000Z",
    status: "Completed",
    workerId: { value: "worker-1" },
    sequence: 1,
    ...overrides,
  };
}

function iteration(overrides: Partial<WorkerIterationSnapshot>): WorkerIterationSnapshot {
  return {
    attemptCount: 1,
    isFinal: true,
    occurredAt: "2026-05-30T10:00:00.000Z",
    sequence: 1,
    status: "Completed",
    ...overrides,
  };
}

test("configuration section helpers group fields and filter descriptors for definition and worker editing", () => {
  const source = descriptor();
  const coordinationTab = source.tabs[0];
  const queueTab = source.tabs[1];

  assert.equal(findTabBasePath(coordinationTab), "options.configuration.coordination");
  assert.equal(findTabBasePath(queueTab), "");
  assert.equal(
    getFieldSectionId("options.configuration.coordination.concurrency.isEnabled", "options.configuration.coordination"),
    "options.configuration.coordination.concurrency"
  );
  assert.equal(getFieldSectionId("input.value", ""), "input");
  assert.equal(labelForFieldSection("root", coordinationTab), "Coordination settings");
  assert.equal(labelForFieldSection("options.configuration.coordination.durability", coordinationTab), "Durability");
  assert.equal(humanizePathSegment("transientRetry"), "Transient Retry");
  assert.equal(
    descriptionForFieldSection("options.configuration.coordination.concurrency", coordinationTab),
    "Capacity rules that decide how many workers may occupy the same group and whether extra work waits or is rejected."
  );

  assert.deepEqual(
    createConfigurationFieldSections(coordinationTab).map((section) => ({
      fieldCount: section.fields.length,
      id: section.id,
      label: section.label,
    })),
    [
      { fieldCount: 2, id: "root", label: "Coordination settings" },
      { fieldCount: 1, id: "options.configuration.coordination.concurrency", label: "Concurrency" },
      { fieldCount: 1, id: "options.configuration.coordination.durability", label: "Durability" },
    ]
  );
  assert.deepEqual(
    createDefinitionConfigurationDescriptor(source)?.tabs.map((tab) => ({
      fields: tab.fields.map((field) => field.path),
      id: tab.id,
    })),
    [
      {
        fields: [
          "options.configuration.coordination.isEnabled",
          "options.configuration.coordination.concurrency.isEnabled",
          "options.configuration.coordination.durability.isEnabled",
        ],
        id: "coordination",
      },
      { fields: ["options.profilingEnabled"], id: "queue" },
    ]
  );
  assert.deepEqual(
    createWorkerConfigurationDescriptor(source)?.tabs.map((tab) => ({
      fields: tab.fields.map((field) => field.path),
      id: tab.id,
    })),
    [
      {
        fields: [
          "options.configuration.coordination.isEnabled",
          "options.configuration.coordination.concurrency.isEnabled",
          "options.configuration.coordination.durability.isEnabled",
        ],
        id: "coordination",
      },
      { fields: ["options.profilingEnabled"], id: "queue" },
    ]
  );
  assert.equal(createDefinitionConfigurationDescriptor(null), null);
  assert.equal(createWorkerConfigurationDescriptor({ ...source, tabs: [{ ...queueTab, fields: [queueTab.fields[0]] }] }), null);
});

test("detail tone and action helpers cover status, completion, severity, and worker action options", () => {
  assert.equal(workerStatusTextTone("Queued"), semanticTextToneClass("info", "strong"));
  assert.equal(workerStatusTextTone("Waiting"), semanticTextToneClass("info", "strong"));
  assert.equal(workerStatusTextTone("Retrying"), semanticTextToneClass("warning", "strong"));
  assert.equal(workerStatusTextTone("Failed"), semanticTextToneClass("danger", "strong"));
  assert.equal(workerStatusTextTone("Completed"), semanticTextToneClass("success", "strong"));
  assert.equal(workerStatusTextTone("Canceled"), semanticTextToneClass("warning", "strong"));
  assert.equal(detailCompletionTone("Completed"), semanticBadgeToneClass("success"));
  assert.equal(detailCompletionTone("Executing"), semanticBadgeToneClass("info"));
  assert.equal(detailCompletionTone("Failed"), semanticBadgeToneClass("danger"));
  assert.equal(detailCompletionTone("Paused"), semanticBadgeToneClass("warning"));
  assert.equal(detailCompletionTone("Canceled"), semanticBadgeToneClass("warning"));
  assert.equal(messageSeverityTone("critical"), semanticBadgeToneClass("danger"));
  assert.equal(messageSeverityTone("ERROR"), semanticBadgeToneClass("danger"));
  assert.equal(messageSeverityTone("warning"), semanticBadgeToneClass("warning"));
  assert.equal(messageSeverityTone("information"), semanticBadgeToneClass("info"));
  assert.equal(messageSeverityFilterTone("debug"), semanticBadgeToneClass("neutral"));
  assert.equal(messageSeverityFilterTone("trace"), semanticBadgeToneClass("neutral"));
  assert.equal(workerActionToneClassName("Start", true), "");
  assert.ok(workerActionToneClassName("Start", false).includes("hover:bg-muted/35"));
  assert.deepEqual(getAvailableWorkerActions("Pausing"), {
    Start: false,
    Pause: false,
    Cancel: false,
    Push: false,
    Purge: false,
  });
  assert.deepEqual(getAvailableWorkerActions("Waiting"), {
    Start: false,
    Pause: true,
    Cancel: true,
    Push: true,
    Purge: false,
  });
  assert.deepEqual(getAvailableWorkerActions("Queued"), {
    Start: true,
    Pause: true,
    Cancel: true,
    Push: false,
    Purge: false,
  });
  assert.deepEqual(getAvailableWorkerActions("Completed"), {
    Start: false,
    Pause: false,
    Cancel: false,
    Push: false,
    Purge: true,
  });
});

test("stack trace helpers normalize severities, fold hidden frames, and label tones", () => {
  assert.equal(normalizeMessageSeverity(" Information "), "information");
  assert.equal(normalizeMessageSeverityLabel("info"), "Information");
  assert.equal(normalizeMessageSeverityLabel(""), "Unknown");
  assert.equal(formatMessageSeverity("Information"), "Info");
  assert.deepEqual(getStackTraceLines("detail\r\nat App.Run()\n\nat System.Task()  "), [
    "detail",
    "at App.Run()",
    "at System.Task()",
  ]);
  assert.equal(classifyStackTraceLine("detail"), "detail");
  assert.equal(classifyStackTraceLine("at Workable.Runner()"), "work");
  assert.equal(classifyStackTraceLine("at Microsoft.Extensions.Runner()"), "library");
  assert.equal(classifyStackTraceLine("at MyApp.Runner()"), "application");

  const entries = createStackTraceDisplayEntries(
    [
      "Failure detail",
      "at Workable.Runner()",
      "at System.Threading.Task()",
      "at MyApp.Runner()",
    ],
    ["work", "library"]
  );
  assert.deepEqual(entries, [
    { kind: "detail", line: "Failure detail", type: "line" },
    {
      counts: { application: 0, library: 1, work: 1 },
      total: 2,
      type: "collapsed",
    },
    { kind: "application", line: "at MyApp.Runner()", type: "line" },
  ]);
  assert.deepEqual(createCollapsedStackCounts(), { application: 0, library: 0, work: 0 });
  assert.equal(formatCollapsedStackEntry(entries[1] as Extract<typeof entries[number], { type: "collapsed" }>), "2 collapsed frames: 1 workable, 1 library");
  assert.equal(stackFrameKindLabel("application"), "App");
  assert.ok(stackFrameFilterTone("application", false).includes("emerald"));
  assert.ok(stackFrameFilterTone("work", false).includes("cyan"));
  assert.ok(stackFrameFilterTone("library", true).includes("text-slate-500"));
  assert.ok(stackTraceLineTone("detail").includes("red"));
});

test("worker overview path and filter helpers serialize log and timeline options", () => {
  assert.equal(createWorkerOverviewPath("worker-1"), "workers/worker-1/overview");
  const query = createWorkerOverviewActivityQuery({
    activity: "Logs",
    activityCursor: "cursor 1",
    activityTake: 25,
    logIterationSequence: 3,
    logLevels: ["Error", "Warning"],
    logSortDirection: "asc",
    timelineFilters: ["failures", "user"],
    timelineSortDirection: "desc",
  });
  assert.equal(
    query,
    "activity=Logs&activityTake=25&activityCursor=cursor+1&logLevels=Error%2CWarning&logIterationSequence=3&logSort=Asc&timelineCategories=Failure%2CUserAction&timelineSort=Desc"
  );
  assert.equal(createWorkerOverviewPath("worker-1", { activity: "Logs" }), "workers/worker-1/overview?activity=Logs");
  assert.equal(createWorkerOverviewLogsPath("worker-1", { logSortDirection: "desc" }), "workers/worker-1/overview/logs?logSort=Desc");
  assert.equal(createWorkerOverviewTimelinePath("worker-1", { timelineSortDirection: "asc" }), "workers/worker-1/overview/timeline?timelineSort=Asc");
  assert.equal(serializeWorkerLogQuery(null, "desc"), "desc:Critical,Error,Warning,Information,Debug,Trace");
  assert.equal(serializeWorkerTimelineQuery(null, "desc"), "desc:user,system,failures");
  assert.equal(isDefaultWorkerLogQuery(null, "desc"), true);
  assert.equal(isDefaultWorkerLogQuery(["Error"], "desc"), false);
  assert.equal(isDefaultWorkerTimelineQuery(null, "desc"), true);
  assert.equal(normalizeSelectedLogLevelsForRequest(["Critical", "Error", "Warning", "Information", "Debug", "Trace"]), null);
  assert.deepEqual(normalizeSelectedLogLevelsForRequest(["Warning", "Error"]), ["Error", "Warning"]);
  assert.deepEqual(updateSelectedLogLevels(null, "Trace", false), ["Critical", "Error", "Warning", "Information", "Debug"]);
  assert.deepEqual(updateSelectedLogLevels(["Trace"], "Trace", false), ["Trace"]);
  assert.deepEqual(createSelectedLogLevelsForFocus("Error"), ["Error"]);
  assert.equal(normalizeSelectedTimelineFiltersForRequest(["user", "system", "failures"]), null);
  assert.deepEqual(normalizeSelectedTimelineFiltersForRequest(["failures", "user"]), ["user", "failures"]);
  assert.deepEqual(updateSelectedTimelineFilters(null, "system", false), ["user", "failures"]);
  assert.deepEqual(updateSelectedTimelineFilters(["system"], "system", false), ["system"]);
  assert.deepEqual(createSelectedTimelineFiltersForFocus("failures"), ["failures"]);
  assert.equal(mapTimelineFilterKindToServerCategory("failures"), "Failure");
  assert.equal(mapTimelineFilterKindToServerCategory("user"), "UserAction");
  assert.equal(mapTimelineFilterKindToServerCategory("system"), "SystemEvent");
});

test("iteration overview path helpers serialize panel-aware options", () => {
  assert.equal(createIterationOverviewPath("worker-1", 7), "workers/worker-1/iterations/7/overview");
  assert.equal(
    createIterationOverviewActivityQuery({
      activity: "None",
      includeInput: false,
      includeOutput: false,
      includeProfile: false,
    }),
    "activity=None&includeInput=false&includeOutput=false&includeProfile=false"
  );
  assert.equal(
    createIterationOverviewPath("worker-1", 7, {
      activity: "Logs",
      activityTake: 25,
      logLevels: ["Error", "Warning"],
      logSortDirection: "asc",
    }),
    "workers/worker-1/iterations/7/overview?activity=Logs&activityTake=25&logSort=Asc&logLevels=Error%2CWarning"
  );
});

test("iteration console view loads the overview landing response and renders the profile panel from it", async () => {
  const fetchMock = installIterationOverviewFetch((call) => {
    if (call.input === "/api/workable/systems/Ops/workers/worker-1/iterations/7/overview?activity=Logs") {
      return Response.json(iterationOverviewComponent());
    }

    return Response.json({ error: `Unhandled request: ${call.input}` }, { status: 500 });
  });
  const result = await renderDom(
    createElement(IterationConsoleView, {
      connection,
      onNavigateBack: () => undefined,
      onOpenDefinition: () => undefined,
      refreshToken: 0,
      sequence: 7,
      workerId: "worker-1",
    })
  );

  try {
    await result.waitFor(() => result.getByText("Worker input"));
    await result.waitFor(() => result.getByText("Iteration output"));
    await result.waitFor(() => result.getByText("Profile"));
    await result.waitFor(() => result.getByText("Executing DemoProfilingSectionWorker.RunAsync"));
    await result.waitFor(() => result.getByText('"orderId"'));
    await result.waitFor(() => result.getByText('"processed"'));
    result.getByRole("textbox", { name: "Search profile nodes" });

    assert.deepEqual(
      fetchMock.calls.map((call) => call.input),
      ["/api/workable/systems/Ops/workers/worker-1/iterations/7/overview?activity=Logs"]
    );
  } finally {
    fetchMock.restore();
    await result.restore();
  }
});

test("iteration log pagination ignores a successful page from an older connection generation", async () => {
  let overviewRequestCount = 0;
  let resolveStaleLogPage: ((response: Response) => void) | undefined;
  const staleLogPage = new Promise<Response>((resolve) => {
    resolveStaleLogPage = resolve;
  });
  const fetchMock = installIterationOverviewFetch((call) => {
    if (call.input.includes("/workers/worker-1/iterations/7/overview/logs?")) {
      return staleLogPage;
    }

    if (call.input.endsWith("/workers/worker-1/iterations/7/overview?activity=Logs")) {
      overviewRequestCount += 1;
      const overview = iterationOverviewComponent();
      const logPage = overview.logs.page;
      assert.ok(logPage);
      logPage.cursor = `cursor-${overviewRequestCount}`;
      logPage.hasMore = true;
      return Response.json(overview);
    }

    return Response.json({ error: `Unhandled request: ${call.input}` }, { status: 500 });
  });
  const element = (activeConnection: WorkableConnection) => createElement(IterationConsoleView, {
    connection: activeConnection,
    onNavigateBack: () => undefined,
    onOpenDefinition: () => undefined,
    refreshToken: 0,
    sequence: 7,
    workerId: "worker-1",
  });
  const result = await renderDom(element(connection));

  try {
    await result.waitFor(() => result.getByText("Scroll to load more"));
    const loadMoreLabel = result.getByText("Scroll to load more");
    const viewport = loadMoreLabel.parentElement?.parentElement;
    assert.ok(viewport instanceof result.dom.window.HTMLElement);
    await result.scroll(viewport, {
      clientHeight: 100,
      scrollHeight: 240,
      scrollTop: 160,
    });
    await result.waitFor(() => {
      assert.equal(
        fetchMock.calls.some((call) =>
          call.input.includes("/workers/worker-1/iterations/7/overview/logs?")
        ),
        true
      );
    });

    await result.rerender(element({ ...connection, systemName: "Beta" }));
    await result.waitFor(() => assert.equal(overviewRequestCount, 2));
    resolveStaleLogPage?.(Response.json({
      page: {
        cursor: null,
        hasMore: false,
        items: [workerLog({ id: "stale-log", message: "Stale Alpha log" })],
      },
      summary: iterationOverviewComponent().logs.summary,
    }));
    await new Promise((resolve) => setTimeout(resolve, 25));

    assert.equal(result.queryByText("Stale Alpha log"), null);
    result.getByText("Scroll to load more");
  } finally {
    resolveStaleLogPage?.(
      Response.json({ error: "Test cleanup" }, { status: 500 })
    );
    fetchMock.restore();
    await result.restore();
  }
});

test("queue json and configuration helpers parse, clone, sanitize, and enforce persistent concurrency rules", () => {
  assert.equal(parseSchemaJsonValue(""), null);
  assert.deepEqual(parseSchemaJsonValue("{\"type\":\"object\"}"), { type: "object" });
  assert.equal(parseSchemaJsonValue("{oops"), "{oops");
  assert.deepEqual(parseJsonText(" "), { ok: true, value: null });
  assert.deepEqual(parseJsonText("{\"ok\":true}"), { ok: true, value: { ok: true } });
  assert.equal(parseJsonText("{oops").ok, false);
  assert.equal(parseQueueJson(" "), undefined);
  assert.deepEqual(parseQueueJson("[1,2]"), [1, 2]);
  assert.throws(() => parseQueueJson("{oops"), /Input must be valid JSON/);
  assert.equal(parseOptionalObjectJson("{}", "Input"), undefined);
  assert.deepEqual(parseOptionalObjectJson<{ ok: boolean }>("{\"ok\":true}", "Input"), { ok: true });
  assert.throws(() => parseOptionalObjectJson("[]", "Input"), /Input must be a JSON object/);
  assert.equal(isPlainObject({}), true);
  assert.equal(isPlainObject([]), false);

  const configuration = persistentConcurrencyConfiguration();
  const request = createQueueDialogRequest(null, {
    concurrencyKey: { type: "Order", value: "" },
    options: {
      configuration,
      profilingEnabled: true,
    },
    subjectId: { type: "", value: "123" },
  });
  assert.equal(request.subjectId, undefined);
  assert.equal(request.concurrencyKey, undefined);
  assert.equal(request.options?.profilingEnabled, true);
  assert.equal(request.options?.configuration?.coordination.concurrency.blockingMode, "WhileExecuting");
  assert.equal(request.options?.configuration?.coordination.concurrency.limitReachedBehavior, "DeferStart");
  assert.equal(request.options?.configuration?.coordination.durability.isEnabled, true);
  assert.equal(isPersistentConcurrencyActive(request), true);
  assert.deepEqual(
    getQueueConfigurationFieldConstraint(request, "options.configuration.coordination.durability.isEnabled"),
    {
      reason: "Persistent concurrency requires durable queueing so accepted workers can wait safely for capacity.",
      value: true,
    }
  );
  assert.equal(getQueueConfigurationFieldConstraint(createDefaultQueueRequest(null), "options.configuration.coordination.durability.isEnabled"), null);

  const stripped = stripInvocationConfiguration({
    ...cloneConfiguration(defaultWorkConfiguration),
    invocation: { secret: true },
  } as WorkConfiguration & { invocation: unknown });
  assert.equal("invocation" in stripped, false);
  assert.notEqual(cloneConfiguration(defaultWorkConfiguration), defaultWorkConfiguration);
  assert.deepEqual(createWorkerReconfiguration({ options: { configuration, profilingEnabled: true } }).coordination?.storage, "Persistent");
  assert.equal(createEffectiveConfigurationOptions(definition({ defaultOptions: { profilingEnabled: true } })).profilingEnabled, true);
  assert.deepEqual(createDefaultQueueRequest(null).completion, "ReturnAfterAccepted");
});

test("worker configuration request helpers diff values and clone queue state without mutating input", () => {
  const configuration = cloneConfiguration(defaultWorkConfiguration);
  configuration.logging.level = "Debug";
  const workerConfiguration = {
    configuration,
    profilingCaptureMode: "Bounded" as const,
    profilingEnabled: true,
    queueRequestSchema: descriptor(),
  };
  assert.deepEqual(createWorkerConfigurationRequest(workerConfiguration).options?.profilingEnabled, true);
  assert.deepEqual(
    createWorkerConfigurationDifferences(
      { options: { configuration, profilingEnabled: true } },
      { options: { configuration: defaultWorkConfiguration, profilingEnabled: false } },
      {
        schema: { jsonSchema: "{}" },
        tabs: [{
          description: "Queue",
          fields: [
            { description: "Profiling", label: "Profiling", path: "options.profilingEnabled" },
            { description: "Log level", label: "Log level", path: "options.configuration.logging.level" },
          ],
          id: "queue",
          label: "Queue",
        }],
      }
    ).map((difference) => ({
      currentValue: difference.currentValue,
      defaultValue: difference.defaultValue,
      label: difference.label,
      path: difference.path,
    })),
    [
      { currentValue: true, defaultValue: false, label: "Profiling", path: "options.profilingEnabled" },
      { currentValue: "Debug", defaultValue: "Information", label: "Log level", path: "options.configuration.logging.level" },
    ]
  );
  assert.equal(getValueAtPath({ a: { b: 1 } }, "a.b"), 1);
  assert.equal(getValueAtPath({ a: {} }, "a.missing"), undefined);
  assert.equal(configurationValuesEqual(undefined, null), true);
  assert.equal(configurationValuesEqual({ a: 1 }, { a: 1 }), true);
  assert.equal(formatConfigurationValue(null), "null");
  assert.equal(formatConfigurationValue("hello"), "hello");
  assert.equal(formatConfigurationValue(true), "true");
  assert.equal(formatConfigurationValue({ a: 1 }), "{\"a\":1}");

  const queueRequest: QueueWorkRequest = {
    concurrencyKey: { type: "Order", value: "100" },
    identifiers: [{ type: "Id", value: "A" }],
    input: { nested: { value: 1 } },
    options: { configuration, profilingEnabled: true },
    subjectId: { type: "Customer", value: "Ada" },
  };
  const cloned = cloneQueueWorkRequest(queueRequest);
  assert.deepEqual(cloned, queueRequest);
  assert.notEqual(cloned.input, queueRequest.input);
  assert.notEqual(cloned.options?.configuration, queueRequest.options?.configuration);
  assert.deepEqual(cloneTypedValue({ type: "A", value: "B" }), { type: "A", value: "B" });
  assert.deepEqual(cloneTypedValues([{ type: "A", value: "B" }]), [{ type: "A", value: "B" }]);
  assert.deepEqual(cloneJsonValue({ a: { b: 1 } }), { a: { b: 1 } });
  assert.equal(cloneWorkerOptions(null), undefined);

  const copied = createCopiedWorkerQueueRequest(
    {
      concurrencyKey: { type: "Order", value: "100" },
      configuration,
      profilingEnabled: false,
      subjectId: { type: "Customer", value: "Ada" },
    },
    { options: { configuration: defaultWorkConfiguration, profilingEnabled: true } }
  );
  assert.deepEqual(copied.subjectId, { type: "Customer", value: "Ada" });
  assert.equal(copied.options?.profilingEnabled, true);
  assert.equal(copied.options?.configuration?.logging.level, "Information");
  assert.equal(applyQueueConfigurationRules({}).options, undefined);
});

test("timeline, duration, hidden panel, and catalog helpers cover ordering and boundary behavior", () => {
  const now = Date.parse("2026-05-30T10:02:00.000Z");
  assert.equal(formatFutureRelativeTime("2026-05-30T10:01:00.000Z", now), "0.00s");
  assert.equal(formatElapsedSince("2026-05-30T10:01:58.500Z", now), "1.50s");
  assert.equal(formatElapsedSince("not-a-date", now), "-");
  assert.equal(formatCompactTimelineRelativeTime("2026-05-30T10:01:15.000Z", now), "45.00s");
  assert.equal(formatCompactTimelineRelativeTime(null, now), "-");
  assert.equal(formatDurationLabel("00:00:09.0000000"), "9.00s");
  assert.equal(formatDurationLabel("not-a-duration"), "not-a-duration");
  assert.equal(parseDurationMilliseconds("-1.02:03:04.5000000"), -93_784_500);
  assert.equal(parseDurationMilliseconds("nope"), null);
  assert.equal(formatMillisecondsCompact(1234), "1.23s");
  assert.equal(formatMillisecondsCompact(12_345), "12s");
  assert.equal(formatMillisecondsCompact(125_000), "2m 5s");
  assert.equal(formatMillisecondsCompact(3_661_000), "1h 1m");
  assert.equal(
    getIterationDurationMilliseconds(iteration({
      startedAt: "2026-05-30T10:01:55.000Z",
      status: "Executing",
    }), now),
    5000
  );
  assert.equal(getIterationDurationMilliseconds(iteration({ executionDuration: "00:00:03" }), now), 3000);
  assert.equal(
    getIterationDurationMilliseconds(iteration({
      completedAt: "2026-05-30T10:00:05.000Z",
      executionDuration: "bad",
      startedAt: "2026-05-30T10:00:00.000Z",
    }), now),
    5000
  );
  const durationGraphScale = createIterationDurationGraphScale([
    { durationMs: 1200, isFinal: true },
    { durationMs: 1220, isFinal: true },
    { durationMs: 1010, isFinal: false },
    { durationMs: 1500, isFinal: false },
  ]);
  assert.deepEqual(durationGraphScale, { lowerBound: 1200, maxDuration: 1220 });
  assert.equal(getIterationDurationGraphBarHeight({ durationMs: 0, isFinal: false }, durationGraphScale), 10);
  assert.equal(getIterationDurationGraphBarHeight({ durationMs: 610, isFinal: false }, durationGraphScale), 28);
  assert.equal(getIterationDurationGraphBarHeight({ durationMs: 1010, isFinal: false }, durationGraphScale), 46);
  assert.equal(getIterationDurationGraphBarHeight({ durationMs: 1210, isFinal: true }, durationGraphScale), 28);
  assert.equal(getIterationDurationGraphBarHeight({ durationMs: 1220, isFinal: true }, durationGraphScale), 56);
  assert.equal(getIterationDurationGraphBarHeight({ durationMs: 1500, isFinal: false }, durationGraphScale), 56);
  assert.deepEqual(
    createIterationDurationGraphScale([
      { durationMs: 800, isFinal: false },
      { durationMs: 1000, isFinal: false },
    ]),
    { lowerBound: 800, maxDuration: 1000 }
  );
  assert.equal(isFinalIterationStatus("Completed"), true);
  assert.equal(isFinalIterationStatus("Executing"), false);
  assert.equal(formatWorkerTimelineBadgeLabel(overviewTimelineItem({
    kind: "StateChange",
    state: "Waiting",
  })), "Waiting");
  assert.equal(formatWorkerTimelineBadgeLabel(overviewTimelineItem({
    kind: "StateChange",
    state: "Queued",
  })), "Queued");
  assert.equal(formatWorkerTimelineBadgeLabel(overviewTimelineItem({
    actionStatus: "Accepted",
    kind: "ActionRequest",
  })), "Accepted");
  assert.equal(formatWorkerTimelineBadgeLabel(overviewTimelineItem({
    actionStatus: null,
    kind: "ActionRequest",
  })), "Requested");
  assert.equal(formatWorkerTimelineBadgeLabel(overviewTimelineItem({
    iterationStatus: "Completed",
    kind: "Iteration",
  })), "Completed");
  assert.deepEqual([...createDefaultWorkerHiddenPanels()].sort(), ["workerConfiguration"]);
  assert.deepEqual([...createDefaultWorkerHiddenPanels(true)], []);
  assert.deepEqual([...createWorkerFocusedHiddenPanels("workerLogs")].sort(), ["workerConfiguration", "workerDuration", "workerTimeline"]);
  assert.deepEqual(
    [...createIterationFocusedHiddenPanels("iterationMessages")].sort(),
    ["iterationLogs", "iterationOutput", "iterationProfile", "iterationSummary"]
  );
  assert.deepEqual(
    [...createIterationFocusedHiddenPanels("iterationProfile")].sort(),
    ["iterationLogs", "iterationMessages", "iterationOutput", "iterationSummary"]
  );
  assert.equal(shouldForgetPagedWorkerTimelineItems(0, 12), true);
  assert.equal(shouldForgetPagedWorkerTimelineItems(160, 12), true);
  assert.equal(shouldForgetPagedWorkerTimelineItems(161, 12), false);
  assert.equal(shouldForgetPagedWorkerTimelineItems(0, 0), false);
  assert.equal(shouldForgetPagedIterationMessages(0, 12), true);
  assert.equal(shouldForgetPagedIterationMessages(160, 12), true);
  assert.equal(shouldForgetPagedIterationMessages(161, 12), false);
  assert.equal(shouldForgetPagedIterationMessages(0, 0), false);
  assert.equal(shouldForgetPushedWorkerTimelineItems(0, 3, "desc", 160, 2), true);
  assert.equal(shouldForgetPushedWorkerTimelineItems(160, 3, "desc", 160, 2), true);
  assert.equal(shouldForgetPushedWorkerTimelineItems(161, 3, "desc", 160, 2), false);
  assert.equal(shouldForgetPushedWorkerTimelineItems(0, 2, "desc", 160, 2), false);
  assert.equal(shouldForgetPushedWorkerTimelineItems(0, 3, "asc", 160, 2), false);
  assert.equal(shouldForgetPushedWorkerTimelineItems(0, 50, "desc"), false);
  assert.equal(shouldForgetPushedWorkerTimelineItems(0, 51, "desc"), true);
  const realtimeTimelineItems = [
    overviewTimelineItem({
      at: "2026-05-30T10:00:00.000Z",
      id: "old",
      kind: "Iteration",
      sequence: 1,
    }),
    overviewTimelineItem({
      at: "2026-05-30T10:00:02.000Z",
      id: "new",
      kind: "Iteration",
      sequence: 3,
    }),
    overviewTimelineItem({
      at: "2026-05-30T10:00:01.000Z",
      id: "middle",
      kind: "Iteration",
      sequence: 2,
    }),
  ];
  assert.deepEqual(
    retainLatestWorkerTimelineRealtimeItems(realtimeTimelineItems, "desc", 2).map((item) => item.id),
    ["new", "middle"]
  );
  assert.deepEqual(
    retainLatestWorkerTimelineRealtimeItems(realtimeTimelineItems, "asc", 2).map((item) => item.id),
    ["middle", "new"]
  );
  assert.equal(
    retainLatestWorkerTimelineRealtimeItems(
      Array.from({ length: 26 }, (_, index) =>
        overviewTimelineItem({
          at: `2026-05-30T10:00:${String(index).padStart(2, "0")}.000Z`,
          id: `timeline-${index}`,
          kind: "Iteration",
          sequence: index,
        })
      ),
      "desc"
    ).length,
    25
  );

  assert.deepEqual(
    mergeWorkerOverviewRecentIterations(
      [recentIteration({ sequence: 1 }), recentIteration({ sequence: 3 })],
      [recentIteration({ sequence: 2 }), recentIteration({ sequence: 3, status: "Failed" })]
    ).map((item) => `${item.sequence}:${item.status}`),
    ["3:Failed", "2:Completed", "1:Completed"]
  );
  const completedRecentIteration = recentIteration({
    completedAt: "2026-05-30T10:00:01.220Z",
    executionDuration: "00:00:01.2200000",
    sequence: 4,
    status: "Completed",
  });
  const staleExecutingRecentIteration = recentIteration({
    completedAt: null,
    executionDuration: null,
    sequence: 4,
    status: "Executing",
  });
  assert.equal(
    getPreferredWorkerOverviewRecentIteration(completedRecentIteration, staleExecutingRecentIteration),
    completedRecentIteration
  );
  assert.deepEqual(
    mergeWorkerOverviewRecentIterations(
      [completedRecentIteration],
      [staleExecutingRecentIteration]
    ),
    [completedRecentIteration]
  );
  assert.deepEqual(
    mergeWorkerOverviewRealtimeEntries(
      [{ id: "a", value: 1 }, { id: "b", value: 1 }],
      [{ id: "b", value: 2 }, { id: "c", value: 2 }],
      "desc"
    ),
    [{ id: "b", value: 2 }, { id: "c", value: 2 }, { id: "a", value: 1 }]
  );
  assert.deepEqual(mergeWorkerOverviewItemsById([{ id: "a" }], [{ id: "a" }, { id: "b" }]), [{ id: "a" }, { id: "b" }]);
  assert.deepEqual(splitCategoryPath(null), ["General"]);
  assert.deepEqual(splitCategoryPath("Ops:Orders"), ["Ops", "Orders"]);
  assert.equal(startsWithCategoryPath(["Ops", "Orders"], ["Ops"]), true);
  assert.equal(definitionMatchesCatalogScope(definition({ category: "Ops:Orders" }), { category: "Ops", includeSubcategories: true }), true);
  assert.equal(definitionMatchesCatalogScope(definition({ category: "Ops:Orders" }), { category: "Ops", includeSubcategories: false }), false);
  assert.equal(definitionMatchesCatalogScope(definition({ name: "Other" }), { definitionName: "ImportOrders" }), false);
});

test("worker log helpers filter, sort, cap, summarize, and compare entries", () => {
  assert.deepEqual(
    stopAutomaticCursorPaginationAfterError({
      hasMore: true,
      loadingMore: true,
      nextCursor: "cursor-2",
    }, "Page unavailable"),
    {
      error: "Page unavailable",
      hasMore: false,
      loadingMore: false,
      nextCursor: null,
    }
  );
  assert.deepEqual(
    restoreBaseCursorPaginationAfterCompaction({
      error: "Page unavailable",
      hasMore: false,
      loadingMore: true,
      nextCursor: null,
    }, true, "base-cursor"),
    {
      error: "Page unavailable",
      hasMore: false,
      loadingMore: false,
      nextCursor: null,
    }
  );
  assert.deepEqual(
    restoreBaseCursorPaginationAfterCompaction({
      hasMore: false,
      loadingMore: true,
      nextCursor: null,
    }, true, "base-cursor"),
    {
      hasMore: true,
      loadingMore: false,
      nextCursor: "base-cursor",
    }
  );

  assert.equal(getWorkerLogStreamCardClassName("compact").includes("min-h-[24rem]"), false);
  assert.ok(getWorkerLogStreamCardClassName("standard").includes("min-h-[24rem] max-h-[70vh]"));
  assert.ok(getWorkerLogStreamCardClassName("detailed").includes("min-h-[24rem] max-h-[calc(100svh-11rem)]"));
  assert.ok(getWorkerLogStreamViewportClassName("standard").includes("max-h-[36rem]"));
  assert.ok(getWorkerLogStreamViewportClassName("detailed").includes("max-h-[42rem]"));
  assert.equal(getWorkerLogServerPageWindowAdvanceCount(50, 100, 0), 0);
  assert.equal(getWorkerLogServerPageWindowAdvanceCount(500, 550, 0), 50);
  assert.equal(getWorkerLogServerPageWindowAdvanceCount(500, 525, 0), 25);
  assert.equal(getWorkerLogServerPageWindowAdvanceCount(550, 600, 50), 50);

  const entries = [
    overviewLog({ id: "info", level: "Information", occurredAt: "2026-05-30T10:00:00.000Z" }),
    overviewLog({ id: "error", level: "Error", occurredAt: "2026-05-30T10:00:01.000Z" }),
    overviewLog({ id: "warning", level: "Warning", occurredAt: "2026-05-30T10:00:02.000Z" }),
  ];
  assert.deepEqual(filterWorkerOverviewLogEntriesBySelectedLevels(entries, ["Error", "Warning"]).map((entry) => entry.id), ["error", "warning"]);
  assert.deepEqual(filterWorkerOverviewLogEntriesBySelectedLevels(entries, null).map((entry) => entry.id), ["info", "error", "warning"]);
  assert.deepEqual(sortWorkerOverviewLogEntries(entries, "desc").map((entry) => entry.id), ["warning", "error", "info"]);
  assert.deepEqual(capWorkerLogEntries(entries, 2).map((entry) => entry.id), ["warning", "error"]);

  const workerLogs = [
    workerLog({ id: "b", level: "Critical", occurredAt: "2026-05-30T10:00:00.000Z", sequence: 1 }),
    workerLog({ id: "a", level: "Trace", occurredAt: "2026-05-30T10:00:00.000Z", sequence: 1, ordinal: 1 }),
    workerLog({ id: "c", level: "Debug", occurredAt: "2026-05-30T10:00:02.000Z" }),
  ];
  assert.deepEqual(sortWorkerLogEntries(workerLogs, "asc").map((entry) => entry.id), ["b", "a", "c"]);
  assert.equal(compareWorkerLogEntries(workerLogs[0], workerLogs[1]) < 0, true);
  assert.equal(compareLogLikeEntries({ id: "a", occurredAt: "2026-05-30T10:00:00.000Z" }, { id: "b", occurredAt: "2026-05-30T10:00:00.000Z" }) < 0, true);
  assert.deepEqual(summarizeWorkerLogEntries(workerLogs), {
    critical: 1,
    debug: 1,
    error: 0,
    errors: 1,
    information: 0,
    total: 3,
    trace: 1,
    warning: 0,
    warnings: 0,
  });
});

test("live timeline visibility helpers keep waiting tiles only when no executing iteration is visible", () => {
  const waiting = { id: "live-state:waiting", kind: "StateChange", iterationStatus: undefined };
  const completed = { id: "iteration:1", kind: "Iteration", iterationStatus: "Completed" };
  const executing = { id: "iteration:2", kind: "Iteration", iterationStatus: "Executing" };

  assert.equal(shouldRetainVisibleWorkerWaitingTile([waiting, completed] as never, "Waiting"), true);
  assert.equal(shouldRetainVisibleWorkerWaitingTile([waiting, executing] as never, "Waiting"), false);
  assert.deepEqual(normalizeVisibleWorkerTimelineItems([waiting, completed] as never, "Waiting"), [waiting, completed]);
  assert.deepEqual(normalizeVisibleWorkerTimelineItems([waiting, executing] as never, "Waiting"), [executing]);
});

type FetchCall = {
  input: string;
  init?: RequestInit;
};

function installIterationOverviewFetch(
  handler: (call: FetchCall) => Response | Promise<Response>
) {
  const previousFetch = globalThis.fetch;
  const calls: FetchCall[] = [];
  globalThis.fetch = (async (input, init) => {
    const call = { input: String(input), init };
    calls.push(call);
    return handler(call);
  }) as typeof fetch;

  return {
    calls,
    restore() {
      globalThis.fetch = previousFetch;
    },
  };
}

function iterationProfileSnapshot(): WorkProfileSnapshot {
  return {
    capturedAt: "2026-06-11T16:33:11.000Z",
    root: {
      children: [
        {
          children: [
            {
              children: [],
              label: "Write demo message",
              instrumentation: "application",
              metricType: "Timing",
              nodeMilliseconds: 47,
              treeMilliseconds: 47,
            },
          ],
          label: "Executing DemoProfilingSectionWorker.RunAsync",
          instrumentation: "application",
          metricType: "MethodScope",
          nodeMilliseconds: 26,
          treeMilliseconds: 73,
        },
      ],
      label: "Executing DemoProfilingLabWork.RunAsync",
      instrumentation: "application",
      metricType: "MethodScope",
      nodeMilliseconds: 18,
      treeMilliseconds: 91,
    },
    startedAt: "2026-06-11T16:33:10.000Z",
  };
}

function iterationOverviewComponent(): WorkWorkerIterationOverviewComponent {
  return {
    activity: "Logs",
    input: {
      json: "{\"orderId\":42,\"mode\":\"demo\"}",
    },
    iteration: {
      attemptCount: 1,
      completedAt: "2026-06-11T16:33:11.500Z",
      executionDuration: "00:00:00.0910000",
      isFinal: true,
      occurredAt: "2026-06-11T16:33:11.500Z",
      output: {
        json: "{\"processed\":true,\"sections\":3}",
      },
      profile: iterationProfileSnapshot(),
      sequence: 7,
      startedAt: "2026-06-11T16:33:10.000Z",
      status: "Completed",
    },
    logs: {
      page: {
        cursor: null,
        hasMore: false,
        items: [],
      },
      summary: {
        critical: 0,
        debug: 0,
        error: 0,
        errors: 0,
        information: 1,
        total: 1,
        trace: 0,
        warning: 0,
        warnings: 0,
      },
    },
    messages: {
      summary: {
        critical: 0,
        debug: 0,
        error: 0,
        errors: 0,
        information: 1,
        total: 1,
        trace: 0,
        warning: 0,
        warnings: 0,
      },
    },
    worker: {
      concurrencyKey: { type: "Tenant", value: "northwind" },
      definitionName: "DemoProfilingLabWork",
      identifiers: [{ type: "SectionBatch", value: "3" }],
      profilingEnabled: false,
      subjectId: { type: "Order", value: "42" },
      workerId: { value: "worker-1" },
    },
  };
}
