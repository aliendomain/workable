import assert from "node:assert/strict";
import test from "node:test";
import {
  ProfilingCaptureRulesCard,
  WorkerProfilingCaptureCard,
} from "@/components/workable/console/profiling-capture-rules-card";
import { renderDom } from "@/test/dom";

test("definition profiling capture card toggles capture without flashing its content", async () => {
  const previousFetch = globalThis.fetch;
  const requests: Array<{ method: string; body?: unknown }> = [];
  let rules: ReturnType<typeof captureRule>[] = [];
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
      canControlSystem
      canViewDiagnostics
      connection={{ apiUrl: "https://console.example.com/workable", systemName: "Ops" }}
      definitionName="orders.run"
    />
  );

  try {
    await result.waitFor(() => result.getByRole("button", { name: "Capture this definition" }));
    assert.match(result.container.textContent ?? "", /500-node automatic SQL, HTTP, and extension limit/);
    assert.equal(result.queryByText("User ID (optional)"), null);

    await result.click(result.getByRole("button", { name: "Capture this definition" }));
    await result.waitFor(() => assert.ok(requests.some((request) => request.method === "POST")));
    assert.deepEqual(
      requests.find((request) => request.method === "POST")?.body,
      {
        definitionName: "orders.run",
        expiresAfterMinutes: 30,
        maximumMatches: 1,
      }
    );
    await result.waitFor(() => result.getByText("Work: orders.run"));
    const successBanner = result.getByText("Profile capture updated");
    const disable = result.getByRole("button", { name: "Disable full capture" });
    assert.equal(disable.querySelector(".animate-spin"), null);

    await result.click(disable);
    assert.equal(result.queryByText("Loading capture rules…"), null);
    await result.waitFor(() => assert.ok(requests.some((request) => request.method === "DELETE")));
    await result.waitFor(() => result.getByRole("button", { name: "Capture this definition" }));
    assert.notEqual(result.getByText("Profile capture updated"), successBanner);
  } finally {
    globalThis.fetch = previousFetch;
    await result.restore();
  }
});

test("definition profiling capture toggle removes duplicate broad rules together", async () => {
  const previousFetch = globalThis.fetch;
  const requests: string[] = [];
  let rules = [
    captureRule({ id: "rule-1" }),
    captureRule({ id: "rule-2" }),
    captureRule({ id: "rule-3" }),
  ];
  globalThis.fetch = (async (input, init) => {
    const method = init?.method?.toUpperCase() ?? "GET";
    requests.push(method);
    if (method === "DELETE") {
      const id = String(input).split("/").at(-1);
      rules = rules.filter((rule) => rule.id !== id);
      return new Response(null, { status: 204 });
    }

    return Response.json({ maximumAutomaticInstrumentationNodes: 500, rules });
  }) as typeof fetch;

  const result = await renderDom(
    <ProfilingCaptureRulesCard
      canControlSystem
      canViewDiagnostics
      connection={{ apiUrl: "https://console.example.com/workable", systemName: "Ops" }}
      definitionName="orders.run"
    />
  );

  try {
    await result.waitFor(() => result.getByRole("button", { name: "Disable full capture" }));
    await result.click(result.getByRole("button", { name: "Disable full capture" }));
    await result.waitFor(() => assert.equal(requests.filter((method) => method === "DELETE").length, 3));
    await result.waitFor(() => result.getByRole("button", { name: "Capture this definition" }));
  } finally {
    globalThis.fetch = previousFetch;
    await result.restore();
  }
});

test("profiling capture card keeps its current state visible during a catalog refresh", async () => {
  const previousFetch = globalThis.fetch;
  let getCount = 0;
  let finishRefresh: (() => void) | undefined;
  globalThis.fetch = (async (_input, init) => {
    if ((init?.method?.toUpperCase() ?? "GET") === "GET") {
      getCount += 1;
      if (getCount === 2) {
        await new Promise<void>((resolve) => {
          finishRefresh = resolve;
        });
      }
    }
    return Response.json({ maximumAutomaticInstrumentationNodes: 500, rules: [] });
  }) as typeof fetch;

  const renderCard = (refreshToken: number) => (
    <ProfilingCaptureRulesCard
      canControlSystem
      canViewDiagnostics
      connection={{ apiUrl: "https://refresh.example.com/workable", systemName: "Ops" }}
      refreshToken={refreshToken}
    />
  );
  const result = await renderDom(renderCard(0));

  try {
    await result.waitFor(() => result.getByRole("button", { name: "Capture all work" }));
    await result.rerender(renderCard(1));
    await result.waitFor(() => assert.equal(getCount, 2));
    assert.equal(result.getByRole("button", { name: "Capture all work" }).hasAttribute("disabled"), false);
    assert.equal(result.queryByText("Loading capture rules…"), null);
    assert.ok(finishRefresh);
    finishRefresh();
    await result.waitFor(() => assert.equal(getCount, 2));
  } finally {
    finishRefresh?.();
    globalThis.fetch = previousFetch;
    await result.restore();
  }
});

test("profiling capture mutation reconciles with a fresh GET during an older catalog refresh", async () => {
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
    return Response.json({ maximumAutomaticInstrumentationNodes: 500, rules: responseRules });
  }) as typeof fetch;

  const renderCard = (refreshToken: number) => (
    <ProfilingCaptureRulesCard
      canControlSystem
      canViewDiagnostics
      connection={{ apiUrl: "https://console.example.com/workable", systemName: "Ops" }}
      refreshToken={refreshToken}
    />
  );
  const result = await renderDom(renderCard(0));

  try {
    await result.waitFor(() => result.getByRole("button", { name: "Capture all work" }));
    await result.rerender(renderCard(1));
    await result.waitFor(() => assert.ok(finishStaleRefresh));

    await result.click(result.getByRole("button", { name: "Capture all work" }));
    await result.waitFor(() => assert.equal(getCount, 3));
    await result.waitFor(() => result.getByRole("button", { name: "Disable full capture" }));
    result.getByText("All work");

    finishStaleRefresh?.();
    await new Promise((resolve) => setTimeout(resolve, 20));
    result.getByRole("button", { name: "Disable full capture" });
    result.getByText("All work");
  } finally {
    finishStaleRefresh?.();
    globalThis.fetch = previousFetch;
    await result.restore();
  }
});

test("profiling capture card discards stale state when the selected system changes", async () => {
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
        maximumAutomaticInstrumentationNodes: 500,
        rules: [captureRule({ definitionName: null })],
      });
    }

    await new Promise<void>((resolve) => {
      finishSystemBLoad = resolve;
    });
    return Response.json({ maximumAutomaticInstrumentationNodes: 250, rules: [] });
  }) as typeof fetch;

  const renderCard = (systemName: string, refreshToken: number) => (
    <ProfilingCaptureRulesCard
      canControlSystem
      canViewDiagnostics
      connection={{ apiUrl: "https://systems.example.com/workable", systemName }}
      refreshToken={refreshToken}
    />
  );
  const result = await renderDom(renderCard("SystemA", 0));

  try {
    await result.waitFor(() => result.getByRole("button", { name: "Disable full capture" }));
    await result.rerender(renderCard("SystemA", 1));
    await result.waitFor(() => assert.ok(finishSystemARefresh));

    await result.rerender(renderCard("SystemB", 0));
    const initializingButton = result.getByRole("button", { name: "Capture all work" });
    assert.equal(initializingButton.hasAttribute("disabled"), true);
    assert.equal(result.queryByText("All work"), null);

    assert.ok(finishSystemBLoad);
    finishSystemBLoad();
    await result.waitFor(() => assert.equal(
      result.getByRole("button", { name: "Capture all work" }).hasAttribute("disabled"),
      false
    ));
    finishSystemARefresh?.();
    await new Promise((resolve) => setTimeout(resolve, 20));
    assert.ok(result.getByRole("button", { name: "Capture all work" }));
    assert.equal(result.queryByText("All work"), null);
  } finally {
    finishSystemARefresh?.();
    finishSystemBLoad?.();
    globalThis.fetch = previousFetch;
    await result.restore();
  }
});

test("profiling capture card waits for every duplicate delete before reloading after a failure", async () => {
  const previousFetch = globalThis.fetch;
  let getCount = 0;
  let finishSecondDelete: (() => void) | undefined;
  const rules = [
    captureRule({ id: "first", definitionName: null }),
    captureRule({ id: "second", definitionName: null }),
  ];
  globalThis.fetch = (async (input, init) => {
    const method = init?.method?.toUpperCase() ?? "GET";
    if (method === "GET") {
      getCount += 1;
      return Response.json({ maximumAutomaticInstrumentationNodes: 500, rules });
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
    <ProfilingCaptureRulesCard
      canControlSystem
      canViewDiagnostics
      connection={{ apiUrl: "https://console.example.com/workable", systemName: "Ops" }}
    />
  );

  try {
    await result.waitFor(() => result.getByRole("button", { name: "Disable full capture" }));
    await result.click(result.getByRole("button", { name: "Disable full capture" }));
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

test("profiling capture card does not request or render diagnostics controls without permission", async () => {
  const previousFetch = globalThis.fetch;
  let fetchCount = 0;
  globalThis.fetch = (async () => {
    fetchCount += 1;
    return Response.json({ maximumAutomaticInstrumentationNodes: 500, rules: [] });
  }) as typeof fetch;

  const result = await renderDom(
    <ProfilingCaptureRulesCard
      canControlSystem={false}
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

test("profiling capture card is read-only without system-control permission", async () => {
  const previousFetch = globalThis.fetch;
  const methods: string[] = [];
  globalThis.fetch = (async (_input, init) => {
    methods.push(init?.method?.toUpperCase() ?? "GET");
    return Response.json({
      maximumAutomaticInstrumentationNodes: 500,
      rules: [captureRule({ definitionName: null })],
    });
  }) as typeof fetch;

  const result = await renderDom(
    <ProfilingCaptureRulesCard
      canControlSystem={false}
      canViewDiagnostics
      connection={{ apiUrl: "https://console.example.com/workable", systemName: "Ops" }}
    />
  );

  try {
    await result.waitFor(() => result.getByRole("button", { name: "Disable full capture" }));
    assert.equal(result.getByRole("button", { name: "Disable full capture" }).hasAttribute("disabled"), true);
    assert.equal(result.getByRole("button", { name: "Remove full profile capture rule" }).hasAttribute("disabled"), true);
    assert.ok([...result.container.querySelectorAll("input")].every((input) => input.disabled));
    result.getByText("System-control permission is required to create or remove temporary full-profile rules.");
    assert.deepEqual(methods, ["GET"]);
  } finally {
    globalThis.fetch = previousFetch;
    await result.restore();
  }
});

test("global profiling capture card creates an all-work rule without selectors", async () => {
  const previousFetch = globalThis.fetch;
  const requests: Array<{ method: string; body?: unknown }> = [];
  let rules: ReturnType<typeof captureRule>[] = [];
  globalThis.fetch = (async (input, init) => {
    const method = init?.method?.toUpperCase() ?? "GET";
    requests.push({
      method,
      body: typeof init?.body === "string" ? JSON.parse(init.body) : undefined,
    });
    if (method === "POST") {
      const created = captureRule({ definitionName: null });
      rules = [created];
      return Response.json(created);
    }
    if (method === "DELETE") {
      const id = String(input).split("/").at(-1);
      rules = rules.filter((rule) => rule.id !== id);
      return new Response(null, { status: 204 });
    }
    return Response.json({
      maximumAutomaticInstrumentationNodes: 500,
      rules,
    });
  }) as typeof fetch;

  const result = await renderDom(
    <ProfilingCaptureRulesCard
      canControlSystem
      canViewDiagnostics
      connection={{ apiUrl: "https://console.example.com/workable", systemName: "Ops" }}
    />
  );

  try {
    await result.waitFor(() => result.getByRole("button", { name: "Capture all work" }));
    await result.click(result.getByRole("button", { name: "Capture all work" }));
    await result.waitFor(() => assert.ok(requests.some((request) => request.method === "POST")));
    assert.deepEqual(
      requests.find((request) => request.method === "POST")?.body,
      {
        expiresAfterMinutes: 30,
        maximumMatches: 1,
      }
    );
  } finally {
    globalThis.fetch = previousFetch;
    await result.restore();
  }
});

test("global profiling capture card keeps mutation errors visible after silent reconciliation", async () => {
  const previousFetch = globalThis.fetch;
  globalThis.fetch = (async (_input, init) => {
    if (init?.method?.toUpperCase() === "POST") {
      return Response.json({
        messages: [{ text: "Global capture was rejected." }],
      }, { status: 400 });
    }

    return Response.json({ maximumAutomaticInstrumentationNodes: 500, rules: [] });
  }) as typeof fetch;

  const result = await renderDom(
    <ProfilingCaptureRulesCard
      canControlSystem
      canViewDiagnostics
      connection={{ apiUrl: "https://console.example.com/workable", systemName: "Ops" }}
    />
  );

  try {
    await result.waitFor(() => result.getByRole("button", { name: "Capture all work" }));
    await result.click(result.getByRole("button", { name: "Capture all work" }));
    await result.waitFor(() => result.getByText("Global capture was rejected."));
    await new Promise((resolve) => setTimeout(resolve, 20));
    result.getByText("Global capture was rejected.");
  } finally {
    globalThis.fetch = previousFetch;
    await result.restore();
  }
});

test("profiling capture card can retry removal of an API-created actor rule", async () => {
  const previousFetch = globalThis.fetch;
  let deleteCount = 0;
  let rules = [captureRule({ actorId: "user-123", definitionName: null })];
  globalThis.fetch = (async (input, init) => {
    const method = init?.method?.toUpperCase() ?? "GET";
    if (method === "DELETE") {
      deleteCount += 1;
      if (deleteCount === 1) {
        return Response.json({
          messages: [{ text: "Actor capture removal failed." }],
        }, { status: 500 });
      }

      const id = String(input).split("/").at(-1);
      rules = rules.filter((rule) => rule.id !== id);
      return new Response(null, { status: 204 });
    }

    return Response.json({ maximumAutomaticInstrumentationNodes: 500, rules });
  }) as typeof fetch;
  const result = await renderDom(
    <ProfilingCaptureRulesCard
      canControlSystem
      canViewDiagnostics
      connection={{ apiUrl: "https://console.example.com/workable", systemName: "Ops" }}
    />
  );

  try {
    await result.waitFor(() => result.getByText("User: user-123"));
    const remove = result.getByRole("button", { name: "Remove full profile capture rule" });
    await result.click(remove);
    await result.waitFor(() => result.getByText("Actor capture removal failed."));
    assert.equal(result.getByText("User: user-123") !== null, true);

    await result.click(remove);
    await result.waitFor(() => result.getByText("Full profile capture rule removed."));
    await result.waitFor(() => assert.equal(result.queryByText("User: user-123"), null));
    assert.equal(deleteCount, 2);
  } finally {
    globalThis.fetch = previousFetch;
    await result.restore();
  }
});

test("worker profiling capture card reconfigures only the selected worker", async () => {
  const previousFetch = globalThis.fetch;
  const requests: Array<{ body?: unknown; url: string }> = [];
  let updated = 0;
  let resolveSecondRequest: (() => void) | undefined;
  globalThis.fetch = (async (input, init) => {
    requests.push({
      body: typeof init?.body === "string" ? JSON.parse(init.body) : undefined,
      url: String(input),
    });
    if (requests.length === 2) {
      await new Promise<void>((resolve) => {
        resolveSecondRequest = resolve;
      });
    }

    return Response.json({
      action: "Start",
      messages: [],
      status: "Accepted",
      workerId: { value: "worker-1" },
    });
  }) as typeof fetch;

  const result = await renderDom(
    <WorkerProfilingCaptureCard
      canReconfigure
      canViewDiagnostics
      connection={{ apiUrl: "https://console.example.com/workable", systemName: "Ops" }}
      fullCaptureEnabled={false}
      isFinal={false}
      onUpdated={() => updated += 1}
      revision={7}
      workerId="worker-1"
    />
  );

  try {
    await result.click(result.getByRole("button", { name: "Capture this worker" }));
    await result.waitFor(() => assert.equal(updated, 1));
    const disable = result.getByRole("button", { name: "Disable full capture" });
    assert.equal(disable.getAttribute("aria-pressed"), "true");
    const successBanner = result.getByText("Profile capture updated");
    assert.match(requests[0].url, /workers\/worker-1\/reconfigure$/);
    assert.deepEqual(requests[0].body, {
      changes: {
        profilingCaptureMode: "Full",
        profilingEnabled: true,
      },
      description: "Enable full profile capture from the Workable admin UI.",
      revision: 7,
    });

    await result.click(disable);
    assert.equal(updated, 1);
    assert.equal(result.getByText("Profile capture updated"), successBanner);
    assert.equal(disable.querySelector(".animate-spin"), null);
    resolveSecondRequest?.();
    await result.waitFor(() => assert.equal(updated, 2));
    assert.notEqual(result.getByText("Profile capture updated"), successBanner);
    result.getByText("Full profile capture is disabled; normal bounded profiling remains enabled.");
    assert.deepEqual(requests[1].body, {
      changes: {
        profilingCaptureMode: "Bounded",
        profilingEnabled: true,
      },
      description: "Disable full profile capture from the Workable admin UI.",
      revision: 8,
    });
    assert.equal(
      result.getByRole("button", { name: "Capture this worker" }).getAttribute("aria-pressed"),
      "false"
    );
  } finally {
    globalThis.fetch = previousFetch;
    await result.restore();
  }
});

test("worker profiling capture card reports rejected and failed reconfiguration", async () => {
  const previousFetch = globalThis.fetch;
  let requests = 0;
  let updated = 0;
  globalThis.fetch = (async () => {
    requests += 1;
    if (requests === 1) {
      return Response.json({
        action: "Start",
        messages: [{ text: "Worker reconfiguration is not authorized." }],
        status: "Unauthorized",
        workerId: { value: "worker-1" },
      });
    }

    throw new Error("Worker reconfiguration could not be reached.");
  }) as typeof fetch;
  const result = await renderDom(
    <WorkerProfilingCaptureCard
      canReconfigure
      canViewDiagnostics
      connection={{ apiUrl: "https://console.example.com/workable", systemName: "Ops" }}
      fullCaptureEnabled={false}
      isFinal={false}
      onUpdated={() => updated += 1}
      revision={7}
      workerId="worker-1"
    />
  );

  try {
    const toggle = result.getByRole("button", { name: "Capture this worker" });
    await result.click(toggle);
    await result.waitFor(() => result.getByText("Worker reconfiguration is not authorized."));
    assert.equal(toggle.getAttribute("aria-pressed"), "false");
    assert.equal(toggle.hasAttribute("disabled"), false);

    await result.click(toggle);
    await result.waitFor(() => result.getByText("Worker reconfiguration could not be reached."));
    assert.equal(updated, 0);
    assert.equal(requests, 2);
  } finally {
    globalThis.fetch = previousFetch;
    await result.restore();
  }
});

test("worker profiling capture card hides without diagnostics and disables final workers", async () => {
  const previousFetch = globalThis.fetch;
  let requests = 0;
  globalThis.fetch = (async () => {
    requests += 1;
    return Response.json({});
  }) as typeof fetch;
  const hidden = await renderDom(
    <WorkerProfilingCaptureCard
      canReconfigure
      canViewDiagnostics={false}
      connection={{ apiUrl: "https://console.example.com/workable", systemName: "Ops" }}
      fullCaptureEnabled={false}
      isFinal={false}
      onUpdated={() => undefined}
      revision={7}
      workerId="worker-1"
    />
  );

  try {
    assert.equal(hidden.container.textContent, "");
  } finally {
    globalThis.fetch = previousFetch;
    await hidden.restore();
  }

  const final = await renderDom(
    <WorkerProfilingCaptureCard
      canReconfigure
      canViewDiagnostics
      connection={{ apiUrl: "https://console.example.com/workable", systemName: "Ops" }}
      fullCaptureEnabled={false}
      isFinal
      onUpdated={() => undefined}
      revision={7}
      workerId="worker-1"
    />
  );
  try {
    const toggle = final.getByRole("button", { name: "Capture this worker" });
    assert.equal(toggle.hasAttribute("disabled"), true);
    final.getByText("This worker is final and cannot be reconfigured.");
    assert.equal(requests, 0);
  } finally {
    await final.restore();
  }
});

test("worker profiling capture card disables its toggle without reconfiguration permission", async () => {
  const previousFetch = globalThis.fetch;
  let requests = 0;
  globalThis.fetch = (async () => {
    requests += 1;
    return Response.json({});
  }) as typeof fetch;
  const result = await renderDom(
    <WorkerProfilingCaptureCard
      canReconfigure={false}
      canViewDiagnostics
      connection={{ apiUrl: "https://console.example.com/workable", systemName: "Ops" }}
      fullCaptureEnabled={false}
      isFinal={false}
      onUpdated={() => undefined}
      revision={7}
      workerId="worker-1"
    />
  );

  try {
    const toggle = result.getByRole("button", { name: "Capture this worker" });
    assert.equal(toggle.hasAttribute("disabled"), true);
    result.getByText("Permission to reconfigure this worker is required to change full profile capture.");
    assert.equal(requests, 0);
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
