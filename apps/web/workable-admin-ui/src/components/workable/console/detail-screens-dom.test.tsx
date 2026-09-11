import assert from "node:assert/strict";
import test from "node:test";
import { ConsoleHeaderCapabilitiesProvider } from "@/components/features/console/header-capabilities";
import {
  defaultWorkConfiguration,
  DefinitionsView,
  QueueDialog,
  WorkerConsoleView,
  resolveIterationHttpClientProfilingAvailable,
  resolveIterationSqlProfilingAvailable,
  type WorkerConsoleViewUiStateSnapshot,
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

test("catalog renders diagnostic capture controls before definitions and refreshes them", async () => {
  const fetchMock = installQueueFetch((call) => {
    if (call.input === "/api/workable/systems/Ops/definitions") {
      return Response.json([definition()]);
    }

    if (call.input === "/api/workable/systems/Ops/execution-diagnostics/capture-rules") {
      return Response.json({ persistenceAvailable: true, rules: [] });
    }

    if (call.input === "/api/workable/systems/Ops/profiling/capture-rules") {
      return Response.json({ maximumAutomaticInstrumentationNodes: 500, rules: [] });
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
    await result.waitFor(() => result.getByText("Full profile capture"));
    await result.waitFor(() => result.getByText("Catalog"));
    const diagnosticsHeading = result.getByText("Persistent execution diagnostics");
    const profileCaptureHeading = result.getByText("Full profile capture");
    const catalogHeading = result.getByText("Catalog");
    assert.equal(
      Boolean(
        profileCaptureHeading.compareDocumentPosition(catalogHeading) &
        result.dom.window.Node.DOCUMENT_POSITION_FOLLOWING
      ),
      true
    );
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
          call.input === "/api/workable/systems/Ops/profiling/capture-rules"
        ).length,
        2
      );
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

test("catalog clears prior-system definitions before a new system request fails", async () => {
  const betaConnection: WorkableConnection = {
    ...connection,
    systemName: "Beta",
  };
  const fetchMock = installQueueFetch((call) => {
    if (call.input === "/api/workable/systems/Ops/definitions") {
      return Response.json([definition()]);
    }

    if (call.input === "/api/workable/systems/Beta/definitions") {
      return Response.json({ error: "Beta unavailable." }, { status: 502 });
    }

    return Response.json({ error: `Unhandled request: ${call.input}` }, { status: 500 });
  });
  const element = (activeConnection: WorkableConnection) => (
    <DefinitionsView
      canControlSystem={false}
      canViewDiagnostics={false}
      catalogScope={null}
      connection={activeConnection}
      onCatalogScopeChange={() => undefined}
      onOpenDefinition={() => undefined}
      onOpenWorker={() => undefined}
      onReady={() => undefined}
      refreshToken={0}
    />
  );
  const result = await renderDom(element(connection));

  try {
    await result.waitFor(() => result.getByText("ImportOrders"));
    await result.rerender(element(betaConnection));
    await result.waitFor(() => result.getByText("Beta unavailable."));
    assert.throws(() => result.getByText("ImportOrders"));
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

test("queue dialog hides scheduling when the work system does not expose it", async () => {
  const result = await renderDom(
    <QueueDialog
      connection={connection}
      definition={definition()}
      fetchQueueSchemaWhenNeeded={false}
      onOpenChange={() => undefined}
      onQueuedWorker={() => undefined}
    />
  );

  try {
    await result.waitFor(() => result.getByText("ImportOrders"));
    assert.equal(result.queryByText("Schedule"), null);
  } finally {
    await result.restore();
  }
});

test("queue dialog creates a one-time schedule from the configured request", async () => {
  const openChanges: boolean[] = [];
  const fetchMock = installQueueFetch((call) => {
    if (call.input === "/api/workable/systems/Ops/work/ImportOrders/schedules") {
      return Response.json({ status: "Accepted", schedule: { id: { value: "schedule-1" } } });
    }

    return Response.json({ error: `Unhandled request: ${call.input}` }, { status: 500 });
  });
  const result = await renderDom(
    <QueueDialog
      connection={{ ...connection, schedulingAvailable: true }}
      definition={definition()}
      fetchQueueSchemaWhenNeeded={false}
      onOpenChange={(open) => openChanges.push(open)}
      onQueuedWorker={() => undefined}
    />
  );

  try {
    await result.waitFor(() => result.getByText("ImportOrders"));
    await result.click(result.getByRole("button", { name: "Schedule" }));
    result.getByText("The latest definition will be used each time this schedule runs.");
    const firstRun = result.getByLabelText("First run");
    assert.ok(firstRun instanceof result.dom.window.HTMLInputElement);
    await result.input(firstRun, "2099-01-02T03:04");
    await result.click(result.getByLabelText("Run after downtime"));
    await result.click(result.getByRole("button", { name: "Create schedule" }));

    await result.waitFor(() => assert.equal(fetchMock.calls.length, 1));
    const body = queueRequest(fetchMock.calls[0]) as unknown as {
      timing: { firstRunAt: string; interval: string | null; runMissedExecution: boolean };
      work: QueueWorkRequest;
    };
    assert.equal(body.timing.firstRunAt, new Date("2099-01-02T03:04").toISOString());
    assert.equal(body.timing.interval, null);
    assert.equal(body.timing.runMissedExecution, false);
    assert.deepEqual(body.work.input, { orderId: "100", priority: "normal" });
    assert.equal("completion" in body.work, false);
    assert.deepEqual(openChanges, [false]);
  } finally {
    fetchMock.restore();
    await result.restore();
  }
});

test("queue dialog creates a recurring interval schedule", async () => {
  const fetchMock = installQueueFetch(() => Response.json({ status: "Accepted" }));
  const result = await renderDom(
    <QueueDialog
      connection={{ ...connection, schedulingAvailable: true }}
      definition={definition()}
      fetchQueueSchemaWhenNeeded={false}
      onOpenChange={() => undefined}
      onQueuedWorker={() => undefined}
    />
  );

  try {
    await result.waitFor(() => result.getByText("ImportOrders"));
    await result.click(result.getByRole("button", { name: "Schedule" }));
    await result.click(result.getByRole("button", { name: "Repeat" }));
    const interval = result.getByLabelText("Repeat every");
    assert.ok(interval instanceof result.dom.window.HTMLInputElement);
    await result.input(interval, "6");
    await result.click(result.getByRole("combobox", { name: "Repeat interval unit" }));
    await result.click(result.getByRole("option", { name: "Days" }));
    await result.click(result.getByRole("button", { name: "Create schedule" }));

    await result.waitFor(() => assert.equal(fetchMock.calls.length, 1));
    const body = queueRequest(fetchMock.calls[0]) as unknown as {
      timing: { interval: string | null; runMissedExecution: boolean };
    };
    assert.equal(body.timing.interval, "6.00:00:00");
    assert.equal(body.timing.runMissedExecution, true);
  } finally {
    fetchMock.restore();
    await result.restore();
  }
});

test("queue dialog previews and creates a timezone-aware cron schedule", async () => {
  const fetchMock = installQueueFetch((call) => {
    if (call.input === "/api/workable/systems/Ops/schedules/cron-preview") {
      return Response.json({
        occurrences: [
          "2099-01-01T10:15:00Z",
          "2099-01-02T10:15:00Z",
          "2099-01-05T10:15:00Z",
          "2099-01-06T10:15:00Z",
          "2099-01-07T10:15:00Z",
        ],
      });
    }

    if (call.input === "/api/workable/systems/Ops/work/ImportOrders/schedules") {
      return Response.json({ status: "Accepted" });
    }

    return Response.json({ error: `Unhandled request: ${call.input}` }, { status: 500 });
  });
  const result = await renderDom(
    <QueueDialog
      connection={{ ...connection, schedulingAvailable: true }}
      definition={definition()}
      fetchQueueSchemaWhenNeeded={false}
      onOpenChange={() => undefined}
      onQueuedWorker={() => undefined}
    />
  );

  try {
    await result.waitFor(() => result.getByText("ImportOrders"));
    await result.click(result.getByRole("button", { name: "Schedule" }));
    await result.click(result.getByRole("button", { name: "Repeat" }));
    await result.click(result.getByRole("button", { name: "Cron" }));
    const startsAfter = result.getByLabelText("Starts after");
    const expression = result.getByLabelText("Cron expression");
    const timeZone = result.getByLabelText("Time zone");
    assert.ok(startsAfter instanceof result.dom.window.HTMLInputElement);
    assert.ok(expression instanceof result.dom.window.HTMLInputElement);
    assert.ok(timeZone instanceof result.dom.window.HTMLInputElement);
    await result.input(startsAfter, "2099-01-01T00:00");
    await result.input(expression, "15 10 * * 1-5");
    await result.input(timeZone, "UTC");

    const create = result.getByRole("button", { name: "Create schedule" });
    await result.waitFor(() => assert.equal((create as HTMLButtonElement).disabled, false));
    await result.click(create);
    await result.waitFor(() => assert.ok(
      fetchMock.calls.some((call) => call.input.endsWith("/work/ImportOrders/schedules"))
    ));

    const previewCall = fetchMock.calls.find((call) => call.input.endsWith("/schedules/cron-preview"));
    const scheduleCall = fetchMock.calls.find((call) => call.input.endsWith("/work/ImportOrders/schedules"));
    assert.ok(previewCall);
    assert.ok(scheduleCall);
    assert.deepEqual(queueRequest(previewCall), {
      cronExpression: "15 10 * * 1-5",
      timeZoneId: "UTC",
      startsAt: new Date("2099-01-01T00:00").toISOString(),
      count: 5,
    });
    const body = queueRequest(scheduleCall) as unknown as {
      timing: {
        firstRunAt: string;
        interval: string | null;
        cronExpression: string | null;
        timeZoneId: string | null;
      };
    };
    assert.equal(body.timing.firstRunAt, new Date("2099-01-01T00:00").toISOString());
    assert.equal(body.timing.interval, null);
    assert.equal(body.timing.cronExpression, "15 10 * * 1-5");
    assert.equal(body.timing.timeZoneId, "UTC");
  } finally {
    fetchMock.restore();
    await result.restore();
  }
});

test("queue dialog blocks cron creation when the server rejects its preview", async () => {
  const fetchMock = installQueueFetch(() => Response.json({
    messages: [{ text: "The cron expression is outside its valid range." }],
  }, { status: 400 }));
  const result = await renderDom(
    <QueueDialog
      connection={{ ...connection, schedulingAvailable: true }}
      definition={definition()}
      fetchQueueSchemaWhenNeeded={false}
      onOpenChange={() => undefined}
      onQueuedWorker={() => undefined}
    />
  );

  try {
    await result.waitFor(() => result.getByText("ImportOrders"));
    await result.click(result.getByRole("button", { name: "Schedule" }));
    await result.click(result.getByRole("button", { name: "Repeat" }));
    await result.click(result.getByRole("button", { name: "Cron" }));
    const expression = result.getByLabelText("Cron expression");
    assert.ok(expression instanceof result.dom.window.HTMLInputElement);
    await result.input(expression, "61 9 * * *");

    await result.waitFor(() => result.getByText("The cron expression is outside its valid range."));
    assert.equal(
      (result.getByRole("button", { name: "Create schedule" }) as HTMLButtonElement).disabled,
      true
    );
    assert.ok(fetchMock.calls.some((call) => call.input.endsWith("/schedules/cron-preview")));
    assert.equal(fetchMock.calls.some((call) => call.input.endsWith("/work/ImportOrders/schedules")), false);
  } finally {
    fetchMock.restore();
    await result.restore();
  }
});

test("queue dialog reports non-error cron preview failures", async () => {
  const fetchMock = installQueueFetch(async () => {
    throw "preview unavailable";
  });
  const result = await renderDom(
    <QueueDialog
      connection={{ ...connection, schedulingAvailable: true }}
      definition={definition()}
      fetchQueueSchemaWhenNeeded={false}
      onOpenChange={() => undefined}
      onQueuedWorker={() => undefined}
    />
  );

  try {
    await result.waitFor(() => result.getByText("ImportOrders"));
    await result.click(result.getByRole("button", { name: "Schedule" }));
    await result.click(result.getByRole("button", { name: "Repeat" }));
    await result.click(result.getByRole("button", { name: "Cron" }));

    await result.waitFor(() => result.getByText("Cron preview failed."));
    assert.equal(
      (result.getByRole("button", { name: "Create schedule" }) as HTMLButtonElement).disabled,
      true
    );
  } finally {
    fetchMock.restore();
    await result.restore();
  }
});

test("queue dialog ignores an aborted cron preview after switching back to interval", async () => {
  const fetchMock = installQueueFetch((call) => new Promise<Response>((_resolve, reject) => {
    call.init?.signal?.addEventListener("abort", () => reject(new Error("Aborted preview.")));
  }));
  const result = await renderDom(
    <QueueDialog
      connection={{ ...connection, schedulingAvailable: true }}
      definition={definition()}
      fetchQueueSchemaWhenNeeded={false}
      onOpenChange={() => undefined}
      onQueuedWorker={() => undefined}
    />
  );

  try {
    await result.waitFor(() => result.getByText("ImportOrders"));
    await result.click(result.getByRole("button", { name: "Schedule" }));
    await result.click(result.getByRole("button", { name: "Repeat" }));
    await result.click(result.getByRole("button", { name: "Cron" }));
    await result.waitFor(() => assert.ok(
      fetchMock.calls.some((call) => call.input.endsWith("/schedules/cron-preview"))
    ));
    await result.click(result.getByRole("button", { name: "Interval" }));

    await result.waitFor(() => result.getByLabelText("Repeat every"));
    assert.equal(result.queryByText("Aborted preview."), null);
    assert.equal(
      (result.getByRole("button", { name: "Create schedule" }) as HTMLButtonElement).disabled,
      false
    );
  } finally {
    fetchMock.restore();
    await result.restore();
  }
});

test("queue dialog disables repeat for definitions with static recurrence", async () => {
  const result = await renderDom(
    <QueueDialog
      connection={{ ...connection, schedulingAvailable: true }}
      definition={{
        ...definition(),
        configuration: {
          ...defaultWorkConfiguration,
          recurrence: {
            ...defaultWorkConfiguration.recurrence,
            isEnabled: true,
          },
        },
      }}
      fetchQueueSchemaWhenNeeded={false}
      onOpenChange={() => undefined}
      onQueuedWorker={() => undefined}
    />
  );

  try {
    await result.waitFor(() => result.getByText("ImportOrders"));
    await result.click(result.getByRole("button", { name: "Schedule" }));
    const repeat = result.getByRole("button", { name: "Repeat" });
    assert.equal((repeat as HTMLButtonElement).disabled, true);
    result.getByText("Repeat is unavailable because this definition already has static recurrence.");
  } finally {
    await result.restore();
  }
});

test("queue dialog validates schedule timing and recurring interval before posting", async () => {
  const fetchMock = installQueueFetch(() => Response.json({ status: "Accepted" }));
  const result = await renderDom(
    <QueueDialog
      connection={{ ...connection, schedulingAvailable: true }}
      definition={definition()}
      fetchQueueSchemaWhenNeeded={false}
      onOpenChange={() => undefined}
      onQueuedWorker={() => undefined}
    />
  );

  try {
    await result.waitFor(() => result.getByText("ImportOrders"));
    await result.click(result.getByRole("button", { name: "Schedule" }));
    const firstRun = result.getByLabelText("First run");
    assert.ok(firstRun instanceof result.dom.window.HTMLInputElement);
    await result.input(firstRun, "");
    await result.click(result.getByRole("button", { name: "Create schedule" }));
    result.getByText("Choose a valid first run date and time.");

    await result.input(firstRun, "2020-01-01T00:00");
    await result.click(result.getByRole("button", { name: "Create schedule" }));
    result.getByText("Choose a first run date and time in the future.");

    await result.input(firstRun, "2099-01-01T00:00");
    await result.click(result.getByRole("button", { name: "Repeat" }));
    const interval = result.getByLabelText("Repeat every");
    assert.ok(interval instanceof result.dom.window.HTMLInputElement);
    await result.input(interval, "0");
    await result.click(result.getByRole("button", { name: "Create schedule" }));
    result.getByText("Repeat every must be a whole number greater than zero.");

    await result.click(result.getByRole("button", { name: "Cron" }));
    const cron = result.getByLabelText("Cron expression");
    assert.ok(cron instanceof result.dom.window.HTMLInputElement);
    await result.input(cron, "0 9 * *");
    await result.waitFor(() => result.getByText("Cron expressions must contain five fields."));
    assert.equal(
      (result.getByRole("button", { name: "Create schedule" }) as HTMLButtonElement).disabled,
      true
    );
    assert.equal(fetchMock.calls.length, 0);
  } finally {
    fetchMock.restore();
    await result.restore();
  }
});

test("queue dialog reports schedule failures and returns to queue actions", async () => {
  const fetchMock = installQueueFetch(() => Response.json({ error: "Schedule storage is offline." }, { status: 503 }));
  const result = await renderDom(
    <QueueDialog
      connection={{ ...connection, schedulingAvailable: true }}
      definition={definition()}
      fetchQueueSchemaWhenNeeded={false}
      onOpenChange={() => undefined}
      onQueuedWorker={() => undefined}
    />
  );

  try {
    await result.waitFor(() => result.getByText("ImportOrders"));
    await result.click(result.getByRole("button", { name: "Schedule" }));
    const firstRun = result.getByLabelText("First run");
    assert.ok(firstRun instanceof result.dom.window.HTMLInputElement);
    await result.input(firstRun, "2099-01-01T00:00");
    await result.click(result.getByRole("button", { name: "Create schedule" }));
    await result.waitFor(() => result.getByText("Schedule failed"));
    result.getByText("Schedule storage is offline.");
    await result.click(result.getByRole("button", { name: "Back" }));
    result.getByRole("button", { name: "Queue" });
    assert.equal(result.queryByText("Schedule failed"), null);
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
          profilingCaptureMode: "Bounded",
          profilingEnabled: false,
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

test("worker console shows the latest iteration failure while the worker remains active", async () => {
  const fetchMock = installQueueFetch((call) => {
    if (call.input.includes("/workers/worker-1/overview")) {
      return Response.json(workerOverview({
        latestIteration: {
          attemptCount: 1,
          completedAt: "2026-06-27T12:01:00.000Z",
          executionDuration: "00:01:00",
          failure: {
            declaredByWork: true,
            kind: "Failure",
            message: "Iteration 1 failed without a log entry.",
          },
          sequence: 1,
          startedAt: "2026-06-27T12:00:00.000Z",
          status: "Failed",
          workerId: { value: "worker-1" },
        },
        worker: {
          ...workerOverview().worker,
          nextRunAt: "2026-06-27T12:05:00.000Z",
          state: "Waiting",
          stateSequence: 2,
        },
      }));
    }

    return Response.json({ error: `Unhandled request: ${call.input}` }, { status: 500 });
  });
  const initialUiState = {
    focusedWorkerHiddenSnapshotPanelIds: ["workerConfiguration"],
    focusedWorkerPanel: "workerLogs",
    hiddenPanelIds: ["workerConfiguration", "workerDuration", "workerTimeline"],
    logSortDirection: "desc",
    selectedLogLevels: null,
    selectedTimelineFilters: null,
    timelineSortDirection: "desc",
    workerConfigurationAutoShowAllValues: true,
    workerConfigurationDisplayMode: "auto",
    workerConfigurationPanelViewState: "compact",
    workerControlsPanelViewState: "compact",
    workerDurationPanelViewState: "standard",
    workerId: "worker-1",
    workerLogsPanelViewState: "detailed",
    workerTimelinePanelViewState: "standard",
  } satisfies WorkerConsoleViewUiStateSnapshot;
  const element = (
    <ConsoleHeaderCapabilitiesProvider>
      <WorkerConsoleView
        canViewDiagnostics={false}
        clearSystemNotification={() => undefined}
        connection={connection}
        initialUiState={initialUiState}
        onActiveRealtimeConnectionCountChange={() => undefined}
        onNavigateBack={() => undefined}
        onOpenDefinitionCatalog={() => undefined}
        onOpenIteration={() => undefined}
        onOpenWorker={() => undefined}
        onOpenWorkflowRun={() => undefined}
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
  const result = await renderDom(element);

  try {
    await result.waitFor(() => result.getByText("Iteration 1 failed without a log entry."));
    assert.equal(result.getByText("Execution failed").closest("section")?.classList.contains("shrink-0"), true);
    await result.click(result.getByRole("button", { name: "Dismiss failure banner" }));
    assert.throws(() => result.getByText("Iteration 1 failed without a log entry."));
  } finally {
    fetchMock.restore();
    await result.restore();
  }
});

test("worker pagination ignores failed and successful pages from an older connection generation", async () => {
  let overviewRequestCount = 0;
  let resolveStaleLogPage: ((response: Response) => void) | undefined;
  let resolveStaleTimelinePage: ((response: Response) => void) | undefined;
  const staleLogPage = new Promise<Response>((resolve) => {
    resolveStaleLogPage = resolve;
  });
  const staleTimelinePage = new Promise<Response>((resolve) => {
    resolveStaleTimelinePage = resolve;
  });
  const initialUiState = {
    focusedWorkerHiddenSnapshotPanelIds: null,
    focusedWorkerPanel: null,
    hiddenPanelIds: [],
    logSortDirection: "desc",
    selectedLogLevels: null,
    selectedTimelineFilters: null,
    timelineSortDirection: "desc",
    workerConfigurationAutoShowAllValues: true,
    workerConfigurationDisplayMode: "auto",
    workerConfigurationPanelViewState: "compact",
    workerControlsPanelViewState: "compact",
    workerDurationPanelViewState: "standard",
    workerId: "worker-1",
    workerLogsPanelViewState: "standard",
    workerTimelinePanelViewState: "standard",
  } satisfies WorkerConsoleViewUiStateSnapshot;
  const fetchMock = installQueueFetch((call) => {
    if (call.input.includes("/workers/worker-1/overview/logs?")) {
      return staleLogPage;
    }

    if (
      call.input.includes("/workers/worker-1/overview/timeline?") &&
      call.input.includes("activityCursor=")
    ) {
      return staleTimelinePage;
    }

    if (call.input.includes("/workers/worker-1/overview/timeline?")) {
      const timeline = workerOverview().timeline;
      assert.ok(timeline?.page);
      timeline.page.cursor = `timeline-cursor-${overviewRequestCount + 1}`;
      timeline.page.hasMore = true;
      timeline.page.items = [{
        at: "2026-06-27T12:00:00.000Z",
        category: "SystemEvent",
        id: `base-timeline-${overviewRequestCount + 1}`,
        kind: "StateChange",
        state: "Queued",
      }];
      return Response.json(timeline);
    }

    if (call.input.includes("/workers/worker-1/overview?")) {
      overviewRequestCount += 1;
      const overview = workerOverview();
      if (overview.logs?.page) {
        overview.logs.page.cursor = `cursor-${overviewRequestCount}`;
        overview.logs.page.hasMore = true;
      }
      if (overview.timeline?.page) {
        overview.timeline.page.cursor = `timeline-cursor-${overviewRequestCount}`;
        overview.timeline.page.hasMore = true;
        overview.timeline.page.items = [{
          at: "2026-06-27T12:00:00.000Z",
          category: "SystemEvent",
          id: `base-timeline-${overviewRequestCount}`,
          kind: "StateChange",
          state: "Queued",
        }];
      }
      return Response.json(overview);
    }

    return Response.json({ error: `Unhandled request: ${call.input}` }, { status: 500 });
  });
  const element = (activeConnection: WorkableConnection) => (
    <ConsoleHeaderCapabilitiesProvider>
      <WorkerConsoleView
        canViewDiagnostics={false}
        clearSystemNotification={() => undefined}
        connection={activeConnection}
        initialUiState={initialUiState}
        onActiveRealtimeConnectionCountChange={() => undefined}
        onNavigateBack={() => undefined}
        onOpenDefinitionCatalog={() => undefined}
        onOpenIteration={() => undefined}
        onOpenWorker={() => undefined}
        onOpenWorkflowRun={() => undefined}
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
  const result = await renderDom(element(connection));
  const loadMoreLabels = () => Array.from(result.container.querySelectorAll("span"))
    .filter((element) => element.textContent === "Scroll to load more");

  try {
    await result.waitFor(() => assert.equal(loadMoreLabels().length, 2));
    for (const loadMoreLabel of loadMoreLabels()) {
      const viewport = loadMoreLabel.parentElement?.parentElement;
      assert.ok(viewport instanceof result.dom.window.HTMLElement);
      await result.scroll(viewport, {
        clientHeight: 100,
        scrollHeight: 240,
        scrollTop: 160,
      });
    }
    await result.waitFor(() => {
      assert.equal(
        fetchMock.calls.some((call) =>
          call.input.includes("/workers/worker-1/overview/logs?")
        ),
        true
      );
      assert.equal(
        fetchMock.calls.some((call) =>
          call.input.includes("/workers/worker-1/overview/timeline?") &&
          call.input.includes("activityCursor=")
        ),
        true
      );
    });

    await result.rerender(element({ ...connection, systemName: "Beta" }));
    await result.waitFor(() => assert.equal(overviewRequestCount, 2));
    resolveStaleLogPage?.(
      Response.json({ error: "Old log page unavailable" }, { status: 502 })
    );
    const staleTimelineSummary = workerOverview().timeline?.summary;
    assert.ok(staleTimelineSummary);
    resolveStaleTimelinePage?.(Response.json({
      page: {
        cursor: null,
        hasMore: false,
        items: [{
          at: "2026-06-27T12:00:01.000Z",
          category: "Failure",
          id: "stale-timeline",
          iterationStatus: "Failed",
          kind: "Iteration",
          sequence: 99,
        }],
      },
      summary: staleTimelineSummary,
    }));
    await new Promise((resolve) => setTimeout(resolve, 25));

    assert.equal(result.queryByText("Old log page unavailable"), null);
    assert.equal(result.queryByText("Iteration #99 failed"), null);
    assert.equal(loadMoreLabels().length, 2);
  } finally {
    resolveStaleLogPage?.(
      Response.json({ error: "Test cleanup" }, { status: 500 })
    );
    resolveStaleTimelinePage?.(
      Response.json({ error: "Test cleanup" }, { status: 500 })
    );
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
      profilingCaptureMode: "Bounded",
      profilingEnabled: false,
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
