"use client";

import { DatabaseZap } from "lucide-react";
import { useCallback, useEffect, useRef, useState } from "react";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card";
import { Input } from "@/components/ui/input";
import { parseTimeSpanMilliseconds } from "@/components/workable/console/console-format";
import {
  ErrorBanner,
  FeedbackBanner,
  transientFeedbackAutoDismissMs,
} from "@/components/workable/console/feedback-panel";
import {
  formatDateTime,
  WorkableApiError,
  workableFetch,
  type WorkableConnection,
  type WorkableExecutionDiagnosticCaptureRule,
  type WorkableExecutionDiagnosticCaptureState,
} from "@/lib/workable";

type PersistentLogLevel = WorkableExecutionDiagnosticCaptureRule["minimumLogLevel"];
type PersistentProfileMode = "None" | "Bounded" | "Full";

export function ExecutionDiagnosticsCaptureCard({
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
  const unavailable = connection.executionDiagnosticsPersistenceAvailable === false;
  const [state, setState] = useState<WorkableExecutionDiagnosticCaptureState | null>(
    unavailable ? { persistenceAvailable: false, rules: [] } : null
  );
  const [loading, setLoading] = useState(!unavailable);
  const [pending, setPending] = useState<string>();
  const [error, setError] = useState<string>();
  const [status, setStatus] = useState<string>();
  const [statusVersion, setStatusVersion] = useState(0);
  const [activeForMinutes, setActiveForMinutes] = useState(30);
  const [retentionMinutes, setRetentionMinutes] = useState(1440);
  const [minimumLogLevel, setMinimumLogLevel] = useState<PersistentLogLevel>("Information");
  const [profileCaptureMode, setProfileCaptureMode] = useState<PersistentProfileMode>("None");
  const hydratedRuleId = useRef<string | undefined>(undefined);
  const scopeKey = `${connection.apiUrl}\n${connection.systemName ?? ""}\n${definitionName?.toUpperCase() ?? ""}`;
  const activeScopeKey = useRef(scopeKey);
  const loadedScopeKey = useRef<string | undefined>(unavailable ? scopeKey : undefined);
  const failedScopeKey = useRef<string | undefined>(undefined);
  const requestGeneration = useRef(0);
  const mutationGeneration = useRef(0);
  activeScopeKey.current = scopeKey;

  const load = useCallback(async (
    showLoading = true,
    clearError = showLoading,
    forceFresh = false
  ) => {
    const generation = ++requestGeneration.current;
    if (connection.executionDiagnosticsPersistenceAvailable === false) {
      if (activeScopeKey.current !== scopeKey) return;
      loadedScopeKey.current = scopeKey;
      failedScopeKey.current = undefined;
      setState({ persistenceAvailable: false, rules: [] });
      if (clearError) {
        setError(undefined);
      }
      setLoading(false);
      return;
    }

    const requestConnection = {
      apiUrl: connection.apiUrl,
      systemName: connection.systemName,
    };
    if (showLoading) {
      setLoading(true);
    }
    try {
      const nextState = await workableFetch<WorkableExecutionDiagnosticCaptureState>(
        requestConnection,
        "execution-diagnostics/capture-rules",
        undefined,
        { coalesce: !forceFresh }
      );
      if (requestGeneration.current !== generation || activeScopeKey.current !== scopeKey) {
        return;
      }
      loadedScopeKey.current = scopeKey;
      failedScopeKey.current = undefined;
      setState(nextState);
      if (clearError) {
        setError(undefined);
      }
    } catch (caught) {
      if (requestGeneration.current !== generation || activeScopeKey.current !== scopeKey) {
        return;
      }
      failedScopeKey.current = scopeKey;
      setError(caught instanceof Error ? caught.message : "Unable to load persistent diagnostic capture rules.");
    } finally {
      if (showLoading && requestGeneration.current === generation && activeScopeKey.current === scopeKey) {
        setLoading(false);
      }
    }
  }, [connection.apiUrl, connection.executionDiagnosticsPersistenceAvailable, connection.systemName, scopeKey]);

  useEffect(() => {
    if (!canViewDiagnostics) return;
    const timer = window.setTimeout(() => void load(loadedScopeKey.current !== scopeKey, true), 0);
    return () => {
      window.clearTimeout(timer);
      requestGeneration.current += 1;
    };
  }, [canViewDiagnostics, load, refreshToken, scopeKey]);

  useEffect(() => {
    mutationGeneration.current += 1;
    setPending(undefined);
    setError(undefined);
    setStatus(undefined);
    hydratedRuleId.current = undefined;
    failedScopeKey.current = undefined;
  }, [scopeKey]);

  const currentState = loadedScopeKey.current === scopeKey ? state : null;
  const scopeFailed = currentState === null && failedScopeKey.current === scopeKey;
  const scopeLoading = loading || (currentState === null && !scopeFailed);
  const scopeUnavailable = currentState === null;

  useEffect(() => {
    const matchingRules = (currentState?.rules ?? []).filter((rule) => definitionName
      ? rule.definitionName?.toUpperCase() === definitionName.toUpperCase()
      : !rule.definitionName);
    const selected = [...matchingRules].sort((left, right) =>
      right.createdAt.localeCompare(left.createdAt))[0];
    if (!selected || hydratedRuleId.current === selected.id) return;

    hydratedRuleId.current = selected.id;
    const activeMilliseconds = Date.parse(selected.activeUntil) - Date.parse(selected.createdAt);
    if (Number.isFinite(activeMilliseconds) && activeMilliseconds > 0) {
      setActiveForMinutes(clampMinutes(String(Math.round(activeMilliseconds / 60_000))));
    }
    const retentionMilliseconds = parseTimeSpanMilliseconds(selected.artifactRetention);
    if (retentionMilliseconds !== null && retentionMilliseconds > 0) {
      setRetentionMinutes(clampMinutes(String(Math.round(retentionMilliseconds / 60_000))));
    }
    setMinimumLogLevel(selected.minimumLogLevel);
    setProfileCaptureMode(selected.profileCaptureMode ?? "None");
  }, [currentState, definitionName]);

  if (!canViewDiagnostics) return null;

  const scopeRules = (currentState?.rules ?? []).filter((rule) => definitionName
    ? rule.definitionName?.toUpperCase() === definitionName.toUpperCase()
    : !rule.definitionName);
  const scopeEnabled = scopeRules.length > 0;

  const saveRule = async () => {
    const generation = ++mutationGeneration.current;
    setPending("save");
    setError(undefined);
    try {
      const created = await workableFetch<WorkableExecutionDiagnosticCaptureRule>(
        connection,
        "execution-diagnostics/capture-rules",
        {
          method: "POST",
          body: JSON.stringify({
            activeForMinutes,
            artifactRetentionMinutes: retentionMinutes,
            definitionName: definitionName || undefined,
            minimumLogLevel,
            profileCaptureMode: profileCaptureMode === "None" ? null : profileCaptureMode,
          }),
        }
      );
      if (mutationGeneration.current !== generation || activeScopeKey.current !== scopeKey) return;
      const replacedIds = new Set(scopeRules.map((rule) => rule.id));
      setState((current) => current
        ? {
            ...current,
            rules: [...current.rules.filter((rule) => !replacedIds.has(rule.id)), created],
          }
        : current);
      setStatus(definitionName
        ? `Persistent diagnostics ${scopeEnabled ? "updated" : "enabled"} temporarily for ${definitionName}.`
        : `System-wide persistent diagnostics ${scopeEnabled ? "updated" : "enabled"} temporarily.`);
      setStatusVersion((current) => current + 1);
      void load(false, false, true);
    } catch (caught) {
      if (mutationGeneration.current !== generation || activeScopeKey.current !== scopeKey) return;
      setError(caught instanceof Error ? caught.message : "Unable to enable persistent diagnostics.");
      void load(false, false, true);
    } finally {
      if (mutationGeneration.current === generation) {
        setPending(undefined);
      }
    }
  };

  const disableScope = async () => {
    const generation = ++mutationGeneration.current;
    setPending("disable");
    setError(undefined);
    try {
      const outcomes = await Promise.allSettled(scopeRules.map(async (rule) => {
        try {
          await workableFetch<void>(
            connection,
            `execution-diagnostics/capture-rules/${rule.id}`,
            { method: "DELETE" }
          );
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
      setStatus("Persistent diagnostic capture stopped. Existing artifacts retain their original expiry.");
      setStatusVersion((current) => current + 1);
      void load(false, false, true);
    } catch (caught) {
      if (mutationGeneration.current !== generation || activeScopeKey.current !== scopeKey) return;
      setError(caught instanceof Error ? caught.message : "Unable to stop persistent diagnostics.");
      await load(false, false, true);
    } finally {
      if (mutationGeneration.current === generation) {
        setPending(undefined);
      }
    }
  };

  const activeRule = [...scopeRules].sort((left, right) =>
    right.createdAt.localeCompare(left.createdAt))[0];
  const available = currentState?.persistenceAvailable ?? false;

  return (
    <Card>
      <CardHeader>
        <CardTitle className="flex items-center gap-2 text-base">
          <DatabaseZap className="size-4" />
          Persistent execution diagnostics
        </CardTitle>
        <CardDescription>
          Temporarily persist iteration logs and profiles for agent-assisted diagnosis. Capture shuts off automatically;
          stored artifacts also have a mandatory expiry of at most 30 days.
        </CardDescription>
      </CardHeader>
      <CardContent className="space-y-4">
        {error ? <ErrorBanner message={error} title="Persistent diagnostics unavailable" /> : null}
        {status ? (
          <FeedbackBanner
            autoDismissAfterMs={transientFeedbackAutoDismissMs}
            key={statusVersion}
            message={status}
            title="Capture updated"
            tone="success"
          />
        ) : null}
        {!scopeLoading && currentState !== null && !available ? (
          <ErrorBanner
            message="Register an execution diagnostics repository, such as Workable SQL Server persistence, to enable this control."
            title="Persistence not registered"
          />
        ) : null}
        <div className="grid gap-3 sm:grid-cols-2 xl:grid-cols-4">
          <label className="space-y-1 text-sm">
            <span className="text-muted-foreground">Capture for (minutes)</span>
            <Input max={43200} min={1} onChange={(event) => setActiveForMinutes(clampMinutes(event.target.value))} type="number" value={activeForMinutes} />
          </label>
          <label className="space-y-1 text-sm">
            <span className="text-muted-foreground">Retain artifacts (minutes)</span>
            <Input max={43200} min={1} onChange={(event) => setRetentionMinutes(clampMinutes(event.target.value))} type="number" value={retentionMinutes} />
          </label>
          <label className="space-y-1 text-sm">
            <span className="text-muted-foreground">Persistent log level</span>
            <select className="h-9 w-full rounded-md border border-input bg-background px-3 text-sm" onChange={(event) => setMinimumLogLevel(event.target.value as PersistentLogLevel)} value={minimumLogLevel}>
              {(["Trace", "Debug", "Information", "Warning", "Error", "Critical"] as PersistentLogLevel[]).map((level) => <option key={level}>{level}</option>)}
            </select>
          </label>
          <label className="space-y-1 text-sm">
            <span className="text-muted-foreground">Profile capture</span>
            <select className="h-9 w-full rounded-md border border-input bg-background px-3 text-sm" onChange={(event) => setProfileCaptureMode(event.target.value as PersistentProfileMode)} value={profileCaptureMode}>
              <option value="None">Logs only</option>
              <option value="Bounded">Bounded profile</option>
              <option value="Full">Full profile (artifact limits apply)</option>
            </select>
          </label>
        </div>
        <div className="flex flex-wrap gap-2">
          <Button
            disabled={scopeLoading || scopeUnavailable || !available || !canControlSystem || Boolean(pending)}
            onClick={() => void saveRule()}
            size="sm"
            type="button"
            variant="outline"
          >
            <DatabaseZap className="size-4" />
            {scopeEnabled
              ? "Update persistent diagnostics"
              : definitionName ? "Persist this work temporarily" : "Persist all work temporarily"}
          </Button>
          {scopeEnabled ? (
            <Button
              disabled={scopeLoading || scopeUnavailable || !available || !canControlSystem || Boolean(pending)}
              onClick={() => void disableScope()}
              size="sm"
              type="button"
              variant="outline"
            >
              <DatabaseZap className="size-4" />
              Disable persistent diagnostics
            </Button>
          ) : null}
        </div>
        {scopeLoading ? (
          <div className="text-muted-foreground text-sm">Loading persistent capture rules…</div>
        ) : scopeFailed ? null : !activeRule ? (
          <div className="rounded-md border border-dashed p-3 text-muted-foreground text-sm">Persistent diagnostics are not active for this scope.</div>
        ) : (
          <div className="rounded-md border p-3">
            <div className="min-w-0 space-y-1 text-sm">
              <div className="flex flex-wrap gap-2">
                <Badge variant="outline">{activeRule.definitionName ? `Work: ${activeRule.definitionName}` : "All work"}</Badge>
                <Badge variant="secondary">Logs: {activeRule.minimumLogLevel}+</Badge>
                <Badge variant="secondary">Profile: {activeRule.profileCaptureMode ?? "off"}</Badge>
              </div>
              <div className="text-muted-foreground">capture ends {formatDateTime(activeRule.activeUntil)} · artifact retention {activeRule.artifactRetention}</div>
              {scopeRules.length > 1 ? (
                <div className="text-muted-foreground">Updating or disabling will also remove {scopeRules.length - 1} older duplicate {scopeRules.length === 2 ? "rule" : "rules"}.</div>
              ) : null}
            </div>
          </div>
        )}
        {!canControlSystem ? (
          <div className="text-muted-foreground text-sm">Control-system permission is required to change persistent capture.</div>
        ) : null}
      </CardContent>
    </Card>
  );
}

function clampMinutes(value: string) {
  return Math.max(1, Math.min(43200, Number(value) || 1));
}
