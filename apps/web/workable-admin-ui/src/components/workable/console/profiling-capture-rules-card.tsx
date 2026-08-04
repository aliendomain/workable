"use client";

import { Activity, Loader2, Trash2 } from "lucide-react";
import { useCallback, useEffect, useMemo, useState } from "react";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card";
import { Input } from "@/components/ui/input";
import { ErrorBanner, FeedbackBanner } from "@/components/workable/console/feedback-panel";
import {
  formatDateTime,
  workableFetch,
  type WorkableConnection,
  type WorkableProfilingCaptureRule,
  type WorkableProfilingCaptureState,
} from "@/lib/workable";

type CaptureTarget = {
  actorId?: string;
  definitionName?: string;
  label: string;
};

export function ProfilingCaptureRulesCard({
  actorId,
  connection,
  definitionName,
}: {
  actorId?: string | null;
  connection: WorkableConnection;
  definitionName?: string | null;
}) {
  const [state, setState] = useState<WorkableProfilingCaptureState | null>(null);
  const [error, setError] = useState<string>();
  const [status, setStatus] = useState<string>();
  const [loading, setLoading] = useState(true);
  const [pendingTarget, setPendingTarget] = useState<string>();
  const [maximumMatches, setMaximumMatches] = useState(1);
  const [expiresAfterMinutes, setExpiresAfterMinutes] = useState(30);

  const targets = useMemo<CaptureTarget[]>(() => {
    const next: CaptureTarget[] = [];
    if (definitionName) {
      next.push({ definitionName, label: "Capture by work type" });
    }
    if (actorId) {
      next.push({ actorId, label: "Capture by user" });
    }
    if (definitionName && actorId) {
      next.push({ actorId, definitionName, label: "Capture this user + work type" });
    }
    return next;
  }, [actorId, definitionName]);

  const load = useCallback(async () => {
    setLoading(true);
    try {
      setState(await workableFetch<WorkableProfilingCaptureState>(connection, "profiling/capture-rules"));
      setError(undefined);
    } catch (caught) {
      setError(caught instanceof Error ? caught.message : "Unable to load profile capture rules.");
    } finally {
      setLoading(false);
    }
  }, [connection]);

  useEffect(() => {
    void load();
  }, [load]);

  const createRule = async (target: CaptureTarget) => {
    setPendingTarget(target.label);
    setError(undefined);
    setStatus(undefined);
    try {
      await workableFetch<WorkableProfilingCaptureRule>(connection, "profiling/capture-rules", {
        method: "POST",
        body: JSON.stringify({
          actorId: target.actorId,
          definitionName: target.definitionName,
          expiresAfterMinutes,
          maximumMatches,
        }),
      });
      setStatus(`${target.label} enabled for the next ${maximumMatches} matching worker${maximumMatches === 1 ? "" : "s"}.`);
      await load();
    } catch (caught) {
      setError(caught instanceof Error ? caught.message : "Unable to create profile capture rule.");
    } finally {
      setPendingTarget(undefined);
    }
  };

  const deleteRule = async (rule: WorkableProfilingCaptureRule) => {
    setPendingTarget(rule.id);
    setError(undefined);
    try {
      await workableFetch<void>(connection, `profiling/capture-rules/${rule.id}`, { method: "DELETE" });
      setStatus("Full profile capture rule removed.");
      await load();
    } catch (caught) {
      setError(caught instanceof Error ? caught.message : "Unable to remove profile capture rule.");
    } finally {
      setPendingTarget(undefined);
    }
  };

  const visibleRules = (state?.rules ?? []).filter((rule) =>
    (definitionName && rule.definitionName === definitionName) ||
    (actorId && rule.actorId === actorId)
  );

  return (
    <Card>
      <CardHeader>
        <CardTitle className="flex items-center gap-2 text-base">
          <Activity className="size-4" />
          Full profile capture
        </CardTitle>
        <CardDescription>
          Temporarily bypass the {state?.maximumAutomaticInstrumentationNodes?.toLocaleString() ?? "configured"}-node
          automatic SQL, HTTP, and extension limit for matching future workers. Rules disappear after they are consumed or expire.
        </CardDescription>
      </CardHeader>
      <CardContent className="space-y-4">
        {error ? <ErrorBanner message={error} title="Profile capture unavailable" /> : null}
        {status ? <FeedbackBanner message={status} title="Profile capture updated" tone="success" /> : null}
        <div className="grid gap-3 sm:grid-cols-2">
          <label className="space-y-1 text-sm">
            <span className="text-muted-foreground">Matching workers</span>
            <Input
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
              max={1440}
              min={1}
              onChange={(event) => setExpiresAfterMinutes(Math.max(1, Math.min(1440, Number(event.target.value) || 1)))}
              type="number"
              value={expiresAfterMinutes}
            />
          </label>
        </div>
        <div className="flex flex-wrap gap-2">
          {targets.map((target) => (
            <Button
              disabled={Boolean(pendingTarget)}
              key={target.label}
              onClick={() => void createRule(target)}
              size="sm"
              type="button"
              variant="outline"
            >
              {pendingTarget === target.label ? <Loader2 className="size-4 animate-spin" /> : <Activity className="size-4" />}
              {target.label}
            </Button>
          ))}
        </div>
        {loading ? (
          <div className="text-muted-foreground text-sm">Loading capture rules…</div>
        ) : visibleRules.length === 0 ? (
          <div className="rounded-md border border-dashed p-3 text-muted-foreground text-sm">
            No active full-capture rules match this view.
          </div>
        ) : (
          <div className="space-y-2">
            {visibleRules.map((rule) => (
              <div className="flex items-center justify-between gap-3 rounded-md border p-3" key={rule.id}>
                <div className="min-w-0 space-y-1 text-sm">
                  <div className="flex flex-wrap gap-2">
                    {rule.definitionName ? <Badge variant="outline">Work: {rule.definitionName}</Badge> : null}
                    {rule.actorId ? <Badge variant="outline">User: {rule.actorId}</Badge> : null}
                  </div>
                  <div className="text-muted-foreground">
                    {rule.remainingMatches} of {rule.maximumMatches} matches remaining · expires {formatDateTime(rule.expiresAt)}
                  </div>
                </div>
                <Button
                  aria-label="Remove full profile capture rule"
                  disabled={Boolean(pendingTarget)}
                  onClick={() => void deleteRule(rule)}
                  size="icon"
                  type="button"
                  variant="ghost"
                >
                  {pendingTarget === rule.id ? <Loader2 className="size-4 animate-spin" /> : <Trash2 className="size-4" />}
                </Button>
              </div>
            ))}
          </div>
        )}
      </CardContent>
    </Card>
  );
}
