"use client";

import {
  HubConnectionBuilder,
  HubConnectionState,
  LogLevel,
  type HubConnection,
  type IHttpConnectionOptions,
} from "@microsoft/signalr";
import { useCallback, useEffect, useRef, useState } from "react";
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

export type ConsoleRealtimeViewLoadable<T, TMessage> = Loadable<T> & {
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

export function useConsoleRealtimeView<T, TMessage>({
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
  const [state, setState] = useState<ConsoleRealtimeViewLoadable<T, TMessage>>({
    clearMessages: () => undefined,
    connectionState: enabled ? "connecting" : "disabled",
    enabled,
    hubUrl: connection ? createWorkableRealtimeUrl(connection) : null,
    loading: false,
    messages: [],
  });
  const hubConnectionRef = useRef<HubConnection | null>(null);
  const hasConnection = connection !== null;
  const apiUrl = connection?.apiUrl ?? "";
  const hubUrl = connection ? createWorkableRealtimeUrl(connection) : null;
  const systemName = connection?.systemName;
  const bodyKey = JSON.stringify(body);
  const bodyKeyRef = useRef(bodyKey);
  const captureEnabledRef = useRef(captureEnabled);
  const createMessageRef = useRef(createMessage);
  const maxMessagesRef = useRef(maxMessages);
  const messageIdRef = useRef(0);
  const systemNameRef = useRef(systemName);

  useEffect(() => {
    bodyKeyRef.current = bodyKey;
    captureEnabledRef.current = captureEnabled;
    createMessageRef.current = createMessage;
    maxMessagesRef.current = maxMessages;
    systemNameRef.current = systemName;
  }, [bodyKey, captureEnabled, createMessage, maxMessages, systemName]);

  const clearMessages = useCallback(() => {
    setState((current) =>
      current.messages.length === 0 ? current : { ...current, messages: [] }
    );
  }, []);

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
    queueMicrotask(() => {
      if (!canceled) {
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
          setState((current) => ({
            ...current,
            connectionState: current.data === undefined ? "connecting" : "reconnecting",
            loading: current.data === undefined,
            refreshing: current.data !== undefined,
          }));
        }
      });
      void hubConnection
        .start()
        .then(() => subscribe())
        .then(() => {
          if (!canceled) {
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

    hubConnection.on("workable.view", (result: T) => {
      if (!canceled) {
        const message = createMessageRef.current(result, ++messageIdRef.current);
        setState((current) => ({
          ...current,
          connectionState: "connected",
          data: result,
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
        setState((current) => ({
          ...current,
          connectionState: "reconnecting",
          refreshing: current.data !== undefined,
        }));
      }
    });
    hubConnection.onreconnected(() => {
      if (!canceled) {
        setState((current) => ({
          ...current,
          connectionState: "connected",
        }));
      }
      void subscribe().catch((error) => {
        if (!canceled && !isExpectedConsoleRealtimeDisconnect(error)) {
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
      void hubConnection.stop().catch(() => undefined);
    };
  }, [apiUrl, enabled, hasConnection, hubUrl, subscriptionName, systemName, viewName]);

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
      .invoke("WatchView", viewName, JSON.parse(bodyKey), systemName ?? null)
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
        void hubConnection.invoke("UnwatchView", viewName, systemName ?? null).catch(() => undefined);
      }
    };
  }, [bodyKey, enabled, systemName, viewName]);

  return { ...state, clearMessages };
}

export function useConsoleRealtimeEventStream<TMessage>({
  connection,
  createBatchMessage,
  createSingleMessage,
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
  connection: WorkableConnection | null;
  createBatchMessage: (batch: WorkableRealtimeEventBatch, nextMessageId: number) => TMessage;
  createSingleMessage: (workEvent: WorkableRealtimeEvent, nextMessageId: number) => TMessage;
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
  const [state, setState] = useState<ConsoleRealtimeEventLoadable<TMessage>>({
    clearMessages: () => undefined,
    connectionState: enabled ? "connecting" : "disabled",
    enabled,
    hubUrl: connection ? createWorkableRealtimeUrl(connection) : null,
    loading: false,
    messages: [],
  });
  const hasConnection = connection !== null;
  const apiUrl = connection?.apiUrl ?? "";
  const hubUrl = connection ? createWorkableRealtimeUrl(connection) : null;
  const systemName = connection?.systemName;
  const createBatchMessageRef = useRef(createBatchMessage);
  const createSingleMessageRef = useRef(createSingleMessage);
  const maxMessagesRef = useRef(maxMessages);
  const messageIdRef = useRef(0);

  useEffect(() => {
    createBatchMessageRef.current = createBatchMessage;
    createSingleMessageRef.current = createSingleMessage;
    maxMessagesRef.current = maxMessages;
  }, [createBatchMessage, createSingleMessage, maxMessages]);

  const clearMessages = useCallback(() => {
    setState((current) =>
      current.messages.length === 0 ? current : { ...current, messages: [] }
    );
  }, []);

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
    queueMicrotask(() => {
      if (!canceled) {
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
        watchArgument,
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
          setState((current) => ({
            ...current,
            connectionState: current.messages.length === 0 ? "connecting" : "reconnecting",
            loading: current.messages.length === 0,
          }));
        }
      });
      void hubConnection
        .start()
        .then(() => invokeWatch())
        .then(() => {
          if (!canceled) {
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
        setState((current) => ({
          ...current,
          connectionState: "connected",
          enabled,
          hubUrl,
          loading: false,
          messages: [message, ...current.messages].slice(0, maxMessagesRef.current),
        }));
      }
    });
    hubConnection.on("workable.events", (batch: WorkableRealtimeEventBatch) => {
      if (!canceled) {
        const message = createBatchMessageRef.current(batch, ++messageIdRef.current);
        setState((current) => ({
          ...current,
          connectionState: "connected",
          enabled,
          hubUrl,
          loading: false,
          messages: [message, ...current.messages].slice(0, maxMessagesRef.current),
        }));
      }
    });
    hubConnection.onreconnecting(() => {
      if (!canceled) {
        setState((current) => ({
          ...current,
          connectionState: "reconnecting",
        }));
      }
    });
    hubConnection.onreconnected(() => {
      if (!canceled) {
        setState((current) => ({
          ...current,
          connectionState: "connected",
        }));
      }
      void invokeWatch().catch((error) => {
        if (!canceled && !isExpectedConsoleRealtimeDisconnect(error)) {
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
      if (unwatchMethod && hubConnection.state === HubConnectionState.Connected) {
        void hubConnection
          .invoke(unwatchMethod, watchArgument, systemName ?? null)
          .catch(() => undefined);
      }
      void hubConnection.stop().catch(() => undefined);
    };
  }, [
    apiUrl,
    enabled,
    hasConnection,
    hubUrl,
    subscriptionErrorMessage,
    systemName,
    unwatchMethod,
    watchArgument,
    watchArgumentKey,
    watchMethod,
    watchReady,
    watchStoppedMessage,
  ]);

  return { ...state, clearMessages };
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
