import assert from "node:assert/strict";
import test from "node:test";
import { ProfilingCaptureRulesCard } from "@/components/workable/console/profiling-capture-rules-card";
import { renderDom } from "@/test/dom";

test("profiling capture card creates work and user rules and removes active rules", async () => {
  const previousFetch = globalThis.fetch;
  const requests: Array<{ method: string; body?: unknown }> = [];
  let rules = [captureRule()];
  globalThis.fetch = (async (_input, init) => {
    const method = init?.method?.toUpperCase() ?? "GET";
    requests.push({
      method,
      body: typeof init?.body === "string" ? JSON.parse(init.body) : undefined,
    });

    if (method === "POST") {
      const body = requests.at(-1)?.body as { actorId?: string; definitionName?: string };
      const created = captureRule({
        id: `rule-${rules.length + 1}`,
        actorId: body.actorId,
        definitionName: body.definitionName,
      });
      rules = [...rules, created];
      return Response.json(created);
    }

    if (method === "DELETE") {
      rules = rules.slice(1);
      return new Response(null, { status: 204 });
    }

    return Response.json({
      maximumAutomaticInstrumentationNodes: 500,
      rules,
    });
  }) as typeof fetch;

  const result = await renderDom(
    <ProfilingCaptureRulesCard
      actorId="user-123"
      canViewDiagnostics
      connection={{ apiUrl: "https://console.example.com/workable", systemName: "Ops" }}
      definitionName="orders.run"
    />
  );

  try {
    await result.waitFor(() => result.getByText("Work: orders.run"));
    assert.match(result.container.textContent ?? "", /500-node automatic SQL, HTTP, and extension limit/);

    await result.click(result.getByRole("button", { name: "Capture by work type" }));
    await result.waitFor(() => assert.ok(requests.some((request) => request.method === "POST")));
    assert.deepEqual(
      requests.find((request) => request.method === "POST")?.body,
      {
        definitionName: "orders.run",
        expiresAfterMinutes: 30,
        maximumMatches: 1,
      }
    );

    const remove = result.container.querySelector('button[aria-label="Remove full profile capture rule"]');
    assert.ok(remove instanceof result.dom.window.HTMLElement);
    await result.click(remove);
    await result.waitFor(() => assert.ok(requests.some((request) => request.method === "DELETE")));
  } finally {
    globalThis.fetch = previousFetch;
    await result.restore();
  }
});

test("profiling capture card does not request or render diagnostics controls without permission", async () => {
  const previousFetch = globalThis.fetch;
  let fetchCount = 0;
  globalThis.fetch = (async () => {
    fetchCount += 1;
    return Response.json({ maximumAutomaticInstrumentationNodes: 500, rules: [] });
  }) as typeof fetch;

  const result = await renderDom(
    <ProfilingCaptureRulesCard
      actorId="user-123"
      canViewDiagnostics={false}
      connection={{ apiUrl: "https://console.example.com/workable", systemName: "Ops" }}
      definitionName="orders.run"
    />
  );

  try {
    await new Promise((resolve) => setTimeout(resolve, 10));
    assert.equal(fetchCount, 0);
    assert.equal(result.container.textContent, "");
  } finally {
    globalThis.fetch = previousFetch;
    await result.restore();
  }
});

function captureRule(overrides: Record<string, unknown> = {}) {
  return {
    id: "rule-1",
    definitionName: "orders.run",
    actorId: null,
    maximumMatches: 2,
    remainingMatches: 2,
    createdAt: "2026-08-03T10:00:00Z",
    expiresAt: "2026-08-03T10:30:00Z",
    createdBy: { id: "admin-1", name: "Admin" },
    ...overrides,
  };
}
