"use client";

import { Activity, Trash2 } from "lucide-react";
import { useCallback, useEffect, useRef, useState } from "react";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card";
import { Input } from "@/components/ui/input";
import {
  ErrorBanner,
  FeedbackBanner,
  transientFeedbackAutoDismissMs,
} from "@/components/workable/console/feedback-panel";
import {
  formatDateTime,
  WorkableApiError,
  workableFetch,
  type WorkActionOutcome,
  type WorkableConnection,
  type WorkableProfilingCaptureRule,
  type WorkableProfilingCaptureState,
} from "@/lib/workable";

export function ProfilingCaptureRulesCard({
  canControlSystem,
  canViewDiagnostics,
  connection,
  definitionName,
  refreshToken = 0,
}: {
  canControlSystem: boolean;
  canViewDiagnostics: boolean;
  connection: WorkableConnection;
  definitionName?: string | null;
  refreshToken?: number;
}) {
  if (!canViewDiagnostics) {
    return null;
  }

  return (
    <AuthorizedProfilingCaptureRulesCard
      canControlSystem={canControlSystem}
      connection={connection}
      definitionName={definitionName}
      refreshToken={refreshToken}
    />
  );
}

function AuthorizedProfilingCaptureRulesCard({
  canControlSystem,
  connection,
  definitionName,
  refreshToken,
}: {
  canControlSystem: boolean;
  connection: WorkableConnection;
  definitionName?: string | null;
  refreshToken: number;
}) {
  const [state, setState] = useState<WorkableProfilingCaptureState | null>(null);
  const [error, setError] = useState<string>();
  const [status, setStatus] = useState<string>();
  const [statusVersion, setStatusVersion] = useState(0);
  const [loading, setLoading] = useState(true);
  const [pending, setPending] = useState<string>();
  const [maximumMatches, setMaximumMatches] = useState(1);
  const [expiresAfterMinutes, setExpiresAfterMinutes] = useState(30);
  const isDefinitionScope = Boolean(definitionName);
  const scopeKey = `${connection.apiUrl}\n${connection.systemName ?? ""}\n${definitionName?.toUpperCase() ?? ""}`;
  const activeScopeKey = useRef(scopeKey);
  const loadedScopeKey = useRef<string | undefined>(undefined);
  const requestGeneration = useRef(0);
  const mutationGeneration = useRef(0);
  const enableLabel = isDefinitionScope
    ? "Capture this definition"
    : "Capture all work";
  activeScopeKey.current = scopeKey;

  const load = useCallback(async (
    showLoading = true,
    clearError = showLoading,
    forceFresh = false
  ) => {
    const generation = ++requestGeneration.current;
    const requestConnection = {
      apiUrl: connection.apiUrl,
      systemName: connection.systemName,
    };
    if (showLoading) {
      setLoading(true);
    }
    try {
      const nextState = await workableFetch<WorkableProfilingCaptureState>(
        requestConnection,
        "profiling/capture-rules",
        undefined,
        { coalesce: !forceFresh }
      );
      if (requestGeneration.current !== generation || activeScopeKey.current !== scopeKey) {
        return;
      }
      loadedScopeKey.current = scopeKey;
      setState(nextState);
      if (clearError) {
        setError(undefined);
      }
    } catch (caught) {
      if (requestGeneration.current !== generation || activeScopeKey.current !== scopeKey) {
        return;
      }
      setError(caught instanceof Error ? caught.message : "Unable to load profile capture rules.");
    } finally {
      if (showLoading && requestGeneration.current === generation && activeScopeKey.current === scopeKey) {
        setLoading(false);
      }
    }
  }, [connection.apiUrl, connection.systemName, scopeKey]);

  useEffect(() => {
    const timer = window.setTimeout(() => void load(loadedScopeKey.current !== scopeKey, true), 0);
    return () => {
      window.clearTimeout(timer);
      requestGeneration.current += 1;
    };
  }, [load, refreshToken, scopeKey]);

  useEffect(() => {
    mutationGeneration.current += 1;
    setPending(undefined);
    setError(undefined);
    setStatus(undefined);
  }, [scopeKey]);

  const currentState = loadedScopeKey.current === scopeKey ? state : null;
  const scopeLoading = loading || currentState === null;

  const visibleRules = (currentState?.rules ?? []).filter((rule) => isDefinitionScope
    ? rule.definitionName?.toUpperCase() === definitionName?.toUpperCase()
    : !rule.definitionName);
  const scopeRules = visibleRules.filter((rule) => !rule.actorId);
  const scopeEnabled = scopeRules.length > 0;

  const toggleScope = async () => {
    const generation = ++mutationGeneration.current;
    const enable = !scopeEnabled;
    setPending("toggle");
    setError(undefined);
    try {
      if (enable) {
        const created = await workableFetch<WorkableProfilingCaptureRule>(connection, "profiling/capture-rules", {
          method: "POST",
          body: JSON.stringify({
            definitionName: definitionName || undefined,
            expiresAfterMinutes,
            maximumMatches,
          }),
        });
        if (mutationGeneration.current !== generation || activeScopeKey.current !== scopeKey) return;
        setState((current) => current
          ? { ...current, rules: [...current.rules, created] }
          : current);
        setStatus(`${enableLabel} enabled for the next ${maximumMatches} worker${maximumMatches === 1 ? "" : "s"}.`);
      } else {
        const outcomes = await Promise.allSettled(scopeRules.map(async (rule) => {
          try {
            await workableFetch<void>(connection, `profiling/capture-rules/${rule.id}`, { method: "DELETE" });
          } catch (caught) {
            if (caught instanceof WorkableApiError && caught.status === 404) return;
            throw caught;
          }
        }));
        const rejected = outcomes.find((outcome) => outcome.status === "rejected");
        if (rejected?.status === "rejected") {
          throw rejected.reason;
        }
        if (mutationGeneration.current !== generation || activeScopeKey.current !== scopeKey) return;
        const removedIds = new Set(scopeRules.map((rule) => rule.id));
        setState((current) => current
          ? { ...current, rules: current.rules.filter((rule) => !removedIds.has(rule.id)) }
          : current);
        setStatus("Full profile capture disabled for this scope.");
      }
      setStatusVersion((current) => current + 1);
      void load(false, false, true);
    } catch (caught) {
      if (mutationGeneration.current !== generation || activeScopeKey.current !== scopeKey) return;
      setStatus(undefined);
      setError(caught instanceof Error ? caught.message : "Unable to update full profile capture.");
      await load(false, false, true);
    } finally {
      if (mutationGeneration.current === generation) {
        setPending(undefined);
      }
    }
  };

  const deleteRule = async (rule: WorkableProfilingCaptureRule) => {
    const generation = ++mutationGeneration.current;
    setPending(rule.id);
    setError(undefined);
    try {
      await workableFetch<void>(connection, `profiling/capture-rules/${rule.id}`, { method: "DELETE" });
      if (mutationGeneration.current !== generation || activeScopeKey.current !== scopeKey) return;
      setState((current) => current
        ? { ...current, rules: current.rules.filter((candidate) => candidate.id !== rule.id) }
        : current);
      setStatus("Full profile capture rule removed.");
      setStatusVersion((current) => current + 1);
      void load(false, false, true);
    } catch (caught) {
      if (mutationGeneration.current !== generation || activeScopeKey.current !== scopeKey) return;
      setStatus(undefined);
      setError(caught instanceof Error ? caught.message : "Unable to remove profile capture rule.");
      void load(false, false, true);
    } finally {
      if (mutationGeneration.current === generation) {
        setPending(undefined);
      }
    }
  };

  return (
    <Card>
      <CardHeader>
        <CardTitle className="flex items-center gap-2 text-base">
          <Activity className="size-4" />
          Full profile capture
        </CardTitle>
        <CardDescription>
          Temporarily bypass the {currentState?.maximumAutomaticInstrumentationNodes?.toLocaleString() ?? "configured"}-node
          automatic SQL, HTTP, and extension limit for {isDefinitionScope ? "future workers of this definition" : "future workers"}.
          Rules disappear after they are consumed or expire.
        </CardDescription>
      </CardHeader>
      <CardContent className="space-y-4">
        {error ? <ErrorBanner message={error} title="Profile capture unavailable" /> : null}
        {status ? (
          <FeedbackBanner
            autoDismissAfterMs={transientFeedbackAutoDismissMs}
            key={statusVersion}
            message={status}
            title="Profile capture updated"
            tone="success"
          />
        ) : null}
        <div className="grid gap-3 sm:grid-cols-2">
          <label className="space-y-1 text-sm">
            <span className="text-muted-foreground">Workers to capture</span>
            <Input
              disabled={!canControlSystem}
              max={1000}
              min={1}
              onChange={(event) => setMaximumMatches(Math.max(1, Math.min(1000, Number(event.target.value) || 1)))}
              type="number"
              value={maximumMatches}
            />
          </label>
          <label className="space-y-1 text-sm">
            <span className="text-muted-foreground">Expires after (minutes)</span>
            <Input
              disabled={!canControlSystem}
              max={1440}
              min={1}
              onChange={(event) => setExpiresAfterMinutes(Math.max(1, Math.min(1440, Number(event.target.value) || 1)))}
              type="number"
              value={expiresAfterMinutes}
            />
          </label>
        </div>
        <FullProfileCaptureToggle
          disabled={!canControlSystem || scopeLoading || Boolean(pending)}
          enabled={scopeEnabled}
          enableLabel={enableLabel}
          onToggle={() => void toggleScope()}
        />
        {!scopeLoading && visibleRules.length === 0 ? (
          <div className="rounded-md border border-dashed p-3 text-muted-foreground text-sm">
            No active full-capture rules for this scope.
          </div>
        ) : !scopeLoading ? (
          <div className="space-y-2">
            {visibleRules.map((rule) => (
              <div className="flex items-center justify-between gap-3 rounded-md border p-3" key={rule.id}>
                <div className="min-w-0 space-y-1 text-sm">
                  <div className="flex flex-wrap gap-2">
                    {rule.definitionName ? <Badge variant="outline">Work: {rule.definitionName}</Badge> : <Badge variant="outline">All work</Badge>}
                    {rule.actorId ? <Badge variant="outline">User: {rule.actorId}</Badge> : <Badge variant="outline">All users</Badge>}
                  </div>
                  <div className="text-muted-foreground">
                    {rule.remainingMatches} of {rule.maximumMatches} workers remaining · expires {formatDateTime(rule.expiresAt)}
                  </div>
                </div>
                <Button
                  aria-label="Remove full profile capture rule"
                  disabled={!canControlSystem || Boolean(pending)}
                  onClick={() => void deleteRule(rule)}
                  size="icon"
                  type="button"
                  variant="ghost"
                >
                  <Trash2 className="size-4" />
                </Button>
              </div>
            ))}
          </div>
        ) : null}
        {!canControlSystem ? (
          <div className="text-muted-foreground text-sm">
            System-control permission is required to create or remove temporary full-profile rules.
          </div>
        ) : null}
      </CardContent>
    </Card>
  );
}

export function WorkerProfilingCaptureCard({
  canReconfigure,
  canViewDiagnostics,
  connection,
  fullCaptureEnabled,
  isFinal,
  onUpdated,
  revision,
  workerId,
}: {
  canReconfigure: boolean;
  canViewDiagnostics: boolean;
  connection: WorkableConnection;
  fullCaptureEnabled: boolean;
  isFinal: boolean;
  onUpdated: () => void;
  revision: number;
  workerId: string;
}) {
  const [error, setError] = useState<string>();
  const [status, setStatus] = useState<string>();
  const [statusVersion, setStatusVersion] = useState(0);
  const [pending, setPending] = useState(false);
  const [locallyEnabled, setLocallyEnabled] = useState(fullCaptureEnabled);
  const [localRevision, setLocalRevision] = useState(revision);

  useEffect(() => {
    setLocallyEnabled(fullCaptureEnabled);
  }, [fullCaptureEnabled]);

  useEffect(() => {
    setLocalRevision(revision);
  }, [revision]);

  if (!canViewDiagnostics) {
    return null;
  }

  const toggle = async () => {
    const enable = !locallyEnabled;
    setPending(true);
    setError(undefined);
    try {
      const outcome = await workableFetch<WorkActionOutcome>(connection, `workers/${workerId}/reconfigure`, {
        method: "POST",
        body: JSON.stringify({
          revision: localRevision,
          changes: {
            profilingEnabled: true,
            profilingCaptureMode: enable ? "Full" : "Bounded",
          },
          description: `${enable ? "Enable" : "Disable"} full profile capture from the Workable admin UI.`,
        }),
      });
      const message = outcome.messages.map((item) => item.text).filter(Boolean).join(" ");
      if (outcome.status !== "Accepted") {
        setStatus(undefined);
        setError(message || `Worker reconfiguration returned ${outcome.status}.`);
        return;
      }

      setLocallyEnabled(enable);
      setLocalRevision(outcome.worker?.revision ?? localRevision + 1);
      setStatus(enable
        ? "Full profile capture is enabled for this worker."
        : "Full profile capture is disabled; normal bounded profiling remains enabled.");
      setStatusVersion((current) => current + 1);
      onUpdated();
    } catch (caught) {
      setStatus(undefined);
      setError(caught instanceof Error ? caught.message : "Unable to enable full profile capture for this worker.");
    } finally {
      setPending(false);
    }
  };

  return (
    <Card>
      <CardHeader>
        <CardTitle className="flex items-center gap-2 text-base">
          <Activity className="size-4" />
          Full profile capture
        </CardTitle>
        <CardDescription>
          Bypass the automatic SQL, HTTP, and extension node limit for this worker only. The change applies to its next execution;
          an iteration already running is not restarted.
        </CardDescription>
      </CardHeader>
      <CardContent className="space-y-4">
        {error ? <ErrorBanner message={error} title="Profile capture unavailable" /> : null}
        {status ? (
          <FeedbackBanner
            autoDismissAfterMs={transientFeedbackAutoDismissMs}
            key={statusVersion}
            message={status}
            title="Profile capture updated"
            tone="success"
          />
        ) : null}
        <FullProfileCaptureToggle
          disabled={pending || isFinal || !canReconfigure}
          enabled={locallyEnabled}
          enableLabel="Capture this worker"
          onToggle={() => void toggle()}
        />
        {!canReconfigure ? (
          <div className="text-muted-foreground text-sm">Permission to reconfigure this worker is required to change full profile capture.</div>
        ) : isFinal && !locallyEnabled ? (
          <div className="text-muted-foreground text-sm">This worker is final and cannot be reconfigured.</div>
        ) : null}
      </CardContent>
    </Card>
  );
}

function FullProfileCaptureToggle({
  disabled,
  enabled,
  enableLabel,
  onToggle,
}: {
  disabled: boolean;
  enabled: boolean;
  enableLabel: string;
  onToggle: () => void;
}) {
  return (
    <Button
      aria-pressed={enabled}
      disabled={disabled}
      onClick={onToggle}
      size="sm"
      type="button"
      variant="outline"
    >
      <Activity className="size-4" />
      {enabled ? "Disable full capture" : enableLabel}
    </Button>
  );
}
