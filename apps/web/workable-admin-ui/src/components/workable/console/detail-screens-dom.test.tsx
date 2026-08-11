import assert from "node:assert/strict";
import test from "node:test";
import { ConsoleHeaderCapabilitiesProvider } from "@/components/features/console/header-capabilities";
import {
  DefinitionsView,
  QueueDialog,
  WorkerConsoleView,
  resolveIterationHttpClientProfilingAvailable,
  resolveIterationSqlProfilingAvailable,
} from "@/components/workable/console/detail-screens";
import { renderDom } from "@/test/dom";
import type {
  QueueWorkRequest,
  WorkDefinition,
  WorkWorkerOverviewComponent,
  WorkableConnection,
} from "@/lib/workable";

const connection: WorkableConnection = {
  apiUrl: "https://console.example.com/workable",
  systemName: "Ops",
};

test("catalog renders persistent execution diagnostics before definitions and refreshes both", async () => {
  const fetchMock = installQueueFetch((call) => {
    if (call.input === "/api/workable/systems/Ops/definitions") {
      return Response.json([definition()]);
    }

    if (call.input === "/api/workable/systems/Ops/execution-diagnostics/capture-rules") {
      return Response.json({ persistenceAvailable: true, rules: [] });
    }

    return Response.json({ error: `Unhandled request: ${call.input}` }, { status: 500 });
  });
  const result = await renderDom(
    <DefinitionsView
      canControlSystem
      canViewDiagnostics
      catalogScope={null}
      connection={connection}
      onCatalogScopeChange={() => undefined}
      onOpenDefinition={() => undefined}
      onOpenWorker={() => undefined}
      onReady={() => undefined}
      refreshToken={0}
    />
  );

  try {
    await result.waitFor(() => result.getByText("Persistent execution diagnostics"));
    await result.waitFor(() => result.getByText("Catalog"));
    const diagnosticsHeading = result.getByText("Persistent execution diagnostics");
    const catalogHeading = result.getByText("Catalog");
    assert.equal(
      Boolean(
        diagnosticsHeading.compareDocumentPosition(catalogHeading) &
        result.dom.window.Node.DOCUMENT_POSITION_FOLLOWING
      ),
      true
    );
    await result.waitFor(() => result.getByText("ImportOrders"));

    await result.rerender(
      <DefinitionsView
        canControlSystem
        canViewDiagnostics
        catalogScope={null}
        connection={connection}
        onCatalogScopeChange={() => undefined}
        onOpenDefinition={() => undefined}
        onOpenWorker={() => undefined}
        onReady={() => undefined}
        refreshToken={1}
      />
    );
    await result.waitFor(() => {
      assert.equal(
        fetchMock.calls.filter((call) =>
          call.input === "/api/workable/systems/Ops/execution-diagnostics/capture-rules"
        ).length,
        2
      );
      assert.equal(
        fetchMock.calls.filter((call) =>
          call.input === "/api/workable/systems/Ops/definitions"
        ).length,
        2
      );
    });
  } finally {
    fetchMock.restore();
    await result.restore();
  }
});

test("iteration profile SQL availability prefers the live overview capability over stored navigation state", () => {
  assert.equal(
    resolveIterationSqlProfilingAvailable({
      capabilities: {
        httpClientProfilingAvailable: false,
        persistentCoordinationAvailable: false,
        sqlProfilingAvailable: true,
      },
    }, false),
    true
  );
  assert.equal(
    resolveIterationSqlProfilingAvailable({
      capabilities: {
        httpClientProfilingAvailable: false,
        persistentCoordinationAvailable: false,
        sqlProfilingAvailable: false,
      },
    }, true),
    false
  );
  assert.equal(resolveIterationSqlProfilingAvailable(null, false), false);
});

test("iteration profile HTTP availability prefers the live overview capability over stored navigation state", () => {
  assert.equal(
    resolveIterationHttpClientProfilingAvailable({
      capabilities: {
        httpClientProfilingAvailable: true,
        persistentCoordinationAvailable: false,
        sqlProfilingAvailable: false,
      },
    }, false),
    true
  );
  assert.equal(
    resolveIterationHttpClientProfilingAvailable({
      capabilities: {
        httpClientProfilingAvailable: false,
        persistentCoordinationAvailable: false,
        sqlProfilingAvailable: false,
      },
    }, true),
    false
  );
  assert.equal(resolveIterationHttpClientProfilingAvailable(null, false), false);
});

test("queue dialog applies schema defaults, submits edited input, and closes on Queue", async () => {
  const openChanges: boolean[] = [];
  const queuedWorkers: string[] = [];
  const fetchMock = installQueueFetch((call) => {
    if (call.input === "/api/workable/systems/Ops/work/ImportOrders") {
      return Response.json({ workerId: { value: "worker-queued" } });
    }

    return Response.json({ error: `Unhandled request: ${call.input}` }, { status: 500 });
  });
  const result = await renderDom(
    <QueueDialog
      connection={connection}
      definition={definition()}
      fetchQueueSchemaWhenNeeded={false}
      onOpenChange={(open) => openChanges.push(open)}
      onQueuedWorker={(workerId) => queuedWorkers.push(workerId)}
    />
  );

  try {
    await result.waitFor(() => result.getByText("ImportOrders"));
    const orderInput = result.getByRole("textbox");
    assert.ok(orderInput instanceof result.dom.window.HTMLInputElement);
    await result.waitFor(() => {
      assert.equal(orderInput.value, "100");
    });

    await result.input(orderInput, "A-100");
    await result.click(result.getByRole("button", { name: "Queue" }));

    await result.waitFor(() => {
      assert.equal(fetchMock.calls.length, 1);
    });
    assert.deepEqual(queueRequest(fetchMock.calls[0]).input, {
      orderId: "A-100",
      priority: "normal",
    });
    assert.deepEqual(openChanges, [false]);
    assert.deepEqual(queuedWorkers, []);
  } finally {
    fetchMock.restore();
    await result.restore();
  }
});

test("queue dialog validates manual JSON before posting", async () => {
  const fetchMock = installQueueFetch((call) =>
    Response.json({ error: `Unexpected request: ${call.input}` }, { status: 500 })
  );
  const result = await renderDom(
    <QueueDialog
      connection={connection}
      definition={manualDefinition()}
      fetchQueueSchemaWhenNeeded={false}
      onOpenChange={() => undefined}
      onQueuedWorker={() => undefined}
    />
  );

  try {
    await result.waitFor(() => result.getByText("ImportOrders"));
    await result.waitFor(() => result.getByRole("tab", { name: "Formatted" }));
    await result.mouseDown(result.getByRole("tab", { name: "Formatted" }));
    await result.mouseUp(result.getByRole("tab", { name: "Formatted" }));

    const manualEditor = result.container.ownerDocument.querySelector("textarea");
    assert.ok(manualEditor instanceof result.dom.window.HTMLTextAreaElement);
    await result.input(manualEditor, "{oops");
    await result.click(result.getByRole("button", { name: "Queue" }));

    await result.waitFor(() => result.getByText("Queue failed"));
    result.getByText("Input must be valid JSON.");
    assert.equal(fetchMock.calls.length, 0);
  } finally {
    fetchMock.restore();
    await result.restore();
  }
});

test("queue dialog posts manual subject and concurrency data and opens the queued worker", async () => {
  const openChanges: boolean[] = [];
  const queuedWorkers: string[] = [];
  const fetchMock = installQueueFetch((call) => {
    if (call.input === "/api/workable/systems/Ops/work/ImportOrders") {
      return Response.json({ workerId: { value: "worker-manual" } });
    }

    return Response.json({ error: `Unhandled request: ${call.input}` }, { status: 500 });
  });
  const result = await renderDom(
    <QueueDialog
      connection={connection}
      definition={manualDefinition()}
      fetchQueueSchemaWhenNeeded={false}
      onOpenChange={(open) => openChanges.push(open)}
      onQueuedWorker={(workerId) => queuedWorkers.push(workerId)}
    />
  );

  try {
    await result.waitFor(() => result.getByText("ImportOrders"));
    await result.waitFor(() => result.getByRole("tab", { name: "Formatted" }));
    await result.mouseDown(result.getByRole("tab", { name: "Formatted" }));
    await result.mouseUp(result.getByRole("tab", { name: "Formatted" }));

    const manualEditor = result.container.ownerDocument.querySelector("textarea");
    assert.ok(manualEditor instanceof result.dom.window.HTMLTextAreaElement);
    await result.input(manualEditor, JSON.stringify({
      completion: "ReturnAfterAccepted",
      concurrencyKey: { type: "Tenant", value: "north" },
      input: { orderId: "B-200" },
      subjectId: { type: "Order", value: "B-200" },
    }, null, 2));
    await result.click(result.getByRole("button", { name: "Watch" }));

    await result.waitFor(() => {
      assert.equal(fetchMock.calls.length, 1);
    });
    assert.deepEqual(queueRequest(fetchMock.calls[0]), {
      completion: "ReturnAfterAccepted",
      concurrencyKey: { type: "Tenant", value: "north" },
      input: { orderId: "B-200" },
      subjectId: { type: "Order", value: "B-200" },
    });
    assert.deepEqual(openChanges, [false]);
    assert.deepEqual(queuedWorkers, ["worker-manual"]);
  } finally {
    fetchMock.restore();
    await result.restore();
  }
});

test("queue dialog keeps the dialog open and reports server queue failures", async () => {
  const openChanges: boolean[] = [];
  const queuedWorkers: string[] = [];
  const fetchMock = installQueueFetch((call) => {
    if (call.input === "/api/workable/systems/Ops/work/ImportOrders") {
      return Response.json({ error: "Queue unavailable" }, { status: 503 });
    }

    return Response.json({ error: `Unhandled request: ${call.input}` }, { status: 500 });
  });
  const result = await renderDom(
    <QueueDialog
      connection={connection}
      definition={definition()}
      fetchQueueSchemaWhenNeeded={false}
      onOpenChange={(open) => openChanges.push(open)}
      onQueuedWorker={(workerId) => queuedWorkers.push(workerId)}
    />
  );

  try {
    await result.waitFor(() => result.getByText("ImportOrders"));
    await result.click(result.getByRole("button", { name: "Queue" }));

    await result.waitFor(() => result.getByText("Queue failed"));
    result.getByText("Queue unavailable");
    assert.equal(fetchMock.calls.length, 1);
    assert.deepEqual(openChanges, []);
    assert.deepEqual(queuedWorkers, []);
  } finally {
    fetchMock.restore();
    await result.restore();
  }
});

test("queue dialog tolerates boolean property schemas from the API", async () => {
  const fetchMock = installQueueFetch((call) => {
    if (call.input === "/api/workable/systems/Ops/work/ImportOrders") {
      return Response.json({ workerId: { value: "worker-boolean-schema" } });
    }

    return Response.json({ error: `Unhandled request: ${call.input}` }, { status: 500 });
  });
  const result = await renderDom(
    <QueueDialog
      connection={connection}
      definition={booleanPropertyDefinition()}
      fetchQueueSchemaWhenNeeded={false}
      onOpenChange={() => undefined}
      onQueuedWorker={() => undefined}
    />
  );

  try {
    await result.waitFor(() => result.getByText("ImportOrders"));

    const payloadInput = result.getByRole("textbox");
    assert.ok(payloadInput instanceof result.dom.window.HTMLInputElement);
    assert.equal(payloadInput.value, "");

    await result.input(payloadInput, "{\"source\":\"backstage\"}");
    await result.click(result.getByRole("button", { name: "Queue" }));

    await result.waitFor(() => {
      assert.equal(fetchMock.calls.length, 1);
    });
    assert.deepEqual(queueRequest(fetchMock.calls[0]).input, {
      payload: "{\"source\":\"backstage\"}",
    });
  } finally {
    fetchMock.restore();
    await result.restore();
  }
});

test("worker console exposes a view workflow action when the overview carries a trusted workflow run id", async () => {
  const openedWorkflowRuns: string[] = [];
  const fetchMock = installQueueFetch((call) => {
    if (call.input.includes("/workers/worker-1/overview")) {
      return Response.json(workerOverview({
        worker: {
          configDifferenceCount: 0,
          createdAt: "2026-06-27T12:00:00.000Z",
          createdOrigin: { channel: "HttpApi" },
          definitionCategory: "Ops",
          definitionName: "ImportOrders",
          identifiers: [{ type: "workflow-run", value: "forged-run" }],
          isFinal: false,
          nextRunAt: null,
          retryAttempt: null,
          revision: 3,
          state: "Running",
          stateChangedAt: "2026-06-27T12:01:00.000Z",
          stateSequence: 4,
          updatedAt: "2026-06-27T12:01:00.000Z",
          workerId: { value: "worker-1" },
          workflowRunId: { value: "run-123" },
        },
      }));
    }

    return Response.json({ error: `Unhandled request: ${call.input}` }, { status: 500 });
  });
  const result = await renderDom(
    <ConsoleHeaderCapabilitiesProvider>
      <WorkerConsoleView
        canViewDiagnostics
        clearSystemNotification={() => undefined}
        connection={connection}
        onActiveRealtimeConnectionCountChange={() => undefined}
        onNavigateBack={() => undefined}
        onOpenDefinitionCatalog={() => undefined}
        onOpenIteration={() => undefined}
        onOpenWorker={() => undefined}
        onOpenWorkflowRun={(workflowRunId) => openedWorkflowRuns.push(workflowRunId)}
        onRealtimePayloadOpenChange={() => undefined}
        refreshToken={0}
        realtimePayloadCaptureEnabled={false}
        realtimePayloadMaxMessages={20}
        realtimePayloadOpen={false}
        reportSystemNotification={() => undefined}
        workerId="worker-1"
      />
    </ConsoleHeaderCapabilitiesProvider>
  );

  try {
    await result.waitFor(() => result.getByRole("button", { name: "View Workflow" }));
    await result.click(result.getByRole("button", { name: "View Workflow" }));
    assert.deepEqual(openedWorkflowRuns, ["run-123"]);
  } finally {
    fetchMock.restore();
    await result.restore();
  }
});

type FetchCall = {
  input: string;
  init?: RequestInit;
};

function installQueueFetch(handler: (call: FetchCall) => Response | Promise<Response>) {
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

function queueRequest(call: FetchCall): QueueWorkRequest {
  const body = call.init?.body;
  if (typeof body !== "string") {
    assert.fail("Expected queue request body to be a string.");
  }
  return JSON.parse(body) as QueueWorkRequest;
}

function workerOverview(
  overrides: Partial<WorkWorkerOverviewComponent> = {}
): WorkWorkerOverviewComponent {
  return {
    activity: "Logs",
    input: null,
    latestIteration: null,
    logs: {
      page: { hasMore: false, items: [] },
      summary: {
        critical: 0,
        debug: 0,
        error: 0,
        errors: 0,
        information: 0,
        total: 0,
        trace: 0,
        warning: 0,
        warnings: 0,
      },
    },
    recentIterations: [],
    timeline: {
      page: { hasMore: false, items: [] },
      summary: {
        failureCount: 0,
        systemEventCount: 0,
        total: 0,
        userActionCount: 0,
      },
    },
    worker: {
      configDifferenceCount: 0,
      createdAt: "2026-06-27T12:00:00.000Z",
      createdOrigin: { channel: "HttpApi" },
      definitionCategory: "Ops",
      definitionName: "ImportOrders",
      identifiers: [],
      isFinal: false,
      nextRunAt: null,
      retryAttempt: null,
      revision: 1,
      state: "Queued",
      stateChangedAt: "2026-06-27T12:00:00.000Z",
      stateSequence: 1,
      updatedAt: "2026-06-27T12:00:00.000Z",
      workerId: { value: "worker-1" },
    },
    ...overrides,
  };
}

function definition(): WorkDefinition {
  return {
    category: "Ops",
    configuration: null,
    defaultOptions: null,
    description: "Imports orders from the queue.",
    id: { value: "definition-import-orders" },
    inputSchema: {
      jsonSchema: JSON.stringify({
        properties: {
          orderId: {
            default: "100",
            title: "Order ID",
            type: "string",
          },
          priority: {
            default: "normal",
            enum: ["normal", "urgent"],
            title: "Priority",
          },
        },
        required: ["orderId"],
        type: "object",
      }),
    },
    metadata: null,
    name: "ImportOrders",
    outputSchema: null,
    revision: 1,
  };
}

function manualDefinition(): WorkDefinition {
  return {
    ...definition(),
    inputSchema: null,
  };
}

function booleanPropertyDefinition(): WorkDefinition {
  return {
    ...definition(),
    inputSchema: {
      jsonSchema: JSON.stringify({
        properties: {
          payload: true,
        },
        type: "object",
      }),
    },
  };
}
