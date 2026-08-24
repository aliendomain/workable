import assert from "node:assert/strict";
import test from "node:test";
import { ExecutionDiagnosticsCaptureCard } from "@/components/workable/console/execution-diagnostics-capture-card";
import { renderDom } from "@/test/dom";

test("persistent diagnostics card replaces and disables every duplicate work-scoped rule", async () => {
  const previousFetch = globalThis.fetch;
  const requests: Array<{ method: string; url: string; body?: Record<string, unknown> }> = [];
  let finishDisable: (() => void) | undefined;
  let rules = [
    captureRule(),
    captureRule({
      id: "rule-duplicate",
      activeUntil: "2026-08-08T10:46:00Z",
      artifactRetention: "00:30:00",
      createdAt: "2026-08-08T10:01:00Z",
      minimumLogLevel: "Warning",
      profileCaptureMode: "Full",
    }),
    captureRule({ id: "global-rule", definitionName: null }),
  ];
  globalThis.fetch = (async (input, init) => {
    const method = init?.method?.toUpperCase() ?? "GET";
    const url = input.toString();
    requests.push({
      method,
      url,
      body: typeof init?.body === "string" ? JSON.parse(init.body) : undefined,
    });
    if (method === "POST") {
      const created = captureRule({ id: "replacement-rule", createdAt: "2026-08-08T10:02:00Z" });
      rules = [
        ...rules.filter((rule) => rule.definitionName?.toUpperCase() !== created.definitionName?.toUpperCase()),
        created,
      ];
      return Response.json(created);
    }
    if (method === "DELETE") {
      const id = url.split("/").at(-1);
      if (id === "replacement-rule") {
        await new Promise<void>((resolve) => {
          finishDisable = resolve;
        });
      }
      rules = rules.filter((rule) => rule.id !== id);
      return new Response(null, { status: 204 });
    }
    return Response.json({ persistenceAvailable: true, rules });
  }) as typeof fetch;

  const result = await renderDom(
    <ExecutionDiagnosticsCaptureCard
      canControlSystem
      canViewDiagnostics
      connection={{ apiUrl: "https://console.example.com/workable", systemName: "Ops" }}
      definitionName="Orders.Run"
    />
  );

  try {
    await result.waitFor(() => result.getByText("Work: orders.run"));
    await result.waitFor(() => assert.equal(
      (result.getByLabelText("Capture for (minutes)") as HTMLInputElement).value,
      "45"
    ));
    await result.click(result.getByRole("button", { name: "Update persistent diagnostics" }));
    await result.waitFor(() => assert.ok(requests.some((request) => request.method === "POST")));
    assert.deepEqual(requests.find((request) => request.method === "POST")?.body, {
      activeForMinutes: 45,
      artifactRetentionMinutes: 30,
      definitionName: "Orders.Run",
      minimumLogLevel: "Warning",
      profileCaptureMode: "Full",
    });
    assert.equal(requests.some((request) => request.method === "DELETE"), false);

    await result.click(result.getByRole("button", { name: "Disable persistent diagnostics" }));
    await result.waitFor(() => assert.equal(requests.filter((request) => request.method === "DELETE").length, 1));
    assert.ok(result.getByText("Persistent diagnostics updated temporarily for Orders.Run."));
    assert.ok(finishDisable);
    finishDisable();
    await result.waitFor(() => result.getByText("Persistent diagnostic capture stopped. Existing artifacts retain their original expiry."));
    assert.equal(requests.find((request) => request.method === "DELETE")?.url.endsWith("/replacement-rule"), true);
    assert.ok(rules.some((rule) => rule.id === "global-rule"));
  } finally {
    globalThis.fetch = previousFetch;
    await result.restore();
  }
});

test("persistent diagnostics card explains missing repository registration", async () => {
  const previousFetch = globalThis.fetch;
  let fetchCalled = false;
  globalThis.fetch = (async () => {
    fetchCalled = true;
    throw new Error("The unavailable path must not call an unmapped diagnostics route.");
  }) as typeof fetch;
  const result = await renderDom(
    <ExecutionDiagnosticsCaptureCard
      canControlSystem
      canViewDiagnostics
      connection={{
        apiUrl: "https://console.example.com/workable",
        executionDiagnosticsPersistenceAvailable: false,
      }}
    />
  );

  try {
    await result.waitFor(() => result.getByText("Persistence not registered"));
    assert.equal(result.getByRole("button", { name: "Persist all work temporarily" }).hasAttribute("disabled"), true);
    assert.equal(fetchCalled, false);
  } finally {
    globalThis.fetch = previousFetch;
    await result.restore();
  }
});

test("persistent diagnostics card remains readable but disables mutations without control permission", async () => {
  const previousFetch = globalThis.fetch;
  const requests: string[] = [];
  globalThis.fetch = (async (_input, init) => {
    requests.push(init?.method?.toUpperCase() ?? "GET");
    return Response.json({ persistenceAvailable: true, rules: [captureRule()] });
  }) as typeof fetch;
  const result = await renderDom(
    <ExecutionDiagnosticsCaptureCard
      canControlSystem={false}
      canViewDiagnostics
      connection={{ apiUrl: "https://console.example.com/workable" }}
      definitionName="orders.run"
    />
  );

  try {
    await result.waitFor(() => result.getByText("Control-system permission is required to change persistent capture."));
    assert.equal(result.getByRole("button", { name: "Update persistent diagnostics" }).hasAttribute("disabled"), true);
    assert.equal(result.getByRole("button", { name: "Disable persistent diagnostics" }).hasAttribute("disabled"), true);
    assert.deepEqual(requests, ["GET"]);
  } finally {
    globalThis.fetch = previousFetch;
    await result.restore();
  }
});

test("work-scoped diagnostics card does not treat the system-wide fallback as its own configuration", async () => {
  const previousFetch = globalThis.fetch;
  globalThis.fetch = (async () => Response.json({
    persistenceAvailable: true,
    rules: [captureRule({ definitionName: null })],
  })) as typeof fetch;
  const result = await renderDom(
    <ExecutionDiagnosticsCaptureCard
      canControlSystem
      canViewDiagnostics
      connection={{ apiUrl: "https://console.example.com/workable" }}
      definitionName="orders.run"
    />
  );

  try {
    await result.waitFor(() => result.getByText("Persistent diagnostics are not active for this scope."));
    assert.equal(result.queryByText("All work"), null);
    assert.ok(result.getByRole("button", { name: "Persist this work temporarily" }));
  } finally {
    globalThis.fetch = previousFetch;
    await result.restore();
  }
});

test("persistent diagnostics mutation reconciles with a fresh GET during an older catalog refresh", async () => {
  const previousFetch = globalThis.fetch;
  let getCount = 0;
  let finishStaleRefresh: (() => void) | undefined;
  let rules: ReturnType<typeof captureRule>[] = [];
  globalThis.fetch = (async (_input, init) => {
    const method = init?.method?.toUpperCase() ?? "GET";
    if (method === "POST") {
      const created = captureRule({ id: "fresh-rule", definitionName: null });
      rules = [created];
      return Response.json(created);
    }

    getCount += 1;
    const responseRules = [...rules];
    if (getCount === 2) {
      await new Promise<void>((resolve) => {
        finishStaleRefresh = resolve;
      });
    }
    return Response.json({ persistenceAvailable: true, rules: responseRules });
  }) as typeof fetch;

  const renderCard = (refreshToken: number) => (
    <ExecutionDiagnosticsCaptureCard
      canControlSystem
      canViewDiagnostics
      connection={{ apiUrl: "https://console.example.com/workable", systemName: "Ops" }}
      refreshToken={refreshToken}
    />
  );
  const result = await renderDom(renderCard(0));

  try {
    await result.waitFor(() => result.getByRole("button", { name: "Persist all work temporarily" }));
    await result.rerender(renderCard(1));
    await result.waitFor(() => assert.ok(finishStaleRefresh));

    await result.click(result.getByRole("button", { name: "Persist all work temporarily" }));
    await result.waitFor(() => assert.equal(getCount, 3));
    await result.waitFor(() => result.getByRole("button", { name: "Update persistent diagnostics" }));
    result.getByText("All work");

    finishStaleRefresh?.();
    await new Promise((resolve) => setTimeout(resolve, 20));
    result.getByRole("button", { name: "Update persistent diagnostics" });
    result.getByText("All work");
  } finally {
    finishStaleRefresh?.();
    globalThis.fetch = previousFetch;
    await result.restore();
  }
});

test("persistent diagnostics card preserves a save error through fresh reconciliation", async () => {
  const previousFetch = globalThis.fetch;
  let getCount = 0;
  globalThis.fetch = (async (_input, init) => {
    if (init?.method?.toUpperCase() === "POST") {
      return Response.json({
        messages: [{ text: "Persistent capture was rejected." }],
      }, { status: 400 });
    }

    getCount += 1;
    return Response.json({ persistenceAvailable: true, rules: [] });
  }) as typeof fetch;
  const result = await renderDom(
    <ExecutionDiagnosticsCaptureCard
      canControlSystem
      canViewDiagnostics
      connection={{ apiUrl: "https://console.example.com/workable", systemName: "Ops" }}
    />
  );

  try {
    await result.waitFor(() => result.getByRole("button", { name: "Persist all work temporarily" }));
    const save = result.getByRole("button", { name: "Persist all work temporarily" });
    await result.click(save);
    await result.waitFor(() => assert.equal(getCount, 2));
    result.getByText("Persistent capture was rejected.");
    assert.equal(save.hasAttribute("disabled"), false);
    assert.equal(result.queryByText("Capture updated"), null);
  } finally {
    globalThis.fetch = previousFetch;
    await result.restore();
  }
});

test("persistent diagnostics card discards stale state when the selected system changes", async () => {
  const previousFetch = globalThis.fetch;
  let systemACalls = 0;
  let finishSystemARefresh: (() => void) | undefined;
  let finishSystemBLoad: (() => void) | undefined;
  globalThis.fetch = (async (input) => {
    const url = String(input);
    if (url.includes("/systems/SystemA/")) {
      systemACalls += 1;
      if (systemACalls === 2) {
        await new Promise<void>((resolve) => {
          finishSystemARefresh = resolve;
        });
      }
      return Response.json({
        persistenceAvailable: true,
        rules: [captureRule({ definitionName: null })],
      });
    }

    await new Promise<void>((resolve) => {
      finishSystemBLoad = resolve;
    });
    return Response.json({ persistenceAvailable: true, rules: [] });
  }) as typeof fetch;

  const renderCard = (systemName: string, refreshToken: number) => (
    <ExecutionDiagnosticsCaptureCard
      canControlSystem
      canViewDiagnostics
      connection={{ apiUrl: "https://systems.example.com/workable", systemName }}
      refreshToken={refreshToken}
    />
  );
  const result = await renderDom(renderCard("SystemA", 0));

  try {
    await result.waitFor(() => result.getByRole("button", { name: "Update persistent diagnostics" }));
    await result.rerender(renderCard("SystemA", 1));
    await result.waitFor(() => assert.ok(finishSystemARefresh));

    await result.rerender(renderCard("SystemB", 0));
    const initializingButton = result.getByRole("button", { name: "Persist all work temporarily" });
    assert.equal(initializingButton.hasAttribute("disabled"), true);
    assert.equal(result.queryByText("All work"), null);

    assert.ok(finishSystemBLoad);
    finishSystemBLoad();
    await result.waitFor(() => assert.equal(
      result.getByRole("button", { name: "Persist all work temporarily" }).hasAttribute("disabled"),
      false
    ));
    finishSystemARefresh?.();
    await new Promise((resolve) => setTimeout(resolve, 20));
    assert.ok(result.getByRole("button", { name: "Persist all work temporarily" }));
    assert.equal(result.queryByText("All work"), null);
  } finally {
    finishSystemARefresh?.();
    finishSystemBLoad?.();
    globalThis.fetch = previousFetch;
    await result.restore();
  }
});

test("persistent diagnostics card settles after its initial rule load fails", async () => {
  const previousFetch = globalThis.fetch;
  globalThis.fetch = (async () => {
    throw new Error("Capture rules could not be loaded.");
  }) as typeof fetch;
  const result = await renderDom(
    <ExecutionDiagnosticsCaptureCard
      canControlSystem
      canViewDiagnostics
      connection={{ apiUrl: "https://console.example.com/workable", systemName: "Ops" }}
    />
  );

  try {
    await result.waitFor(() => result.getByText("Capture rules could not be loaded."));
    assert.equal(result.queryByText("Loading persistent capture rules…"), null);
    assert.equal(
      result.getByRole("button", { name: "Persist all work temporarily" }).hasAttribute("disabled"),
      true
    );
  } finally {
    globalThis.fetch = previousFetch;
    await result.restore();
  }
});

test("persistent diagnostics card waits for every duplicate delete before reloading after a failure", async () => {
  const previousFetch = globalThis.fetch;
  let getCount = 0;
  let finishSecondDelete: (() => void) | undefined;
  const rules = [captureRule({ id: "first" }), captureRule({ id: "second" })];
  globalThis.fetch = (async (input, init) => {
    const method = init?.method?.toUpperCase() ?? "GET";
    if (method === "GET") {
      getCount += 1;
      return Response.json({ persistenceAvailable: true, rules });
    }

    if (String(input).endsWith("/first")) {
      return Response.json({ error: "delete failed" }, { status: 500 });
    }

    await new Promise<void>((resolve) => {
      finishSecondDelete = resolve;
    });
    return new Response(null, { status: 204 });
  }) as typeof fetch;
  const result = await renderDom(
    <ExecutionDiagnosticsCaptureCard
      canControlSystem
      canViewDiagnostics
      connection={{ apiUrl: "https://console.example.com/workable", systemName: "Ops" }}
      definitionName="orders.run"
    />
  );

  try {
    await result.waitFor(() => result.getByRole("button", { name: "Disable persistent diagnostics" }));
    await result.click(result.getByRole("button", { name: "Disable persistent diagnostics" }));
    await result.waitFor(() => assert.ok(finishSecondDelete));
    assert.equal(getCount, 1);
    finishSecondDelete?.();
    await result.waitFor(() => assert.equal(getCount, 2));
    await result.waitFor(() => result.getByText("delete failed"));
  } finally {
    finishSecondDelete?.();
    globalThis.fetch = previousFetch;
    await result.restore();
  }
});

function captureRule(overrides: Record<string, unknown> = {}) {
  return {
    id: "rule-1",
    definitionName: "orders.run",
    minimumLogLevel: "Information",
    profileCaptureMode: null,
    artifactRetention: "1.00:00:00",
    createdAt: "2026-08-08T10:00:00Z",
    activeUntil: "2026-08-08T10:30:00Z",
    createdBy: { id: "admin-1", name: "Admin" },
    ...overrides,
  };
}
