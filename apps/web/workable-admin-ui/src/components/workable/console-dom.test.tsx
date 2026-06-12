import assert from "node:assert/strict";
import test from "node:test";
import type { JSDOM } from "jsdom";
import { getNextNavigationRouterCalls, resetNextNavigationMock } from "@/test/next-navigation";
import { renderDom } from "@/test/dom";
import {
  STORAGE_KEY,
  WorkableConsole,
  createDefaultConsoleStorage,
} from "@/components/workable/console";
import type { WorkableHostConnection } from "@/components/features/console/types";
import type {
  WorkComponentQueryResult,
  WorkDefinition,
  WorkSystemAccessSummary,
  WorkViewWorkerGridDetailed,
  WorkableHttpHostDescriptor,
  WorkerState,
} from "@/lib/workable";

test("workable console mounts the empty server route state and opens add-server flow", async () => {
  resetNextNavigationMock();
  const result = await renderDom(<WorkableConsole />);

  try {
    result.getByText("Server Explorer");
    result.getByText("No servers");
    result.getByText("Add a Workable HTTP host to discover its systems.");

    await result.click(result.getByRole("button", { name: "Add server" }));

    result.getByText("Add server");
    result.getByText("Discover Workable systems exposed by a host and add selected systems to the tree.");
    result.getByText("Enter a URL and load systems.");
  } finally {
    await result.restore();
  }
});

test("workable console sign-out posts logout and routes back to login", async () => {
  resetNextNavigationMock();
  const calls: Array<{ input: string; init?: RequestInit }> = [];
  const previousFetch = globalThis.fetch;
  globalThis.fetch = (async (input, init) => {
    calls.push({ input: String(input), init });
    return Response.json({ ok: true });
  }) as typeof fetch;

  const result = await renderDom(<WorkableConsole />);

  try {
    await result.click(result.getByRole("button", { name: "Sign out" }));

    assert.deepEqual(calls.map((call) => ({
      input: call.input,
      method: call.init?.method,
    })), [
      {
        input: "/api/auth/logout",
        method: "POST",
      },
    ]);
    assert.deepEqual(getNextNavigationRouterCalls(), {
      refreshCount: 1,
      replaces: ["/login"],
    });
  } finally {
    globalThis.fetch = previousFetch;
    await result.restore();
  }
});

test("workable console restores an authenticated host, loads overview data, and navigates to query views", async () => {
  resetNextNavigationMock();
  const access = fullAccess();
  const fetchMock = installConsoleFetch(access);
  const result = await renderDom(<WorkableConsole />, {
    setupWindow: (window) => {
      window.localStorage.setItem(STORAGE_KEY, JSON.stringify(consoleStorage(access)));
      installVirtualLayout(window);
    },
  });

  try {
    await result.waitFor(() => result.getByText("Console API"));
    await result.waitFor(() => result.getByText("Active workers"));
    result.getByText("Ops");
    result.getByText("7");
    assert.equal(result.queryByText("No servers"), null);

    await result.click(result.getByRole("button", { name: "Workers" }));
    await result.waitFor(() => result.getByText("No workers matched the current query."));

    await result.click(result.getByRole("button", { name: "Iterations" }));
    await result.waitFor(() => result.getByText("No iterations matched the current query."));

    assert.equal(
      fetchMock.calls.some((call) => call.input === "/api/workable/host"),
      true
    );
    assert.equal(
      fetchMock.calls.some((call) => call.input === "/api/workable/systems/Ops/views/overview"),
      true
    );
    assert.equal(
      fetchMock.calls.some((call) => call.input === "/api/workable/systems/Ops/views/workers"),
      true
    );
    assert.equal(
      fetchMock.calls.some((call) => call.input === "/api/workable/systems/Ops/views/iterations"),
      true
    );
    assert.deepEqual(
      fetchMock.calls
        .filter((call) => call.input.startsWith("/api/workable/"))
        .map((call) => new Headers(call.init?.headers).get("x-workable-api-url"))
        .filter((value, index, values) => values.indexOf(value) === index),
      ["https://console.example.com/workable"]
    );
  } finally {
    fetchMock.restore();
    await result.restore();
  }
});

test("workable console navigates to catalog, refreshes definitions, and restores history", async () => {
  resetNextNavigationMock();
  const access = fullAccess();
  const fetchMock = installConsoleFetch(access, {
    definitions: [
      definition({
        category: "Billing",
        description: "Imports orders into billing.",
        id: { value: "definition-import-orders" },
        name: "ImportOrders",
      }),
    ],
  });
  const result = await renderDom(<WorkableConsole />, {
    setupWindow: (window) => {
      window.localStorage.setItem(STORAGE_KEY, JSON.stringify(consoleStorage(access)));
      installVirtualLayout(window);
    },
  });

  try {
    await result.waitFor(() => result.getByText("Active workers"));

    await result.click(result.getByRole("button", { name: "Catalog" }));
    await result.waitFor(() => result.getByText("ImportOrders"));
    result.getByText("Imports orders into billing.");
    result.getByRole("button", { name: "Refresh catalog" });
    await result.waitFor(() => {
      assert.equal(readStoredView(result.dom), "definitions");
    });

    const catalogFetchCount = fetchMock.calls.filter((call) =>
      call.input === "/api/workable/systems/Ops/definitions"
    ).length;

    await result.click(result.getByRole("button", { name: "Refresh catalog" }));
    await result.waitFor(() => {
      assert.equal(
        fetchMock.calls.filter((call) =>
          call.input === "/api/workable/systems/Ops/definitions"
        ).length,
        catalogFetchCount + 1
      );
    });

    await result.click(result.getByRole("button", { name: "Workers" }));
    await result.waitFor(() => result.getByText("No workers matched the current query."));
    await result.waitFor(() => {
      assert.equal(readStoredView(result.dom), "workers");
    });

    await result.click(result.getByRole("button", { name: "Go back" }));
    await result.waitFor(() => result.getByRole("button", { name: "Refresh catalog" }));
    await result.waitFor(() => {
      assert.equal(readStoredView(result.dom), "definitions");
    });

    await result.click(result.getByRole("button", { name: "Go forward" }));
    await result.waitFor(() => result.getByRole("button", { name: "Refresh workers" }));
    await result.waitFor(() => {
      assert.equal(readStoredView(result.dom), "workers");
    });
  } finally {
    fetchMock.restore();
    await result.restore();
  }
});

test("workable console enforces no-work-access permissions in the mounted overview", async () => {
  resetNextNavigationMock();
  const access = {
    ...fullAccess(),
    canOperateAllWork: false,
    canReadAllWork: false,
    canViewDiagnostics: false,
    isWorkAdministrator: false,
    operableDefinitionCount: 0,
    readableDefinitionCount: 0,
  };
  const fetchMock = installConsoleFetch(access);
  const result = await renderDom(<WorkableConsole />, {
    setupWindow: (window) => {
      window.localStorage.setItem(STORAGE_KEY, JSON.stringify(consoleStorage(access)));
    },
  });

  try {
    await result.waitFor(() => result.getByText("No work access"));
    assert.match(
      result.dom.window.document.body.textContent ?? "",
      /You can connect to this system, but you do not have permission to read work\./
    );
    assert.equal(result.queryByText("Active workers"), null);
    assert.equal(result.queryByText("Filter workers"), null);

    const overviewCall = fetchMock.calls.find((call) =>
      call.input === "/api/workable/systems/Ops/views/overview"
    );
    assert.ok(overviewCall);
    assert.deepEqual(
      componentIds(overviewCall),
      ["system"]
    );
  } finally {
    fetchMock.restore();
    await result.restore();
  }
});

test("workable console hides worker mutation controls for read-only work access", async () => {
  resetNextNavigationMock();
  const access = {
    ...fullAccess(),
    canOperateAllWork: false,
    isWorkAdministrator: false,
    operableDefinitionCount: 0,
  };
  const fetchMock = installConsoleFetch(access, {
    workers: [
      worker({
        definitionName: "ImportOrders",
        id: { value: "worker-readonly" },
        state: "Queued",
      }),
    ],
  });
  const result = await renderDom(<WorkableConsole />, {
    setupWindow: (window) => {
      window.localStorage.setItem(STORAGE_KEY, JSON.stringify(consoleStorage(access)));
    },
  });

  try {
    await result.waitFor(() => result.getByText("Active workers"));

    await result.click(result.getByRole("button", { name: "Workers" }));
    await result.waitFor(() => result.getByText("1 worker"));

    assert.equal(findButtonByName(result.container, "Open actions for ImportOrders"), null);
    assert.equal(
      fetchMock.calls.some((call) => call.input.includes("/actions/")),
      false
    );
  } finally {
    fetchMock.restore();
    await result.restore();
  }
});

type FetchCall = {
  input: string;
  init?: RequestInit;
};

function installConsoleFetch(
  access: WorkSystemAccessSummary,
  options?: {
    definitions?: WorkDefinition[];
    workers?: WorkViewWorkerGridDetailed[];
  }
) {
  const previousFetch = globalThis.fetch;
  const calls: FetchCall[] = [];
  globalThis.fetch = (async (input, init) => {
    const call = { input: String(input), init };
    calls.push(call);

    if (call.input === "/api/workable/host") {
      return Response.json(hostDescriptor(access));
    }

    if (call.input === "/api/workable/systems/Ops/views/overview") {
      return Response.json(overviewResult(access));
    }

    if (call.input === "/api/workable/systems/Ops/definitions") {
      return Response.json(options?.definitions ?? [definition()]);
    }

    if (call.input === "/api/workable/systems/Ops/views/workers") {
      return Response.json(componentResult("workerGrid", {
        skip: 0,
        take: 50,
        totalCount: options?.workers?.length ?? 0,
        workers: options?.workers ?? [],
      }));
    }

    if (call.input === "/api/workable/systems/Ops/views/iterations") {
      return Response.json(componentResult("iterationGrid", {
        iterations: [],
        skip: 0,
        take: 50,
        totalCount: 0,
      }));
    }

    return Response.json(
      { error: `Unhandled console test request: ${call.input}` },
      { status: 500 }
    );
  }) as typeof fetch;

  return {
    calls,
    restore() {
      globalThis.fetch = previousFetch;
    },
  };
}

function definition(overrides: Partial<WorkDefinition> = {}): WorkDefinition {
  return {
    category: "Billing",
    description: "Imports orders.",
    id: { value: "definition-import-orders" },
    name: "ImportOrders",
    revision: 1,
    ...overrides,
  };
}

function worker(overrides: Partial<WorkViewWorkerGridDetailed> = {}): WorkViewWorkerGridDetailed {
  return {
    definitionName: "ImportOrders",
    id: { value: "worker-1" },
    identifiers: [],
    isFinal: false,
    revision: 1,
    state: "Queued" satisfies WorkerState,
    subjectId: null,
    totalExecutionDuration: "00:00:02",
    updatedAt: "2026-06-01T11:59:00.000Z",
    ...overrides,
  };
}

function consoleStorage(access: WorkSystemAccessSummary) {
  return {
    ...createDefaultConsoleStorage(),
    activeSystemId: "system-ops",
    expandedHostIds: ["host-1"],
    expandedSystemIds: ["system-ops"],
    hosts: [storedHost(access)],
    view: "overview",
  };
}

function storedHost(access: WorkSystemAccessSummary): WorkableHostConnection {
  return {
    apiUrl: "https://console.example.com/workable",
    id: "host-1",
    name: "Console API",
    realtimeEnabled: false,
    realtimeHubPath: null,
    realtimeTransport: null,
    systems: [
      {
        access,
        hostId: "host-1",
        id: "system-ops",
        name: "Ops",
        capabilities: {
          persistentCoordinationAvailable: true,
          sqlProfilingAvailable: false,
        },
        state: "Started",
        systemName: "Ops",
      },
    ],
  };
}

function hostDescriptor(access: WorkSystemAccessSummary): WorkableHttpHostDescriptor {
  return {
    capabilities: {
      realtime: {
        enabled: false,
        hubPath: null,
        transport: null,
      },
    },
    systems: [
      {
        access,
        capabilities: {
          persistentCoordinationAvailable: true,
          sqlProfilingAvailable: false,
        },
        id: { value: "server-ops" },
        isDefault: false,
        name: "Ops",
        state: "Started",
      },
    ],
  };
}

function overviewResult(access: WorkSystemAccessSummary): WorkComponentQueryResult {
  if (!access.canReadAllWork && access.readableDefinitionCount === 0) {
    return componentResult("system", {
      systemName: "Ops",
      systemState: "Started",
    });
  }

  return {
    generatedAt: "2026-06-01T12:00:00.000Z",
    components: {
      system: okComponent({
        systemName: "Ops",
        systemState: "Started",
      }),
      workers: okComponent({
        activeWorkerCount: 7,
        definitionCount: 3,
        failedWorkerCount: 1,
        finalWorkerCount: 2,
        oldestQueuedAt: null,
        workerCountByState: {
          Queued: 5,
          Running: 2,
        },
      }),
      failedWorkers: okComponent([]),
      iterations: okComponent({
        commonKeyTypes: [],
        iterationCountByStatus: {
          Completed: 4,
          Failed: 1,
        },
      }),
      failedIterations: okComponent([]),
      completedIterations: okComponent([]),
      throughput: okComponent({
        activeWorkerCount: 7,
        throughput: {
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
          settledCount: 5,
          windowSeconds: 60,
        },
      }),
    },
  };
}

function componentResult(id: string, data: unknown): WorkComponentQueryResult {
  return {
    generatedAt: "2026-06-01T12:00:00.000Z",
    components: {
      [id]: okComponent(data),
    },
  };
}

function okComponent(data: unknown) {
  return {
    data,
    status: "ok",
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
    operableDefinitionCount: 3,
    readableDefinitionCount: 3,
    totalDefinitionCount: 3,
  };
}

function readStoredView(result: JSDOM | Pick<JSDOM, "window">) {
  const stored = result.window.localStorage.getItem(STORAGE_KEY);
  assert.ok(stored);
  return (JSON.parse(stored) as { view?: string }).view;
}

function componentIds(call: FetchCall) {
  const body = typeof call.init?.body === "string"
    ? JSON.parse(call.init.body) as { components?: Array<{ id: string }> }
    : {};
  return body.components?.map((component) => component.id) ?? [];
}

function findButtonByName(root: ParentNode, name: string) {
  return Array.from(root.querySelectorAll("button")).find((button) =>
    (button.getAttribute("aria-label") ?? button.textContent?.replace(/\s+/g, " ").trim() ?? "") === name
  ) ?? null;
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
