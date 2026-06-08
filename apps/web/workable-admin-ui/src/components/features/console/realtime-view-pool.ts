"use client";

import { HubConnectionBuilder, HubConnectionState, LogLevel, type HubConnection, type IHttpConnectionOptions } from "@microsoft/signalr";
import { createWorkableRealtimeUrl, getWorkableRealtimeAccessToken } from "../../../lib/workable.ts";

export const consoleRealtimeAutomaticReconnectDelaysMs = [0, 2000, 10000, 30000] as const;
export const consoleRealtimeFallbackRestartDelayMs = 5000;
const consoleRealtimeSharedConnectionReleaseDelayMs = 1000;

type SharedConnectionSnapshot = {
  connectionId?: string | null;
  connectionState: string;
  error?: string;
};

export type ConsoleRealtimeSharedViewConnectionLease = {
  connectionKey: string;
  ensureStarted: () => void;
  getSnapshot: () => SharedConnectionSnapshot;
  hubUrl: string;
  invoke: (method: string, ...args: unknown[]) => Promise<unknown>;
  release: () => void;
  subscribeMethod: <T = unknown>(method: string, handler: (payload: T) => void) => () => void;
  subscribeState: (listener: (snapshot: SharedConnectionSnapshot) => void) => () => void;
};

type ConsoleRealtimeSharedViewConnectionPool = {
  acquire: (options: {
    apiUrl: string;
    connectionKey: string;
    hubUrl: string;
  }) => ConsoleRealtimeSharedViewConnectionLease;
};

type SharedConnectionFactory = (options: {
  apiUrl: string;
  hubUrl: string;
  options?: Partial<IHttpConnectionOptions>;
}) => HubConnection;

type SharedMethodDispatcher = {
  dispatch: (payload: unknown) => void;
  handlers: Map<string, (payload: unknown) => void>;
};

type SharedConnectionStateListener = (snapshot: SharedConnectionSnapshot) => void;

type SharedConnectionEntry = {
  apiUrl: string;
  connectionKey: string;
  consumerCount: number;
  createConnection: SharedConnectionFactory;
  dispatchers: Map<string, SharedMethodDispatcher>;
  error?: string;
  hasConnected: boolean;
  hubConnection: HubConnection;
  hubUrl: string;
  listeners: Map<string, SharedConnectionStateListener>;
  releaseTimer: ReturnType<typeof setTimeout> | null;
  retryTimer: ReturnType<typeof setTimeout> | null;
  startPromise: Promise<void> | null;
};

export function createConsoleRealtimeSharedViewPool({
  createConnection = createConsoleRealtimeHubConnection,
  stopDelayMs = consoleRealtimeSharedConnectionReleaseDelayMs,
}: {
  createConnection?: SharedConnectionFactory;
  stopDelayMs?: number;
} = {}): ConsoleRealtimeSharedViewConnectionPool {
  const entries = new Map<string, SharedConnectionEntry>();
  let nextLeaseId = 0;

  const emitState = (entry: SharedConnectionEntry) => {
    const snapshot = createSnapshot(entry);
    for (const listener of entry.listeners.values()) {
      try {
        listener(snapshot);
      } catch (error) {
        reportSharedConnectionHandlerError("realtime shared state listener", error);
      }
    }
  };

  const scheduleRestart = (entry: SharedConnectionEntry, error: unknown) => {
    if (entry.retryTimer || entry.consumerCount <= 0) {
      setEntryState(entry, "disconnected", error);
      return;
    }

    setEntryState(entry, "disconnected", error);
    entry.retryTimer = setTimeout(() => {
      entry.retryTimer = null;
      if (entry.consumerCount > 0 && entry.hubConnection.state === HubConnectionState.Disconnected) {
        startEntry(entry);
      }
    }, consoleRealtimeFallbackRestartDelayMs);
  };

  const setEntryState = (
    entry: SharedConnectionEntry,
    connectionState: string,
    error?: unknown
  ) => {
    entry.error = error && !isExpectedConsoleRealtimeDisconnect(error)
      ? getConsoleRealtimeErrorMessage(error, "Realtime view connection closed.")
      : undefined;
    emitState(entry);
    if (entry.hubConnection.state === HubConnectionState.Connected && connectionState !== "connected") {
      return;
    }
    void connectionState;
  };

  const updateEntrySnapshot = (
    entry: SharedConnectionEntry,
    connectionState: string,
    error?: unknown
  ) => {
    entry.error = error && !isExpectedConsoleRealtimeDisconnect(error)
      ? getConsoleRealtimeErrorMessage(error, "Realtime view connection closed.")
      : undefined;
    const state = connectionState;
    if (state === "connected") {
      entry.error = undefined;
    }
    Object.defineProperty(entry, "__state", {
      configurable: true,
      enumerable: false,
      value: state,
      writable: true,
    });
    emitState(entry);
  };

  const getEntryState = (entry: SharedConnectionEntry) => {
    const state = (entry as SharedConnectionEntry & { __state?: string }).__state;
    if (typeof state === "string") {
      return state;
    }

    return entry.hubConnection.state.toLowerCase();
  };

  const createEntry = ({
    apiUrl,
    connectionKey,
    hubUrl,
  }: {
    apiUrl: string;
    connectionKey: string;
    hubUrl: string;
  }) => {
    const hubConnection = createConnection({ apiUrl, hubUrl });
    const entry: SharedConnectionEntry = {
      apiUrl,
      connectionKey,
      consumerCount: 0,
      createConnection,
      dispatchers: new Map(),
      hasConnected: false,
      hubConnection,
      hubUrl,
      listeners: new Map(),
      releaseTimer: null,
      retryTimer: null,
      startPromise: null,
    };

    updateEntrySnapshot(entry, "disconnected");

    hubConnection.onreconnecting((error) => {
      updateEntrySnapshot(entry, "reconnecting", error);
    });
    hubConnection.onreconnected(() => {
      entry.hasConnected = true;
      updateEntrySnapshot(entry, "connected");
    });
    hubConnection.onclose((error) => {
      if (entry.consumerCount <= 0) {
        updateEntrySnapshot(entry, "disconnected", error);
        return;
      }

      scheduleRestart(entry, error);
    });

    entries.set(connectionKey, entry);
    return entry;
  };

  const startEntry = (entry: SharedConnectionEntry) => {
    if (entry.startPromise || entry.consumerCount <= 0 || entry.hubConnection.state !== HubConnectionState.Disconnected) {
      return;
    }

    updateEntrySnapshot(entry, entry.hasConnected ? "reconnecting" : "connecting");
    entry.startPromise = entry.hubConnection
      .start()
      .then(() => {
        entry.hasConnected = true;
        updateEntrySnapshot(entry, "connected");
      })
      .catch((error) => {
        scheduleRestart(entry, error);
      })
      .finally(() => {
        entry.startPromise = null;
      });
  };

  const stopEntry = (entry: SharedConnectionEntry) => {
    if (entry.releaseTimer) {
      clearTimeout(entry.releaseTimer);
      entry.releaseTimer = null;
    }

    if (entry.retryTimer) {
      clearTimeout(entry.retryTimer);
      entry.retryTimer = null;
    }

    entries.delete(entry.connectionKey);
    updateEntrySnapshot(entry, "disconnected");
    void entry.hubConnection.stop().catch(() => undefined);
  };

  const createSnapshot = (entry: SharedConnectionEntry): SharedConnectionSnapshot => ({
    connectionId: entry.hubConnection.connectionId ?? null,
    connectionState: getEntryState(entry),
    error: entry.error,
  });

  return {
    acquire({ apiUrl, connectionKey, hubUrl }) {
      const entry = entries.get(connectionKey) ?? createEntry({ apiUrl, connectionKey, hubUrl });
      if (entry.releaseTimer) {
        clearTimeout(entry.releaseTimer);
        entry.releaseTimer = null;
      }
      entry.consumerCount += 1;
      const leaseId = `lease:${++nextLeaseId}`;
      let released = false;

      return {
        connectionKey,
        ensureStarted() {
          startEntry(entry);
        },
        getSnapshot() {
          return createSnapshot(entry);
        },
        hubUrl,
        invoke(method: string, ...args: unknown[]) {
          return entry.hubConnection.invoke(method, ...args);
        },
        release() {
          if (released) {
            return;
          }

          released = true;
          entry.listeners.delete(leaseId);
          for (const dispatcher of entry.dispatchers.values()) {
            dispatcher.handlers.delete(leaseId);
          }

          for (const [method, dispatcher] of entry.dispatchers) {
            if (dispatcher.handlers.size === 0) {
              entry.hubConnection.off(method, dispatcher.dispatch);
              entry.dispatchers.delete(method);
            }
          }

          entry.consumerCount = Math.max(0, entry.consumerCount - 1);
          if (entry.consumerCount === 0) {
            entry.releaseTimer = setTimeout(() => {
              entry.releaseTimer = null;
              if (entry.consumerCount === 0) {
                stopEntry(entry);
              }
            }, Math.max(0, stopDelayMs));
          }
        },
        subscribeMethod<T = unknown>(method: string, handler: (payload: T) => void) {
          let dispatcher = entry.dispatchers.get(method);
          if (!dispatcher) {
            dispatcher = {
              dispatch: (payload: unknown) => {
                const handlers = entry.dispatchers.get(method)?.handlers.values() ?? [];
                for (const candidate of handlers) {
                  try {
                    candidate(payload);
                  } catch (error) {
                    reportSharedConnectionHandlerError(
                      `realtime shared method handler (${method})`,
                      error
                    );
                  }
                }
              },
              handlers: new Map(),
            };
            entry.dispatchers.set(method, dispatcher);
            entry.hubConnection.on(method, dispatcher.dispatch);
          }

          dispatcher.handlers.set(leaseId, handler as (payload: unknown) => void);
          return () => {
            const current = entry.dispatchers.get(method);
            if (!current) {
              return;
            }

            current.handlers.delete(leaseId);
            if (current.handlers.size === 0) {
              entry.hubConnection.off(method, current.dispatch);
              entry.dispatchers.delete(method);
            }
          };
        },
        subscribeState(listener: (snapshot: SharedConnectionSnapshot) => void) {
          entry.listeners.set(leaseId, listener);
          return () => {
            entry.listeners.delete(leaseId);
          };
        },
      };
    },
  };
}

export function createConsoleRealtimeHubConnection({
  apiUrl,
  hubUrl,
  options,
}: {
  apiUrl: string;
  hubUrl: string;
  options?: Partial<IHttpConnectionOptions>;
}): HubConnection {
  return new HubConnectionBuilder()
    .withUrl(hubUrl, {
      accessTokenFactory: () => getWorkableRealtimeAccessToken(apiUrl),
      withCredentials: true,
      ...options,
    })
    .withAutomaticReconnect([...consoleRealtimeAutomaticReconnectDelaysMs])
    .configureLogging(LogLevel.None)
    .build();
}

function getConsoleRealtimeErrorMessage(error: unknown, fallback: string) {
  if (error instanceof Error && error.message.trim().length > 0) {
    return error.message;
  }

  return fallback;
}

function isExpectedConsoleRealtimeDisconnect(error: unknown) {
  const message = getConsoleRealtimeErrorMessage(error, "").toLowerCase();
  return (
    message.includes("failed to fetch") ||
    message.includes("failed to complete negotiation") ||
    message.includes("failed to start the connection") ||
    message.includes("websocket closed with status code: 1006")
  );
}

function reportSharedConnectionHandlerError(scope: string, error: unknown) {
  console.error(`${scope} failed.`, error);
}

export function createConsoleRealtimeSharedConnectionKey(
  apiUrl: string,
  systemName: string | null | undefined,
  hubUrl: string | null,
  instanceKey?: string | null
) {
  return [
    apiUrl,
    systemName ?? "",
    hubUrl ?? "",
    instanceKey ?? "",
  ].join("::");
}

export function createWorkableRealtimeHubUrl(
  connection: {
    apiUrl: string;
    realtimeHubPath?: string | null;
  } | null
) {
  return connection ? createWorkableRealtimeUrl(connection) : null;
}
