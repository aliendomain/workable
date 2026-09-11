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
import { type ReactNode, useCallback, useEffect, useMemo, useState } from "react";
import { ConsoleEmptyState } from "@/components/features/console/empty-state";
import { ConsolePageLayout } from "@/components/features/console/console-primitives";
import { PanelShell } from "@/components/features/console/panel-shell";
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
  type WorkScheduleOccurrenceQueryResult,
  type WorkScheduleQueryResult,
  type WorkScheduleSnapshot,
  type WorkScheduleSummary,
  type WorkableConnection,
} from "@/lib/workable";

type QueueScheduleDialogState = {
  definition: WorkDefinition;
  queueRequestSchema: QueueRequestSchemaDescriptor;
};

const schedulePageSize = 1000;

export async function loadScheduleIndex(connection: WorkableConnection): Promise<WorkScheduleSummary[]> {
  const recent = await workableFetch<WorkScheduleQueryResult>(
    connection,
    `schedules?take=${schedulePageSize}`
  );
  if (recent.schedules.length < schedulePageSize) {
    return recent.schedules;
  }

  const active = await workableFetch<WorkScheduleQueryResult>(
    connection,
    `schedules?status=Active&take=${schedulePageSize}`
  );
  const schedulesById = new Map(
    recent.schedules.map((schedule) => [schedule.id.value, schedule] as const)
  );
  for (const schedule of active.schedules) {
    schedulesById.set(schedule.id.value, schedule);
  }

  return Array.from(schedulesById.values());
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
  return schedules
    .filter((schedule) => schedule.status === "Active" && Boolean(schedule.nextRunAt))
    .toSorted((left, right) => Date.parse(left.nextRunAt!) - Date.parse(right.nextRunAt!));
}

export function SchedulesView({
  connection,
  isLoadingTarget,
  onOpenWorker,
  onReady,
  refreshToken,
}: {
  connection: WorkableConnection;
  isLoadingTarget: boolean;
  onOpenWorker: (workerId: string) => void;
  onReady: () => void;
  refreshToken: number;
}) {
  const [schedules, setSchedules] = useState<WorkScheduleSummary[]>([]);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string>();
  const [localRefreshToken, setLocalRefreshToken] = useState(0);
  const [selectedScheduleId, setSelectedScheduleId] = useState<string | null>(null);
  const [occurrences, setOccurrences] = useState<WorkScheduleOccurrence[]>([]);
  const [occurrencesLoading, setOccurrencesLoading] = useState(false);
  const [occurrencesError, setOccurrencesError] = useState<string>();
  const [pendingCancel, setPendingCancel] = useState<WorkScheduleSummary | null>(null);
  const [canceling, setCanceling] = useState(false);
  const [pickerOpen, setPickerOpen] = useState(false);
  const [pickerPath, setPickerPath] = useState("");
  const [pickerError, setPickerError] = useState<string>();
  const [loadingDefinitionName, setLoadingDefinitionName] = useState<string | null>(null);
  const [queueDialog, setQueueDialog] = useState<QueueScheduleDialogState | null>(null);

  const reload = useCallback(() => setLocalRefreshToken((current) => current + 1), []);

  useEffect(() => {
    if (!isLoadingTarget) {
      return;
    }

    let canceled = false;
    queueMicrotask(() => {
      if (!canceled) {
        setLoading(true);
        setError(undefined);
      }
    });
    loadScheduleIndex(connection)
      .then((loadedSchedules) => {
        if (canceled) {
          return;
        }

        setSchedules(loadedSchedules);
        setSelectedScheduleId((current) =>
          current && loadedSchedules.some((schedule) => schedule.id.value === current)
            ? current
            : loadedSchedules[0]?.id.value ?? null
        );
        setLoading(false);
        onReady();
      })
      .catch((caught) => {
        if (!canceled) {
          setError(caught instanceof Error ? caught.message : "Schedules could not be loaded.");
          setLoading(false);
          onReady();
        }
      });

    return () => {
      canceled = true;
    };
  }, [connection, isLoadingTarget, localRefreshToken, onReady, refreshToken]);

  const selectedSchedule = schedules.find((schedule) => schedule.id.value === selectedScheduleId) ?? null;
  useEffect(() => {
    if (!selectedScheduleId) {
      queueMicrotask(() => {
        setOccurrences([]);
        setOccurrencesError(undefined);
        setOccurrencesLoading(false);
      });
      return;
    }

    let canceled = false;
    queueMicrotask(() => {
      if (!canceled) {
        setOccurrencesLoading(true);
        setOccurrencesError(undefined);
      }
    });
    workableFetch<WorkScheduleOccurrenceQueryResult>(
      connection,
      `schedules/${selectedScheduleId}/occurrences?take=50`
    )
      .then((result) => {
        if (!canceled) {
          setOccurrences(result.occurrences);
          setOccurrencesLoading(false);
        }
      })
      .catch((caught) => {
        if (!canceled) {
          setOccurrences([]);
          setOccurrencesError(
            caught instanceof Error ? caught.message : "Schedule history could not be loaded."
          );
          setOccurrencesLoading(false);
        }
      });

    return () => {
      canceled = true;
    };
  }, [connection, selectedScheduleId, localRefreshToken, refreshToken]);

  const upcoming = useMemo(() => getUpcomingSchedules(schedules), [schedules]);
  const activeCount = schedules.filter((schedule) => schedule.status === "Active").length;
  const recurringCount = schedules.filter((schedule) =>
    schedule.status === "Active" && Boolean(schedule.timing.interval || schedule.timing.cronExpression)
  ).length;

  const cancelSchedule = async () => {
    if (!pendingCancel) {
      return;
    }

    setCanceling(true);
    setError(undefined);
    try {
      const outcome = await workableFetch<WorkScheduleCancellationOutcome>(
        connection,
        `schedules/${pendingCancel.id.value}/cancel`,
        { method: "POST" }
      );
      if (outcome.status !== "Accepted") {
        const detail = outcome.messages.map((message) => message.text).filter(Boolean).join(" ");
        setError(detail || `Cancellation returned ${outcome.status}.`);
        return;
      }

      if (outcome.schedule) {
        setSchedules((current) => current.map((schedule) =>
          schedule.id.value === outcome.schedule!.id.value ? outcome.schedule! : schedule
        ));
      }
      setPendingCancel(null);
      reload();
    } catch (caught) {
      setError(caught instanceof Error ? caught.message : "Schedule could not be canceled.");
    } finally {
      setCanceling(false);
    }
  };

  return (
    <>
      <ConsolePageLayout
        fill
        scrollMode="panel"
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
        <div className="workable-grid-scrollbar min-h-0 flex-1 space-y-6 overflow-y-auto pb-2">
          {error && <ErrorBanner message={error} title="Schedule operation failed" />}
          <div className="grid gap-3 sm:grid-cols-3">
            <ScheduleMetric label="Active schedules" value={activeCount} />
            <ScheduleMetric label="Upcoming work" value={upcoming.length} />
            <ScheduleMetric label="Recurring schedules" value={recurringCount} />
          </div>

          <PanelShell
            description="The next persisted execution for every active schedule, ordered by due time."
            title="Upcoming work"
          >
            <ScheduleTableEmptyOrLoading
              empty="No scheduled work is currently pending."
              loading={loading}
              rows={upcoming.length}
            >
              <Table>
                <TableHeader>
                  <TableRow>
                    <TableHead>Definition</TableHead>
                    <TableHead>Next execution</TableHead>
                    <TableHead>Timing</TableHead>
                    <TableHead>Created by</TableHead>
                    <TableHead className="w-24" />
                  </TableRow>
                </TableHeader>
                <TableBody>
                  {upcoming.map((schedule) => (
                    <TableRow
                      className="cursor-pointer"
                      data-state={selectedScheduleId === schedule.id.value ? "selected" : undefined}
                      key={schedule.id.value}
                      onClick={() => setSelectedScheduleId(schedule.id.value)}
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
                      <TableCell>
                        <Button
                          aria-label={`Cancel schedule for ${schedule.definitionName}`}
                          onClick={(event) => {
                            event.stopPropagation();
                            setPendingCancel(schedule);
                          }}
                          size="icon-sm"
                          variant="ghost"
                        >
                          <XCircle className="size-4" />
                        </Button>
                      </TableCell>
                    </TableRow>
                  ))}
                </TableBody>
              </Table>
            </ScheduleTableEmptyOrLoading>
          </PanelShell>

          <PanelShell
            description="Active, completed, and canceled durable schedules retained by this work system."
            title="Schedules"
          >
            <ScheduleTableEmptyOrLoading
              empty="No schedules have been created for this work system."
              loading={loading}
              rows={schedules.length}
            >
              <Table>
                <TableHeader>
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
                  {schedules.map((schedule) => (
                    <TableRow
                      className="cursor-pointer"
                      data-state={selectedScheduleId === schedule.id.value ? "selected" : undefined}
                      key={schedule.id.value}
                      onClick={() => setSelectedScheduleId(schedule.id.value)}
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
                {occurrencesError ? (
                  <ErrorBanner message={occurrencesError} title="Schedule history unavailable" />
                ) : (
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
                )}
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
          setPickerError(undefined);
          setLoadingDefinitionName(definitionName);
          try {
            const info = await loadDefinitionInfo(definitionName);
            setQueueDialog({
              definition: info.definition,
              queueRequestSchema: info.queueRequestSchema,
            });
            setPickerOpen(false);
          } catch (caught) {
            setPickerError(caught instanceof Error ? caught.message : "Definition could not be loaded.");
          } finally {
            setLoadingDefinitionName(null);
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
            <AlertDialogAction disabled={canceling} onClick={(event) => {
              event.preventDefault();
              void cancelSchedule();
            }}>
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
  rows,
}: {
  children: ReactNode;
  empty: string;
  loading: boolean;
  rows: number;
}) {
  if (loading && rows === 0) {
    return <div className="space-y-2">{Array.from({ length: 3 }).map((_, index) => <Skeleton className="h-12" key={index} />)}</div>;
  }

  if (rows === 0) {
    return <ConsoleEmptyState padding="spacious">{empty}</ConsoleEmptyState>;
  }

  return <div className="overflow-x-auto rounded-lg border">{children}</div>;
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
