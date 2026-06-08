import assert from "node:assert/strict";
import test from "node:test";
import { ConsoleHeaderCapabilitiesProvider } from "@/components/features/console/header-capabilities";
import {
  ConsoleNavigationHeader,
  DeleteTargetDialog,
  EmptyServerState,
  ServerDialog,
  ServerTree,
  StopSystemDialog,
  catalogExplorerBodyClassName,
  catalogExplorerShellClassName,
  createUnknownAccessSummary,
  createWorkableApiUrlCandidates,
  formatHostEndpoint,
  formatWorkableApiUrl,
  getSystemAccessBadges,
  getSystemDisplayName,
  getSystemLifecycleAction,
  getSystemLifecycleActionLabel,
  getSystemSecondaryText,
  getSystemStorageKey,
  getWorkAccessBadge,
  getDeleteTargetDialogText,
  getStopSystemDialogTitle,
  isWorkableHostResponse,
  navTitle,
  normalizeOptional,
  reconcileStoredHostWithDiscovery,
  stripTrailingPathSegment,
  systemStateDotClass,
} from "@/components/workable/console/navigation";
import {
  assertMarkupExcludes,
  assertMarkupIncludes,
  renderMarkup,
} from "@/test/render";
import { renderDom } from "@/test/dom";
import { SidebarProvider } from "@/components/ui/sidebar";
import { clearDefinitionCatalogLevelCache } from "@/components/workable/console/catalog-browser-data";
import type {
  WorkableHostConnection,
  WorkableSystemConnection,
} from "@/components/features/console/types";
import type {
  WorkSystemAccessSummary,
  WorkableHttpHostDescriptor,
  WorkableHttpSystemDescriptor,
} from "@/lib/workable";

const access: WorkSystemAccessSummary = {
  canControlSystem: true,
  canOperateAllWork: false,
  canReadAllWork: true,
  canViewDiagnostics: true,
  isSystemAdministrator: false,
  isWorkAdministrator: true,
  operableDefinitionCount: 2,
  readableDefinitionCount: 5,
  totalDefinitionCount: 5,
};

function system(overrides: Partial<WorkableSystemConnection>): WorkableSystemConnection {
  return {
    hostId: "host-1",
    id: "system-1",
    name: "Default",
    persistentCoordinationAvailable: false,
    state: "Started",
    ...overrides,
  };
}

test("reconcile stored host updates realtime metadata, preserves matched ids, and drops missing systems", () => {
  const host: WorkableHostConnection = {
    apiUrl: "https://workable.test",
    id: "host-1",
    name: "Workable",
    realtimeEnabled: false,
    realtimeHubPath: null,
    realtimeTransport: null,
    systems: [
      system({ id: "default-existing", name: "Default", systemName: undefined }),
      system({ id: "ops-existing", name: "Ops", systemName: "Ops" }),
      system({ id: "missing-existing", name: "Missing", systemName: "Missing" }),
    ],
  };
  const discovered: WorkableHttpHostDescriptor = {
    capabilities: {
      realtime: {
        enabled: true,
        hubPath: "/hub",
        transport: "WebSockets",
      },
    },
    systems: [
      {
        access,
        capabilities: { persistentCoordinationAvailable: true },
        id: { value: "server-default" },
        isDefault: true,
        name: null,
        state: "Started",
      },
      {
        access,
        capabilities: { persistentCoordinationAvailable: false },
        id: { value: "server-ops" },
        isDefault: false,
        name: "Ops",
        state: "Stopped",
      },
    ],
  };

  const reconciled = reconcileStoredHostWithDiscovery(host, discovered);

  assert.equal(reconciled.realtimeEnabled, true);
  assert.equal(reconciled.realtimeHubPath, "/hub");
  assert.equal(reconciled.realtimeTransport, "WebSockets");
  assert.deepEqual(
    reconciled.systems.map((entry) => ({
      id: entry.id,
      name: entry.name,
      persistentCoordinationAvailable: entry.persistentCoordinationAvailable,
      state: entry.state,
      systemName: entry.systemName,
    })),
    [
      {
        id: "default-existing",
        name: "Default",
        persistentCoordinationAvailable: true,
        state: "Started",
        systemName: undefined,
      },
      {
        id: "ops-existing",
        name: "Ops",
        persistentCoordinationAvailable: false,
        state: "Stopped",
        systemName: "Ops",
      },
    ]
  );
});

test("navigation empty state and destructive dialogs render their optional text paths", () => {
  const empty = renderMarkup(
    <EmptyServerState
      description="No hosts configured."
      onAddServer={() => undefined}
      title="Start here"
    />
  );
  assertMarkupIncludes(empty, "Start here");
  assertMarkupIncludes(empty, "No hosts configured.");
  assertMarkupIncludes(empty, "Add server");

  const host: WorkableHostConnection = {
    apiUrl: "https://workable.test",
    id: "host-1",
    name: "Workable",
    realtimeEnabled: false,
    systems: [system({ access, id: "system-1", name: "Ops", systemName: "Ops" })],
  };
  assert.deepEqual(getDeleteTargetDialogText({ kind: "host", host }), {
    title: "Remove Workable?",
    description: "This removes the server group and every Workable system saved under it from this browser.",
  });
  assert.deepEqual(getDeleteTargetDialogText({ kind: "system", host, system: host.systems[0] }), {
    title: "Remove Ops?",
    description: "This removes Ops. Because it is the last system under Workable, the server group will be removed too.",
  });
  assert.deepEqual(
    getDeleteTargetDialogText({
      kind: "system",
      host: {
        ...host,
        systems: [
          host.systems[0],
          system({ id: "system-2", name: "Billing", systemName: "Billing" }),
        ],
      },
      system: host.systems[0],
    }),
    {
      title: "Remove Ops?",
      description: "This removes only this Workable system from the sidebar.",
    }
  );
  assert.deepEqual(getDeleteTargetDialogText(null), {
    title: "Remove item?",
    description: "This removes only this Workable system from the sidebar.",
  });
  assert.equal(getStopSystemDialogTitle({ system: host.systems[0] }), "Stop Ops?");
  assert.equal(getStopSystemDialogTitle(null), "Stop system?");

  renderMarkup(
    <DeleteTargetDialog
      onConfirm={() => undefined}
      onOpenChange={() => undefined}
      target={{ kind: "host", host }}
    />
  );
  renderMarkup(
    <StopSystemDialog
      onConfirm={() => undefined}
      onOpenChange={() => undefined}
      target={{ system: host.systems[0] }}
    />
  );
});

test("catalog explorer shell keeps the definition list scrollable with bottom padding", () => {
  assertMarkupIncludes(catalogExplorerShellClassName, "mb-2");
  assertMarkupIncludes(catalogExplorerShellClassName, "overflow-hidden");
  assertMarkupIncludes(catalogExplorerBodyClassName, "workable-grid-scrollbar");
  assertMarkupIncludes(catalogExplorerBodyClassName, "overflow-y-auto");
  assertMarkupIncludes(catalogExplorerBodyClassName, "max-h-72");
});

test("server discovery URL helpers normalize host endpoints and valid responses", () => {
  assert.deepEqual(createWorkableApiUrlCandidates(" "), []);
  assert.deepEqual(createWorkableApiUrlCandidates("not a url"), ["not a url"]);
  assert.deepEqual(
    createWorkableApiUrlCandidates("https://workable.test/workable/host/"),
    ["https://workable.test/workable"]
  );
  assert.deepEqual(
    createWorkableApiUrlCandidates("https://workable.test/api/systems?tenant=ops"),
    [
      "https://workable.test/api?tenant=ops",
      "https://workable.test/api/workable?tenant=ops",
    ]
  );
  assert.equal(
    stripTrailingPathSegment(new URL("https://workable.test/api/HOST/"), "host").pathname,
    "/api"
  );
  assert.equal(
    formatWorkableApiUrl(new URL("https://workable.test/workable/?tenant=ops")),
    "https://workable.test/workable?tenant=ops"
  );
  assert.equal(
    formatHostEndpoint("https://workable.test/workable/"),
    "https://workable.test/workable/host"
  );
  assert.equal(isWorkableHostResponse({ capabilities: {}, systems: [] }), true);
  assert.equal(isWorkableHostResponse({ capabilities: {}, systems: {} }), false);
});

test("server dialog discovers systems, selects them by default, and saves a host", async () => {
  const calls: Array<{ input: string; init?: RequestInit }> = [];
  const previousFetch = globalThis.fetch;
  globalThis.fetch = (async (input, init) => {
    calls.push({ input: String(input), init });
    return Response.json(discoveredHost());
  }) as typeof fetch;
  const savedHosts: WorkableHostConnection[] = [];
  const openChanges: boolean[] = [];
  const result = await renderDom(
    <ServerDialog
      mode="add"
      onOpenChange={(open) => openChanges.push(open)}
      onSave={(host) => savedHosts.push(host)}
      open
    />
  );

  try {
    const [nameInput, urlInput] = textInputs(result.dom.window.document.body);
    await result.input(nameInput, "Local Workable");
    await result.input(urlInput, "https://discover.example.com/workable");

    await result.click(result.getByRole("button", { name: "Load systems" }));

    assert.equal(calls.length, 1);
    assert.equal(calls[0]?.input, "/api/workable/host");
    assert.equal(
      new Headers(calls[0]?.init?.headers).get("x-workable-api-url"),
      "https://discover.example.com/workable"
    );
    result.getByText("Default");
    result.getByText("Default system");
    result.getByText("Ops");
    result.getByText("Diagnostics");
    result.getByText("Operate 2/5 defs");

    await result.click(result.getByRole("button", { name: "Save" }));

    assert.equal(savedHosts.length, 1);
    assert.equal(savedHosts[0]?.name, "Local Workable");
    assert.equal(savedHosts[0]?.apiUrl, "https://discover.example.com/workable");
    assert.equal(savedHosts[0]?.realtimeEnabled, true);
    assert.equal(savedHosts[0]?.realtimeHubPath, "/workable/realtime");
    assert.deepEqual(
      savedHosts[0]?.systems.map((entry) => ({
        name: entry.name,
        state: entry.state,
        systemName: entry.systemName,
      })),
      [
        {
          name: "Default",
          state: "Started",
          systemName: undefined,
        },
        {
          name: "Ops",
          state: "Stopped",
          systemName: "Ops",
        },
      ]
    );
    assert.deepEqual(openChanges, [false]);
  } finally {
    globalThis.fetch = previousFetch;
    await result.restore();
  }
});

test("server dialog disables save when every discovered system is unchecked", async () => {
  const previousFetch = globalThis.fetch;
  globalThis.fetch = (async () => Response.json(discoveredHost())) as typeof fetch;
  const result = await renderDom(
    <ServerDialog
      mode="add"
      onOpenChange={() => undefined}
      onSave={() => assert.fail("Save should stay disabled when no systems are selected.")}
      open
    />
  );

  try {
    const [, urlInput] = textInputs(result.dom.window.document.body);
    await result.input(urlInput, "https://unchecked.example.com/workable");
    await result.click(result.getByRole("button", { name: "Load systems" }));

    for (const checkbox of checkboxes(result.dom.window.document.body)) {
      await result.click(checkbox);
    }

    assert.equal(result.getByRole("button", { name: "Save" }).hasAttribute("disabled"), true);
  } finally {
    globalThis.fetch = previousFetch;
    await result.restore();
  }
});

test("server dialog explains discovery authorization failures", async () => {
  const previousFetch = globalThis.fetch;
  globalThis.fetch = (async () =>
    Response.json({ error: "Forbidden" }, { status: 403 })) as typeof fetch;
  const result = await renderDom(
    <ServerDialog
      mode="add"
      onOpenChange={() => undefined}
      onSave={() => assert.fail("Failed discovery should not save.")}
      open
    />
  );

  try {
    const [, urlInput] = textInputs(result.dom.window.document.body);
    await result.input(urlInput, "https://forbidden.example.com/workable");
    await result.click(result.getByRole("button", { name: "Load systems" }));

    result.getByText("Discovery failed");
    result.getByText("This user is not allowed to access Workable system discovery on that host.");
    assert.equal(result.getByRole("button", { name: "Save" }).hasAttribute("disabled"), true);
  } finally {
    globalThis.fetch = previousFetch;
    await result.restore();
  }
});

test("server dialog preserves specific hosted authentication guidance for discovery failures", async () => {
  const previousFetch = globalThis.fetch;
  globalThis.fetch = (async () =>
    Response.json(
      {
        error:
          "The hosted Workable API rejected the bearer token because the token issuer does not match its Entra configuration. Check that the target API app registration is configured to issue v2 access tokens.",
      },
      { status: 401 }
    )) as typeof fetch;
  const result = await renderDom(
    <ServerDialog
      mode="add"
      onOpenChange={() => undefined}
      onSave={() => assert.fail("Failed discovery should not save.")}
      open
    />
  );

  try {
    const [, urlInput] = textInputs(result.dom.window.document.body);
    await result.input(urlInput, "https://issuer.example.com/workable");
    await result.click(result.getByRole("button", { name: "Load systems" }));

    result.getByText("Discovery failed");
    result.getByText(
      "The hosted Workable API rejected the bearer token because the token issuer does not match its Entra configuration. Check that the target API app registration is configured to issue v2 access tokens."
    );
  } finally {
    globalThis.fetch = previousFetch;
    await result.restore();
  }
});

test("server dialog avoids duplicating reachability wording when the hosted error already explains it", async () => {
  const previousFetch = globalThis.fetch;
  globalThis.fetch = (async () =>
    Response.json(
      {
        error: "Unable to reach the Workable HTTP API.",
      },
      { status: 502 }
    )) as typeof fetch;
  const result = await renderDom(
    <ServerDialog
      mode="add"
      onOpenChange={() => undefined}
      onSave={() => assert.fail("Failed discovery should not save.")}
      open
    />
  );

  try {
    const [, urlInput] = textInputs(result.dom.window.document.body);
    await result.input(urlInput, "http://localhost:5187");
    await result.click(result.getByRole("button", { name: "Load systems" }));

    result.getByText("Discovery failed");
    result.getByText(
      "Unable to reach the Workable HTTP API. Tried http://localhost:5187/host, http://localhost:5187/workable/host. Check that the protocol and port match the server."
    );
  } finally {
    globalThis.fetch = previousFetch;
    await result.restore();
  }
});

test("server dialog edit mode refreshes discovery, preserves matched systems, and drops missing systems", async () => {
  const existingHost: WorkableHostConnection = {
    apiUrl: "https://old.example.com/workable",
    id: "host-existing",
    name: "Existing Workable",
    realtimeEnabled: false,
    realtimeHubPath: null,
    realtimeTransport: null,
    systems: [
      system({
        hostId: "host-existing",
        id: "ops-existing",
        name: "Ops",
        persistentCoordinationAvailable: false,
        state: "Started",
        systemName: "Ops",
      }),
      system({
        hostId: "host-existing",
        id: "missing-existing",
        name: "Missing",
        persistentCoordinationAvailable: false,
        state: "Started",
        systemName: "Missing",
      }),
    ],
  };
  const previousFetch = globalThis.fetch;
  const calls: Array<{ input: string; init?: RequestInit }> = [];
  globalThis.fetch = (async (input, init) => {
    calls.push({ input: String(input), init });
    return Response.json(discoveredHost({
      realtime: {
        enabled: true,
        hubPath: "/updated/realtime",
        transport: "ServerSentEvents",
      },
      systems: [
        discoveredSystem({ name: "Ops", state: "Stopped" }),
        discoveredSystem({ name: "Billing", state: "Started" }),
      ],
    }));
  }) as typeof fetch;
  const savedHosts: WorkableHostConnection[] = [];
  const result = await renderDom(
    <ServerDialog
      host={existingHost}
      mode="edit"
      onOpenChange={() => undefined}
      onSave={(host) => savedHosts.push(host)}
      open
    />
  );

  try {
    const [nameInput, urlInput] = textInputs(result.dom.window.document.body);
    assert.equal(nameInput?.value, "Existing Workable");
    assert.equal(urlInput?.value, "https://old.example.com/workable");

    await result.waitFor(() => result.getByText("Billing"));
    assert.equal(calls.length, 1);
    assert.equal(
      new Headers(calls[0]?.init?.headers).get("x-workable-api-url"),
      "https://old.example.com/workable"
    );

    await result.click(result.getByRole("button", { name: "Save" }));

    assert.equal(savedHosts.length, 1);
    assert.equal(savedHosts[0]?.id, "host-existing");
    assert.equal(savedHosts[0]?.realtimeEnabled, true);
    assert.equal(savedHosts[0]?.realtimeHubPath, "/updated/realtime");
    assert.deepEqual(
      savedHosts[0]?.systems.map((entry) => ({
        hostId: entry.hostId,
        id: entry.id,
        name: entry.name,
        state: entry.state,
        systemName: entry.systemName,
      })),
      [
        {
          hostId: "host-existing",
          id: "ops-existing",
          name: "Ops",
          state: "Stopped",
          systemName: "Ops",
        },
      ]
    );
  } finally {
    globalThis.fetch = previousFetch;
    await result.restore();
  }
});

test("server dialog cancel closes edit mode without saving", async () => {
  const previousFetch = globalThis.fetch;
  globalThis.fetch = (async () => Response.json(discoveredHost())) as typeof fetch;
  const openChanges: boolean[] = [];
  const result = await renderDom(
    <ServerDialog
      host={{
        apiUrl: "https://cancel.example.com/workable",
        id: "host-cancel",
        name: "Cancel Workable",
        realtimeEnabled: false,
        systems: [system({ hostId: "host-cancel", id: "ops-cancel", name: "Ops", systemName: "Ops" })],
      }}
      mode="edit"
      onOpenChange={(open) => openChanges.push(open)}
      onSave={() => assert.fail("Cancel should not save the host.")}
      open
    />
  );

  try {
    await result.click(result.getByRole("button", { name: "Cancel" }));

    assert.deepEqual(openChanges, [false]);
  } finally {
    globalThis.fetch = previousFetch;
    await result.restore();
  }
});

test("server dialog disables actions while loading and retries discovery failures", async () => {
  const pending = deferred<Response>();
  const previousFetch = globalThis.fetch;
  let requestCount = 0;
  globalThis.fetch = (async () => {
    requestCount += 1;
    if (requestCount === 1) {
      return pending.promise;
    }

    return Response.json(discoveredHost());
  }) as typeof fetch;
  const savedHosts: WorkableHostConnection[] = [];
  const result = await renderDom(
    <ServerDialog
      mode="add"
      onOpenChange={() => undefined}
      onSave={(host) => savedHosts.push(host)}
      open
    />
  );

  try {
    const [, urlInput] = textInputs(result.dom.window.document.body);
    await result.input(urlInput, "https://retry.example.com/workable");
    await result.click(result.getByRole("button", { name: "Load systems" }));

    assert.equal(result.getByRole("button", { name: "Load systems" }).hasAttribute("disabled"), true);
    assert.equal(result.getByRole("button", { name: "Save" }).hasAttribute("disabled"), true);

    pending.resolve(Response.json({ error: "Forbidden" }, { status: 403 }));
    await result.waitFor(() => result.getByText("Discovery failed"));
    result.getByText("This user is not allowed to access Workable system discovery on that host.");

    await result.click(result.getByRole("button", { name: "Load systems" }));
    await result.waitFor(() => result.getByText("Ops"));
    assert.equal(result.queryByText("Discovery failed"), null);

    await result.click(result.getByRole("button", { name: "Save" }));
    assert.equal(savedHosts.length, 1);
    assert.deepEqual(savedHosts[0]?.systems.map((entry) => entry.name), ["Default", "Ops"]);
  } finally {
    globalThis.fetch = previousFetch;
    await result.restore();
  }
});

test("navigation system helpers cover access badges, names, lifecycle, and states", () => {
  assert.deepEqual(getSystemAccessBadges(access), [
    "Work admin",
    "Diagnostics",
    "Control system",
    "Read all work",
    "Operate 2/5 defs",
  ]);
  assert.deepEqual(getSystemAccessBadges(createUnknownAccessSummary()), [
    "No work access",
  ]);
  assert.equal(getWorkAccessBadge("Read", 0, 5, false), null);
  assert.equal(getWorkAccessBadge("Operate", 5, 5, false), "Operate all work");
  assert.equal(normalizeOptional("  Ops  "), "Ops");
  assert.equal(normalizeOptional("  "), undefined);

  const defaultSystem: WorkableHttpSystemDescriptor = {
    access,
    capabilities: { persistentCoordinationAvailable: true },
    id: { value: "server-default" },
    isDefault: true,
    name: null,
    state: "Started",
  };
  const namedSystem: WorkableHttpSystemDescriptor = {
    ...defaultSystem,
    isDefault: false,
    name: " Ops ",
  };
  assert.equal(getSystemStorageKey(defaultSystem), "");
  assert.equal(getSystemStorageKey(namedSystem), "Ops");
  assert.equal(getSystemDisplayName(defaultSystem), "Default");
  assert.equal(getSystemDisplayName(namedSystem), "Ops");
  assert.equal(getSystemSecondaryText(defaultSystem), "Default system");
  assert.equal(getSystemSecondaryText(namedSystem), null);

  const host: WorkableHostConnection = {
    apiUrl: "https://workable.test",
    id: "host-1",
    name: "Workable",
    realtimeEnabled: false,
    systems: [system({ access, id: "system-1", name: "Ops", systemName: "Ops" })],
  };
  assert.equal(getSystemLifecycleAction("Created"), "start");
  assert.equal(getSystemLifecycleAction("Stopped"), "start");
  assert.equal(getSystemLifecycleAction("started"), "stop");
  assert.equal(getSystemLifecycleAction("Starting"), null);
  assert.equal(
    getSystemLifecycleActionLabel("Stopped", host.systems[0], host),
    "Start the workable system 'Ops' at https://workable.test"
  );
  assert.equal(
    getSystemLifecycleActionLabel("Started", host.systems[0], host),
    "Stop the workable system 'Ops' at https://workable.test"
  );
  assert.equal(
    getSystemLifecycleActionLabel("Unknown", host.systems[0], host),
    "Lifecycle action unavailable"
  );
  assert.equal(systemStateDotClass("Started"), "bg-[var(--status-success-solid)]");
  assert.equal(systemStateDotClass("Starting"), "bg-[var(--status-info-solid)]");
  assert.equal(systemStateDotClass("Stopping"), "bg-[var(--status-info-solid)]");
  assert.equal(systemStateDotClass("Stopped"), "bg-[var(--status-neutral-solid)]");
  assert.equal(systemStateDotClass("Created"), "bg-[var(--status-neutral-solid)]");
  assert.equal(systemStateDotClass("Unknown"), "bg-[var(--status-neutral-solid)]");
  assert.equal(navTitle("worker"), "Worker Console");
  assert.equal(navTitle("iteration"), "Iteration");
  assert.equal(navTitle("definition"), "Definition");
  assert.equal(navTitle("workers"), "Workers");
});

function discoveredHost(options?: {
  realtime?: WorkableHttpHostDescriptor["capabilities"]["realtime"];
  systems?: WorkableHttpSystemDescriptor[];
}): WorkableHttpHostDescriptor {
  return {
    capabilities: {
      realtime: options?.realtime ?? {
        enabled: true,
        hubPath: "/workable/realtime",
        transport: "WebSockets",
      },
    },
    systems: options?.systems ?? [
      {
        access,
        capabilities: { persistentCoordinationAvailable: true },
        id: { value: "default-server-id" },
        isDefault: true,
        name: null,
        state: "Started",
      },
      {
        access,
        capabilities: { persistentCoordinationAvailable: false },
        id: { value: "ops-server-id" },
        isDefault: false,
        name: "Ops",
        state: "Stopped",
      },
    ],
  };
}

function discoveredSystem(
  overrides: Partial<WorkableHttpSystemDescriptor> & { name?: string | null }
): WorkableHttpSystemDescriptor {
  const name = overrides.name ?? "Ops";
  return {
    access,
    capabilities: { persistentCoordinationAvailable: false },
    id: { value: `${name ?? "default"}-server-id` },
    isDefault: name === null,
    name,
    state: "Started",
    ...overrides,
  };
}

function textInputs(root: ParentNode) {
  const inputs = Array.from(root.querySelectorAll("input"))
    .filter((input) => !input.type || input.type === "text");
  assert.equal(inputs.length >= 2, true);
  return inputs as HTMLInputElement[];
}

function checkboxes(root: ParentNode) {
  return Array.from(root.querySelectorAll("input[type='checkbox']")) as HTMLInputElement[];
}

function deferred<T>() {
  let resolve!: (value: T) => void;
  const promise = new Promise<T>((nextResolve) => {
    resolve = nextResolve;
  });
  return { promise, resolve };
}

test("server tree and navigation header render expanded and breadcrumb option paths", () => {
  const host: WorkableHostConnection = {
    apiUrl: "https://workable.test",
    id: "host-1",
    name: "Workable",
    realtimeEnabled: false,
    systems: [system({ access, id: "system-1", name: "Ops", systemName: "Ops" })],
  };

  const tree = renderMarkup(
    <SidebarProvider>
      <ServerTree
        activeSystemId="system-1"
        catalogScopeBySystemId={{}}
        expandedHostIds={["host-1"]}
        expandedSystemIds={["system-1"]}
        hosts={[host]}
        lifecycleActionSystemId={null}
        onAddServer={() => undefined}
        onEditHost={() => undefined}
        onLifecycleAction={() => undefined}
        onOpenCatalogScope={() => undefined}
        onOpenDefinition={() => undefined}
        onOpenView={() => undefined}
        onOpenWorker={() => undefined}
        onRemoveHost={() => undefined}
        onRemoveSystem={() => undefined}
        onToggleHost={() => undefined}
        onToggleSystem={() => undefined}
        view="overview"
      />
    </SidebarProvider>
  );
  assertMarkupIncludes(tree, "Workable");
  assertMarkupIncludes(tree, "Ops");
  assertMarkupIncludes(tree, "Overview");
  assertMarkupIncludes(tree, "Catalog");
  assertMarkupIncludes(tree, "Workers");
  assertMarkupIncludes(tree, "Iterations");
  assertMarkupIncludes(tree, "Stop the workable system &#x27;Ops&#x27; at https://workable.test");
  assertMarkupIncludes(tree, "Add server");

  const header = renderMarkup(
    <ConsoleHeaderCapabilitiesProvider>
      <SidebarProvider>
        <ConsoleNavigationHeader
          breadcrumbParent={{
            label: "ImportOrders",
            onSelect: () => undefined,
          }}
          canGoBack
          canGoForward={false}
          definitionId={null}
          definitionName={null}
          host={host}
          iterationSequence={12}
          onBack={() => undefined}
          onForward={() => undefined}
          onOpenView={() => undefined}
          system={host.systems[0]}
          view="iteration"
          workerId={null}
        />
      </SidebarProvider>
    </ConsoleHeaderCapabilitiesProvider>
  );
  assertMarkupIncludes(header, "Go back");
  assertMarkupIncludes(header, "Go forward");
  assertMarkupIncludes(header, "Workable");
  assertMarkupIncludes(header, "Ops");
  assertMarkupIncludes(header, "ImportOrders");
  assertMarkupIncludes(header, "#12");
});

test("server tree hides lifecycle and queue controls when system access is restricted", async () => {
  clearDefinitionCatalogLevelCache();
  const restrictedAccess: WorkSystemAccessSummary = {
    canControlSystem: false,
    canOperateAllWork: false,
    canReadAllWork: true,
    canViewDiagnostics: false,
    isSystemAdministrator: false,
    isWorkAdministrator: false,
    operableDefinitionCount: 0,
    readableDefinitionCount: 1,
    totalDefinitionCount: 1,
  };
  const host: WorkableHostConnection = {
    apiUrl: "https://workable.test",
    id: "host-1",
    name: "Workable",
    realtimeEnabled: false,
    systems: [
      system({
        access: restrictedAccess,
        id: "system-1",
        name: "Ops",
        state: "Started",
        systemName: "Ops",
      }),
    ],
  };
  const calls: string[] = [];
  const previousFetch = globalThis.fetch;
  globalThis.fetch = (async (input) => {
    calls.push(String(input));
    return Response.json({
      categories: [],
      definitions: [
        {
          category: "Ops",
          id: { value: "def-1" },
          name: "ImportOrders",
        },
      ],
    });
  }) as typeof fetch;
  const lifecycleCalls: string[] = [];
  const result = await renderDom(
    <SidebarProvider>
      <ServerTree
        activeSystemId="system-1"
        catalogScopeBySystemId={{}}
        expandedHostIds={["host-1"]}
        expandedSystemIds={["system-1"]}
        hosts={[host]}
        lifecycleActionSystemId={null}
        onAddServer={() => undefined}
        onEditHost={() => undefined}
        onLifecycleAction={(_, action) => lifecycleCalls.push(action)}
        onOpenCatalogScope={() => undefined}
        onOpenDefinition={() => undefined}
        onOpenView={() => undefined}
        onOpenWorker={() => undefined}
        onRemoveHost={() => undefined}
        onRemoveSystem={() => undefined}
        onToggleHost={() => undefined}
        onToggleSystem={() => undefined}
        view="overview"
      />
    </SidebarProvider>
  );

  try {
    assert.equal(findButtonByName(result.container, /Stop the workable system/), null);

    await result.click(result.getByRole("button", { name: "Explore worker categories and definitions" }));
    await result.waitFor(() => result.getByText("ImportOrders"));

    assert.equal(calls[0], "/api/workable/systems/Ops/definitions?level=true");
    assert.equal(findButtonByName(result.container, "Queue ImportOrders"), null);
    assert.deepEqual(lifecycleCalls, []);
  } finally {
    globalThis.fetch = previousFetch;
    await result.restore();
    clearDefinitionCatalogLevelCache();
  }
});

test("server tree exposes lifecycle and queue controls for matching system access", async () => {
  clearDefinitionCatalogLevelCache();
  const host: WorkableHostConnection = {
    apiUrl: "https://workable.test",
    id: "host-1",
    name: "Workable",
    realtimeEnabled: false,
    systems: [
      system({
        access,
        id: "system-1",
        name: "Ops",
        state: "Stopped",
        systemName: "Ops",
      }),
    ],
  };
  const previousFetch = globalThis.fetch;
  globalThis.fetch = (async () =>
    Response.json({
      categories: [],
      definitions: [
        {
          category: "Ops",
          id: { value: "def-1" },
          name: "ImportOrders",
        },
      ],
    })) as typeof fetch;
  const lifecycleCalls: string[] = [];
  const result = await renderDom(
    <SidebarProvider>
      <ServerTree
        activeSystemId="system-1"
        catalogScopeBySystemId={{}}
        expandedHostIds={["host-1"]}
        expandedSystemIds={["system-1"]}
        hosts={[host]}
        lifecycleActionSystemId={null}
        onAddServer={() => undefined}
        onEditHost={() => undefined}
        onLifecycleAction={(_, action) => lifecycleCalls.push(action)}
        onOpenCatalogScope={() => undefined}
        onOpenDefinition={() => undefined}
        onOpenView={() => undefined}
        onOpenWorker={() => undefined}
        onRemoveHost={() => undefined}
        onRemoveSystem={() => undefined}
        onToggleHost={() => undefined}
        onToggleSystem={() => undefined}
        view="overview"
      />
    </SidebarProvider>
  );

  try {
    const startSystem = findButtonByName(
      result.container,
      "Start the workable system 'Ops' at https://workable.test"
    );
    assert.ok(startSystem);
    await result.click(startSystem);
    assert.deepEqual(lifecycleCalls, ["start"]);

    await result.click(result.getByRole("button", { name: "Explore worker categories and definitions" }));
    await result.waitFor(() => result.getByText("ImportOrders"));

    assert.notEqual(findButtonByName(result.container, "Queue ImportOrders"), null);
  } finally {
    globalThis.fetch = previousFetch;
    await result.restore();
    clearDefinitionCatalogLevelCache();
  }
});

test("server tree omits lifecycle controls from restricted server markup", () => {
  const restrictedAccess: WorkSystemAccessSummary = {
    ...access,
    canControlSystem: false,
  };
  const host: WorkableHostConnection = {
    apiUrl: "https://workable.test",
    id: "host-1",
    name: "Workable",
    realtimeEnabled: false,
    systems: [
      system({
        access: restrictedAccess,
        id: "system-1",
        name: "Ops",
        state: "Started",
        systemName: "Ops",
      }),
    ],
  };

  const tree = renderMarkup(
    <SidebarProvider>
      <ServerTree
        activeSystemId="system-1"
        catalogScopeBySystemId={{}}
        expandedHostIds={["host-1"]}
        expandedSystemIds={["system-1"]}
        hosts={[host]}
        lifecycleActionSystemId={null}
        onAddServer={() => undefined}
        onEditHost={() => undefined}
        onLifecycleAction={() => undefined}
        onOpenCatalogScope={() => undefined}
        onOpenDefinition={() => undefined}
        onOpenView={() => undefined}
        onOpenWorker={() => undefined}
        onRemoveHost={() => undefined}
        onRemoveSystem={() => undefined}
        onToggleHost={() => undefined}
        onToggleSystem={() => undefined}
        view="overview"
      />
    </SidebarProvider>
  );

  assertMarkupExcludes(tree, "Stop the workable system");
});

function findButtonByName(root: ParentNode, name: string | RegExp) {
  return Array.from(root.querySelectorAll("button")).find((button) => {
    const accessibleName =
      button.getAttribute("aria-label") ??
      button.textContent?.replace(/\s+/g, " ").trim() ??
      "";
    return typeof name === "string" ? accessibleName === name : name.test(accessibleName);
  }) ?? null;
}
