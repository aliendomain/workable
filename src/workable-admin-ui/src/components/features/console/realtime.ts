"use client";

import {
  HubConnectionBuilder,
  HubConnectionState,
  LogLevel,
  type HubConnection,
  type IHttpConnectionOptions,
} from "@microsoft/signalr";
import { useCallback, useEffect, useMemo, useRef, useState, useSyncExternalStore } from "react";
import type { RealtimePayloadMessage } from "@/components/features/console/realtime-payload";
import type { Loadable } from "@/components/features/console/types";
import {
  createWorkableRealtimeUrl,
  getWorkableRealtimeAccessToken,
  type WorkableConnection,
  type WorkableRealtimeEvent,
  type WorkableRealtimeEventBatch,
} from "@/lib/workable";

export const consoleRealtimeAutomaticReconnectDelaysMs = [0, 2000, 10000, 30000] as const;
export const consoleRealtimeFallbackRestartDelayMs = 5000;

export type ConsoleRealtimeViewLoadable<T, TMessage extends RealtimePayloadMessage> = Loadable<T> & {
  clearMessages: () => void;
  connectionState: string;
  enabled: boolean;
  hubUrl?: string | null;
  messages: TMessage[];
};

export type ConsoleRealtimeEventLoadable<TMessage> = {
  clearMessages: () => void;
  connectionState: string;
  enabled: boolean;
  error?: string;
  hubUrl?: string | null;
  loading?: boolean;
  messages: TMessage[];
};

export type ConsoleRealtimeEventMessage = {
  batchId?: string;
  batchSize?: number;
  bytes: number;
  bytesEstimated?: boolean;
  events: WorkableRealtimeEvent[];
  eventTypes: string[];
  id: string;
  receivedAt: number;
  sentAt?: string;
  value: WorkableRealtimeEvent | WorkableRealtimeEventBatch;
};

type ConsoleRealtimeViewEnvelope<T> = {
  result: T;
  subscriptionId: string;
  viewName: string;
};

export type ConsoleRealtimeStatsEntry = {
  connectionKey: string;
  connectionState: string;
  consumerCount: number;
  enabled: boolean;
  hubUrl: string | null;
  id: string;
  kind: "events" | "view";
  label: string;
  lifecycleHandlerCount: number;
  onHandlerCount: number;
  subscriptionCount: number;
};

export type ConsoleRealtimeStatsSnapshot = {
  activeConsumerCount: number;
  activeSubscriptionCount: number;
  connections: ConsoleRealtimeStatsEntry[];
  lifecycleHandlerCount: number;
  onHandlerCount: number;
  physicalConnectionCount: number;
  signalrHandlerCount: number;
};

type ConsoleRealtimePayloadCaptureEntry = {
  clearMessages: () => void;
  id: string;
  messages: RealtimePayloadMessage[];
};

export type ConsoleRealtimePayloadCaptureSnapshot = {
  messageCount: number;
  messages: RealtimePayloadMessage[];
  sourceCount: number;
};

type ConsoleRealtimeEventCaptureEntry = {
  clearMessages: () => void;
  id: string;
  messages: ConsoleRealtimeEventMessage[];
};

export type ConsoleRealtimeEventCaptureSnapshot = {
  messageCount: number;
  messages: ConsoleRealtimeEventMessage[];
  sourceCount: number;
};

const emptyConsoleRealtimeStatsSnapshot: ConsoleRealtimeStatsSnapshot = {
  activeConsumerCount: 0,
  activeSubscriptionCount: 0,
  connections: [],
  lifecycleHandlerCount: 0,
  onHandlerCount: 0,
  physicalConnectionCount: 0,
  signalrHandlerCount: 0,
};

const emptyConsoleRealtimePayloadCaptureSnapshot: ConsoleRealtimePayloadCaptureSnapshot = {
  messageCount: 0,
  messages: [],
  sourceCount: 0,
};

const emptyConsoleRealtimeEventCaptureSnapshot: ConsoleRealtimeEventCaptureSnapshot = {
  messageCount: 0,
  messages: [],
  sourceCount: 0,
};

const consoleRealtimeStatsEntries = new Map<string, ConsoleRealtimeStatsEntry>();
const consoleRealtimeStatsListeners = new Set<() => void>();
const consoleRealtimePayloadCaptureEntries = new Map<string, ConsoleRealtimePayloadCaptureEntry>();
const consoleRealtimePayloadCaptureListeners = new Set<() => void>();
const consoleRealtimeEventCaptureEntries = new Map<string, ConsoleRealtimeEventCaptureEntry>();
const consoleRealtimeEventCaptureListeners = new Set<() => void>();
let nextConsoleRealtimeStatsEntryId = 0;
let consoleRealtimeStatsSnapshot = emptyConsoleRealtimeStatsSnapshot;
let consoleRealtimeStatsChangeQueued = false;
let consoleRealtimePayloadCaptureSnapshot = emptyConsoleRealtimePayloadCaptureSnapshot;
let consoleRealtimePayloadCaptureChangeQueued = false;
let consoleRealtimeEventCaptureSnapshot = emptyConsoleRealtimeEventCaptureSnapshot;
let consoleRealtimeEventCaptureChangeQueued = false;

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

export function useConsoleRealtimeView<T, TMessage extends RealtimePayloadMessage>({
  body,
  captureEnabled,
  connection,
  createMessage,
  enabled,
  maxMessages,
  viewName,
  subscription,
}: {
  body: unknown;
  captureEnabled: boolean;
  connection: WorkableConnection | null;
  createMessage: (result: T, nextMessageId: number) => TMessage;
  enabled: boolean;
  maxMessages: number;
  subscription?: string;
  viewName: string;
}): ConsoleRealtimeViewLoadable<T, TMessage> {
  const subscriptionName = subscription ?? viewName;
  const subscriptionId = subscriptionName;
  const [state, setState] = useState<ConsoleRealtimeViewLoadable<T, TMessage>>({
    clearMessages: () => undefined,
    connectionState: enabled ? "connecting" : "disabled",
    enabled,
    hubUrl: connection ? createWorkableRealtimeUrl(connection) : null,
    loading: false,
    messages: [],
  });
  const hubConnectionRef = useRef<HubConnection | null>(null);
  const payloadCaptureEntryIdRef = useRef<string>(createConsoleRealtimeEntryId("payload"));
  const statsEntryIdRef = useRef<string>(createConsoleRealtimeStatsEntryId("view"));
  const hasConnection = connection !== null;
  const apiUrl = connection?.apiUrl ?? "";
  const hubUrl = connection ? createWorkableRealtimeUrl(connection) : null;
  const systemName = connection?.systemName;
  const statsConnectionKey = useMemo(
    () => createConsoleRealtimeStatsConnectionKey(apiUrl, systemName, hubUrl),
    [apiUrl, hubUrl, systemName]
  );
  const bodyKey = JSON.stringify(body);
  const bodyKeyRef = useRef(bodyKey);
  const captureEnabledRef = useRef(captureEnabled);
  const previousCaptureEnabledRef = useRef(captureEnabled);
  const createMessageRef = useRef(createMessage);
  const hasDataRef = useRef(false);
  const maxMessagesRef = useRef(maxMessages);
  const messageIdRef = useRef(0);
  const systemNameRef = useRef(systemName);

  useEffect(() => {
    bodyKeyRef.current = bodyKey;
    captureEnabledRef.current = captureEnabled;
    createMessageRef.current = createMessage;
    hasDataRef.current = state.data !== undefined;
    maxMessagesRef.current = maxMessages;
    systemNameRef.current = systemName;
  }, [bodyKey, captureEnabled, createMessage, maxMessages, state.data, systemName]);

  const clearMessages = useCallback(() => {
    setState((current) =>
      current.messages.length === 0 ? current : { ...current, messages: [] }
    );
  }, []);

  useEffect(() => {
    const wasCaptureEnabled = previousCaptureEnabledRef.current;
    previousCaptureEnabledRef.current = captureEnabled;

    if (!captureEnabled) {
      deleteConsoleRealtimePayloadCaptureEntry(payloadCaptureEntryIdRef.current);
      if (wasCaptureEnabled) {
        queueMicrotask(() => {
          setState((current) =>
            current.messages.length === 0 ? current : { ...current, messages: [] }
          );
        });
      }
      return;
    }

    upsertConsoleRealtimePayloadCaptureEntry({
      clearMessages,
      id: payloadCaptureEntryIdRef.current,
      messages: state.messages,
    });
  }, [captureEnabled, clearMessages, state.messages]);

  useEffect(
    () => () => {
      deleteConsoleRealtimePayloadCaptureEntry(payloadCaptureEntryIdRef.current);
    },
    []
  );

  useEffect(() => {
    queueMicrotask(() => {
      setState((current) =>
        current.messages.length > maxMessages
          ? { ...current, messages: current.messages.slice(0, maxMessages) }
          : current
      );
    });
  }, [maxMessages]);

  useEffect(() => {
    if (!hasConnection || !enabled || !hubUrl) {
      queueMicrotask(() =>
        setState((current) =>
          current.connectionState === "disabled" &&
          current.enabled === enabled &&
          current.hubUrl === hubUrl &&
          !current.loading &&
          !current.refreshing
            ? current
            : {
                ...current,
                connectionState: "disabled",
                enabled,
                hubUrl,
                loading: false,
                refreshing: false,
              }
        )
      );
      return;
    }

    let canceled = false;
    let retryTimer: ReturnType<typeof setTimeout> | null = null;
    const statsEntryId = statsEntryIdRef.current;
    const updateStats = (connectionState: string) => {
      upsertConsoleRealtimeStatsEntry({
        connectionKey: statsConnectionKey,
        connectionState,
        consumerCount: 1,
        enabled,
        hubUrl,
        id: statsEntryId,
        kind: "view",
        label: subscriptionName,
        lifecycleHandlerCount: 3,
        onHandlerCount: 1,
        subscriptionCount: 1,
      });
    };

    queueMicrotask(() => {
      if (!canceled) {
        updateStats("connecting");
        setState((current) => ({
          ...current,
          connectionState: "connecting",
          enabled,
          hubUrl,
        }));
      }
    });
    const hubConnection = createConsoleRealtimeHubConnection({
      apiUrl,
      hubUrl,
    });

    hubConnectionRef.current = hubConnection;
    const subscribe = () =>
      hubConnection.invoke(
        "WatchView",
        subscriptionId,
        viewName,
        JSON.parse(bodyKeyRef.current),
        systemNameRef.current ?? null
      );
    const scheduleRestart = (error: unknown, delayMs = consoleRealtimeFallbackRestartDelayMs) => {
      if (canceled || retryTimer) {
        return;
      }

      retryTimer = setTimeout(() => {
        retryTimer = null;
        if (!canceled && hubConnection.state === HubConnectionState.Disconnected) {
          startConnection();
        }
      }, delayMs);
      updateStats("disconnected");
      setState((current) => ({
        ...current,
        connectionState: "disconnected",
        error: error && !isExpectedConsoleRealtimeDisconnect(error)
          ? getConsoleRealtimeErrorMessage(error, "Realtime view connection closed.")
          : undefined,
        loading: false,
        refreshing: false,
      }));
    };
    const startConnection = () => {
      if (canceled || hubConnection.state !== HubConnectionState.Disconnected) {
        return;
      }

      queueMicrotask(() => {
        if (!canceled) {
          const nextConnectionState = hasDataRef.current ? "reconnecting" : "connecting";
          updateStats(nextConnectionState);
          setState((current) => {
            return {
              ...current,
              connectionState: nextConnectionState,
              loading: !hasDataRef.current,
              refreshing: hasDataRef.current,
            };
          });
        }
      });
      void hubConnection
        .start()
        .then(() => subscribe())
        .then(() => {
          if (!canceled) {
            updateStats("connected");
            setState((current) => ({
              ...current,
              connectionState: "connected",
              error: undefined,
              loading: false,
              refreshing: false,
            }));
          }
        })
        .catch((error) => {
          if (!canceled) {
            scheduleRestart(error);
          }
        });
    };

    hubConnection.on("workable.view", (envelope: ConsoleRealtimeViewEnvelope<T>) => {
      if (!canceled && envelope.subscriptionId === subscriptionId) {
        const message = createMessageRef.current(envelope.result, ++messageIdRef.current);
        updateStats("connected");
        setState((current) => ({
          ...current,
          connectionState: "connected",
          data: envelope.result,
          enabled,
          hubUrl,
          loading: false,
          messages: captureEnabledRef.current
            ? [message, ...current.messages].slice(0, maxMessagesRef.current)
            : current.messages,
          refreshing: false,
        }));
      }
    });
    hubConnection.onreconnecting(() => {
      if (!canceled) {
        updateStats("reconnecting");
        setState((current) => ({
          ...current,
          connectionState: "reconnecting",
          refreshing: current.data !== undefined,
        }));
      }
    });
    hubConnection.onreconnected(() => {
      if (!canceled) {
        updateStats("connected");
        setState((current) => ({
          ...current,
          connectionState: "connected",
        }));
      }
      void subscribe().catch((error) => {
        if (!canceled && !isExpectedConsoleRealtimeDisconnect(error)) {
          updateStats("error");
          setState((current) => ({
            ...current,
            connectionState: "error",
            error: getConsoleRealtimeErrorMessage(error, "Realtime view subscription failed."),
            loading: false,
            refreshing: false,
          }));
        }
      });
    });
    hubConnection.onclose((error) => {
      if (!canceled) {
        scheduleRestart(error);
      }
    });

    startConnection();

    return () => {
      canceled = true;
      if (retryTimer) {
        clearTimeout(retryTimer);
      }
      hubConnectionRef.current = null;
      deleteConsoleRealtimeStatsEntry(statsEntryId);
      void hubConnection.stop().catch(() => undefined);
    };
  }, [apiUrl, enabled, hasConnection, hubUrl, statsConnectionKey, subscriptionId, subscriptionName, systemName, viewName]);

  useEffect(() => {
    const hubConnection = hubConnectionRef.current;
    if (!enabled || !hubConnection || hubConnection.state !== HubConnectionState.Connected) {
      return;
    }

    let canceled = false;
    queueMicrotask(() => {
      if (!canceled) {
        setState((current) => ({
          ...current,
          connectionState: hubConnection.state.toLowerCase(),
          error: undefined,
          loading: current.data === undefined,
          refreshing: current.data !== undefined,
        }));
      }
    });

    hubConnection
      .invoke("WatchView", subscriptionId, viewName, JSON.parse(bodyKey), systemName ?? null)
      .catch((error) => {
        if (!canceled) {
          setState((current) => ({
            ...current,
            connectionState: "error",
            error: getConsoleRealtimeErrorMessage(error, "Realtime view subscription failed."),
            loading: false,
            refreshing: false,
          }));
        }
      });

    return () => {
      canceled = true;
      if (hubConnection.state === HubConnectionState.Connected) {
        void hubConnection.invoke("UnwatchView", subscriptionId, systemName ?? null).catch(() => undefined);
      }
    };
  }, [bodyKey, enabled, subscriptionId, systemName, viewName]);

  return { ...state, clearMessages };
}

export function useConsoleRealtimeEventStream<TMessage extends ConsoleRealtimeEventMessage>({
  captureEnabled,
  connection,
  createBatchMessage,
  createSingleMessage,
  debugLabel,
  enabled,
  maxMessages,
  subscriptionErrorMessage,
  watchArgument,
  watchArgumentKey,
  watchMethod,
  watchReady = true,
  watchStoppedMessage,
  unwatchMethod,
}: {
  captureEnabled: boolean;
  connection: WorkableConnection | null;
  createBatchMessage: (batch: WorkableRealtimeEventBatch, nextMessageId: number) => TMessage;
  createSingleMessage: (workEvent: WorkableRealtimeEvent, nextMessageId: number) => TMessage;
  debugLabel?: string;
  enabled: boolean;
  maxMessages: number;
  subscriptionErrorMessage: string;
  watchArgument: unknown;
  watchArgumentKey: string;
  watchMethod: string;
  watchReady?: boolean;
  watchStoppedMessage: string;
  unwatchMethod?: string;
}): ConsoleRealtimeEventLoadable<TMessage> {
  // We intentionally key the watch argument by its serialized identity so
  // equivalent object recreation does not tear down and recreate the connection.
  // eslint-disable-next-line react-hooks/exhaustive-deps
  const stableWatchArgument = useMemo(() => watchArgument, [watchArgumentKey]);
  const [state, setState] = useState<ConsoleRealtimeEventLoadable<TMessage>>({
    clearMessages: () => undefined,
    connectionState: enabled ? "connecting" : "disabled",
    enabled,
    hubUrl: connection ? createWorkableRealtimeUrl(connection) : null,
    loading: false,
    messages: [],
  });
  const payloadCaptureEntryIdRef = useRef<string>(createConsoleRealtimeEntryId("event-payload"));
  const statsEntryIdRef = useRef<string>(createConsoleRealtimeStatsEntryId("events"));
  const hasConnection = connection !== null;
  const apiUrl = connection?.apiUrl ?? "";
  const hubUrl = connection ? createWorkableRealtimeUrl(connection) : null;
  const systemName = connection?.systemName;
  const statsConnectionKey = useMemo(
    () => createConsoleRealtimeStatsConnectionKey(apiUrl, systemName, hubUrl),
    [apiUrl, hubUrl, systemName]
  );
  const createBatchMessageRef = useRef(createBatchMessage);
  const createSingleMessageRef = useRef(createSingleMessage);
  const captureEnabledRef = useRef(captureEnabled);
  const previousCaptureEnabledRef = useRef(captureEnabled);
  const hasMessagesRef = useRef(false);
  const maxMessagesRef = useRef(maxMessages);
  const messageIdRef = useRef(0);

  useEffect(() => {
    captureEnabledRef.current = captureEnabled;
    createBatchMessageRef.current = createBatchMessage;
    createSingleMessageRef.current = createSingleMessage;
    hasMessagesRef.current = state.messages.length > 0;
    maxMessagesRef.current = maxMessages;
  }, [captureEnabled, createBatchMessage, createSingleMessage, maxMessages, state.messages.length]);

  const clearMessages = useCallback(() => {
    setState((current) =>
      current.messages.length === 0 ? current : { ...current, messages: [] }
    );
  }, []);

  useEffect(() => {
    const wasCaptureEnabled = previousCaptureEnabledRef.current;
    previousCaptureEnabledRef.current = captureEnabled;

    if (!captureEnabled) {
      deleteConsoleRealtimeEventCaptureEntry(payloadCaptureEntryIdRef.current);
      if (wasCaptureEnabled) {
        queueMicrotask(() => {
          setState((current) =>
            current.messages.length === 0 ? current : { ...current, messages: [] }
          );
        });
      }
      return;
    }

    upsertConsoleRealtimeEventCaptureEntry({
      clearMessages,
      id: payloadCaptureEntryIdRef.current,
      messages: state.messages as ConsoleRealtimeEventMessage[],
    });
  }, [captureEnabled, clearMessages, state.messages]);

  useEffect(
    () => () => {
      deleteConsoleRealtimeEventCaptureEntry(payloadCaptureEntryIdRef.current);
    },
    []
  );

  useEffect(() => {
    queueMicrotask(() => {
      setState((current) =>
        current.messages.length > maxMessages
          ? { ...current, messages: current.messages.slice(0, maxMessages) }
          : current
      );
    });
  }, [maxMessages]);

  useEffect(() => {
    if (!hasConnection || !enabled || !hubUrl || !watchReady) {
      queueMicrotask(() =>
        setState((current) =>
          current.connectionState === "disabled" &&
          current.enabled === enabled &&
          current.hubUrl === hubUrl &&
          !current.loading
            ? current
            : {
                ...current,
                connectionState: "disabled",
                enabled,
                hubUrl,
                loading: false,
              }
        )
      );
      return;
    }

    let canceled = false;
    let retryTimer: ReturnType<typeof setTimeout> | null = null;
    const statsEntryId = statsEntryIdRef.current;
    const updateStats = (connectionState: string) => {
      upsertConsoleRealtimeStatsEntry({
        connectionKey: statsConnectionKey,
        connectionState,
        consumerCount: 1,
        enabled,
        hubUrl,
        id: statsEntryId,
        kind: "events",
        label: debugLabel ?? watchMethod,
        lifecycleHandlerCount: 3,
        onHandlerCount: 2,
        subscriptionCount: 1,
      });
    };

    queueMicrotask(() => {
      if (!canceled) {
        updateStats("connecting");
        setState((current) => ({
          ...current,
          connectionState: "connecting",
          enabled,
          hubUrl,
          loading: current.messages.length === 0,
        }));
      }
    });
    const hubConnection = createConsoleRealtimeHubConnection({
      apiUrl,
      hubUrl,
    });

    const invokeWatch = () =>
      hubConnection.invoke(
        watchMethod,
        stableWatchArgument,
        systemName ?? null
      );
    const scheduleRestart = (error: unknown, delayMs = consoleRealtimeFallbackRestartDelayMs) => {
      if (canceled || retryTimer) {
        return;
      }

      retryTimer = setTimeout(() => {
        retryTimer = null;
        if (!canceled && hubConnection.state === HubConnectionState.Disconnected) {
          startConnection();
        }
      }, delayMs);
      updateStats("disconnected");
      setState((current) => ({
        ...current,
        connectionState: "disconnected",
        error: error && !isExpectedConsoleRealtimeDisconnect(error)
          ? getConsoleRealtimeErrorMessage(error, watchStoppedMessage)
          : undefined,
        loading: false,
      }));
    };
    const startConnection = () => {
      if (canceled || hubConnection.state !== HubConnectionState.Disconnected) {
        return;
      }

      queueMicrotask(() => {
        if (!canceled) {
          const nextConnectionState = hasMessagesRef.current ? "reconnecting" : "connecting";
          updateStats(nextConnectionState);
          setState((current) => {
            return {
              ...current,
              connectionState: nextConnectionState,
              loading: !hasMessagesRef.current,
            };
          });
        }
      });
      void hubConnection
        .start()
        .then(() => invokeWatch())
        .then(() => {
          if (!canceled) {
            updateStats("connected");
            setState((current) => ({
              ...current,
              connectionState: "connected",
              error: undefined,
              loading: false,
            }));
          }
        })
        .catch((error) => {
          if (!canceled) {
            scheduleRestart(error);
          }
        });
    };

    hubConnection.on("workable.event", (workEvent: WorkableRealtimeEvent) => {
      if (!canceled) {
        const message = createSingleMessageRef.current(workEvent, ++messageIdRef.current);
        updateStats("connected");
        setState((current) => ({
          ...current,
          connectionState: "connected",
          enabled,
          hubUrl,
          loading: false,
          messages: captureEnabledRef.current
            ? [message, ...current.messages].slice(0, maxMessagesRef.current)
            : current.messages,
        }));
      }
    });
    hubConnection.on("workable.events", (batch: WorkableRealtimeEventBatch) => {
      if (!canceled) {
        const message = createBatchMessageRef.current(batch, ++messageIdRef.current);
        updateStats("connected");
        setState((current) => ({
          ...current,
          connectionState: "connected",
          enabled,
          hubUrl,
          loading: false,
          messages: captureEnabledRef.current
            ? [message, ...current.messages].slice(0, maxMessagesRef.current)
            : current.messages,
        }));
      }
    });
    hubConnection.onreconnecting(() => {
      if (!canceled) {
        updateStats("reconnecting");
        setState((current) => ({
          ...current,
          connectionState: "reconnecting",
        }));
      }
    });
    hubConnection.onreconnected(() => {
      if (!canceled) {
        updateStats("connected");
        setState((current) => ({
          ...current,
          connectionState: "connected",
        }));
      }
      void invokeWatch().catch((error) => {
        if (!canceled && !isExpectedConsoleRealtimeDisconnect(error)) {
          updateStats("error");
          setState((current) => ({
            ...current,
            connectionState: "error",
            error: getConsoleRealtimeErrorMessage(error, subscriptionErrorMessage),
            loading: false,
          }));
        }
      });
    });
    hubConnection.onclose((error) => {
      if (!canceled) {
        scheduleRestart(error);
      }
    });

    startConnection();

    return () => {
      canceled = true;
      if (retryTimer) {
        clearTimeout(retryTimer);
      }
      deleteConsoleRealtimeStatsEntry(statsEntryId);
      if (unwatchMethod && hubConnection.state === HubConnectionState.Connected) {
        void hubConnection
          .invoke(unwatchMethod, stableWatchArgument, systemName ?? null)
          .catch(() => undefined);
      }
      void hubConnection.stop().catch(() => undefined);
    };
  }, [
    apiUrl,
    debugLabel,
    enabled,
    hasConnection,
    hubUrl,
    statsConnectionKey,
    stableWatchArgument,
    subscriptionErrorMessage,
    systemName,
    unwatchMethod,
    watchMethod,
    watchReady,
    watchStoppedMessage,
  ]);

  return { ...state, clearMessages };
}

export function useConsoleRealtimeStats() {
  return useSyncExternalStore(
    subscribeConsoleRealtimeStats,
    getConsoleRealtimeStatsSnapshot,
    getConsoleRealtimeStatsSnapshot
  );
}

export function useConsoleRealtimePayloadCapture() {
  return useSyncExternalStore(
    subscribeConsoleRealtimePayloadCapture,
    getConsoleRealtimePayloadCaptureSnapshot,
    getConsoleRealtimePayloadCaptureSnapshot
  );
}

export function useConsoleRealtimeEventCapture() {
  return useSyncExternalStore(
    subscribeConsoleRealtimeEventCapture,
    getConsoleRealtimeEventCaptureSnapshot,
    getConsoleRealtimeEventCaptureSnapshot
  );
}

export function clearConsoleRealtimePayloadCapture() {
  for (const entry of consoleRealtimePayloadCaptureEntries.values()) {
    entry.clearMessages();
  }
}

export function clearConsoleRealtimeEventCapture() {
  for (const entry of consoleRealtimeEventCaptureEntries.values()) {
    entry.clearMessages();
  }
}

function createConsoleRealtimeStatsEntryId(prefix: string) {
  return createConsoleRealtimeEntryId(prefix);
}

function createConsoleRealtimeEntryId(prefix: string) {
  nextConsoleRealtimeStatsEntryId += 1;
  return `${prefix}:${nextConsoleRealtimeStatsEntryId}`;
}

function createConsoleRealtimeStatsConnectionKey(
  apiUrl: string,
  systemName: string | null | undefined,
  hubUrl: string | null
) {
  return [
    apiUrl,
    systemName ?? "",
    hubUrl ?? "",
  ].join("::");
}

function subscribeConsoleRealtimeStats(listener: () => void) {
  consoleRealtimeStatsListeners.add(listener);
  return () => {
    consoleRealtimeStatsListeners.delete(listener);
  };
}

function getConsoleRealtimeStatsSnapshot(): ConsoleRealtimeStatsSnapshot {
  return consoleRealtimeStatsSnapshot;
}

function subscribeConsoleRealtimePayloadCapture(listener: () => void) {
  consoleRealtimePayloadCaptureListeners.add(listener);
  return () => {
    consoleRealtimePayloadCaptureListeners.delete(listener);
  };
}

function getConsoleRealtimePayloadCaptureSnapshot(): ConsoleRealtimePayloadCaptureSnapshot {
  return consoleRealtimePayloadCaptureSnapshot;
}

function subscribeConsoleRealtimeEventCapture(listener: () => void) {
  consoleRealtimeEventCaptureListeners.add(listener);
  return () => {
    consoleRealtimeEventCaptureListeners.delete(listener);
  };
}

function getConsoleRealtimeEventCaptureSnapshot(): ConsoleRealtimeEventCaptureSnapshot {
  return consoleRealtimeEventCaptureSnapshot;
}

function recomputeConsoleRealtimeStatsSnapshot(): ConsoleRealtimeStatsSnapshot {
  if (consoleRealtimeStatsEntries.size === 0) {
    return emptyConsoleRealtimeStatsSnapshot;
  }

  const connections = Array.from(consoleRealtimeStatsEntries.values())
    .sort((left, right) =>
      left.label.localeCompare(right.label) ||
      left.id.localeCompare(right.id)
    );

  const physicalConnectionCount = connections.length;
  const onHandlerCount = connections.reduce((sum, connection) => sum + connection.onHandlerCount, 0);
  const lifecycleHandlerCount = connections.reduce(
    (sum, connection) => sum + connection.lifecycleHandlerCount,
    0
  );
  const activeSubscriptionCount = connections.reduce(
    (sum, connection) => sum + connection.subscriptionCount,
    0
  );
  const activeConsumerCount = connections.reduce(
    (sum, connection) => sum + connection.consumerCount,
    0
  );

  return {
    activeConsumerCount,
    activeSubscriptionCount,
    connections,
    lifecycleHandlerCount,
    onHandlerCount,
    physicalConnectionCount,
    signalrHandlerCount: onHandlerCount + lifecycleHandlerCount,
  };
}

function recomputeConsoleRealtimePayloadCaptureSnapshot(): ConsoleRealtimePayloadCaptureSnapshot {
  if (consoleRealtimePayloadCaptureEntries.size === 0) {
    return emptyConsoleRealtimePayloadCaptureSnapshot;
  }

  const byId = new Map<string, RealtimePayloadMessage>();
  for (const entry of consoleRealtimePayloadCaptureEntries.values()) {
    for (const message of entry.messages) {
      byId.set(message.id, message);
    }
  }

  const messages = Array.from(byId.values())
    .sort((left, right) => right.receivedAt - left.receivedAt);

  return {
    messageCount: messages.length,
    messages,
    sourceCount: consoleRealtimePayloadCaptureEntries.size,
  };
}

function recomputeConsoleRealtimeEventCaptureSnapshot(): ConsoleRealtimeEventCaptureSnapshot {
  if (consoleRealtimeEventCaptureEntries.size === 0) {
    return emptyConsoleRealtimeEventCaptureSnapshot;
  }

  const byId = new Map<string, ConsoleRealtimeEventMessage>();
  for (const entry of consoleRealtimeEventCaptureEntries.values()) {
    for (const message of entry.messages) {
      byId.set(message.id, message);
    }
  }

  const messages = Array.from(byId.values())
    .sort((left, right) => right.receivedAt - left.receivedAt);

  return {
    messageCount: messages.length,
    messages,
    sourceCount: consoleRealtimeEventCaptureEntries.size,
  };
}

function upsertConsoleRealtimeStatsEntry(entry: ConsoleRealtimeStatsEntry) {
  consoleRealtimeStatsEntries.set(entry.id, entry);
  queueConsoleRealtimeStatsChange();
}

function upsertConsoleRealtimePayloadCaptureEntry(entry: ConsoleRealtimePayloadCaptureEntry) {
  consoleRealtimePayloadCaptureEntries.set(entry.id, entry);
  queueConsoleRealtimePayloadCaptureChange();
}

function upsertConsoleRealtimeEventCaptureEntry(entry: ConsoleRealtimeEventCaptureEntry) {
  consoleRealtimeEventCaptureEntries.set(entry.id, entry);
  queueConsoleRealtimeEventCaptureChange();
}

function deleteConsoleRealtimeStatsEntry(id: string) {
  if (consoleRealtimeStatsEntries.delete(id)) {
    queueConsoleRealtimeStatsChange();
  }
}

function deleteConsoleRealtimePayloadCaptureEntry(id: string) {
  if (consoleRealtimePayloadCaptureEntries.delete(id)) {
    queueConsoleRealtimePayloadCaptureChange();
  }
}

function deleteConsoleRealtimeEventCaptureEntry(id: string) {
  if (consoleRealtimeEventCaptureEntries.delete(id)) {
    queueConsoleRealtimeEventCaptureChange();
  }
}

function queueConsoleRealtimeStatsChange() {
  if (consoleRealtimeStatsChangeQueued) {
    return;
  }

  consoleRealtimeStatsChangeQueued = true;
  queueMicrotask(() => {
    consoleRealtimeStatsChangeQueued = false;
    emitConsoleRealtimeStatsChange();
  });
}

function queueConsoleRealtimePayloadCaptureChange() {
  if (consoleRealtimePayloadCaptureChangeQueued) {
    return;
  }

  consoleRealtimePayloadCaptureChangeQueued = true;
  queueMicrotask(() => {
    consoleRealtimePayloadCaptureChangeQueued = false;
    emitConsoleRealtimePayloadCaptureChange();
  });
}

function queueConsoleRealtimeEventCaptureChange() {
  if (consoleRealtimeEventCaptureChangeQueued) {
    return;
  }

  consoleRealtimeEventCaptureChangeQueued = true;
  queueMicrotask(() => {
    consoleRealtimeEventCaptureChangeQueued = false;
    emitConsoleRealtimeEventCaptureChange();
  });
}

function emitConsoleRealtimeStatsChange() {
  consoleRealtimeStatsSnapshot = recomputeConsoleRealtimeStatsSnapshot();
  for (const listener of consoleRealtimeStatsListeners) {
    listener();
  }
}

function emitConsoleRealtimePayloadCaptureChange() {
  consoleRealtimePayloadCaptureSnapshot = recomputeConsoleRealtimePayloadCaptureSnapshot();
  for (const listener of consoleRealtimePayloadCaptureListeners) {
    listener();
  }
}

function emitConsoleRealtimeEventCaptureChange() {
  consoleRealtimeEventCaptureSnapshot = recomputeConsoleRealtimeEventCaptureSnapshot();
  for (const listener of consoleRealtimeEventCaptureListeners) {
    listener();
  }
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
