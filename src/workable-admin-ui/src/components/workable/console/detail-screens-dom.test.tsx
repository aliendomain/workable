import assert from "node:assert/strict";
import test from "node:test";
import { QueueDialog } from "@/components/workable/console/detail-screens";
import { renderDom } from "@/test/dom";
import type {
  QueueWorkRequest,
  WorkDefinition,
  WorkableConnection,
} from "@/lib/workable";

const connection: WorkableConnection = {
  apiUrl: "https://console.example.com/workable",
  systemName: "Ops",
};

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
