"use client";

import {
  createContext,
  useCallback,
  useContext,
  useEffect,
  useMemo,
  useRef,
  useState,
  type ReactNode,
} from "react";
import { useConsoleRealtimeView, type ConsoleRealtimeViewLoadable } from "@/components/features/console/realtime";
import { createRealtimePayloadMessage, type RealtimePayloadMessage } from "@/components/features/console/realtime-payload";
import type { WorkableConnection } from "@/lib/workable";

export type ConsolePageRealtimeViewDescriptor = {
  body: unknown;
  captureEnabled: boolean;
  connection: WorkableConnection | null;
  connectionInstanceKey?: string;
  enabled: boolean;
  maxMessages: number;
  subscription?: string;
  viewName: string;
};

type ConsolePageRealtimeViewRegistration = {
  active: boolean;
  descriptor: ConsolePageRealtimeViewDescriptor | null;
  sequence: number;
};

type ConsolePageRealtimeViewContextValue = {
  register: (id: string, active: boolean, descriptor: ConsolePageRealtimeViewDescriptor | null) => void;
  resolvedDescriptorId: string | null;
  resolvedView: ConsoleRealtimeViewLoadable<unknown, RealtimePayloadMessage>;
  unregister: (id: string) => void;
};

const ConsolePageRealtimeViewContext = createContext<ConsolePageRealtimeViewContextValue | null>(null);

export function ConsolePageRealtimeViewProvider({ children }: { children: ReactNode }) {
  const sequenceRef = useRef(0);
  const [registrations, setRegistrations] = useState<Record<string, ConsolePageRealtimeViewRegistration>>({});

  const register = useCallback((id: string, active: boolean, descriptor: ConsolePageRealtimeViewDescriptor | null) => {
    const sequence = ++sequenceRef.current;
    setRegistrations((current) => {
      const existing = current[id];
      if (existing?.active === active && existing.descriptor === descriptor) {
        return current;
      }

      return {
        ...current,
        [id]: {
          active,
          descriptor,
          sequence,
        },
      };
    });
  }, []);

  const unregister = useCallback((id: string) => {
    setRegistrations((current) => {
      if (!(id in current)) {
        return current;
      }

      const next = { ...current };
      delete next[id];
      return next;
    });
  }, []);

  const resolvedRegistration = useMemo(
    () =>
      Object.entries(registrations)
        .filter(([, entry]) => entry.active && entry.descriptor)
        .sort(([, left], [, right]) => right.sequence - left.sequence)[0] ?? null,
    [registrations]
  );

  const resolvedDescriptorId = resolvedRegistration?.[0] ?? null;
  const resolvedDescriptor = resolvedRegistration?.[1].descriptor ?? null;
  const resolvedView = useConsoleRealtimeView<unknown, RealtimePayloadMessage>({
    body: resolvedDescriptor?.body ?? null,
    captureEnabled: resolvedDescriptor?.captureEnabled ?? false,
    connection: resolvedDescriptor?.connection ?? null,
    connectionInstanceKey: resolvedDescriptor?.connectionInstanceKey,
    createMessage: (result, nextMessageId) => {
      const viewName = resolvedDescriptor?.viewName ?? "page";
      const subscription = resolvedDescriptor?.subscription ?? viewName;
      const payloadJson = JSON.stringify(result);
      return createRealtimePayloadMessage(
        result,
        payloadJson,
        `${subscription}:${nextMessageId}`,
        viewName,
        subscription,
        resolvedDescriptor?.connection ?? null
      );
    },
    enabled: resolvedDescriptor?.enabled ?? false,
    maxMessages: resolvedDescriptor?.maxMessages ?? 100,
    subscription: resolvedDescriptor?.subscription ?? resolvedDescriptor?.viewName ?? "page",
    viewName: resolvedDescriptor?.viewName ?? "page",
  });

  const value = useMemo(
    () => ({
      register,
      resolvedDescriptorId,
      resolvedView,
      unregister,
    }),
    [register, resolvedDescriptorId, resolvedView, unregister]
  );

  return (
    <ConsolePageRealtimeViewContext.Provider value={value}>
      {children}
    </ConsolePageRealtimeViewContext.Provider>
  );
}

export function useRegisterConsolePageRealtimeView({
  active = true,
  descriptor,
  id,
}: {
  active?: boolean;
  descriptor: ConsolePageRealtimeViewDescriptor | null;
  id: string;
}) {
  const { register, unregister } = useConsolePageRealtimeViewContext();

  useEffect(() => {
    register(id, active, descriptor);
    return () => unregister(id);
  }, [active, descriptor, id, register, unregister]);
}

export function useConsolePageRealtimeView<T>(id: string): ConsoleRealtimeViewLoadable<T, RealtimePayloadMessage> {
  const { resolvedDescriptorId, resolvedView } = useConsolePageRealtimeViewContext();

  return useMemo(() => {
    if (resolvedDescriptorId !== id) {
      return createDisabledConsolePageRealtimeView<T>();
    }

    return resolvedView as ConsoleRealtimeViewLoadable<T, RealtimePayloadMessage>;
  }, [id, resolvedDescriptorId, resolvedView]);
}

export function useResolvedConsolePageRealtimeView<T>(): ConsoleRealtimeViewLoadable<T, RealtimePayloadMessage> {
  return useConsolePageRealtimeViewContext().resolvedView as ConsoleRealtimeViewLoadable<T, RealtimePayloadMessage>;
}

export function useResolvedConsolePageRealtimeViewDescriptorId() {
  return useConsolePageRealtimeViewContext().resolvedDescriptorId;
}

function useConsolePageRealtimeViewContext() {
  const context = useContext(ConsolePageRealtimeViewContext);
  if (!context) {
    throw new Error("Console page realtime view must be used within a provider.");
  }

  return context;
}

export function createDisabledConsolePageRealtimeView<T>(): ConsoleRealtimeViewLoadable<T, RealtimePayloadMessage> {
  return {
    clearMessages: () => undefined,
    connectionState: "disabled",
    enabled: false,
    hubUrl: null,
    loading: false,
    messages: [],
  };
}
