"use client";

import { Loader2, RefreshCw } from "lucide-react";
import { useCallback, useEffect, useState } from "react";
import { FormField } from "@/components/features/console/form-controls";
import type {
  WorkableHostConnection,
  WorkableSystemConnection,
} from "@/components/features/console/types";
import { Button } from "@/components/ui/button";
import { Dialog, DialogContent, DialogDescription, DialogHeader, DialogTitle } from "@/components/ui/dialog";
import { Input } from "@/components/ui/input";
import { StackedSkeleton } from "@/components/features/console/stacked-skeleton";
import { ErrorBanner } from "@/components/workable/console/feedback-panel";
import {
  WorkableApiError,
  workableFetch,
  type WorkSystemAccessSummary,
  type WorkableHttpHostDescriptor,
  type WorkableHttpSystemDescriptor,
} from "@/lib/workable";

export function ServerDialog({
  mode,
  open,
  onOpenChange,
  onSave,
  host,
}: {
  mode: "add" | "edit";
  open: boolean;
  onOpenChange: (open: boolean) => void;
  onSave: (host: WorkableHostConnection) => void;
  host?: WorkableHostConnection;
}) {
  const [name, setName] = useState(host?.name ?? "");
  const [apiUrl, setApiUrl] = useState(host?.apiUrl ?? "");
  const [discovered, setDiscovered] = useState<WorkableHttpSystemDescriptor[]>(
    () => host?.systems.map(createDiscoveredSystemFromStored) ?? []
  );
  const [discoveredRealtime, setDiscoveredRealtime] = useState<WorkableHttpHostDescriptor["capabilities"]["realtime"] | null>(
    () =>
      host
        ? {
            enabled: host.realtimeEnabled,
            hubPath: host.realtimeHubPath ?? null,
            transport: host.realtimeTransport ?? null,
          }
        : null
  );
  const [selectedSystemIds, setSelectedSystemIds] = useState<Set<string>>(
    () => new Set(host?.systems.map((system) => system.systemName ?? "") ?? [])
  );
  const [isLoadingSystems, setIsLoadingSystems] = useState(false);
  const [hasLoadedSystems, setHasLoadedSystems] = useState(false);
  const [systemsError, setSystemsError] = useState<string | undefined>();

  const fetchSystems = useCallback(async () => {
    if (!apiUrl.trim()) {
      return;
    }

    setIsLoadingSystems(true);
    setSystemsError(undefined);

    try {
      const result = await discoverHost(apiUrl);
      const systems = result.systems ?? [];
      setHasLoadedSystems(true);
      setApiUrl(result.apiUrl);
      setDiscovered(systems);
      setDiscoveredRealtime(result.capabilities.realtime);

      setSelectedSystemIds((current) => {
        if (current.size > 0) {
          return current;
        }

        return new Set(systems.map(getSystemStorageKey));
      });
    } catch (caught) {
      setHasLoadedSystems(false);
      setDiscovered([]);
      setSystemsError(
        caught instanceof Error ? caught.message : "Unable to load Workable systems."
      );
    } finally {
      setIsLoadingSystems(false);
    }
  }, [apiUrl]);

  useEffect(() => {
    if (!open || mode !== "edit" || !host?.apiUrl) {
      return;
    }

    const timeoutId = window.setTimeout(() => {
      void fetchSystems();
    }, 0);

    return () => window.clearTimeout(timeoutId);
  }, [fetchSystems, host?.apiUrl, mode, open]);

  const save = () => {
    const selected = discovered.filter((system) =>
      selectedSystemIds.has(getSystemStorageKey(system))
    );
    const hasSelectedDiscoveredSystem = selected.length > 0;

    if (!hasSelectedDiscoveredSystem) {
      setSystemsError("Select at least one Workable system.");
      return;
    }

    const hostId = host?.id ?? createServerId();
    onSave({
      id: hostId,
      name: name.trim() || "Workable host",
      apiUrl: apiUrl.trim(),
      realtimeEnabled: Boolean(discoveredRealtime?.enabled),
      realtimeHubPath: discoveredRealtime?.hubPath ?? null,
      realtimeTransport: discoveredRealtime?.transport ?? null,
      systems: selected.map((system) =>
        createStoredSystem(
          hostId,
          system,
          findStoredSystemByKey(host, system)
        )
      ),
    });
    onOpenChange(false);
  };

  const toggleSelectedSystem = (system: WorkableHttpSystemDescriptor, checked: boolean) => {
    const key = getSystemStorageKey(system);
    setSelectedSystemIds((current) => {
      const next = new Set(current);
      if (checked) {
        next.add(key);
      } else {
        next.delete(key);
      }
      return next;
    });
  };

  return (
    <Dialog onOpenChange={onOpenChange} open={open}>
      <DialogContent className="sm:max-w-2xl">
        <DialogHeader>
          <DialogTitle>{mode === "add" ? "Add server" : "Edit server"}</DialogTitle>
          <DialogDescription>
            Discover Workable systems exposed by a host and add selected systems to the tree.
          </DialogDescription>
        </DialogHeader>
        <div className="space-y-4">
          <FormField label="Host name" maxWidth="none">
            <Input
              onChange={(event) => setName(event.target.value)}
              value={name}
            />
          </FormField>
          <FormField label="HTTP API URL" maxWidth="none">
            <div className="flex gap-2">
              <Input
                onChange={(event) => {
                  setApiUrl(event.target.value);
                  setDiscovered([]);
                  setDiscoveredRealtime(null);
                  setSelectedSystemIds(new Set());
                  setHasLoadedSystems(false);
                  setSystemsError(undefined);
                }}
                value={apiUrl}
              />
              <Button
                disabled={isLoadingSystems || !apiUrl.trim()}
                onClick={() => void fetchSystems()}
                type="button"
                variant="outline"
              >
                {isLoadingSystems ? (
                  <Loader2 className="size-4 animate-spin" />
                ) : (
                  <RefreshCw className="size-4" />
                )}
                Load systems
              </Button>
            </div>
          </FormField>
          {systemsError && (
            <ErrorBanner key={systemsError} message={systemsError} title="Discovery failed" />
          )}
          <div className="rounded-lg border">
            <div className="border-b px-3 py-2 font-medium text-muted-foreground text-xs">
              <span>System</span>
            </div>
            <div className="max-h-72 overflow-y-auto">
              {isLoadingSystems ? (
                <div className="p-3">
                  <StackedSkeleton count={3} />
                </div>
              ) : discovered.length === 0 ? (
                <div className="p-6 text-center text-muted-foreground text-sm">
                  {hasLoadedSystems && !systemsError
                    ? "Connected to the host, but this signed-in user does not have Connect permission for any Workable systems exposed there."
                    : "Enter a URL and load systems."}
                </div>
              ) : (
                discovered.map((system) => {
                  const key = getSystemStorageKey(system);
                  const accessBadges = getSystemAccessBadges(system.access);

                  return (
                    <div
                      className="border-b px-3 py-3 last:border-b-0"
                      key={key}
                    >
                      <label className="flex min-w-0 items-start gap-3">
                        <input
                          checked={selectedSystemIds.has(key)}
                          className="mt-0.5 size-4 rounded border"
                          onChange={(event) => toggleSelectedSystem(system, event.target.checked)}
                          type="checkbox"
                        />
                        <span className="min-w-0">
                          <span className="block truncate font-medium text-sm">
                            {getSystemDisplayName(system)}
                          </span>
                          {getSystemSecondaryText(system) && (
                            <span className="block text-muted-foreground text-xs">
                              {getSystemSecondaryText(system)}
                            </span>
                          )}
                          <span className="mt-2 flex flex-wrap items-center gap-1.5">
                            <span className="mr-1 text-[11px] font-medium uppercase tracking-[0.12em] text-muted-foreground/80">
                              Permissions
                            </span>
                            {accessBadges.map((badge) => (
                              <span
                                className="rounded-full border border-border/70 bg-muted/40 px-2 py-0.5 text-[11px] text-muted-foreground"
                                key={badge}
                              >
                                {badge}
                              </span>
                            ))}
                          </span>
                        </span>
                      </label>
                    </div>
                  );
                })
              )}
            </div>
          </div>
          <div className="flex justify-end gap-2">
            <Button onClick={() => onOpenChange(false)} variant="outline">
              Cancel
            </Button>
            <Button
              disabled={
                !apiUrl.trim() ||
                isLoadingSystems ||
                discovered.length === 0 ||
                !discovered.some((system) => selectedSystemIds.has(getSystemStorageKey(system)))
              }
              onClick={save}
            >
              Save
            </Button>
          </div>
        </div>
      </DialogContent>
    </Dialog>
  );
}

function createStoredSystem(
  hostId: string,
  system: WorkableHttpSystemDescriptor,
  existingSystem?: WorkableSystemConnection
): WorkableSystemConnection {
  const key = getSystemStorageKey(system);

  return {
    id: existingSystem?.id ?? `${hostId}-${key || "default"}`,
    hostId,
    name: getSystemDisplayName(system),
    systemName: normalizeOptional(system.name),
    access: system.access,
    persistentCoordinationAvailable: system.capabilities.persistentCoordinationAvailable,
    state: system.state,
  };
}

function findStoredSystemByKey(
  host: WorkableHostConnection | undefined,
  system: WorkableHttpSystemDescriptor
) {
  const key = getSystemStorageKey(system);
  return host?.systems.find(
    (storedSystem) => getSystemStorageKey(createDiscoveredSystemFromStored(storedSystem)) === key
  );
}

export function getSystemAccessBadges(access: WorkSystemAccessSummary) {
  const badges = ["Connect"];

  if (access.isSystemAdministrator) {
    badges.push("System admin");
  }

  if (access.isWorkAdministrator) {
    badges.push("Work admin");
  }

  if (access.canViewDiagnostics) {
    badges.push("Diagnostics");
  }

  if (access.canControlSystem) {
    badges.push("Control system");
  }

  const readBadge = getWorkAccessBadge(
    "Read",
    access.readableDefinitionCount,
    access.totalDefinitionCount,
    access.canReadAllWork
  );
  if (readBadge) {
    badges.push(readBadge);
  }

  const operateBadge = getWorkAccessBadge(
    "Operate",
    access.operableDefinitionCount,
    access.totalDefinitionCount,
    access.canOperateAllWork
  );
  if (operateBadge) {
    badges.push(operateBadge);
  }

  if (access.readableDefinitionCount === 0 && access.operableDefinitionCount === 0) {
    badges.push("No work access");
  }

  return badges;
}

export function createUnknownAccessSummary(): WorkSystemAccessSummary {
  return {
    canConnect: true,
    isSystemAdministrator: false,
    isWorkAdministrator: false,
    canViewDiagnostics: false,
    canControlSystem: false,
    canReadAllWork: false,
    canOperateAllWork: false,
    totalDefinitionCount: 0,
    readableDefinitionCount: 0,
    operableDefinitionCount: 0,
  };
}

export function getWorkAccessBadge(
  label: "Read" | "Operate",
  count: number,
  total: number,
  allAccess: boolean
) {
  if (total === 0 || count === 0) {
    return null;
  }

  if (allAccess || count >= total) {
    return `${label} all work`;
  }

  return `${label} ${formatCompactCount(count)}/${formatCompactCount(total)} defs`;
}

export function formatCompactCount(value: number) {
  return new Intl.NumberFormat("en-US", { notation: value >= 1000 ? "compact" : "standard" }).format(value);
}

export async function discoverHost(apiUrl: string): Promise<WorkableHttpHostDescriptor & { apiUrl: string }> {
  const candidates = createWorkableApiUrlCandidates(apiUrl);
  let lastError: unknown;

  for (const candidate of candidates) {
    try {
      const result = await workableFetch<WorkableHttpHostDescriptor>(
        {
          apiUrl: candidate,
        },
        "host"
      );
      if (!isWorkableHostResponse(result)) {
        continue;
      }

      return {
        ...result,
        apiUrl: candidate,
      };
    } catch (caught) {
      lastError = caught;
    }
  }

  const attempted = candidates.map(formatHostEndpoint).join(", ");
  if (lastError instanceof WorkableApiError) {
    if (lastError.status === 401) {
      throw new Error(
        "This Workable host requires authentication before its systems can be discovered. Sign in and try again."
      );
    }

    if (lastError.status === 403) {
      throw new Error(
        "This user cannot discover systems on that host. Workable Connect access is required to add the server."
      );
    }

    if (lastError.status === 404) {
      throw new Error(
        `No Workable host endpoint was found at that address. Make sure the URL points to the Workable HTTP API root, usually ending in /workable. Tried ${attempted}.`
      );
    }
  }

  const detail =
    lastError instanceof Error && lastError.message !== "fetch failed"
      ? ` ${lastError.message}`
      : "";

  throw new Error(
    `Unable to reach the Workable API.${detail} Tried ${attempted}. Check that the protocol and port match the server.`
  );
}

export function createWorkableApiUrlCandidates(value: string) {
  const trimmed = value.trim().replace(/\/+$/, "");
  if (!trimmed) {
    return [];
  }

  try {
    const entered = new URL(trimmed);
    const candidates: string[] = [];
    const addCandidate = (url: URL) => {
      const candidate = formatWorkableApiUrl(url);
      if (!candidates.includes(candidate)) {
        candidates.push(candidate);
      }
    };

    const hostBase = stripTrailingPathSegment(
      stripTrailingPathSegment(entered, "systems"),
      "host"
    );
    addCandidate(hostBase);

    const path = hostBase.pathname.replace(/\/+$/, "");
    if (!path.toLowerCase().endsWith("/workable")) {
      const workableBase = new URL(hostBase.toString());
      workableBase.pathname = `${path}/workable`.replace(/^\/?/, "/");
      addCandidate(workableBase);
    }

    return candidates;
  } catch {
    return [trimmed];
  }
}

export function stripTrailingPathSegment(url: URL, segment: string) {
  const next = new URL(url.toString());
  const path = next.pathname.replace(/\/+$/, "");

  if (path.toLowerCase().endsWith(`/${segment.toLowerCase()}`)) {
    next.pathname = path.slice(0, -(segment.length + 1)) || "/";
  }

  return next;
}

export function formatWorkableApiUrl(url: URL) {
  const path = url.pathname === "/" ? "" : url.pathname.replace(/\/+$/, "");
  return `${url.origin}${path}${url.search}`;
}

export function formatHostEndpoint(apiUrl: string) {
  const normalized = apiUrl.replace(/\/+$/, "");
  return `${normalized}/host`;
}

export function isWorkableHostResponse(value: unknown): value is WorkableHttpHostDescriptor {
  return Boolean(
    value &&
      typeof value === "object" &&
      Array.isArray((value as Partial<WorkableHttpHostDescriptor>).systems) &&
      (value as Partial<WorkableHttpHostDescriptor>).capabilities
  );
}

function createDiscoveredSystemFromStored(
  system: WorkableSystemConnection
): WorkableHttpSystemDescriptor {
  return {
    id: { value: system.id },
    name: system.systemName ?? null,
    state: system.state ?? "Unknown",
    isDefault: !system.systemName,
    capabilities: {
      persistentCoordinationAvailable: system.persistentCoordinationAvailable,
    },
    access: system.access ?? createUnknownAccessSummary(),
  };
}

export function reconcileStoredHostWithDiscovery(
  host: WorkableHostConnection,
  discoveredHost: WorkableHttpHostDescriptor
): WorkableHostConnection {
  return {
    ...host,
    realtimeEnabled: Boolean(discoveredHost.capabilities.realtime.enabled),
    realtimeHubPath: discoveredHost.capabilities.realtime.hubPath ?? null,
    realtimeTransport: discoveredHost.capabilities.realtime.transport ?? null,
    systems: host.systems.flatMap((storedSystem) => {
      const discoveredSystem = discoveredHost.systems.find(
        (system) =>
          getSystemStorageKey(system) ===
          getSystemStorageKey(createDiscoveredSystemFromStored(storedSystem))
      );

      return discoveredSystem
        ? [createStoredSystem(host.id, discoveredSystem, storedSystem)]
        : [];
    }),
  };
}

export function getSystemStorageKey(system: WorkableHttpSystemDescriptor) {
  return system.name?.trim() ?? "";
}

export function getSystemDisplayName(system: WorkableHttpSystemDescriptor) {
  return normalizeOptional(system.name) ?? "Default";
}

export function getSystemSecondaryText(system: WorkableHttpSystemDescriptor) {
  return system.isDefault ? "Default system" : null;
}

function createServerId() {
  if (typeof crypto !== "undefined" && "randomUUID" in crypto) {
    return crypto.randomUUID();
  }

  return `server-${Date.now().toString(36)}`;
}

export function normalizeOptional(value?: string | null) {
  const trimmed = value?.trim();
  return trimmed ? trimmed : undefined;
}
