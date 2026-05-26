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

export type ConsoleHeaderRealtimeCapability = {
  connectionState: string;
  enabled: boolean;
  menuItems?: ConsoleHeaderMenuItem[] | null;
  title?: string;
};

export type ConsoleHeaderRefreshCapability = {
  ariaLabel?: string;
  disabled?: boolean;
  hidden?: boolean;
  onRefresh?: () => void;
  refreshing?: boolean;
  title?: string;
};

export type ConsoleHeaderMenuItem = {
  active?: boolean;
  disabled?: boolean;
  icon?: ReactNode;
  id: string;
  label: string;
  onSelect: () => void;
};

export type ConsoleHeaderCapabilities = {
  realtime?: ConsoleHeaderRealtimeCapability | null;
  refresh?: ConsoleHeaderRefreshCapability | null;
};

type ConsoleHeaderRegistration = {
  active: boolean;
  capabilities: ConsoleHeaderCapabilities | null;
  sequence: number;
};

type ConsoleHeaderCapabilitiesContextValue = {
  register: (
    id: string,
    active: boolean,
    capabilities: ConsoleHeaderCapabilities | null
  ) => void;
  resolvedCapabilities: ConsoleHeaderCapabilities | null;
  unregister: (id: string) => void;
};

const ConsoleHeaderCapabilitiesContext = createContext<ConsoleHeaderCapabilitiesContextValue | null>(null);

export function ConsoleHeaderCapabilitiesProvider({
  children,
  defaultCapabilities = null,
}: {
  children: ReactNode;
  defaultCapabilities?: ConsoleHeaderCapabilities | null;
}) {
  const sequenceRef = useRef(0);
  const [registrations, setRegistrations] = useState<Record<string, ConsoleHeaderRegistration>>({});

  const register = useCallback((
    id: string,
    active: boolean,
    capabilities: ConsoleHeaderCapabilities | null
  ) => {
    const sequence = ++sequenceRef.current;
    setRegistrations((current) => {
      const existing = current[id];
      if (existing?.active === active && existing.capabilities === capabilities) {
        return current;
      }

      return {
        ...current,
        [id]: {
          active,
          capabilities,
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

  const resolvedCapabilities = useMemo(() => {
    const activeRegistration = Object.values(registrations)
      .filter((entry) => entry.active && entry.capabilities)
      .sort((left, right) => right.sequence - left.sequence)[0];

    return mergeConsoleHeaderCapabilities(defaultCapabilities, activeRegistration?.capabilities ?? null);
  }, [defaultCapabilities, registrations]);

  const value = useMemo(
    () => ({
      register,
      resolvedCapabilities,
      unregister,
    }),
    [register, resolvedCapabilities, unregister]
  );

  return (
    <ConsoleHeaderCapabilitiesContext.Provider value={value}>
      {children}
    </ConsoleHeaderCapabilitiesContext.Provider>
  );
}

export function useRegisterConsoleHeaderCapabilities({
  active = true,
  capabilities,
  id,
}: {
  active?: boolean;
  capabilities: ConsoleHeaderCapabilities | null;
  id: string;
}) {
  const { register, unregister } = useConsoleHeaderCapabilitiesContext();

  useEffect(() => {
    register(id, active, capabilities);
    return () => unregister(id);
  }, [active, capabilities, id, register, unregister]);
}

export function useResolvedConsoleHeaderCapabilities() {
  return useConsoleHeaderCapabilitiesContext().resolvedCapabilities;
}

function useConsoleHeaderCapabilitiesContext() {
  const context = useContext(ConsoleHeaderCapabilitiesContext);
  if (!context) {
    throw new Error("Console header capabilities must be used within a provider.");
  }

  return context;
}

function mergeConsoleHeaderCapabilities(
  defaultCapabilities: ConsoleHeaderCapabilities | null | undefined,
  activeCapabilities: ConsoleHeaderCapabilities | null | undefined
) {
  if (!defaultCapabilities && !activeCapabilities) {
    return null;
  }

  return {
    realtime: activeCapabilities?.realtime ?? defaultCapabilities?.realtime ?? null,
    refresh: mergeConsoleHeaderRefreshCapability(
      defaultCapabilities?.refresh ?? null,
      activeCapabilities?.refresh ?? null
    ),
  } satisfies ConsoleHeaderCapabilities;
}

function mergeConsoleHeaderRefreshCapability(
  defaultRefresh: ConsoleHeaderRefreshCapability | null,
  activeRefresh: ConsoleHeaderRefreshCapability | null
) {
  if (!defaultRefresh && !activeRefresh) {
    return null;
  }

  return {
    ariaLabel: activeRefresh?.ariaLabel ?? defaultRefresh?.ariaLabel,
    disabled: activeRefresh?.disabled ?? defaultRefresh?.disabled,
    hidden: activeRefresh?.hidden ?? defaultRefresh?.hidden,
    onRefresh: activeRefresh?.onRefresh ?? defaultRefresh?.onRefresh,
    refreshing: activeRefresh?.refreshing ?? defaultRefresh?.refreshing,
    title: activeRefresh?.title ?? defaultRefresh?.title,
  } satisfies ConsoleHeaderRefreshCapability;
}
