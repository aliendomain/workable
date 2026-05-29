import assert from "node:assert/strict";
import test from "node:test";
import { HubConnectionState, type HubConnection } from "@microsoft/signalr";
import {
  createConsoleRealtimeSharedViewPool,
  type ConsoleRealtimeSharedViewConnectionLease,
} from "./realtime-view-pool.ts";

test("shared view pool reuses one physical connection for matching keys", async () => {
  const created: FakeHubConnection[] = [];
  const pool = createConsoleRealtimeSharedViewPool({
    createConnection: () => {
      const connection = new FakeHubConnection();
      created.push(connection);
      return connection as unknown as HubConnection;
    },
  });

  const first = acquire(pool);
  const second = acquire(pool);

  first.ensureStarted();
  second.ensureStarted();
  await flushMicrotasks();

  assert.equal(created.length, 1);
  assert.equal(created[0]?.startCount, 1);

  first.release();
  assert.equal(created[0]?.stopCount, 0);

  second.release();
  assert.equal(created[0]?.stopCount, 1);
});

test("shared view pool fans out method payloads to all leases", async () => {
  const connection = new FakeHubConnection();
  const pool = createConsoleRealtimeSharedViewPool({
    createConnection: () => connection as unknown as HubConnection,
  });

  const first = acquire(pool);
  const second = acquire(pool);
  const firstPayloads: string[] = [];
  const secondPayloads: string[] = [];

  first.ensureStarted();
  second.ensureStarted();
  await flushMicrotasks();

  const unlistenFirst = first.subscribeMethod<string>("workable.view", (payload) => {
    firstPayloads.push(payload);
  });
  const unlistenSecond = second.subscribeMethod<string>("workable.view", (payload) => {
    secondPayloads.push(payload);
  });

  connection.emit("workable.view", "payload-1");

  assert.deepEqual(firstPayloads, ["payload-1"]);
  assert.deepEqual(secondPayloads, ["payload-1"]);
  assert.equal(connection.onRegistrations.get("workable.view"), 1);

  unlistenFirst();
  unlistenSecond();
  first.release();
  second.release();

  assert.equal(connection.offRegistrations.get("workable.view"), 1);
});

test("shared view pool isolates method handler exceptions", async () => {
  const connection = new FakeHubConnection();
  const pool = createConsoleRealtimeSharedViewPool({
    createConnection: () => connection as unknown as HubConnection,
  });

  const first = acquire(pool);
  const second = acquire(pool);
  const secondPayloads: string[] = [];
  const consoleErrorCalls: unknown[][] = [];
  const originalConsoleError = console.error;
  console.error = (...args: unknown[]) => {
    consoleErrorCalls.push(args);
  };

  try {
    first.ensureStarted();
    second.ensureStarted();
    await flushMicrotasks();

    first.subscribeMethod<string>("workable.view", () => {
      throw new Error("boom");
    });
    second.subscribeMethod<string>("workable.view", (payload) => {
      secondPayloads.push(payload);
    });

    connection.emit("workable.view", "payload-1");

    assert.deepEqual(secondPayloads, ["payload-1"]);
    assert.equal(consoleErrorCalls.length, 1);
  } finally {
    console.error = originalConsoleError;
    first.release();
    second.release();
  }
});

test("shared view pool publishes connection state changes to listeners", async () => {
  const connection = new FakeHubConnection();
  const pool = createConsoleRealtimeSharedViewPool({
    createConnection: () => connection as unknown as HubConnection,
  });
  const lease = acquire(pool);
  const states: string[] = [];

  const unsubscribe = lease.subscribeState((snapshot) => {
    states.push(snapshot.connectionState);
  });

  lease.ensureStarted();
  await flushMicrotasks();
  connection.triggerReconnecting(new Error("temporary"));
  connection.triggerReconnected();

  unsubscribe();
  lease.release();

  assert.ok(states.includes("connected"));
  assert.ok(states.includes("reconnecting"));
});

function acquire(pool: ReturnType<typeof createConsoleRealtimeSharedViewPool>): ConsoleRealtimeSharedViewConnectionLease {
  return pool.acquire({
    apiUrl: "https://workable.example.com/workable",
    connectionKey: "https://workable.example.com/workable::::https://workable.example.com/workable/realtime",
    hubUrl: "https://workable.example.com/workable/realtime",
  });
}

async function flushMicrotasks() {
  await Promise.resolve();
  await Promise.resolve();
}

class FakeHubConnection {
  public state = HubConnectionState.Disconnected;
  public readonly invokes: Array<{ args: unknown[]; method: string }> = [];
  public readonly offRegistrations = new Map<string, number>();
  public readonly onRegistrations = new Map<string, number>();
  public startCount = 0;
  public stopCount = 0;

  private readonly closeHandlers = new Set<(error?: Error) => void>();
  private readonly methodHandlers = new Map<string, Set<(payload: unknown) => void>>();
  private readonly reconnectedHandlers = new Set<() => void>();
  private readonly reconnectingHandlers = new Set<(error?: Error) => void>();

  public invoke(method: string, ...args: unknown[]) {
    this.invokes.push({ args, method });
    return Promise.resolve(undefined);
  }

  public off(method: string, handler: (payload: unknown) => void) {
    const handlers = this.methodHandlers.get(method);
    if (handlers) {
      handlers.delete(handler);
      if (handlers.size === 0) {
        this.methodHandlers.delete(method);
      }
    }

    this.offRegistrations.set(method, (this.offRegistrations.get(method) ?? 0) + 1);
  }

  public on(method: string, handler: (payload: unknown) => void) {
    const handlers = this.methodHandlers.get(method) ?? new Set<(payload: unknown) => void>();
    handlers.add(handler);
    this.methodHandlers.set(method, handlers);
    this.onRegistrations.set(method, (this.onRegistrations.get(method) ?? 0) + 1);
  }

  public onclose(handler: (error?: Error) => void) {
    this.closeHandlers.add(handler);
  }

  public onreconnected(handler: () => void) {
    this.reconnectedHandlers.add(handler);
  }

  public onreconnecting(handler: (error?: Error) => void) {
    this.reconnectingHandlers.add(handler);
  }

  public start() {
    this.startCount += 1;
    this.state = HubConnectionState.Connected;
    return Promise.resolve();
  }

  public stop() {
    this.stopCount += 1;
    this.state = HubConnectionState.Disconnected;
    return Promise.resolve();
  }

  public emit(method: string, payload: unknown) {
    for (const handler of this.methodHandlers.get(method) ?? []) {
      handler(payload);
    }
  }

  public triggerClose(error?: Error) {
    this.state = HubConnectionState.Disconnected;
    for (const handler of this.closeHandlers) {
      handler(error);
    }
  }

  public triggerReconnected() {
    this.state = HubConnectionState.Connected;
    for (const handler of this.reconnectedHandlers) {
      handler();
    }
  }

  public triggerReconnecting(error?: Error) {
    this.state = HubConnectionState.Reconnecting;
    for (const handler of this.reconnectingHandlers) {
      handler(error);
    }
  }
}
