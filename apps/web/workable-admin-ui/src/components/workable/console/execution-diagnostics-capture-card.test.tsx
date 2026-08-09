import assert from "node:assert/strict";
import test from "node:test";
import { ExecutionDiagnosticsCaptureCard } from "@/components/workable/console/execution-diagnostics-capture-card";
import { renderDom } from "@/test/dom";

test("persistent diagnostics card creates and removes a work-scoped capture rule", async () => {
  const previousFetch = globalThis.fetch;
  const requests: Array<{ method: string; body?: Record<string, unknown> }> = [];
  let rules = [captureRule()];
  globalThis.fetch = (async (_input, init) => {
    const method = init?.method?.toUpperCase() ?? "GET";
    requests.push({
      method,
      body: typeof init?.body === "string" ? JSON.parse(init.body) : undefined,
    });
    if (method === "POST") {
      const created = captureRule({ id: "rule-2" });
      rules = [...rules, created];
      return Response.json(created);
    }
    if (method === "DELETE") {
      rules = [];
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
    await result.click(result.getByRole("button", { name: "Persist this work temporarily" }));
    await result.waitFor(() => assert.ok(requests.some((request) => request.method === "POST")));
    assert.deepEqual(requests.find((request) => request.method === "POST")?.body, {
      activeForMinutes: 30,
      artifactRetentionMinutes: 1440,
      definitionName: "Orders.Run",
      minimumLogLevel: "Information",
      profileCaptureMode: null,
    });

    const remove = result.container.querySelector('button[aria-label="Stop persistent diagnostic capture"]');
    assert.ok(remove instanceof result.dom.window.HTMLElement);
    await result.click(remove);
    await result.waitFor(() => assert.ok(requests.some((request) => request.method === "DELETE")));
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
    assert.equal(result.getByRole("button", { name: "Persist this work temporarily" }).hasAttribute("disabled"), true);
    const remove = result.container.querySelector('button[aria-label="Stop persistent diagnostic capture"]');
    assert.ok(remove instanceof result.dom.window.HTMLButtonElement);
    assert.equal(remove.disabled, true);
    assert.deepEqual(requests, ["GET"]);
  } finally {
    globalThis.fetch = previousFetch;
    await result.restore();
  }
});

test("work-scoped diagnostics card shows matching system-wide rules", async () => {
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
    await result.waitFor(() => result.getByText("All work"));
    assert.equal(result.queryByText("No active persistent capture rules match this view."), null);
  } finally {
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
