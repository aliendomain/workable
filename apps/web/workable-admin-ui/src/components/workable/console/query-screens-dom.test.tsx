import assert from "node:assert/strict";
import test from "node:test";
import type { JSDOM } from "jsdom";
import {
  IterationsView,
  WorkersView,
} from "@/components/workable/console/query-screens";
import { renderDom } from "@/test/dom";
import type {
  WorkComponentQueryResult,
  WorkComponentRequest,
  WorkCompletionStatus,
  WorkViewIterationGridDetailed,
  WorkViewWorkerGridDetailed,
  WorkableConnection,
  WorkerState,
} from "@/lib/workable";

const connection: WorkableConnection = {
  apiUrl: "https://console.example.com/workable",
  systemName: "Ops",
};

test("workers view loads filtered rows, appends another page, opens rows, and runs actions", async () => {
  const openedWorkers: string[] = [];
  const fetchMock = installQueryFetch((call) => {
    if (call.input === "/api/workable/systems/Ops/views/workers") {
      const options = componentOptions(call);
      if (options.skip === 0) {
        return Response.json(componentResult("workerGrid", {
          skip: 0,
          take: 50,
          totalCount: 2,
          workers: [
            worker({
              definitionName: "ImportOrders",
              id: { value: "worker-queued" },
              state: "Queued",
              subjectId: { type: "Order", value: "100" },
            }),
          ],
        }));
      }

      if (options.skip === 1) {
        return Response.json(componentResult("workerGrid", {
          skip: 1,
          take: 50,
          totalCount: 2,
          workers: [
            worker({
              definitionName: "ShipOrders",
              id: { value: "worker-running" },
              state: "Running",
            }),
          ],
        }));
      }
    }

    if (call.input === "/api/workable/systems/Ops/workers/worker-queued/actions/start") {
      return Response.json({ status: "Accepted" });
    }

    return Response.json({ error: `Unhandled request: ${call.input}` }, { status: 500 });
  });
  const result = await renderDom(
    <WorkersView
      categoryFilter="Ops"
      connection={connection}
      definitionFilter="ImportOrders"
      isLoadingTarget
      isVisible
      keyKindFilter="Subject"
      keyTypeFilter="Order"
      keyValueFilter="100"
      onOpenWorker={(workerId) => openedWorkers.push(workerId)}
      onReady={() => undefined}
      refreshToken={0}
      stateFilter={["Queued"]}
    />,
    { setupWindow: installVirtualLayout }
  );

  try {
    await result.waitFor(() => result.getByText("ImportOrders"));
    result.getByText("worker-queued");
    result.getByText("Queued");
    result.getByText("2 workers");

    const firstQuery = fetchMock.calls.find((call) =>
      call.input === "/api/workable/systems/Ops/views/workers"
    );
    assert.ok(firstQuery);
    assert.deepEqual(componentOptions(firstQuery), {
      keyKind: "Subject",
      keyType: "Order",
      keyValue: "100",
      skip: 0,
      states: ["Queued"],
      take: 50,
    });
    assert.deepEqual(requestBody(firstQuery).scope, {
      category: "Ops",
      definitionName: "ImportOrders",
      includeSubcategories: true,
    });

    await result.click(result.getByText("worker-queued"));
    assert.deepEqual(openedWorkers, ["worker-queued"]);

    await result.scroll(queryViewport(result), {
      clientHeight: 100,
      scrollHeight: 240,
      scrollTop: 160,
    });
    await result.waitFor(() => result.getByText("ShipOrders"));
    result.getByText("Showing 2 workers");

    const actionTrigger = result.getByRole("button", { name: "Open actions for ImportOrders" });
    await result.pointerDown(actionTrigger);
    await result.pointerUp(actionTrigger);
    await result.click(actionTrigger);
    await result.waitFor(() => result.getByRole("menuitem", { name: "Start & View" }));
    await result.click(result.getByRole("menuitem", { name: "Start & View" }));

    await result.waitFor(() => {
      assert.equal(fetchMock.calls.some((call) =>
        call.input === "/api/workable/systems/Ops/workers/worker-queued/actions/start" &&
        call.init?.method === "POST"
      ), true);
    });
    assert.deepEqual(openedWorkers, ["worker-queued", "worker-queued"]);
  } finally {
    fetchMock.restore();
    await result.restore();
  }
});

test("workers view hides mutation action controls for read-only access", async () => {
  const openedWorkers: string[] = [];
  const fetchMock = installQueryFetch((call) => {
    if (call.input === "/api/workable/systems/Ops/views/workers") {
      return Response.json(componentResult("workerGrid", {
        skip: 0,
        take: 50,
        totalCount: 1,
        workers: [
          worker({
            definitionName: "ImportOrders",
            id: { value: "worker-readonly" },
            state: "Queued",
          }),
        ],
      }));
    }

    return Response.json({ error: `Unhandled request: ${call.input}` }, { status: 500 });
  });
  const result = await renderDom(
    <WorkersView
      categoryFilter=""
      connection={connection}
      definitionFilter=""
      isLoadingTarget
      isVisible
      keyKindFilter="Any"
      keyTypeFilter=""
      keyValueFilter=""
      onOpenWorker={(workerId) => openedWorkers.push(workerId)}
      onReady={() => undefined}
      refreshToken={0}
      showActions={false}
      stateFilter={[]}
    />,
    { setupWindow: installVirtualLayout }
  );

  try {
    await result.waitFor(() => result.getByText("worker-readonly"));

    assert.equal(findButtonByName(result.container, "Open actions for ImportOrders"), null);
    await result.click(result.getByText("worker-readonly"));
    assert.deepEqual(openedWorkers, ["worker-readonly"]);
    assert.equal(
      fetchMock.calls.some((call) => call.input.includes("/actions/")),
      false
    );
  } finally {
    fetchMock.restore();
    await result.restore();
  }
});

test("workers view surfaces query errors", async () => {
  const fetchMock = installQueryFetch((call) => {
    if (call.input === "/api/workable/systems/Ops/views/workers") {
      return Response.json({ error: "Workers unavailable" }, { status: 500 });
    }

    return Response.json({ error: `Unhandled request: ${call.input}` }, { status: 500 });
  });
  const result = await renderDom(
    <WorkersView
      categoryFilter=""
      connection={connection}
      definitionFilter=""
      isLoadingTarget
      isVisible
      keyKindFilter="Any"
      keyTypeFilter=""
      keyValueFilter=""
      onOpenWorker={() => undefined}
      onReady={() => undefined}
      refreshToken={0}
      stateFilter={[]}
    />,
    { setupWindow: installVirtualLayout }
  );

  try {
    await result.waitFor(() => result.getByText("Worker query failed. Workers unavailable"));
    result.getByText("No workers matched the current query.");
  } finally {
    fetchMock.restore();
    await result.restore();
  }
});

test("workers view stops automatic pagination after a failed page", async () => {
  const fetchMock = installQueryFetch((call) => {
    if (call.input === "/api/workable/systems/Ops/views/workers") {
      const options = componentOptions(call);
      if (options.skip === 0) {
        return Response.json(componentResult("workerGrid", {
          skip: 0,
          take: 50,
          totalCount: 2,
          workers: [worker({ id: { value: "worker-loaded" } })],
        }));
      }

      return Response.json({ error: "Next worker page unavailable" }, { status: 502 });
    }

    return Response.json({ error: `Unhandled request: ${call.input}` }, { status: 500 });
  });
  const result = await renderDom(
    <WorkersView
      categoryFilter=""
      connection={connection}
      definitionFilter=""
      isLoadingTarget
      isVisible
      keyKindFilter="Any"
      keyTypeFilter=""
      keyValueFilter=""
      onOpenWorker={() => undefined}
      onReady={() => undefined}
      refreshToken={0}
      stateFilter={[]}
    />,
    { setupWindow: installVirtualLayout }
  );

  try {
    await result.waitFor(() => result.getByText("worker-loaded"));
    const viewport = queryViewport(result);
    await result.scroll(viewport, {
      clientHeight: 100,
      scrollHeight: 240,
      scrollTop: 160,
    });
    await result.waitFor(() =>
      result.getByText("Worker query failed. Next worker page unavailable")
    );
    result.getByText("Showing 1 worker");

    await result.scroll(viewport, {
      clientHeight: 100,
      scrollHeight: 240,
      scrollTop: 160,
    });
    await new Promise((resolve) => setTimeout(resolve, 25));

    assert.equal(
      fetchMock.calls.filter((call) =>
        call.input === "/api/workable/systems/Ops/views/workers"
      ).length,
      2
    );
  } finally {
    fetchMock.restore();
    await result.restore();
  }
});

test("workers view clears prior-system rows before a new system request fails", async () => {
  const fetchMock = installQueryFetch((call) => {
    if (call.input === "/api/workable/systems/Ops/views/workers") {
      return Response.json(componentResult("workerGrid", {
        skip: 0,
        take: 50,
        totalCount: 1,
        workers: [worker({ id: { value: "ops-worker" } })],
      }));
    }

    if (call.input === "/api/workable/systems/Restricted/views/workers") {
      return Response.json({ error: "Restricted workers unavailable" }, { status: 502 });
    }

    return Response.json({ error: `Unhandled request: ${call.input}` }, { status: 500 });
  });
  const element = (nextConnection: WorkableConnection) => (
    <WorkersView
      categoryFilter=""
      connection={nextConnection}
      definitionFilter=""
      isLoadingTarget
      isVisible
      keyKindFilter="Any"
      keyTypeFilter=""
      keyValueFilter=""
      onOpenWorker={() => undefined}
      onReady={() => undefined}
      refreshToken={0}
      stateFilter={[]}
    />
  );
  const result = await renderDom(element(connection), { setupWindow: installVirtualLayout });

  try {
    await result.waitFor(() => result.getByText("ops-worker"));
    await result.rerender(element({ ...connection, systemName: "Restricted" }));
    await result.waitFor(() =>
      result.getByText("Worker query failed. Restricted workers unavailable")
    );

    assert.equal(result.queryByText("ops-worker"), null);
    result.getByText("No workers matched the current query.");
  } finally {
    fetchMock.restore();
    await result.restore();
  }
});

test("iterations view loads filtered rows, appends another page, and opens executing iterations", async () => {
  const openedIterations: Array<{ sequence: number; workerId: string }> = [];
  const fetchMock = installQueryFetch((call) => {
    if (call.input === "/api/workable/systems/Ops/views/iterations") {
      const options = componentOptions(call);
      if (options.skip === 0) {
        return Response.json(componentResult("iterationGrid", {
          iterations: [
            iteration({
              definitionName: "ImportOrders",
              isFinal: false,
              sequence: 3,
              status: "Executing",
              workerId: { value: "worker-final" },
              workerState: "Running",
            }),
          ],
          skip: 0,
          take: 50,
          totalCount: 2,
        }));
      }

      if (options.skip === 1) {
        return Response.json(componentResult("iterationGrid", {
          iterations: [
            iteration({
              definitionName: "ShipOrders",
              sequence: 4,
              status: "Failed",
              workerId: { value: "worker-failed" },
            }),
          ],
          skip: 1,
          take: 50,
          totalCount: 2,
        }));
      }
    }

    return Response.json({ error: `Unhandled request: ${call.input}` }, { status: 500 });
  });
  const result = await renderDom(
    <IterationsView
      categoryFilter="Ops"
      connection={connection}
      definitionFilter="ImportOrders"
      isLoadingTarget
      isVisible
      keyKindFilter="Identifier"
      keyTypeFilter="Batch"
      keyValueFilter="west"
      onOpenIteration={(workerId, sequence) => openedIterations.push({ sequence, workerId })}
      onReady={() => undefined}
      refreshToken={0}
      statusFilter={["Completed"]}
    />,
    { setupWindow: installVirtualLayout }
  );

  try {
    await result.waitFor(() => result.getByText("ImportOrders"));
    result.getByText("#3 / worker-final");
    result.getByText("Executing");
    result.getByText("2 iterations");

    const firstQuery = fetchMock.calls.find((call) =>
      call.input === "/api/workable/systems/Ops/views/iterations"
    );
    assert.ok(firstQuery);
    assert.deepEqual(componentOptions(firstQuery), {
      keyKind: "Identifier",
      keyType: "Batch",
      keyValue: "west",
      skip: 0,
      statuses: ["Completed"],
      take: 50,
    });
    assert.deepEqual(requestBody(firstQuery).scope, {
      category: "Ops",
      definitionName: "ImportOrders",
      includeSubcategories: true,
    });

    await result.click(result.getByText("#3 / worker-final"));
    assert.deepEqual(openedIterations, [
      {
        sequence: 3,
        workerId: "worker-final",
      },
    ]);

    await result.scroll(queryViewport(result), {
      clientHeight: 100,
      scrollHeight: 240,
      scrollTop: 160,
    });
    await result.waitFor(() => result.getByText("ShipOrders"));
    result.getByText("#4 / worker-failed");
    result.getByText("Showing 2 iterations");
  } finally {
    fetchMock.restore();
    await result.restore();
  }
});

test("iterations view surfaces query errors", async () => {
  const fetchMock = installQueryFetch((call) => {
    if (call.input === "/api/workable/systems/Ops/views/iterations") {
      return Response.json({ error: "Iterations unavailable" }, { status: 500 });
    }

    return Response.json({ error: `Unhandled request: ${call.input}` }, { status: 500 });
  });
  const result = await renderDom(
    <IterationsView
      categoryFilter=""
      connection={connection}
      definitionFilter=""
      isLoadingTarget
      isVisible
      keyKindFilter="Any"
      keyTypeFilter=""
      keyValueFilter=""
      onOpenIteration={() => undefined}
      onReady={() => undefined}
      refreshToken={0}
      statusFilter={[]}
    />,
    { setupWindow: installVirtualLayout }
  );

  try {
    await result.waitFor(() => result.getByText("Iteration query failed. Iterations unavailable"));
    result.getByText("No iterations matched the current query.");
  } finally {
    fetchMock.restore();
    await result.restore();
  }
});

test("iterations view stops automatic pagination after a failed page", async () => {
  const fetchMock = installQueryFetch((call) => {
    if (call.input === "/api/workable/systems/Ops/views/iterations") {
      const options = componentOptions(call);
      if (options.skip === 0) {
        return Response.json(componentResult("iterationGrid", {
          iterations: [iteration({ workerId: { value: "iteration-worker-loaded" } })],
          skip: 0,
          take: 50,
          totalCount: 2,
        }));
      }

      return Response.json({ error: "Next iteration page unavailable" }, { status: 502 });
    }

    return Response.json({ error: `Unhandled request: ${call.input}` }, { status: 500 });
  });
  const result = await renderDom(
    <IterationsView
      categoryFilter=""
      connection={connection}
      definitionFilter=""
      isLoadingTarget
      isVisible
      keyKindFilter="Any"
      keyTypeFilter=""
      keyValueFilter=""
      onOpenIteration={() => undefined}
      onReady={() => undefined}
      refreshToken={0}
      statusFilter={[]}
    />,
    { setupWindow: installVirtualLayout }
  );

  try {
    await result.waitFor(() => result.getByText("#1 / iteration-worker-loaded"));
    const viewport = queryViewport(result);
    await result.scroll(viewport, {
      clientHeight: 100,
      scrollHeight: 240,
      scrollTop: 160,
    });
    await result.waitFor(() =>
      result.getByText("Iteration query failed. Next iteration page unavailable")
    );
    result.getByText("Showing 1 iteration");

    await result.scroll(viewport, {
      clientHeight: 100,
      scrollHeight: 240,
      scrollTop: 160,
    });
    await new Promise((resolve) => setTimeout(resolve, 25));

    assert.equal(
      fetchMock.calls.filter((call) =>
        call.input === "/api/workable/systems/Ops/views/iterations"
      ).length,
      2
    );
  } finally {
    fetchMock.restore();
    await result.restore();
  }
});

test("iterations view clears prior-system rows before a new system request fails", async () => {
  const fetchMock = installQueryFetch((call) => {
    if (call.input === "/api/workable/systems/Ops/views/iterations") {
      return Response.json(componentResult("iterationGrid", {
        iterations: [iteration({ workerId: { value: "ops-iteration-worker" } })],
        skip: 0,
        take: 50,
        totalCount: 1,
      }));
    }

    if (call.input === "/api/workable/systems/Restricted/views/iterations") {
      return Response.json({ error: "Restricted iterations unavailable" }, { status: 502 });
    }

    return Response.json({ error: `Unhandled request: ${call.input}` }, { status: 500 });
  });
  const element = (nextConnection: WorkableConnection) => (
    <IterationsView
      categoryFilter=""
      connection={nextConnection}
      definitionFilter=""
      isLoadingTarget
      isVisible
      keyKindFilter="Any"
      keyTypeFilter=""
      keyValueFilter=""
      onOpenIteration={() => undefined}
      onReady={() => undefined}
      refreshToken={0}
      statusFilter={[]}
    />
  );
  const result = await renderDom(element(connection), { setupWindow: installVirtualLayout });

  try {
    await result.waitFor(() => result.getByText("#1 / ops-iteration-worker"));
    await result.rerender(element({ ...connection, systemName: "Restricted" }));
    await result.waitFor(() =>
      result.getByText("Iteration query failed. Restricted iterations unavailable")
    );

    assert.equal(result.queryByText("#1 / ops-iteration-worker"), null);
    result.getByText("No iterations matched the current query.");
  } finally {
    fetchMock.restore();
    await result.restore();
  }
});

type FetchCall = {
  input: string;
  init?: RequestInit;
};

function installQueryFetch(
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

function installVirtualLayout(window: JSDOM["window"]) {
  Object.defineProperty(window.HTMLElement.prototype, "clientHeight", {
    configurable: true,
    get() {
      return 480;
    },
  });
  Object.defineProperty(window.HTMLElement.prototype, "clientWidth", {
    configurable: true,
    get() {
      return 1024;
    },
  });
  Object.defineProperty(window.HTMLElement.prototype, "offsetHeight", {
    configurable: true,
    get() {
      return 480;
    },
  });
  Object.defineProperty(window.HTMLElement.prototype, "offsetWidth", {
    configurable: true,
    get() {
      return 1024;
    },
  });
  window.HTMLElement.prototype.getBoundingClientRect = function getBoundingClientRect() {
    return new window.DOMRect(0, 0, 1024, 480);
  };
}

function queryViewport(result: Awaited<ReturnType<typeof renderDom>>) {
  const viewport = result.container.querySelector(".workable-grid-scrollbar");
  assert.ok(viewport instanceof result.dom.window.HTMLElement);
  return viewport;
}

function findButtonByName(root: ParentNode, name: string) {
  return Array.from(root.querySelectorAll("button")).find((button) =>
    (
      button.getAttribute("aria-label") ??
      button.textContent?.replace(/\s+/g, " ").trim() ??
      ""
    ) === name
  ) ?? null;
}

function requestBody(call: FetchCall) {
  const body = call.init?.body;
  if (typeof body !== "string") {
    assert.fail("Expected query request body to be a string.");
  }
  return JSON.parse(body) as {
    components: Array<WorkComponentRequest>;
    scope?: unknown;
  };
}

function componentOptions(call: FetchCall) {
  return requestBody(call).components[0]?.options as Record<string, unknown>;
}

function componentResult(id: string, data: unknown): WorkComponentQueryResult {
  return {
    components: {
      [id]: {
        data,
        status: "ok",
      },
    },
    generatedAt: "2026-06-01T12:00:00.000Z",
  };
}

function worker(overrides: Partial<WorkViewWorkerGridDetailed> = {}): WorkViewWorkerGridDetailed {
  return {
    definitionName: "ImportOrders",
    id: { value: "worker-1" },
    identifiers: [{ type: "Tenant", value: "north" }],
    isFinal: false,
    revision: 1,
    state: "Queued" satisfies WorkerState,
    subjectId: null,
    totalExecutionDuration: "00:00:02",
    updatedAt: "2026-06-01T11:59:00.000Z",
    ...overrides,
  };
}

function iteration(
  overrides: Partial<WorkViewIterationGridDetailed> = {}
): WorkViewIterationGridDetailed {
  return {
    completedAt: "2026-06-01T11:59:00.000Z",
    definitionName: "ImportOrders",
    executionDuration: "00:00:01",
    identifiers: [{ type: "Tenant", value: "north" }],
    isFinal: true,
    sequence: 1,
    status: "Completed" satisfies WorkCompletionStatus,
    subjectId: { type: "Order", value: "100" },
    workerId: { value: "worker-1" },
    workerState: "Completed",
    ...overrides,
  };
}
