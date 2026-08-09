"use client";

import { DatabaseZap, Loader2, Trash2 } from "lucide-react";
import { useCallback, useEffect, useState } from "react";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card";
import { Input } from "@/components/ui/input";
import { ErrorBanner, FeedbackBanner } from "@/components/workable/console/feedback-panel";
import {
  formatDateTime,
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
}: {
  canControlSystem: boolean;
  canViewDiagnostics: boolean;
  connection: WorkableConnection;
  definitionName?: string | null;
}) {
  const unavailable = connection.executionDiagnosticsPersistenceAvailable === false;
  const [state, setState] = useState<WorkableExecutionDiagnosticCaptureState | null>(
    unavailable ? { persistenceAvailable: false, rules: [] } : null
  );
  const [loading, setLoading] = useState(!unavailable);
  const [pending, setPending] = useState<string>();
  const [error, setError] = useState<string>();
  const [status, setStatus] = useState<string>();
  const [activeForMinutes, setActiveForMinutes] = useState(30);
  const [retentionMinutes, setRetentionMinutes] = useState(1440);
  const [minimumLogLevel, setMinimumLogLevel] = useState<PersistentLogLevel>("Information");
  const [profileCaptureMode, setProfileCaptureMode] = useState<PersistentProfileMode>("None");

  const load = useCallback(async () => {
    if (connection.executionDiagnosticsPersistenceAvailable === false) {
      setState({ persistenceAvailable: false, rules: [] });
      setError(undefined);
      setLoading(false);
      return;
    }

    setLoading(true);
    try {
      setState(await workableFetch<WorkableExecutionDiagnosticCaptureState>(
        connection,
        "execution-diagnostics/capture-rules"
      ));
      setError(undefined);
    } catch (caught) {
      setError(caught instanceof Error ? caught.message : "Unable to load persistent diagnostic capture rules.");
    } finally {
      setLoading(false);
    }
  }, [connection]);

  useEffect(() => {
    if (!canViewDiagnostics) return;
    const timer = window.setTimeout(() => void load(), 0);
    return () => window.clearTimeout(timer);
  }, [canViewDiagnostics, load]);

  if (!canViewDiagnostics) return null;

  const createRule = async () => {
    setPending("create");
    setError(undefined);
    setStatus(undefined);
    try {
      await workableFetch<WorkableExecutionDiagnosticCaptureRule>(
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
      setStatus(definitionName
        ? `Persistent diagnostics enabled temporarily for ${definitionName}.`
        : "System-wide persistent diagnostics enabled temporarily.");
      await load();
    } catch (caught) {
      setError(caught instanceof Error ? caught.message : "Unable to enable persistent diagnostics.");
    } finally {
      setPending(undefined);
    }
  };

  const deleteRule = async (rule: WorkableExecutionDiagnosticCaptureRule) => {
    setPending(rule.id);
    setError(undefined);
    try {
      await workableFetch<void>(connection, `execution-diagnostics/capture-rules/${rule.id}`, { method: "DELETE" });
      setStatus("Persistent diagnostic capture stopped. Existing artifacts retain their original expiry.");
      await load();
    } catch (caught) {
      setError(caught instanceof Error ? caught.message : "Unable to stop persistent diagnostics.");
    } finally {
      setPending(undefined);
    }
  };

  const visibleRules = (state?.rules ?? []).filter((rule) => definitionName
    ? !rule.definitionName || rule.definitionName.toUpperCase() === definitionName.toUpperCase()
    : !rule.definitionName);
  const available = state?.persistenceAvailable ?? false;

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
        {status ? <FeedbackBanner message={status} title="Capture updated" tone="success" /> : null}
        {!loading && !available ? (
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
        <Button disabled={loading || !available || !canControlSystem || Boolean(pending)} onClick={() => void createRule()} size="sm" type="button" variant="outline">
          {pending === "create" ? <Loader2 className="size-4 animate-spin" /> : <DatabaseZap className="size-4" />}
          {definitionName ? "Persist this work temporarily" : "Persist all work temporarily"}
        </Button>
        {loading ? (
          <div className="text-muted-foreground text-sm">Loading persistent capture rules…</div>
        ) : visibleRules.length === 0 ? (
          <div className="rounded-md border border-dashed p-3 text-muted-foreground text-sm">No active persistent capture rules match this view.</div>
        ) : (
          <div className="space-y-2">
            {visibleRules.map((rule) => (
              <div className="flex items-center justify-between gap-3 rounded-md border p-3" key={rule.id}>
                <div className="min-w-0 space-y-1 text-sm">
                  <div className="flex flex-wrap gap-2">
                    <Badge variant="outline">{rule.definitionName ? `Work: ${rule.definitionName}` : "All work"}</Badge>
                    <Badge variant="secondary">Logs: {rule.minimumLogLevel}+</Badge>
                    <Badge variant="secondary">Profile: {rule.profileCaptureMode ?? "off"}</Badge>
                  </div>
                  <div className="text-muted-foreground">capture ends {formatDateTime(rule.activeUntil)} · artifact retention {rule.artifactRetention}</div>
                </div>
                <Button aria-label="Stop persistent diagnostic capture" disabled={!canControlSystem || Boolean(pending)} onClick={() => void deleteRule(rule)} size="icon" type="button" variant="ghost">
                  {pending === rule.id ? <Loader2 className="size-4 animate-spin" /> : <Trash2 className="size-4" />}
                </Button>
              </div>
            ))}
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
