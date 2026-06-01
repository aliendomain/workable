import assert from "node:assert/strict";
import test from "node:test";
import { ConsoleHeaderCapabilitiesProvider } from "@/components/features/console/header-capabilities";
import {
  ConsoleNavigationHeader,
  DeleteTargetDialog,
  EmptyServerState,
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
  assertMarkupIncludes,
  renderMarkup,
} from "@/test/render";
import { SidebarProvider } from "@/components/ui/sidebar";
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
  canConnect: true,
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
    systems: [system({ id: "system-1", name: "Ops", systemName: "Ops" })],
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

test("navigation system helpers cover access badges, names, lifecycle, and states", () => {
  assert.deepEqual(getSystemAccessBadges(access), [
    "Connect",
    "Work admin",
    "Diagnostics",
    "Control system",
    "Read all work",
    "Operate 2/5 defs",
  ]);
  assert.deepEqual(getSystemAccessBadges(createUnknownAccessSummary()), [
    "Connect",
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
    systems: [system({ id: "system-1", name: "Ops", systemName: "Ops" })],
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
  assert.equal(systemStateDotClass("Started"), "bg-emerald-400");
  assert.equal(systemStateDotClass("Starting"), "bg-amber-300");
  assert.equal(systemStateDotClass("Stopping"), "bg-amber-300");
  assert.equal(systemStateDotClass("Stopped"), "bg-zinc-500");
  assert.equal(systemStateDotClass("Created"), "bg-zinc-500");
  assert.equal(systemStateDotClass("Unknown"), "bg-muted-foreground/45");
  assert.equal(navTitle("worker"), "Worker Console");
  assert.equal(navTitle("iteration"), "Iteration");
  assert.equal(navTitle("definition"), "Definition");
  assert.equal(navTitle("workers"), "Workers");
});

test("server tree and navigation header render expanded and breadcrumb option paths", () => {
  const host: WorkableHostConnection = {
    apiUrl: "https://workable.test",
    id: "host-1",
    name: "Workable",
    realtimeEnabled: false,
    systems: [system({ id: "system-1", name: "Ops", systemName: "Ops" })],
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
