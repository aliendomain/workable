import assert from "node:assert/strict";
import test from "node:test";
import { HubConnectionBuilder, HubConnectionState, type HubConnection } from "@microsoft/signalr";
import { useMemo } from "react";
import {
  ConsolePageRealtimeViewProvider,
  createDisabledConsolePageRealtimeView,
  useConsolePageRealtimeView,
  useRegisterConsolePageRealtimeView,
  useResolvedConsolePageRealtimeViewDescriptorId,
} from "@/components/features/console/page-realtime-view";
import { useConsoleRealtimeStats } from "@/components/features/console/realtime";
import { renderDom } from "@/test/dom";

test("disabled page realtime view exposes the inert loadable shape used for inactive views", () => {
  const view = createDisabledConsolePageRealtimeView<{ count: number }>();

  assert.equal(view.connectionState, "disabled");
  assert.equal(view.enabled, false);
  assert.equal(view.hubUrl, null);
  assert.equal(view.loading, false);
  assert.deepEqual(view.messages, []);
  assert.equal(view.clearMessages(), undefined);
});

test("page realtime provider resolves active registrations by recency and disables inactive consumers", async () => {
  function Registration({
    active = true,
    id,
    viewName,
  }: {
    active?: boolean;
    id: string;
    viewName: string;
  }) {
    const descriptor = useMemo(
      () => ({
        body: { viewName },
        captureEnabled: false,
        connection: null,
        enabled: false,
        maxMessages: 25,
        viewName,
      }),
      [viewName]
    );

    useRegisterConsolePageRealtimeView({
      active,
      descriptor,
      id,
    });
    return null;
  }

  function Probe({ id }: { id: string }) {
    const descriptorId = useResolvedConsolePageRealtimeViewDescriptorId();
    const view = useConsolePageRealtimeView(id);
    return (
      <output data-testid={id}>
        {descriptorId ?? "none"} / {view.enabled ? "enabled" : "disabled"} / {view.connectionState}
      </output>
    );
  }

  const render = await renderDom(
    <ConsolePageRealtimeViewProvider>
      <Registration id="first" viewName="First" />
      <Probe id="first" />
      <Probe id="second" />
    </ConsolePageRealtimeViewProvider>
  );

  try {
    const read = (id: string) =>
      render.container.querySelector(`[data-testid='${id}']`)?.textContent;

    assert.equal(read("first"), "first / disabled / disabled");
    assert.equal(read("second"), "first / disabled / disabled");

    await render.rerender(
      <ConsolePageRealtimeViewProvider>
        <Registration id="first" viewName="First" />
        <Registration id="second" viewName="Second" />
        <Probe id="first" />
        <Probe id="second" />
      </ConsolePageRealtimeViewProvider>
    );
    assert.equal(read("first"), "second / disabled / disabled");
    assert.equal(read("second"), "second / disabled / disabled");

    await render.rerender(
      <ConsolePageRealtimeViewProvider>
        <Registration id="first" viewName="First" />
        <Registration active={false} id="second" viewName="Second" />
        <Probe id="first" />
        <Probe id="second" />
      </ConsolePageRealtimeViewProvider>
    );
    assert.equal(read("first"), "first / disabled / disabled");
    assert.equal(read("second"), "first / disabled / disabled");

    await render.rerender(
      <ConsolePageRealtimeViewProvider>
        <Probe id="first" />
      </ConsolePageRealtimeViewProvider>
    );
    assert.equal(read("first"), "none / disabled / disabled");
  } finally {
    await render.restore();
  }
});

test("page realtime provider forwards connection instance keys into the resolved realtime connection key", async () => {
  const originalWithUrl = HubConnectionBuilder.prototype.withUrl;
  const originalWithAutomaticReconnect = HubConnectionBuilder.prototype.withAutomaticReconnect;
  const originalConfigureLogging = HubConnectionBuilder.prototype.configureLogging;
  const originalBuild = HubConnectionBuilder.prototype.build;

  HubConnectionBuilder.prototype.withUrl = function withUrl(this: HubConnectionBuilder) {
    return this;
  } as typeof HubConnectionBuilder.prototype.withUrl;
  HubConnectionBuilder.prototype.withAutomaticReconnect = function withAutomaticReconnect(this: HubConnectionBuilder) {
    return this;
  } as typeof HubConnectionBuilder.prototype.withAutomaticReconnect;
  HubConnectionBuilder.prototype.configureLogging = function configureLogging(this: HubConnectionBuilder) {
    return this;
  } as typeof HubConnectionBuilder.prototype.configureLogging;
  HubConnectionBuilder.prototype.build = function build() {
    return createFakeHubConnection();
  } as typeof HubConnectionBuilder.prototype.build;

  function Registration({ instanceKey }: { instanceKey: string }) {
    const descriptor = useMemo(
      () => ({
        body: null,
        captureEnabled: false,
        connection: {
          apiUrl: "https://console.example.com/workable",
          realtimeHubPath: "/realtime",
          systemName: "Ops",
        },
        connectionInstanceKey: instanceKey,
        enabled: true,
        maxMessages: 25,
        viewName: "Overview",
      }),
      [instanceKey]
    );

    useRegisterConsolePageRealtimeView({
      descriptor,
      id: "overview",
    });
    return null;
  }

  function StatsProbe() {
    const stats = useConsoleRealtimeStats();
    return (
      <output data-testid="stats">
        {stats.connections.map((connection) => connection.connectionKey).join(",") || "none"}
      </output>
    );
  }

  const render = await renderDom(
    <ConsolePageRealtimeViewProvider>
      <Registration instanceKey="recovery:1" />
      <StatsProbe />
    </ConsolePageRealtimeViewProvider>
  );

  try {
    const readStats = () =>
      render.container.querySelector("[data-testid='stats']")?.textContent ?? "";

    await render.waitFor(() => {
      assert.equal(
        readStats(),
        "https://console.example.com/workable::Ops::https://console.example.com/realtime::recovery:1"
      );
    });

    await render.rerender(
      <ConsolePageRealtimeViewProvider>
        <Registration instanceKey="recovery:2" />
        <StatsProbe />
      </ConsolePageRealtimeViewProvider>
    );

    await render.waitFor(() => {
      assert.equal(
        readStats(),
        "https://console.example.com/workable::Ops::https://console.example.com/realtime::recovery:2"
      );
    });
  } finally {
    HubConnectionBuilder.prototype.withUrl = originalWithUrl;
    HubConnectionBuilder.prototype.withAutomaticReconnect = originalWithAutomaticReconnect;
    HubConnectionBuilder.prototype.configureLogging = originalConfigureLogging;
    HubConnectionBuilder.prototype.build = originalBuild;
    await render.restore();
  }
});

function createFakeHubConnection(): HubConnection {
  const methodHandlers = new Map<string, (payload: unknown) => void>();
  let reconnectingHandler: ((error?: Error) => void) | undefined;
  let reconnectedHandler: ((connectionId?: string) => void) | undefined;
  let closeHandler: ((error?: Error) => void) | undefined;

  const connection = {
    connectionId: "fake-connection",
    state: HubConnectionState.Disconnected,
    invoke: async () => undefined,
    off(method: string, handler?: (...args: unknown[]) => void) {
      const current = methodHandlers.get(method);
      if (!handler || current === handler) {
        methodHandlers.delete(method);
      }
    },
    on(method: string, handler: (...args: unknown[]) => void) {
      methodHandlers.set(method, handler as (payload: unknown) => void);
    },
    onclose(handler: (error?: Error) => void) {
      closeHandler = handler;
    },
    onreconnected(handler: (connectionId?: string) => void) {
      reconnectedHandler = handler;
    },
    onreconnecting(handler: (error?: Error) => void) {
      reconnectingHandler = handler;
    },
    start: async () => {
      void reconnectingHandler;
      connection.state = HubConnectionState.Connected;
      reconnectedHandler?.(connection.connectionId);
    },
    stop: async () => {
      connection.state = HubConnectionState.Disconnected;
      closeHandler?.();
    },
  };

  return connection as unknown as HubConnection;
}
