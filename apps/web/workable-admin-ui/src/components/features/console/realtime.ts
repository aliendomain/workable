"use client";

import {
  HubConnectionState,
} from "@microsoft/signalr";
import { useCallback, useEffect, useMemo, useRef, useState, useSyncExternalStore } from "react";
import type { RealtimePayloadMessage } from "@/components/features/console/realtime-payload";
import {
  consoleRealtimeAutomaticReconnectDelaysMs,
  consoleRealtimeFallbackRestartDelayMs,
  createConsoleRealtimeHubConnection,
  createConsoleRealtimeSharedConnectionKey,
  createConsoleRealtimeSharedViewPool,
  createWorkableRealtimeHubUrl,
  shouldStopConsoleRealtimeRetries,
  type ConsoleRealtimeSharedViewConnectionLease,
} from "@/components/features/console/realtime-view-pool";
import type { Loadable } from "@/components/features/console/types";
import {
  createWorkableRealtimeUrl,
  hasWorkableRequestHeadersTooLargeFailure,
  stopWorkableRequestsForOversizedHeaders,
  type WorkableConnection,
  type WorkableRealtimeEvent,
  type WorkableRealtimeEventBatch,
} from "@/lib/workable";

export { consoleRealtimeAutomaticReconnectDelaysMs, consoleRealtimeFallbackRestartDelayMs };

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

type ConsoleRealtimeSharedViewConnectionLeaseState = {
  connectionKey: string;
  lease: ConsoleRealtimeSharedViewConnectionLease;
};

export type ConsoleRealtimeStatsEntry = {
  connectionId?: string | null;
  connectionKey: string;
  connectionState: string;
  consumerCount: number;
  enabled: boolean;
  hubUrl: string | null;
  id: string;
  kind: "events" | "view";
  lastMessageAt?: number;
  lastMessageLabel?: string;
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

type ConsoleRealtimeWatchRetryDisposition = "retry" | "none";

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
const consoleRealtimeSharedViewPool = createConsoleRealtimeSharedViewPool();

export function useConsoleRealtimeView<T, TMessage extends RealtimePayloadMessage>({
  body,
  captureEnabled,
  clientMethod = "workable.view",
  connectionInstanceKey,
  connection,
  createMessage,
  enabled,
  maxMessages,
  subscriptionErrorMessage = "Realtime view subscription failed.",
  viewName,
  subscription,
  subscriptionInstanceKey,
  watchMethod = "WatchView",
  unwatchMethod = "UnwatchView",
}: {
  body: unknown;
  captureEnabled: boolean;
  clientMethod?: string;
  connectionInstanceKey?: string;
  connection: WorkableConnection | null;
  createMessage: (result: T, nextMessageId: number) => TMessage;
  enabled: boolean;
  maxMessages: number;
  subscriptionErrorMessage?: string;
  subscription?: string;
  subscriptionInstanceKey?: string;
  viewName: string;
  watchMethod?: string;
  unwatchMethod?: string;
}): ConsoleRealtimeViewLoadable<T, TMessage> {
  const subscriptionName = subscription ?? viewName;
  const serverSubscriptionId = useMemo(
    () => {
      void subscriptionInstanceKey;
      return createConsoleRealtimeEntryId("view-subscription");
    },
    [subscriptionInstanceKey]
  );
  const [state, setState] = useState<ConsoleRealtimeViewLoadable<T, TMessage>>({
    clearMessages: () => undefined,
    connectionState: enabled ? "connecting" : "disabled",
    enabled,
    hubUrl: createWorkableRealtimeHubUrl(connection),
    loading: false,
    messages: [],
  });
  const payloadCaptureEntryIdRef = useRef<string>(createConsoleRealtimeEntryId("payload"));
  const statsEntryIdRef = useRef<string>(createConsoleRealtimeStatsEntryId("view"));
  const hasConnection = connection !== null;
  const apiUrl = connection?.apiUrl ?? "";
  const hubUrl = createWorkableRealtimeHubUrl(connection);
  const systemName = connection?.systemName;
  const statsConnectionKey = useMemo(
    () => createConsoleRealtimeSharedConnectionKey(apiUrl, systemName, hubUrl, connectionInstanceKey),
    [apiUrl, connectionInstanceKey, hubUrl, systemName]
  );
  const desiredSharedConnectionKey = useMemo(
    () => enabled && hasConnection && hubUrl ? statsConnectionKey : null,
    [enabled, hasConnection, hubUrl, statsConnectionKey]
  );
  const [sharedConnectionState, setSharedConnectionState] =
    useState<ConsoleRealtimeSharedViewConnectionLeaseState | null>(null);
  const sharedConnection =
    sharedConnectionState?.connectionKey === desiredSharedConnectionKey
      ? sharedConnectionState.lease
      : null;
  const bodyKey = JSON.stringify(body);
  const captureEnabledRef = useRef(captureEnabled);
  const previousCaptureEnabledRef = useRef(captureEnabled);
  const createMessageRef = useRef(createMessage);
  const maxMessagesRef = useRef(maxMessages);
  const messageIdRef = useRef(0);

  useEffect(() => {
    let canceled = false;

    if (!desiredSharedConnectionKey || !hubUrl) {
      queueMicrotask(() => {
        if (!canceled) {
          setSharedConnectionState((current) => (current === null ? current : null));
        }
      });
      return () => {
        canceled = true;
      };
    }

    const lease = consoleRealtimeSharedViewPool.acquire({
      apiUrl,
      connectionKey: desiredSharedConnectionKey,
      hubUrl,
    });
    queueMicrotask(() => {
      if (!canceled) {
        setSharedConnectionState({
          connectionKey: desiredSharedConnectionKey,
          lease,
        });
      }
    });

    return () => {
      canceled = true;
      lease.release();
      setSharedConnectionState((current) =>
        current?.connectionKey === desiredSharedConnectionKey && current.lease === lease
          ? null
          : current
      );
    };
  }, [apiUrl, desiredSharedConnectionKey, hubUrl]);

  useEffect(() => {
    captureEnabledRef.current = captureEnabled;
    createMessageRef.current = createMessage;
    maxMessagesRef.current = maxMessages;
  }, [captureEnabled, createMessage, maxMessages]);

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
  }, [enabled, hasConnection, hubUrl]);

  useEffect(() => {
    const statsEntryId = statsEntryIdRef.current;
    if (!enabled || !hasConnection || !hubUrl || !desiredSharedConnectionKey) {
      deleteConsoleRealtimeStatsEntry(statsEntryId);
      return;
    }

    upsertConsoleRealtimeStatsEntry({
      connectionId: null,
      connectionKey: statsConnectionKey,
      connectionState: "connecting",
      consumerCount: 1,
      enabled,
      hubUrl,
      id: statsEntryId,
      kind: "view",
      label: subscriptionName,
      lifecycleHandlerCount: 0,
      lastMessageAt: undefined,
      lastMessageLabel: undefined,
      onHandlerCount: 0,
      subscriptionCount: 1,
    });

    return () => {
      deleteConsoleRealtimeStatsEntry(statsEntryId);
    };
  }, [
    desiredSharedConnectionKey,
    enabled,
    hasConnection,
    hubUrl,
    statsConnectionKey,
    subscriptionName,
  ]);

  useEffect(() => {
    if (!sharedConnection) {
      return;
    }

    let canceled = false;
    const statsEntryId = statsEntryIdRef.current;
    const applySnapshot = (snapshot: { connectionId?: string | null; connectionState: string; error?: string }) => {
      if (canceled || !enabled) {
        return;
      }

      const currentStatsEntry = consoleRealtimeStatsEntries.get(statsEntryId);
      upsertConsoleRealtimeStatsEntry({
        connectionId: snapshot.connectionId ?? null,
        connectionKey: statsConnectionKey,
        connectionState: snapshot.connectionState,
        consumerCount: 1,
        enabled,
        hubUrl,
        id: statsEntryId,
        kind: "view",
        label: subscriptionName,
        lifecycleHandlerCount: 0,
        lastMessageAt: currentStatsEntry?.lastMessageAt,
        lastMessageLabel: currentStatsEntry?.lastMessageLabel,
        onHandlerCount: 0,
        subscriptionCount: 1,
      });
      setState((current) => ({
        ...current,
        connectionState: snapshot.connectionState,
        enabled,
        error: snapshot.error,
        hubUrl,
        loading: snapshot.connectionState === "connecting" && current.data === undefined,
        refreshing: snapshot.connectionState === "reconnecting" && current.data !== undefined,
      }));
    };

    applySnapshot(sharedConnection.getSnapshot());
    const unsubscribeState = sharedConnection.subscribeState((snapshot) => {
      queueMicrotask(() => applySnapshot(snapshot));
    });
    const unsubscribeMethod = sharedConnection.subscribeMethod<ConsoleRealtimeViewEnvelope<T>>(
      clientMethod,
        (envelope) => {
          if (
            canceled ||
          envelope.subscriptionId !== serverSubscriptionId
          ) {
            return;
          }

        const message = createMessageRef.current(envelope.result, ++messageIdRef.current);
        const receivedAt = Date.now();
        upsertConsoleRealtimeStatsEntry({
          connectionId: sharedConnection.getSnapshot().connectionId ?? null,
          connectionKey: statsConnectionKey,
          connectionState: "connected",
          consumerCount: 1,
          enabled,
          hubUrl,
          id: statsEntryId,
          kind: "view",
          label: subscriptionName,
          lifecycleHandlerCount: 0,
          lastMessageAt: receivedAt,
          lastMessageLabel: envelope.viewName,
          onHandlerCount: 0,
          subscriptionCount: 1,
        });
        setState((current) => ({
          ...current,
          connectionState: "connected",
          data: envelope.result,
          enabled,
          error: undefined,
          hubUrl,
          loading: false,
          messages: captureEnabledRef.current
            ? [message, ...current.messages].slice(0, maxMessagesRef.current)
            : current.messages,
          refreshing: false,
        }));
      }
    );

    return () => {
      canceled = true;
      unsubscribeState();
      unsubscribeMethod();
    };
  }, [clientMethod, enabled, hubUrl, serverSubscriptionId, sharedConnection, statsConnectionKey, subscriptionName]);

  useEffect(() => {
    if (enabled && sharedConnection) {
      sharedConnection.ensureStarted();
    }
  }, [enabled, sharedConnection]);

  useEffect(() => {
    if (!enabled || !sharedConnection || state.connectionState !== "connected") {
      return;
    }

    let canceled = false;
    const watchRetrier = createConsoleRealtimeWatchRetrier({
      invokeWatch: () =>
        sharedConnection.invoke(
          watchMethod,
          serverSubscriptionId,
          viewName,
          JSON.parse(bodyKey),
          systemName ?? null
        ),
      isCanceled: () => canceled,
      isConnected: () => sharedConnection.getSnapshot().connectionState === "connected",
      onBeforeAttempt: () => {
        queueMicrotask(() => {
          if (!canceled) {
            setState((current) => ({
              ...current,
              connectionState: "connected",
              error: undefined,
              loading: current.data === undefined,
              refreshing: current.data !== undefined,
            }));
          }
        });
      },
      onFailure: (error) => {
        setState((current) => ({
          ...current,
          error: getConsoleRealtimeErrorMessage(error, subscriptionErrorMessage),
          loading: false,
          refreshing: false,
        }));
        return "retry";
      },
    });

    void watchRetrier.run();

    return () => {
      canceled = true;
      watchRetrier.clear();
    };
  }, [bodyKey, enabled, serverSubscriptionId, sharedConnection, state.connectionState, subscriptionErrorMessage, systemName, unwatchMethod, viewName, watchMethod]);

  useEffect(() => {
    if (!enabled || !sharedConnection) {
      return;
    }

    return () => {
      void sharedConnection
        .invoke(unwatchMethod, serverSubscriptionId, systemName ?? null)
        .catch(() => undefined);
    };
  }, [enabled, serverSubscriptionId, sharedConnection, systemName, unwatchMethod]);

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
      const currentStatsEntry = consoleRealtimeStatsEntries.get(statsEntryId);
      upsertConsoleRealtimeStatsEntry({
        connectionId: hubConnection.connectionId ?? null,
        connectionKey: statsConnectionKey,
        connectionState,
        consumerCount: 1,
        enabled,
        hubUrl,
        id: statsEntryId,
        kind: "events",
        label: debugLabel ?? watchMethod,
        lifecycleHandlerCount: 3,
        lastMessageAt: currentStatsEntry?.lastMessageAt,
        lastMessageLabel: currentStatsEntry?.lastMessageLabel,
        onHandlerCount: 2,
        subscriptionCount: 1,
      });
    };

    queueMicrotask(() => {
      if (!canceled && !hasWorkableRequestHeadersTooLargeFailure()) {
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
      if (shouldStopConsoleRealtimeRetries(error)) {
        if (retryTimer) {
          clearTimeout(retryTimer);
          retryTimer = null;
        }
        updateStats("disconnected");
        setState((current) => ({
          ...current,
          connectionState: "disconnected",
          error: error instanceof Error ? error.message : "Request headers are too large.",
          loading: false,
        }));
        return;
      }

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
    const watchRetrier = createConsoleRealtimeWatchRetrier({
      invokeWatch,
      isCanceled: () => canceled,
      isConnected: () => hubConnection.state === HubConnectionState.Connected,
      onSuccess: () => {
        updateStats("connected");
        setState((current) => ({
          ...current,
          connectionState: "connected",
          error: undefined,
          loading: false,
        }));
      },
      onFailure: (error) => {
        if (isExpectedConsoleRealtimeDisconnect(error)) {
          scheduleRestart(error);
          return "none";
        }

        updateStats("error");
        setState((current) => ({
          ...current,
          connectionState: "error",
          error: getConsoleRealtimeErrorMessage(error, subscriptionErrorMessage),
          loading: false,
        }));
        return "retry";
      },
    });
    const startConnection = () => {
      if (hasWorkableRequestHeadersTooLargeFailure()) {
        scheduleRestart(stopWorkableRequestsForOversizedHeaders());
        return;
      }

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
        .then(() => watchRetrier.run())
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
        upsertConsoleRealtimeStatsEntry({
          ...(consoleRealtimeStatsEntries.get(statsEntryIdRef.current) ?? {
            connectionId: hubConnection.connectionId ?? null,
            connectionKey: statsConnectionKey,
            connectionState: "connected",
            consumerCount: 1,
            enabled,
            hubUrl,
            id: statsEntryIdRef.current,
            kind: "events" as const,
            label: debugLabel ?? watchMethod,
            lifecycleHandlerCount: 3,
            onHandlerCount: 2,
            subscriptionCount: 1,
          }),
          connectionId: hubConnection.connectionId ?? null,
          connectionState: "connected",
          lastMessageAt: message.receivedAt,
          lastMessageLabel: workEvent.eventType,
        });
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
        upsertConsoleRealtimeStatsEntry({
          ...(consoleRealtimeStatsEntries.get(statsEntryIdRef.current) ?? {
            connectionId: hubConnection.connectionId ?? null,
            connectionKey: statsConnectionKey,
            connectionState: "connected",
            consumerCount: 1,
            enabled,
            hubUrl,
            id: statsEntryIdRef.current,
            kind: "events" as const,
            label: debugLabel ?? watchMethod,
            lifecycleHandlerCount: 3,
            onHandlerCount: 2,
            subscriptionCount: 1,
          }),
          connectionId: hubConnection.connectionId ?? null,
          connectionState: "connected",
          lastMessageAt: message.receivedAt,
          lastMessageLabel: batch.events[0]?.eventType
            ? `${batch.events[0].eventType}${batch.events.length > 1 ? ` +${batch.events.length - 1}` : ""}`
            : "batch",
        });
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
      void watchRetrier.run();
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
      watchRetrier.clear();
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

  const physicalConnectionCount = new Set(
    connections.map((connection) => `${connection.kind}:${connection.connectionKey}`)
  ).size;
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

function createConsoleRealtimeWatchRetrier({
  invokeWatch,
  isCanceled,
  isConnected,
  onBeforeAttempt,
  onFailure,
  onSuccess,
}: {
  invokeWatch: () => Promise<unknown>;
  isCanceled: () => boolean;
  isConnected: () => boolean;
  onBeforeAttempt?: () => void;
  onFailure: (error: unknown) => ConsoleRealtimeWatchRetryDisposition | void;
  onSuccess?: () => void;
}) {
  let inFlight = false;
  let retryTimer: ReturnType<typeof setTimeout> | null = null;

  const clear = () => {
    if (retryTimer) {
      clearTimeout(retryTimer);
      retryTimer = null;
    }
  };

  const run = async () => {
    if (isCanceled()) {
      return;
    }

    clear();
    if (inFlight) {
      return;
    }

    inFlight = true;
    onBeforeAttempt?.();

    try {
      await invokeWatch();
      if (!isCanceled()) {
        onSuccess?.();
      }
    } catch (error) {
      if (isCanceled()) {
        return;
      }

      const disposition = onFailure(error) ?? "retry";
      if (disposition !== "retry" || !isConnected()) {
        return;
      }

      if (!retryTimer) {
        retryTimer = setTimeout(() => {
          retryTimer = null;
          if (isCanceled() || !isConnected()) {
            return;
          }

          void run();
        }, consoleRealtimeFallbackRestartDelayMs);
      }
    } finally {
      inFlight = false;
    }
  };

  return {
    clear,
    run,
  };
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
