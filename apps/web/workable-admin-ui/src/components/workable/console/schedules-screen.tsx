"use client";

import {
  CalendarClock,
  ChevronRight,
  FileCode2,
  Folder,
  Loader2,
  Plus,
  XCircle,
} from "lucide-react";
import { type ReactNode, useCallback, useEffect, useLayoutEffect, useMemo, useRef, useState } from "react";
import { ConsoleEmptyState } from "@/components/features/console/empty-state";
import { ConsolePageLayout } from "@/components/features/console/console-primitives";
import { PanelScrollViewport, PanelShell } from "@/components/features/console/panel-shell";
import {
  AlertDialog,
  AlertDialogAction,
  AlertDialogCancel,
  AlertDialogContent,
  AlertDialogDescription,
  AlertDialogFooter,
  AlertDialogHeader,
  AlertDialogTitle,
} from "@/components/ui/alert-dialog";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogHeader,
  DialogTitle,
} from "@/components/ui/dialog";
import { Skeleton } from "@/components/ui/skeleton";
import {
  Table,
  TableBody,
  TableCell,
  TableHead,
  TableHeader,
  TableRow,
} from "@/components/ui/table";
import {
  DefinitionCatalogBrowser,
  defaultCatalogBrowserBackButtonClassName,
  defaultCatalogBrowserHeaderClassName,
  defaultCatalogBrowserTitleClassName,
} from "@/components/workable/console/catalog-browser";
import { QueueDialog } from "@/components/workable/console/detail-screens";
import { ErrorBanner } from "@/components/workable/console/feedback-panel";
import { LiveRelativeTime } from "@/components/workable/console/live-relative-time";
import {
  workableFetch,
  type QueueRequestSchemaDescriptor,
  type WorkDefinition,
  type WorkScheduleCancellationOutcome,
  type WorkScheduleOccurrence,
  type WorkScheduleOverviewResult,
  type WorkScheduleQueryResult,
  type WorkScheduleSnapshot,
  type WorkScheduleSummary,
  type WorkScheduleCursor,
  type WorkScheduleUpcomingCursor,
  type WorkScheduleUpcomingQueryResult,
  type WorkableConnection,
} from "@/lib/workable";

type QueueScheduleDialogState = {
  definition: WorkDefinition;
  queueRequestSchema: QueueRequestSchemaDescriptor;
};

export const schedulePageSize = 25;
export const scheduleLoadedWindowSize = schedulePageSize * 5;
export const schedulePollIntervalMs = 10_000;
export const schedulePollMaximumIntervalMs = 60_000;

type ScheduleOverviewWindow = {
  recentCursor?: WorkScheduleCursor | null;
  recentTake?: number;
  upcomingCursor?: WorkScheduleUpcomingCursor | null;
  upcomingTake?: number;
};

export function calculateSchedulePollDelay(
  intervalMs: number,
  consecutiveFailures: number,
  randomValue = Math.random()
) {
  const boundedInterval = Math.max(1, intervalMs);
  const exponentialDelay = Math.min(
    schedulePollMaximumIntervalMs,
    boundedInterval * (2 ** Math.min(Math.max(0, consecutiveFailures), 10))
  );
  const boundedRandomValue = Math.min(1, Math.max(0, randomValue));
  const jitteredDelay = exponentialDelay * (0.8 + (boundedRandomValue * 0.4));
  return Math.max(1, Math.min(schedulePollMaximumIntervalMs, Math.round(jitteredDelay)));
}

export function createScheduleOverviewPath(
  selectedScheduleId?: string | null,
  window: ScheduleOverviewWindow = {}
) {
  const query = new URLSearchParams({
    occurrenceTake: "50",
    recentTake: String(window.recentTake ?? schedulePageSize),
    upcomingTake: String(window.upcomingTake ?? schedulePageSize),
  });
  if (selectedScheduleId) {
    query.set("selectedScheduleId", selectedScheduleId);
  }
  if (window.recentCursor) {
    query.set("recentCursorCreatedAt", window.recentCursor.createdAt);
    query.set("recentCursorScheduleId", window.recentCursor.scheduleId.value);
  }
  if (window.upcomingCursor) {
    query.set("upcomingCursorNextRunAt", window.upcomingCursor.nextRunAt);
    query.set("upcomingCursorScheduleId", window.upcomingCursor.scheduleId.value);
  }

  return `schedules/overview?${query}`;
}

export function createSchedulePagePath(cursor: WorkScheduleCursor) {
  const query = new URLSearchParams({
    cursorCreatedAt: cursor.createdAt,
    cursorScheduleId: cursor.scheduleId.value,
    take: String(schedulePageSize),
  });
  return `schedules?${query}`;
}

export function createUpcomingSchedulePagePath(cursor: WorkScheduleUpcomingCursor) {
  const query = new URLSearchParams({
    cursorNextRunAt: cursor.nextRunAt,
    cursorScheduleId: cursor.scheduleId.value,
    take: String(schedulePageSize),
  });
  return `schedules/upcoming?${query}`;
}

export function loadScheduleOverview(
  connection: WorkableConnection,
  selectedScheduleId?: string | null,
  signal?: AbortSignal,
  window?: ScheduleOverviewWindow
) {
  return workableFetch<WorkScheduleOverviewResult>(
    connection,
    createScheduleOverviewPath(selectedScheduleId, window),
    { signal },
    { coalesce: false }
  );
}

export function appendProviderOrderedSchedulePage(
  current: WorkScheduleSummary[],
  incoming: WorkScheduleSummary[],
  maximumSize = scheduleLoadedWindowSize
) {
  const seen = new Set(current.map((schedule) => schedule.id.value));
  const combined = [...current];
  for (const schedule of incoming) {
    if (!seen.has(schedule.id.value)) {
      seen.add(schedule.id.value);
      combined.push(schedule);
    }
  }
  const trimCount = Math.max(0, combined.length - maximumSize);
  return {
    schedules: trimCount === 0 ? combined : combined.slice(trimCount),
    trimmedThrough: trimCount === 0 ? null : combined[trimCount - 1],
  };
}

const dateTimeFormatter = new Intl.DateTimeFormat(undefined, {
  dateStyle: "medium",
  timeStyle: "short",
});

export function formatScheduleDateTime(value?: string | null) {
  if (!value) {
    return "-";
  }

  const date = new Date(value);
  return Number.isNaN(date.getTime()) ? value : dateTimeFormatter.format(date);
}

export function formatScheduleTiming(schedule: WorkScheduleSummary) {
  if (schedule.timing.cronExpression) {
    return `${schedule.timing.cronExpression} · ${schedule.timing.timeZoneId || "UTC"}`;
  }

  if (schedule.timing.interval) {
    return `Every ${schedule.timing.interval}`;
  }

  return "Once";
}

export function getUpcomingSchedules(schedules: WorkScheduleSummary[]) {
  return schedules.filter(
    (schedule) => schedule.status === "Active" && Boolean(schedule.nextRunAt)
  );
}

export function SchedulesView({
  connection,
  isLoadingTarget,
  onOpenWorker,
  onReady,
  pollIntervalMs = schedulePollIntervalMs,
  loadedWindowSize = scheduleLoadedWindowSize,
  refreshToken,
}: {
  connection: WorkableConnection;
  isLoadingTarget: boolean;
  onOpenWorker: (workerId: string) => void;
  onReady: () => void;
  pollIntervalMs?: number;
  loadedWindowSize?: number;
  refreshToken: number;
}) {
  const [schedules, setSchedules] = useState<WorkScheduleSummary[]>([]);
  const [recentCursor, setRecentCursor] = useState<WorkScheduleCursor | null>(null);
  const [upcomingSchedules, setUpcomingSchedules] = useState<WorkScheduleSummary[]>([]);
  const [upcomingCursor, setUpcomingCursor] = useState<WorkScheduleUpcomingCursor | null>(null);
  const [activeCount, setActiveCount] = useState(0);
  const [upcomingCount, setUpcomingCount] = useState(0);
  const [recurringCount, setRecurringCount] = useState(0);
  const [loading, setLoading] = useState(false);
  const [loadingRecentPage, setLoadingRecentPage] = useState(false);
  const [loadingUpcomingPage, setLoadingUpcomingPage] = useState(false);
  const [recentWindowTrimmed, setRecentWindowTrimmed] = useState(false);
  const [upcomingWindowTrimmed, setUpcomingWindowTrimmed] = useState(false);
  const [error, setError] = useState<string>();
  const [localRefreshToken, setLocalRefreshToken] = useState(0);
  const [selectedScheduleId, setSelectedScheduleId] = useState<string | null>(null);
  const [selectedSchedule, setSelectedSchedule] = useState<WorkScheduleSummary | null>(null);
  const [occurrences, setOccurrences] = useState<WorkScheduleOccurrence[]>([]);
  const [occurrencesLoading, setOccurrencesLoading] = useState(false);
  const [pendingCancel, setPendingCancel] = useState<WorkScheduleSummary | null>(null);
  const [canceling, setCanceling] = useState(false);
  const [pickerOpen, setPickerOpen] = useState(false);
  const [pickerPath, setPickerPath] = useState("");
  const [pickerError, setPickerError] = useState<string>();
  const [loadingDefinitionName, setLoadingDefinitionName] = useState<string | null>(null);
  const [queueDialog, setQueueDialog] = useState<QueueScheduleDialogState | null>(null);
  const [pollState, setPollState] = useState({ consecutiveFailures: 0, revision: 0 });
  const loadingRef = useRef(false);
  const pagingRef = useRef(false);
  const recentExpandedRef = useRef(false);
  const upcomingExpandedRef = useRef(false);
  const recentWindowTakeRef = useRef(schedulePageSize);
  const upcomingWindowTakeRef = useRef(schedulePageSize);
  const recentWindowStartCursorRef = useRef<WorkScheduleCursor | null>(null);
  const upcomingWindowStartCursorRef = useRef<WorkScheduleUpcomingCursor | null>(null);
  const schedulesRef = useRef<WorkScheduleSummary[]>([]);
  const upcomingSchedulesRef = useRef<WorkScheduleSummary[]>([]);
  const connectionGenerationRef = useRef(0);
  const selectedScheduleIdRef = useRef<string | null>(null);

  useLayoutEffect(() => {
    connectionGenerationRef.current += 1;
    pagingRef.current = false;
    recentExpandedRef.current = false;
    upcomingExpandedRef.current = false;
    recentWindowTakeRef.current = schedulePageSize;
    upcomingWindowTakeRef.current = schedulePageSize;
    recentWindowStartCursorRef.current = null;
    upcomingWindowStartCursorRef.current = null;
    schedulesRef.current = [];
    upcomingSchedulesRef.current = [];
    selectedScheduleIdRef.current = null;
    setSchedules([]);
    setRecentCursor(null);
    setUpcomingSchedules([]);
    setUpcomingCursor(null);
    setSelectedScheduleId(null);
    setSelectedSchedule(null);
    setOccurrences([]);
    setActiveCount(0);
    setUpcomingCount(0);
    setRecurringCount(0);
    setLoadingRecentPage(false);
    setLoadingUpcomingPage(false);
    setRecentWindowTrimmed(false);
    setUpcomingWindowTrimmed(false);
    setPendingCancel(null);
    setCanceling(false);
    setPickerOpen(false);
    setPickerPath("");
    setPickerError(undefined);
    setLoadingDefinitionName(null);
    setQueueDialog(null);
  }, [connection.apiUrl, connection.systemName]);

  const reload = useCallback(() => {
    loadingRef.current = true;
    setLocalRefreshToken((current) => current + 1);
  }, []);

  useEffect(() => {
    if (!isLoadingTarget) {
      return;
    }

    let canceled = false;
    const controller = new AbortController();
    loadingRef.current = true;
    queueMicrotask(() => {
      if (!canceled) {
        setLoading(true);
        setOccurrencesLoading(true);
        setError(undefined);
      }
    });
    const overviewWindow: ScheduleOverviewWindow = {
      recentCursor: recentWindowStartCursorRef.current,
      recentTake: recentExpandedRef.current
        ? recentWindowTakeRef.current
        : schedulePageSize,
      upcomingCursor: upcomingWindowStartCursorRef.current,
      upcomingTake: upcomingExpandedRef.current
        ? upcomingWindowTakeRef.current
        : schedulePageSize,
    };
    loadScheduleOverview(
      connection,
      selectedScheduleIdRef.current,
      controller.signal,
      overviewWindow
    )
      .then((overview) => {
        if (canceled) {
          return;
        }

        const resolvedSelectedScheduleId = overview.selectedSchedule?.id.value ?? null;
        selectedScheduleIdRef.current = resolvedSelectedScheduleId;
        schedulesRef.current = overview.recent.schedules;
        setSchedules(overview.recent.schedules);
        setRecentCursor(overview.recent.cursor ?? null);
        upcomingSchedulesRef.current = overview.upcoming.schedules;
        setUpcomingSchedules(overview.upcoming.schedules);
        setUpcomingCursor(overview.upcoming.cursor ?? null);
        setActiveCount(overview.upcoming.activeScheduleCount);
        setUpcomingCount(overview.upcoming.upcomingScheduleCount);
        setRecurringCount(overview.upcoming.recurringScheduleCount);
        setSelectedScheduleId(resolvedSelectedScheduleId);
        setSelectedSchedule(overview.selectedSchedule ?? null);
        setOccurrences(overview.occurrences);
        setLoading(false);
        setOccurrencesLoading(false);
        setPollState((current) => ({
          consecutiveFailures: 0,
          revision: current.revision + 1,
        }));
        onReady();
      })
      .catch((caught) => {
        if (!canceled) {
          setError(caught instanceof Error ? caught.message : "Schedules could not be loaded.");
          setLoading(false);
          setOccurrencesLoading(false);
          setPollState((current) => ({
            consecutiveFailures: current.consecutiveFailures + 1,
            revision: current.revision + 1,
          }));
          onReady();
        }
      })
      .finally(() => {
        if (!canceled) {
          loadingRef.current = false;
        }
      });

    return () => {
      canceled = true;
      controller.abort();
    };
  }, [connection, isLoadingTarget, loadedWindowSize, localRefreshToken, onReady, refreshToken]);

  useEffect(() => {
    if (!isLoadingTarget) {
      return;
    }

    const refreshIfVisible = () => {
      if (document.visibilityState === "visible" && !loadingRef.current && !pagingRef.current) {
        reload();
      }
    };

    const timeout = window.setTimeout(
      refreshIfVisible,
      calculateSchedulePollDelay(pollIntervalMs, pollState.consecutiveFailures)
    );
    document.addEventListener("visibilitychange", refreshIfVisible);
    return () => {
      window.clearTimeout(timeout);
      document.removeEventListener("visibilitychange", refreshIfVisible);
    };
  }, [isLoadingTarget, pollIntervalMs, pollState, reload]);

  const selectSchedule = useCallback((scheduleId: string) => {
    selectedScheduleIdRef.current = scheduleId;
    setSelectedScheduleId(scheduleId);
    setSelectedSchedule(
      schedules.find((schedule) => schedule.id.value === scheduleId) ??
      upcomingSchedules.find((schedule) => schedule.id.value === scheduleId) ??
      null
    );
    reload();
  }, [reload, schedules, upcomingSchedules]);

  const upcoming = useMemo(() => getUpcomingSchedules(upcomingSchedules), [upcomingSchedules]);

  const loadMoreRecent = async (cursor: WorkScheduleCursor) => {
    if (pagingRef.current) {
      return;
    }

    pagingRef.current = true;
    const generation = connectionGenerationRef.current;
    setLoadingRecentPage(true);
    setError(undefined);
    try {
      const page = await workableFetch<WorkScheduleQueryResult>(
        connection,
        createSchedulePagePath(cursor)
      );
      if (generation !== connectionGenerationRef.current) {
        return;
      }
      const result = appendProviderOrderedSchedulePage(
        schedulesRef.current,
        page.schedules,
        loadedWindowSize
      );
      schedulesRef.current = result.schedules;
      setSchedules(result.schedules);
      recentWindowTakeRef.current = recentExpandedRef.current
        ? Math.min(
            loadedWindowSize,
            Math.max(recentWindowTakeRef.current, result.schedules.length)
          )
        : Math.max(1, result.schedules.length);
      if (result.trimmedThrough) {
        recentWindowStartCursorRef.current = {
          createdAt: result.trimmedThrough.createdAt,
          scheduleId: result.trimmedThrough.id,
        };
        setRecentWindowTrimmed(true);
      }
      setRecentCursor(page.cursor ?? null);
      recentExpandedRef.current = true;
    } catch (caught) {
      if (generation === connectionGenerationRef.current) {
        setError(caught instanceof Error ? caught.message : "More schedules could not be loaded.");
      }
    } finally {
      if (generation === connectionGenerationRef.current) {
        pagingRef.current = false;
        setLoadingRecentPage(false);
      }
    }
  };

  const loadMoreUpcoming = async (cursor: WorkScheduleUpcomingCursor) => {
    if (pagingRef.current) {
      return;
    }

    pagingRef.current = true;
    const generation = connectionGenerationRef.current;
    setLoadingUpcomingPage(true);
    setError(undefined);
    try {
      const page = await workableFetch<WorkScheduleUpcomingQueryResult>(
        connection,
        createUpcomingSchedulePagePath(cursor)
      );
      if (generation !== connectionGenerationRef.current) {
        return;
      }
      const result = appendProviderOrderedSchedulePage(
        upcomingSchedulesRef.current,
        page.schedules,
        loadedWindowSize
      );
      upcomingSchedulesRef.current = result.schedules;
      setUpcomingSchedules(result.schedules);
      upcomingWindowTakeRef.current = upcomingExpandedRef.current
        ? Math.min(
            loadedWindowSize,
            Math.max(upcomingWindowTakeRef.current, result.schedules.length)
          )
        : Math.max(1, result.schedules.length);
      if (result.trimmedThrough) {
        upcomingWindowStartCursorRef.current = {
          nextRunAt: result.trimmedThrough.nextRunAt!,
          scheduleId: result.trimmedThrough.id,
        };
        setUpcomingWindowTrimmed(true);
      }
      setUpcomingCursor(page.cursor ?? null);
      setActiveCount(page.activeScheduleCount);
      setUpcomingCount(page.upcomingScheduleCount);
      setRecurringCount(page.recurringScheduleCount);
      upcomingExpandedRef.current = true;
    } catch (caught) {
      if (generation === connectionGenerationRef.current) {
        setError(caught instanceof Error ? caught.message : "More upcoming schedules could not be loaded.");
      }
    } finally {
      if (generation === connectionGenerationRef.current) {
        pagingRef.current = false;
        setLoadingUpcomingPage(false);
      }
    }
  };

  const cancelSchedule = async () => {
    if (!pendingCancel) {
      return;
    }

    const generation = connectionGenerationRef.current;
    const schedule = pendingCancel;
    setCanceling(true);
    setError(undefined);
    try {
      const outcome = await workableFetch<WorkScheduleCancellationOutcome>(
        connection,
        `schedules/${schedule.id.value}/cancel`,
        { method: "POST" }
      );
      if (generation !== connectionGenerationRef.current) {
        return;
      }

      if (outcome.status !== "Accepted") {
        const detail = outcome.messages.map((message) => message.text).filter(Boolean).join(" ");
        setError(detail || `Cancellation returned ${outcome.status}.`);
        return;
      }

      if (outcome.schedule) {
        const recent = schedulesRef.current.map((schedule) =>
          schedule.id.value === outcome.schedule!.id.value ? outcome.schedule! : schedule
        );
        schedulesRef.current = recent;
        setSchedules(recent);
        const nextUpcoming = upcomingSchedulesRef.current.filter((schedule) =>
          schedule.id.value !== outcome.schedule!.id.value
        );
        upcomingSchedulesRef.current = nextUpcoming;
        setUpcomingSchedules(nextUpcoming);
        setSelectedSchedule(outcome.schedule);
      }
      setPendingCancel(null);
      reload();
    } catch (caught) {
      if (generation === connectionGenerationRef.current) {
        setError(caught instanceof Error ? caught.message : "Schedule could not be canceled.");
      }
    } finally {
      if (generation === connectionGenerationRef.current) {
        setCanceling(false);
      }
    }
  };

  return (
    <>
      <ConsolePageLayout
        toolbar={(
          <Button
            disabled={connection.schedulingAvailable !== true}
            onClick={() => {
              setPickerError(undefined);
              setPickerOpen(true);
            }}
            size="sm"
          >
            <Plus className="size-4" />
            New schedule
          </Button>
        )}
      >
        <div className="space-y-6 pb-2">
          {error && <ErrorBanner message={error} title="Schedule operation failed" />}
          <div className="grid gap-3 sm:grid-cols-3">
            <ScheduleMetric label="Active schedules" value={activeCount} />
            <ScheduleMetric label="Upcoming work" value={upcomingCount} />
            <ScheduleMetric label="Recurring schedules" value={recurringCount} />
          </div>

          <PanelShell
            description="The next persisted execution for the active schedules shown on this page, ordered by due time."
            title="Upcoming work"
          >
            <ScheduleTableEmptyOrLoading
              empty="No scheduled work is currently pending."
              loading={loading}
              renderWhenEmpty={upcomingWindowTrimmed}
              rows={upcoming.length}
            >
              <PanelScrollViewport
                className="schedule-upcoming-viewport h-[28rem] rounded-lg border [&_[data-slot=table-container]]:overflow-visible"
                hasMore={Boolean(upcomingCursor)}
                loadedCount={upcoming.length}
                loading={loading}
                loadingMore={loadingUpcomingPage}
                noun="upcoming schedule"
                onLoadMore={() => void loadMoreUpcoming(upcomingCursor!)}
              >
                <Table>
                  <TableHeader className="sticky top-0 z-10 bg-card shadow-[0_1px_0_var(--border)]">
                    <TableRow>
                      <TableHead>Definition</TableHead>
                      <TableHead>Next execution</TableHead>
                      <TableHead>Timing</TableHead>
                      <TableHead>Created by</TableHead>
                    </TableRow>
                  </TableHeader>
                  <TableBody>
                    {upcoming.length === 0 && (
                      <TableRow>
                        <TableCell className="py-8 text-center text-muted-foreground" colSpan={4}>
                          No scheduled work is currently pending in this window.
                        </TableCell>
                      </TableRow>
                    )}
                    {upcoming.map((schedule) => (
                      <TableRow
                        className="cursor-pointer"
                        data-state={selectedScheduleId === schedule.id.value ? "selected" : undefined}
                        key={schedule.id.value}
                        onClick={() => selectSchedule(schedule.id.value)}
                      >
                        <TableCell className="font-mono font-medium">{schedule.definitionName}</TableCell>
                        <TableCell>
                          <div>{formatScheduleDateTime(schedule.nextRunAt)}</div>
                          <div className="text-muted-foreground text-xs"><LiveRelativeTime value={schedule.nextRunAt} /></div>
                        </TableCell>
                        <TableCell className="max-w-80 truncate" title={formatScheduleTiming(schedule)}>
                          {formatScheduleTiming(schedule)}
                        </TableCell>
                        <TableCell>{schedule.createdBy.name || schedule.createdBy.email || schedule.createdBy.id || "Unknown"}</TableCell>
                      </TableRow>
                    ))}
                  </TableBody>
                </Table>
                {upcomingWindowTrimmed && (
                  <div className="flex justify-center border-t p-3">
                    <Button
                      disabled={loading}
                      onClick={() => {
                        upcomingExpandedRef.current = false;
                        upcomingWindowTakeRef.current = schedulePageSize;
                        upcomingWindowStartCursorRef.current = null;
                        upcomingSchedulesRef.current = [];
                        setUpcomingSchedules([]);
                        setUpcomingCursor(null);
                        setUpcomingWindowTrimmed(false);
                        reload();
                      }}
                      size="sm"
                      variant="ghost"
                    >
                      Return to soonest
                    </Button>
                  </div>
                )}
              </PanelScrollViewport>
            </ScheduleTableEmptyOrLoading>
          </PanelShell>

          <PanelShell
            description="Active, completed, and canceled durable schedules retained by this work system."
            title="Schedules"
          >
            <ScheduleTableEmptyOrLoading
              empty="No schedules have been created for this work system."
              loading={loading}
              renderWhenEmpty={recentWindowTrimmed}
              rows={schedules.length}
            >
              <PanelScrollViewport
                className="schedule-recent-viewport h-[28rem] rounded-lg border [&_[data-slot=table-container]]:overflow-visible"
                hasMore={Boolean(recentCursor)}
                loadedCount={schedules.length}
                loading={loading}
                loadingMore={loadingRecentPage}
                noun="schedule"
                onLoadMore={() => void loadMoreRecent(recentCursor!)}
              >
                <Table>
                  <TableHeader className="sticky top-0 z-10 bg-card shadow-[0_1px_0_var(--border)]">
                    <TableRow>
                      <TableHead>Definition</TableHead>
                      <TableHead>Status</TableHead>
                      <TableHead>Timing</TableHead>
                      <TableHead>Next run</TableHead>
                      <TableHead>Last run</TableHead>
                      <TableHead className="w-10" />
                    </TableRow>
                  </TableHeader>
                  <TableBody>
                    {schedules.length === 0 && (
                      <TableRow>
                        <TableCell className="py-8 text-center text-muted-foreground" colSpan={6}>
                          No schedules remain in this window.
                        </TableCell>
                      </TableRow>
                    )}
                    {schedules.map((schedule) => (
                      <TableRow
                        className="cursor-pointer"
                        data-state={selectedScheduleId === schedule.id.value ? "selected" : undefined}
                        key={schedule.id.value}
                        onClick={() => selectSchedule(schedule.id.value)}
                      >
                        <TableCell className="font-mono font-medium">{schedule.definitionName}</TableCell>
                        <TableCell><ScheduleStatusBadge status={schedule.status} /></TableCell>
                        <TableCell className="max-w-72 truncate" title={formatScheduleTiming(schedule)}>
                          {formatScheduleTiming(schedule)}
                        </TableCell>
                        <TableCell>{formatScheduleDateTime(schedule.nextRunAt)}</TableCell>
                        <TableCell>{formatScheduleDateTime(schedule.lastRunAt)}</TableCell>
                        <TableCell><ChevronRight className="size-4 text-muted-foreground" /></TableCell>
                      </TableRow>
                    ))}
                  </TableBody>
                </Table>
                {recentWindowTrimmed && (
                  <div className="flex justify-center border-t p-3">
                    <Button
                      disabled={loading}
                      onClick={() => {
                        recentExpandedRef.current = false;
                        recentWindowTakeRef.current = schedulePageSize;
                        recentWindowStartCursorRef.current = null;
                        schedulesRef.current = [];
                        setSchedules([]);
                        setRecentCursor(null);
                        setRecentWindowTrimmed(false);
                        reload();
                      }}
                      size="sm"
                      variant="ghost"
                    >
                      Return to newest
                    </Button>
                  </div>
                )}
              </PanelScrollViewport>
            </ScheduleTableEmptyOrLoading>
          </PanelShell>

          {selectedSchedule && (
            <PanelShell
              description={`${selectedSchedule.id.value} · created ${formatScheduleDateTime(selectedSchedule.createdAt)}`}
              title={(
                <span className="flex items-center gap-2">
                  <CalendarClock className="size-4 text-sky-300" />
                  {selectedSchedule.definitionName}
                </span>
              )}
              actions={selectedSchedule.status === "Active" ? (
                <Button onClick={() => setPendingCancel(selectedSchedule)} size="sm" variant="outline">
                  <XCircle className="size-4" />
                  Cancel schedule
                </Button>
              ) : undefined}
            >
              <div className="grid gap-3 rounded-lg border bg-muted/20 p-3 text-sm sm:grid-cols-4">
                <ScheduleDetail label="Status" value={selectedSchedule.status} />
                <ScheduleDetail label="Timing" value={formatScheduleTiming(selectedSchedule)} />
                <ScheduleDetail label="Next execution" value={formatScheduleDateTime(selectedSchedule.nextRunAt)} />
                <ScheduleDetail label="Run after downtime" value={selectedSchedule.timing.runMissedExecution ? "Yes" : "No"} />
              </div>
              <div>
                <h3 className="mb-2 font-medium text-sm">Recent dispatches</h3>
                <ScheduleTableEmptyOrLoading
                  empty="This schedule has no retained dispatches yet."
                  loading={occurrencesLoading}
                  rows={occurrences.length}
                >
                    <Table>
                      <TableHeader>
                        <TableRow>
                          <TableHead>Scheduled</TableHead>
                          <TableHead>Attempted</TableHead>
                          <TableHead>Result</TableHead>
                          <TableHead>Queue result</TableHead>
                          <TableHead>Worker</TableHead>
                        </TableRow>
                      </TableHeader>
                      <TableBody>
                        {occurrences.map((occurrence) => (
                          <TableRow key={occurrence.occurrenceId}>
                            <TableCell>{formatScheduleDateTime(occurrence.scheduledAt)}</TableCell>
                            <TableCell>{formatScheduleDateTime(occurrence.attemptedAt)}</TableCell>
                            <TableCell><OccurrenceStatusBadge occurrence={occurrence} /></TableCell>
                            <TableCell>{occurrence.queueStatus || "-"}</TableCell>
                            <TableCell>
                              {occurrence.workerId ? (
                                <Button
                                  className="h-auto p-0 font-mono text-xs"
                                  onClick={() => onOpenWorker(occurrence.workerId!.value)}
                                  variant="link"
                                >
                                  {occurrence.workerId.value}
                                </Button>
                              ) : "-"}
                            </TableCell>
                          </TableRow>
                        ))}
                      </TableBody>
                    </Table>
                </ScheduleTableEmptyOrLoading>
              </div>
            </PanelShell>
          )}
        </div>
      </ConsolePageLayout>

      <ScheduleDefinitionPicker
        connection={connection}
        error={pickerError}
        loadingDefinitionName={loadingDefinitionName}
        onDefinitionSelected={async (definitionName, loadDefinitionInfo) => {
          const generation = connectionGenerationRef.current;
          setPickerError(undefined);
          setLoadingDefinitionName(definitionName);
          try {
            const info = await loadDefinitionInfo(definitionName);
            if (generation !== connectionGenerationRef.current) {
              return;
            }

            setQueueDialog({
              definition: info.definition,
              queueRequestSchema: info.queueRequestSchema,
            });
            setPickerOpen(false);
          } catch (caught) {
            if (generation === connectionGenerationRef.current) {
              setPickerError(caught instanceof Error ? caught.message : "Definition could not be loaded.");
            }
          } finally {
            if (generation === connectionGenerationRef.current) {
              setLoadingDefinitionName(null);
            }
          }
        }}
        onOpenChange={setPickerOpen}
        onPathChange={setPickerPath}
        open={pickerOpen}
        path={pickerPath}
      />

      <QueueDialog
        connection={connection}
        definition={queueDialog?.definition ?? null}
        fetchQueueSchemaWhenNeeded={false}
        initialSchedulePanelOpen
        onOpenChange={(open) => {
          if (!open) {
            setQueueDialog(null);
          }
        }}
        onQueuedWorker={onOpenWorker}
        onScheduled={reload}
        preloadedQueueSchemaDescriptor={queueDialog?.queueRequestSchema ?? null}
      />

      <AlertDialog
        onOpenChange={(open) => {
          if (!open && !canceling) {
            setPendingCancel(null);
          }
        }}
        open={pendingCancel !== null}
      >
        <AlertDialogContent>
          <AlertDialogHeader>
            <AlertDialogTitle>Cancel this schedule?</AlertDialogTitle>
            <AlertDialogDescription>
              {pendingCancel
                ? `Future runs of ${pendingCancel.definitionName} will not be queued. Workers already created by this schedule are unaffected.`
                : "Future runs will not be queued."}
            </AlertDialogDescription>
          </AlertDialogHeader>
          <AlertDialogFooter>
            <AlertDialogCancel disabled={canceling}>Keep schedule</AlertDialogCancel>
            <AlertDialogAction
              className="bg-[var(--status-danger-solid)] text-[var(--status-danger-contrast)] hover:bg-[var(--status-danger-text)] focus-visible:ring-[var(--status-danger-border)]"
              disabled={canceling}
              onClick={(event) => {
                event.preventDefault();
                void cancelSchedule();
              }}
            >
              {canceling && <Loader2 className="size-4 animate-spin" />}
              Cancel schedule
            </AlertDialogAction>
          </AlertDialogFooter>
        </AlertDialogContent>
      </AlertDialog>
    </>
  );
}

function ScheduleMetric({ label, value }: { label: string; value: number }) {
  return (
    <div className="rounded-xl bg-card p-4 ring-1 ring-foreground/10">
      <div className="text-muted-foreground text-xs">{label}</div>
      <div className="mt-1 font-semibold text-2xl tabular-nums">{value}</div>
    </div>
  );
}

function ScheduleDetail({ label, value }: { label: string; value: string }) {
  return (
    <div className="min-w-0">
      <div className="text-muted-foreground text-xs">{label}</div>
      <div className="mt-1 truncate" title={value}>{value}</div>
    </div>
  );
}

function ScheduleStatusBadge({ status }: { status: WorkScheduleSummary["status"] }) {
  return (
    <Badge
      className={
        status === "Active"
          ? "border-sky-400/30 bg-sky-400/10 text-sky-300"
          : status === "Completed"
            ? "border-emerald-400/30 bg-emerald-400/10 text-emerald-300"
            : "border-muted-foreground/30 bg-muted text-muted-foreground"
      }
      variant="outline"
    >
      {status}
    </Badge>
  );
}

function OccurrenceStatusBadge({ occurrence }: { occurrence: WorkScheduleOccurrence }) {
  return (
    <Badge
      className={
        occurrence.status === "Accepted"
          ? "border-emerald-400/30 bg-emerald-400/10 text-emerald-300"
          : occurrence.status === "Skipped"
            ? "border-amber-400/30 bg-amber-400/10 text-amber-300"
            : "border-red-400/30 bg-red-400/10 text-red-300"
      }
      variant="outline"
    >
      {occurrence.status}
    </Badge>
  );
}

function ScheduleTableEmptyOrLoading({
  children,
  empty,
  loading,
  renderWhenEmpty = false,
  rows,
}: {
  children: ReactNode;
  empty: string;
  loading: boolean;
  renderWhenEmpty?: boolean;
  rows: number;
}) {
  if (loading && rows === 0) {
    return <div className="space-y-2">{Array.from({ length: 3 }).map((_, index) => <Skeleton className="h-12" key={index} />)}</div>;
  }

  if (rows === 0 && !renderWhenEmpty) {
    return <ConsoleEmptyState padding="spacious">{empty}</ConsoleEmptyState>;
  }

  return children;
}

function ScheduleDefinitionPicker({
  connection,
  error,
  loadingDefinitionName,
  onDefinitionSelected,
  onOpenChange,
  onPathChange,
  open,
  path,
}: {
  connection: WorkableConnection;
  error?: string;
  loadingDefinitionName: string | null;
  onDefinitionSelected: (
    definitionName: string,
    loadDefinitionInfo: (definitionName: string) => Promise<import("@/lib/workable").WorkInfo>
  ) => Promise<void>;
  onOpenChange: (open: boolean) => void;
  onPathChange: (path: string) => void;
  open: boolean;
  path: string;
}) {
  return (
    <Dialog onOpenChange={onOpenChange} open={open}>
      <DialogContent className="flex max-h-[80vh] flex-col sm:max-w-2xl">
        <DialogHeader>
          <DialogTitle>Choose work to schedule</DialogTitle>
          <DialogDescription>
            Select a definition, then configure its input and schedule.
          </DialogDescription>
        </DialogHeader>
        {error && <ErrorBanner message={error} title="Definition unavailable" />}
        <DefinitionCatalogBrowser
          backButtonClassName={defaultCatalogBrowserBackButtonClassName()}
          bodyClassName="workable-grid-scrollbar max-h-[55vh] overflow-y-auto"
          connection={connection}
          emptyState={<ConsoleEmptyState padding="spacious">No definitions are available.</ConsoleEmptyState>}
          headerClassName={defaultCatalogBrowserHeaderClassName("rounded-t-lg border")}
          loadingState={Array.from({ length: 5 }).map((_, index) => <Skeleton className="mx-2 my-2 h-9" key={index} />)}
          onNavigate={onPathChange}
          path={path}
          renderCategory={(category) => (
            <button
              className="flex h-10 w-full items-center gap-2 border-x px-3 text-left text-sm hover:bg-muted"
              onClick={() => onPathChange(category.path)}
              type="button"
            >
              <Folder className="size-4 text-sky-300" />
              <span className="flex-1">{category.label}</span>
              <span className="text-muted-foreground text-xs">{category.count}</span>
              <ChevronRight className="size-4 text-muted-foreground" />
            </button>
          )}
          renderDefinition={(definition, catalog) => (
            <button
              className="flex h-10 w-full items-center gap-2 border-x px-3 text-left text-sm hover:bg-muted disabled:opacity-60"
              disabled={loadingDefinitionName !== null}
              onClick={() => void onDefinitionSelected(definition.name, catalog.loadDefinitionInfo)}
              type="button"
            >
              {loadingDefinitionName === definition.name ? (
                <Loader2 className="size-4 animate-spin text-sky-300" />
              ) : (
                <FileCode2 className="size-4 text-sky-300" />
              )}
              <span className="font-mono">{definition.name}</span>
            </button>
          )}
          renderError={(message) => <ErrorBanner message={message} title="Catalog unavailable" />}
          titleClassName={defaultCatalogBrowserTitleClassName()}
        />
      </DialogContent>
    </Dialog>
  );
}
