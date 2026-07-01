import assert from "node:assert/strict";
import test from "node:test";
import { act } from "react";
import { ConsoleHeaderCapabilitiesProvider } from "@/components/features/console/header-capabilities";
import { ConsolePageRealtimeViewProvider } from "@/components/features/console/page-realtime-view";
import type {
  OverviewPanelId,
  OverviewPanelShapeMap,
} from "@/components/features/console/overview-panels";
import {
  OverviewView,
  overviewResumeRefreshThresholdMs,
} from "@/components/workable/console/overview-screen";
import { renderDom } from "@/test/dom";
import type {
  WorkComponentQueryResult,
  WorkComponentShape,
  WorkCompletionStatus,
  WorkOverviewFailedWorkerDetailed,
  WorkOverviewIteration,
  WorkIterationKeyTypeFacet,
  WorkSystemAccessSummary,
  WorkSystemThroughput,
  WorkableConnection,
  WorkerState,
} from "@/lib/workable";

const connection: WorkableConnection = {
  apiUrl: "https://console.example.com/workable",
  systemName: "Ops",
};

test("overview view shows loading, component errors, and panel controls", async () => {
  const callbacks = createOverviewCallbacks();
  const response = deferred<Response>();
  const fetchMock = installOverviewFetch((call) => {
    if (call.input === "/api/workable/systems/Ops/views/overview") {
      return response.promise;
    }

    return Response.json({ error: `Unhandled request: ${call.input}` }, { status: 500 });
  });
  const result = await renderDom(overviewElement(callbacks, {
    hiddenPanelIds: ["failedWorkers", "throughput", "failedIterations", "completedIterations"],
  }));

  try {
    result.getByText("Overview loading");

    response.resolve(Response.json(overviewResult({
      iterationsError: "Iterations unavailable",
    })));

    await result.waitFor(() => result.getByText("Active workers"));
    await result.waitFor(() => result.getByText("Iterations unavailable"));
    assert.ok(
      result.getByRole("button", { name: "Open workers filtered by Queued" })
        .closest(".workable-grid-scrollbar")
    );
    assert.deepEqual(callbacks.statesLoaded, ["Started"]);
    assert.equal(callbacks.readyCount > 0, true);

    await result.click(result.getByRole("button", { name: "Next view: Compact" }));
    assert.deepEqual(callbacks.shapeChanges.at(-1), {
      panelId: "workers",
      shape: "compact",
    });

    const panelOptions = result.getByRole("button", { name: "Panel options" });
    await result.pointerDown(panelOptions);
    await result.pointerUp(panelOptions);
    await result.click(panelOptions);
    await result.waitFor(() => result.getByRole("menuitem", { name: "Hide panel" }));
    await result.click(result.getByRole("menuitem", { name: "Hide panel" }));
    assert.deepEqual(callbacks.visibilityChanges.at(-1), {
      panelId: "workers",
      visible: false,
    });
  } finally {
    fetchMock.restore();
    await result.restore();
  }
});

test("overview view surfaces request errors and reports connection failure", async () => {
  const callbacks = createOverviewCallbacks();
  const fetchMock = installOverviewFetch((call) => {
    if (call.input === "/api/workable/systems/Ops/views/overview") {
      return Response.json({ error: "Overview unavailable" }, { status: 503 });
    }

    return Response.json({ error: `Unhandled request: ${call.input}` }, { status: 500 });
  });
  const result = await renderDom(overviewElement(callbacks));

  try {
    await result.waitFor(() => result.getByText("Overview unavailable"));
    assert.equal(callbacks.connectionErrorCount, 1);
  } finally {
    fetchMock.restore();
    await result.restore();
  }
});

test("overview failed-worker actions refresh the failed-worker slice when realtime is not connected", async () => {
  const callbacks = createOverviewCallbacks();
  const fetchMock = installOverviewFetch((call) => {
    if (call.input === "/api/workable/systems/Ops/views/overview") {
      const body = requestBody(call);
      if (body.components.some((component) => component.id === "workers")) {
        return Response.json(overviewResult({
          failedWorkers: [],
          workerCounts: {
            activeWorkerCount: 0,
            failedWorkerCount: 0,
            finalWorkerCount: 1,
          },
        }));
      }

      return Response.json(overviewResult());
    }

    if (call.input === "/api/workable/systems/Ops/workers/failed-worker/actions/start") {
      return Response.json({ status: "Accepted" });
    }

    return Response.json({ error: `Unhandled request: ${call.input}` }, { status: 500 });
  });
  const result = await renderDom(overviewElement(callbacks, {
    hiddenPanelIds: ["workers", "throughput", "iterations", "failedIterations", "completedIterations"],
  }));

  try {
    await result.waitFor(() => result.getByText("ImportOrders"));
    const actionTrigger = result.getByRole("button", { name: "Open actions for ImportOrders" });
    await result.pointerDown(actionTrigger);
    await result.pointerUp(actionTrigger);
    await result.click(actionTrigger);
    await result.waitFor(() => result.getByRole("menuitem", { name: "Start" }));
    await result.click(result.getByRole("menuitem", { name: "Start" }));

    await result.waitFor(() => result.getByText("No failed workers."));
    assert.equal(fetchMock.calls.some((call) =>
      call.input === "/api/workable/systems/Ops/workers/failed-worker/actions/start" &&
      call.init?.method === "POST" &&
      JSON.parse(String(call.init.body)).revision === 7
    ), true);
  } finally {
    fetchMock.restore();
    await result.restore();
  }
});

test("overview hides failed-worker mutation controls without operate permission", async () => {
  const callbacks = createOverviewCallbacks();
  const fetchMock = installOverviewFetch((call) => {
    if (call.input === "/api/workable/systems/Ops/views/overview") {
      return Response.json(overviewResult());
    }

    return Response.json({ error: `Unhandled request: ${call.input}` }, { status: 500 });
  });
  const result = await renderDom(overviewElement(callbacks, {
    access: {
      ...fullAccess(),
      canOperateAllWork: false,
      isWorkAdministrator: false,
      operableDefinitionCount: 0,
    },
    hiddenPanelIds: ["workers", "throughput", "iterations", "failedIterations", "completedIterations"],
  }));

  try {
    await result.waitFor(() => result.getByText("ImportOrders"));
    assert.equal(
      result.dom.window.document.body.textContent?.includes("Open actions for ImportOrders"),
      false
    );
  } finally {
    fetchMock.restore();
    await result.restore();
  }
});

test("overview throughput panel switches chart window, mode, and series visibility", async () => {
  const callbacks = createOverviewCallbacks();
  const delayedWindowResponse = deferred<Response>();
  let delayedRequested = false;
  const fetchMock = installOverviewFetch((call) => {
    if (call.input === "/api/workable/systems/Ops/views/overview") {
      const throughputRequest = requestBody(call).components.find((component) =>
        component.id === "throughput"
      );
      const options = throughputRequest?.options as
        | { bucketSeconds?: number; windowSeconds?: number }
        | undefined;
      if (options?.bucketSeconds === 5 && options?.windowSeconds === 300) {
        delayedRequested = true;
        return delayedWindowResponse.promise;
      }

      return Response.json(overviewResult({
        throughput: populatedThroughput(),
      }));
    }

    return Response.json({ error: `Unhandled request: ${call.input}` }, { status: 500 });
  });
  const result = await renderDom(overviewElement(callbacks, {
    hiddenPanelIds: ["workers", "failedWorkers", "iterations", "failedIterations", "completedIterations"],
  }));

  try {
    await result.waitFor(() => {
      assert.ok(result.container.querySelector("svg[aria-label='Throughput chart']"));
    });

    await result.click(result.getByRole("button", { name: "Completed" }));
    assert.deepEqual(callbacks.toggledSeries, ["completed"]);

    await result.click(result.getByRole("button", { name: "5m" }));
    await result.waitFor(() => {
      assert.equal(delayedRequested, true);
    });
    assert.ok(result.container.querySelector("svg[aria-label='Throughput chart']"));
    assert.equal(result.queryByText("Waiting for throughput data."), null);

    delayedWindowResponse.resolve(Response.json(overviewResult({
      throughput: populatedThroughput(),
    })));

    await result.mouseDown(result.getByRole("tab", { name: "Execution" }));
    await result.waitFor(() => {
      assert.ok(
        result.container.querySelector("svg[aria-label='Execution time chart']"),
        Array.from(result.container.querySelectorAll("svg"))
          .map((svg) => svg.getAttribute("aria-label") ?? "(unlabeled)")
          .join(", ")
      );
    });
    result.getByText("Avg");
    result.getByText("P95");
    result.getByText("1.1s");
  } finally {
    fetchMock.restore();
    await result.restore();
  }
});

test("overview iteration panels open status, key type, and recent worker flows", async () => {
  const callbacks = createOverviewCallbacks();
  const fetchMock = installOverviewFetch((call) => {
    if (call.input === "/api/workable/systems/Ops/views/overview") {
      return Response.json(overviewResult({
        failedIterations: [overviewIteration()],
        iterations: {
          commonKeyTypes: [iterationKeyType()],
          iterationCountByStatus: {
            Completed: 4,
            Executing: 1,
            Failed: 2,
          },
        },
      }));
    }

    return Response.json({ error: `Unhandled request: ${call.input}` }, { status: 500 });
  });
  const result = await renderDom(overviewElement(callbacks, {
    hiddenPanelIds: ["workers", "failedWorkers", "throughput", "completedIterations"],
  }));

  try {
    await result.waitFor(() => result.getByText("BillInvoices"));
    assert.ok(
      result.getByRole("button", { name: "Open iterations filtered by Failed" })
        .closest(".workable-grid-scrollbar")
    );

    await result.click(result.getByRole("button", { name: "Open iterations filtered by Failed" }));
    assert.deepEqual(callbacks.iterationFilters.at(-1), ["Failed"]);

    await result.click(result.getByRole("button", { name: "Open iterations for key type Tenant" }));
    assert.deepEqual(callbacks.openedKeyTypes, ["Tenant"]);

    const row = result.getByText("BillInvoices").closest("tr");
    assert.ok(row);
    await result.click(row);
    assert.deepEqual(callbacks.openedWorkers, ["iteration-worker"]);
  } finally {
    fetchMock.restore();
    await result.restore();
  }
});

test("overview view refreshes the overview snapshot after a long page resume", async () => {
  const callbacks = createOverviewCallbacks();
  let overviewRequestCount = 0;
  const fetchMock = installOverviewFetch((call) => {
    if (call.input === "/api/workable/systems/Ops/views/overview") {
      overviewRequestCount += 1;
      return Response.json(overviewResult({
        workerCounts: overviewRequestCount === 1
          ? {
              activeWorkerCount: 2,
              failedWorkerCount: 1,
              finalWorkerCount: 3,
            }
          : {
              activeWorkerCount: 11,
              failedWorkerCount: 1,
              finalWorkerCount: 3,
            },
      }));
    }

    return Response.json({ error: `Unhandled request: ${call.input}` }, { status: 500 });
  });
  const result = await renderDom(overviewElement(callbacks, {
    hiddenPanelIds: ["failedWorkers", "throughput", "failedIterations", "completedIterations"],
  }));
  const originalDateNow = Date.now;
  let now = 0;

  try {
    Date.now = () => now;
    await result.waitFor(() => result.getByText("Active workers"));
    await result.waitFor(() => result.getByText("2"));
    assert.equal(overviewRequestCount, 1);

    let visibilityState: "visible" | "hidden" = "visible";
    Object.defineProperty(result.dom.window.document, "visibilityState", {
      configurable: true,
      get() {
        return visibilityState;
      },
    });

    visibilityState = "hidden";
    now = 1_000;
    await act(async () => {
      result.dom.window.document.dispatchEvent(new result.dom.window.Event("visibilitychange"));
    });

    visibilityState = "visible";
    now = 1_000 + overviewResumeRefreshThresholdMs;
    await act(async () => {
      result.dom.window.document.dispatchEvent(new result.dom.window.Event("visibilitychange"));
    });

    await result.waitFor(() => {
      assert.equal(overviewRequestCount, 2);
    });
    await result.waitFor(() => result.getByText("11"));
  } finally {
    Date.now = originalDateNow;
    fetchMock.restore();
    await result.restore();
  }
});

type FetchCall = {
  input: string;
  init?: RequestInit;
};

type OverviewCallbacks = ReturnType<typeof createOverviewCallbacks>;

function overviewElement(
  callbacks: OverviewCallbacks,
  options?: {
    access?: WorkSystemAccessSummary;
    hiddenPanelIds?: OverviewPanelId[];
  }
) {
  return (
    <ConsoleHeaderCapabilitiesProvider>
      <ConsolePageRealtimeViewProvider>
        <OverviewView
          access={options?.access ?? fullAccess()}
          connection={connection}
          hiddenPanelIds={options?.hiddenPanelIds ?? []}
          hiddenThroughputSeries={[]}
          isVisible
          onActiveRealtimeConnectionCountChange={() => undefined}
          onConnectionError={() => {
            callbacks.connectionErrorCount += 1;
          }}
          onOpenCatalog={() => {
            callbacks.openedCatalogCount += 1;
          }}
          onOpenIterations={() => {
            callbacks.openedIterationsCount += 1;
          }}
          onOpenKeyType={(keyType) => callbacks.openedKeyTypes.push(keyType)}
          onOpenWorker={(workerId) => callbacks.openedWorkers.push(workerId)}
          onPanelShapeChange={(panelId, shape) => callbacks.shapeChanges.push({ panelId, shape })}
          onPanelVisibilityChange={(panelId, visible) =>
            callbacks.visibilityChanges.push({ panelId, visible })}
          onReady={() => {
            callbacks.readyCount += 1;
          }}
          onRealtimePayloadOpenChange={() => undefined}
          onResetUi={() => {
            callbacks.resetCount += 1;
          }}
          onStateLoaded={(state) => callbacks.statesLoaded.push(state)}
          onThroughputSeriesToggle={(seriesId) => callbacks.toggledSeries.push(seriesId)}
          onViewIterationsByStatus={(statuses) => callbacks.iterationFilters.push(statuses)}
          onViewWorkersByState={(states) => callbacks.workerFilters.push(states)}
          overviewScope={null}
          panelShapes={defaultPanelShapes()}
          realtimePayloadCaptureEnabled={false}
          realtimePayloadMaxMessages={10}
          realtimePayloadOpen={false}
          refreshToken={0}
          renderControls={({ loading, refreshing }) => (
            <div>
              {loading ? "Overview loading" : refreshing ? "Overview refreshing" : "Overview ready"}
            </div>
          )}
        />
      </ConsolePageRealtimeViewProvider>
    </ConsoleHeaderCapabilitiesProvider>
  );
}

function createOverviewCallbacks() {
  return {
    connectionErrorCount: 0,
    iterationFilters: [] as WorkCompletionStatus[][],
    openedCatalogCount: 0,
    openedIterationsCount: 0,
    openedKeyTypes: [] as string[],
    openedWorkers: [] as string[],
    readyCount: 0,
    resetCount: 0,
    shapeChanges: [] as Array<{ panelId: OverviewPanelId; shape: WorkComponentShape }>,
    statesLoaded: [] as string[],
    toggledSeries: [] as string[],
    visibilityChanges: [] as Array<{ panelId: OverviewPanelId; visible: boolean }>,
    workerFilters: [] as WorkerState[][],
  };
}

function installOverviewFetch(
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

function requestBody(call: FetchCall): {
  components: Array<{
    id: string;
    options?: Record<string, unknown>;
  }>;
} {
  const body = call.init?.body;
  if (typeof body !== "string") {
    assert.fail("Expected overview request body to be a string.");
  }
  return JSON.parse(body) as {
    components: Array<{
      id: string;
      options?: Record<string, unknown>;
    }>;
  };
}

function deferred<T>() {
  let resolve!: (value: T) => void;
  const promise = new Promise<T>((nextResolve) => {
    resolve = nextResolve;
  });
  return { promise, resolve };
}

function overviewResult(options?: {
  failedWorkers?: WorkOverviewFailedWorkerDetailed[];
  failedIterations?: WorkOverviewIteration[];
  iterations?: {
    commonKeyTypes?: WorkIterationKeyTypeFacet[];
    iterationCountByStatus: Partial<Record<WorkCompletionStatus, number>>;
  };
  iterationsError?: string;
  throughput?: WorkSystemThroughput;
  workerCounts?: {
    activeWorkerCount: number;
    failedWorkerCount: number;
    finalWorkerCount: number;
  };
}): WorkComponentQueryResult {
  const workerCounts = options?.workerCounts ?? {
    activeWorkerCount: 2,
    failedWorkerCount: 1,
    finalWorkerCount: 3,
  };

  return {
    components: {
      completedIterations: okComponent([]),
      failedWorkers: okComponent(options?.failedWorkers ?? [failedWorker()]),
      iterations: options?.iterationsError
        ? { error: options.iterationsError, status: "error" }
        : okComponent(options?.iterations ?? {
            commonKeyTypes: [],
            iterationCountByStatus: {
              Completed: 3,
              Failed: 1,
            },
        }),
      system: okComponent({
        systemName: "Ops",
        systemState: "Started",
      }),
      throughput: okComponent({
        activeWorkerCount: workerCounts.activeWorkerCount,
        throughput: options?.throughput ?? emptyThroughput(),
      }),
      failedIterations: okComponent(options?.failedIterations ?? []),
      workers: okComponent({
        definitionCount: 2,
        oldestQueuedAt: null,
        workerCountByState: {
          Failed: workerCounts.failedWorkerCount,
          Queued: workerCounts.activeWorkerCount,
        },
        ...workerCounts,
      }),
    },
    generatedAt: "2026-06-01T12:00:00.000Z",
  };
}

function okComponent(data: unknown) {
  return {
    data,
    status: "ok",
  };
}

function failedWorker(): WorkOverviewFailedWorkerDetailed {
  return {
    definitionName: "ImportOrders",
    id: { value: "failed-worker" },
    identifiers: [{ type: "Tenant", value: "north" }],
    revision: 7,
    state: "Failed",
    subjectId: { type: "Order", value: "100" },
    totalExecutionDuration: "00:00:02",
    updatedAt: "2026-06-01T11:59:00.000Z",
  };
}

function overviewIteration(): WorkOverviewIteration {
  return {
    completedAt: "2026-06-01T11:58:00.000Z",
    definitionName: "BillInvoices",
    executionDuration: "00:00:01.5000000",
    identifiers: [{ type: "Tenant", value: "north" }],
    sequence: 12,
    subjectId: { type: "Invoice", value: "900" },
    workerId: { value: "iteration-worker" },
    workerState: "Failed",
  };
}

function iterationKeyType(): WorkIterationKeyTypeFacet {
  return {
    iterationCount: 7,
    iterationCountByKind: {
      ConcurrencyKey: 2,
      Identifier: 2,
      Subject: 3,
    },
    type: "Tenant",
  };
}

function emptyThroughput() {
  return {
    bucketSeconds: 1,
    buckets: [],
    executionSummary: {
      averageExecutionMilliseconds: 0,
      executionCount: 0,
      p95ExecutionMilliseconds: 0,
      p99ExecutionMilliseconds: 0,
      slowestExecutionMilliseconds: 0,
    },
    liveSummary: {
      canceledPerSecond: 0,
      completedPerSecond: 0,
      failedPerSecond: 0,
      inFlightDeltaPerSecond: 0,
      rateWindowSeconds: 60,
      startedPerSecond: 0,
    },
    settledCount: 0,
    windowSeconds: 60,
  };
}

function populatedThroughput(): WorkSystemThroughput {
  return {
    ...emptyThroughput(),
    bucketSeconds: 1,
    buckets: [
      {
        at: "2026-06-01T11:59:58.000Z",
        averageExecutionMilliseconds: 900,
        canceled: 0,
        completed: 1,
        failed: 0,
        started: 2,
      },
      {
        at: "2026-06-01T11:59:59.000Z",
        averageExecutionMilliseconds: 1200,
        canceled: 0,
        completed: 2,
        failed: 1,
        started: 4,
      },
    ],
    executionSummary: {
      averageExecutionMilliseconds: 1050,
      executionCount: 3,
      p95ExecutionMilliseconds: 1600,
      p99ExecutionMilliseconds: 1800,
      slowestExecutionMilliseconds: 2100,
    },
    from: "2026-06-01T11:59:00.000Z",
    liveSummary: {
      canceledPerSecond: 0,
      completedPerSecond: 2,
      failedPerSecond: 0.5,
      inFlightDeltaPerSecond: 1,
      rateWindowSeconds: 60,
      startedPerSecond: 4,
    },
    settledCount: 3,
    to: "2026-06-01T12:00:00.000Z",
    windowSeconds: 60,
  };
}

function defaultPanelShapes(): OverviewPanelShapeMap {
  return {
    completedIterations: "standard",
    failedIterations: "standard",
    failedWorkers: "detailed",
    iterations: "standard",
    throughput: "standard",
    workers: "standard",
  };
}

function fullAccess(): WorkSystemAccessSummary {
  return {
    canControlSystem: true,
    canOperateAllWork: true,
    canReadAllWork: true,
    canViewDiagnostics: true,
    isSystemAdministrator: true,
    isWorkAdministrator: true,
    operableDefinitionCount: 2,
    readableDefinitionCount: 2,
    totalDefinitionCount: 2,
  };
}
