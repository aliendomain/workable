"use client";

import { useVirtualizer } from "@tanstack/react-virtual";
import {
  Ban,
  Clock3,
  Loader2,
  MoreHorizontal,
  Pause,
  Play,
  SquareArrowOutUpRight,
  Trash2,
} from "lucide-react";
import type { MutableRefObject } from "react";
import { useEffect, useRef } from "react";
import { ConsoleEmptyState, ConsolePlaceholder } from "@/components/features/console/empty-state";
import { PanelScrollViewport } from "@/components/features/console/panel-shell";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import {
  DropdownMenu,
  DropdownMenuContent,
  DropdownMenuItem,
  DropdownMenuTrigger,
} from "@/components/ui/dropdown-menu";
import {
  Table,
  TableBody,
  TableCell,
  TableRow,
} from "@/components/ui/table";
import {
  completionTone,
  formatExecutionDuration,
  type DurationDisplay,
} from "@/components/workable/console/console-format";
import { semanticTextToneClass } from "@/lib/ui/state-tones";
import {
  formatRelativeTime,
  useLiveRelativeTimeNow,
} from "@/components/workable/console/live-relative-time";
import { getIterationRowKey } from "@/components/workable/console/query-data";
import {
  stateTone,
  WorkableApiError,
  type WorkAction,
  type WorkComponentShape,
  type WorkTypedValue,
  type WorkViewIterationGridDetailed,
  type WorkViewWorkerGridDetailed,
  type WorkerState,
} from "@/lib/workable";

type WorkerRowHighlight = {
  fallbackIndex: number;
  workerId: string | null;
};

export function QueryTableStatus({
  label,
}: {
  label: string;
}) {
  return (
    <ConsoleEmptyState fill padding="spacious">
      <span>{label}</span>
    </ConsoleEmptyState>
  );
}

export function QueryTablePlaceholder() {
  return <ConsolePlaceholder fill />;
}

export function isWorkerNotFoundError(error: unknown) {
  if (!(error instanceof WorkableApiError) || error.status !== 404) {
    return false;
  }

  if (!isObjectWithMessages(error.body)) {
    return false;
  }

  return error.body.messages.some((message) => message.code === "workable.worker.not_found");
}

export function isObjectWithMessages(value: unknown): value is { messages: Array<{ code?: string }> } {
  return typeof value === "object" &&
    value !== null &&
    "messages" in value &&
    Array.isArray((value as { messages?: unknown }).messages);
}

export function getNextVisibleWorkerHighlight(
  workers: WorkViewWorkerGridDetailed[],
  workerId: string
): WorkerRowHighlight | null {
  const index = workers.findIndex((worker) => worker.id.value === workerId);
  if (index < 0) {
    return null;
  }

  const fallbackIndex = Math.min(index, Math.max(0, workers.length - 2));
  return {
    fallbackIndex,
    workerId: null,
  };
}

export function VirtualWorkerTable({
  hasMore,
  highlightedWorkerId,
  highlightedWorkerIndex,
  loading,
  loadingMore,
  loadMore,
  onAction,
  onActionMenuOpen,
  onScrollPositionChange,
  onSelect,
  onView,
  pendingActionWorkerId,
  scrollMemory,
  scrollResetKey,
  shape,
  showActions,
  totalCount,
  workers,
}: {
  hasMore: boolean;
  highlightedWorkerId?: string | null;
  highlightedWorkerIndex?: number;
  loading: boolean;
  loadingMore: boolean;
  loadMore: () => void;
  onAction: (
    worker: WorkViewWorkerGridDetailed,
    action: WorkAction,
    options?: { watch?: boolean }
  ) => Promise<void>;
  onActionMenuOpen: (worker: WorkViewWorkerGridDetailed) => void;
  onScrollPositionChange: (scrollTop: number) => void;
  onSelect: (worker: WorkViewWorkerGridDetailed) => void;
  onView: (worker: WorkViewWorkerGridDetailed) => void;
  pendingActionWorkerId: string | null;
  scrollMemory: MutableRefObject<number>;
  scrollResetKey: string;
  shape: WorkComponentShape;
  showActions: boolean;
  totalCount?: number;
  workers: WorkViewWorkerGridDetailed[];
}) {
  const detailed = shape === "detailed";
  const scrollRef = useRef<HTMLDivElement>(null);
  const lastScrollResetKeyRef = useRef(scrollResetKey);
  const relativeNow = useLiveRelativeTimeNow();
  // eslint-disable-next-line react-hooks/incompatible-library -- TanStack Virtual owns scroll measurement state.
  const rowVirtualizer = useVirtualizer({
    count: workers.length,
    estimateSize: () => 64,
    getScrollElement: () => scrollRef.current,
    getItemKey: (index) => workers[index]?.id.value ?? index,
    overscan: 10,
  });
  const virtualItems = rowVirtualizer.getVirtualItems();
  const hasHighlightedWorker = highlightedWorkerId
    ? workers.some((worker) => worker.id.value === highlightedWorkerId)
    : false;

  useEffect(() => {
    if (scrollMemory.current > 0) {
      scrollRef.current?.scrollTo({ top: scrollMemory.current });
    }
  }, [scrollMemory]);

  useEffect(() => {
    if (
      loading &&
      !loadingMore &&
      lastScrollResetKeyRef.current !== scrollResetKey
    ) {
      lastScrollResetKeyRef.current = scrollResetKey;
      onScrollPositionChange(0);
      scrollRef.current?.scrollTo({ top: 0 });
    }
  }, [loading, loadingMore, onScrollPositionChange, scrollResetKey]);

  if (loading && workers.length === 0) {
    if (workers.length === 0 && totalCount === 0) {
      return <QueryTableStatus label="No workers matched the current query." />;
    }

    return <QueryTablePlaceholder />;
  }

  if (workers.length === 0) {
    return <QueryTableStatus label="No workers matched the current query." />;
  }

  return (
    <div className="flex min-h-0 flex-1 flex-col overflow-hidden rounded-lg border">
      <div className="grid bg-card shadow-[0_1px_0_var(--border)]">
        <div className="flex min-h-12">
          <div className="flex h-12 flex-[2_2_22rem] items-center px-3 font-medium text-sm">Definition</div>
          <div className="flex h-12 w-32 items-center px-3 font-medium text-sm">State</div>
          {detailed && <div className="flex h-12 w-72 items-center px-3 font-medium text-sm">Subject id</div>}
          {detailed && <div className="flex h-12 flex-[2_2_20rem] items-center px-3 font-medium text-sm">Identifiers</div>}
          <div className="flex h-12 w-36 items-center px-3 font-medium text-sm">Updated</div>
          <div className="flex h-12 w-28 items-center px-3 font-medium text-sm">Duration</div>
          {showActions && <div className="flex h-12 w-12 items-center px-3" />}
        </div>
      </div>
      <PanelScrollViewport
        className="workable-grid-scrollbar"
        hasMore={hasMore}
        loadedCount={workers.length}
        loading={loading}
        loadingMore={loadingMore}
        noun="worker"
        onLoadMore={loadMore}
        onScroll={(event) => {
          onScrollPositionChange(event.currentTarget.scrollTop);
        }}
        viewportRef={scrollRef}
      >
        <Table className="grid">
          <TableBody
            className="relative grid"
            style={{ height: `${rowVirtualizer.getTotalSize()}px` }}
          >
            {virtualItems.map((virtualRow) => {
              const worker = workers[virtualRow.index];
              if (!worker) {
                return null;
              }
              const isHighlighted = hasHighlightedWorker
                ? worker.id.value === highlightedWorkerId
                : virtualRow.index === highlightedWorkerIndex;

              return (
                <TableRow
                  className={`absolute flex h-16 w-full cursor-pointer overflow-hidden ${
                    isHighlighted
                      ? "bg-sky-500/10 ring-1 ring-inset ring-sky-500/40"
                      : ""
                  }`}
                  data-index={virtualRow.index}
                  key={virtualRow.key}
                  onClick={(event) => {
                    const target = event.target;
                    if (
                      target instanceof Element &&
                      target.closest("[data-worker-row-action]")
                    ) {
                      return;
                    }

                    onSelect(worker);
                  }}
                  style={{
                    transform: `translateY(${virtualRow.start}px)`,
                  }}
                >
                  <TableCell className="min-w-0 flex-[2_2_22rem] overflow-hidden">
                    <div className="font-mono text-xs">{worker.definitionName}</div>
                    <div
                      className="truncate font-mono text-muted-foreground text-xs"
                      title={worker.id.value}
                    >
                      {worker.id.value}
                    </div>
                  </TableCell>
                  <TableCell className="w-32 overflow-hidden">
                    <Badge className={stateTone(worker.state)} variant="outline">
                      {worker.state}
                    </Badge>
                  </TableCell>
                  {detailed && (
                    <TableCell className="w-72 overflow-hidden font-mono text-muted-foreground text-xs">
                      <TypedValueSummary values={worker.subjectId ? [worker.subjectId] : []} />
                    </TableCell>
                  )}
                  {detailed && (
                    <TableCell className="min-w-0 flex-[2_2_20rem] overflow-hidden font-mono text-muted-foreground text-xs">
                      <IdentifierSummary identifiers={worker.identifiers} />
                    </TableCell>
                  )}
                  <TableCell className="w-36 overflow-hidden text-muted-foreground text-xs">
                    {formatRelativeTime(worker.updatedAt, relativeNow)}
                  </TableCell>
                  <TableCell className="w-28 overflow-hidden">
                    <DurationValue
                      className="font-mono text-xs"
                      duration={formatWorkerDuration(worker)}
                    />
                  </TableCell>
                  {showActions && (
                    <TableCell className="flex w-12 items-center justify-center overflow-hidden" data-worker-row-action>
                      <WorkerActionMenu
                        disabled={pendingActionWorkerId === worker.id.value}
                        onAction={(action) => onAction(worker, action)}
                        onOpen={() => onActionMenuOpen(worker)}
                        onStartAndWatch={() => onAction(worker, "Start", { watch: true })}
                        onView={() => onView(worker)}
                        worker={worker}
                      />
                    </TableCell>
                  )}
                </TableRow>
              );
            })}
          </TableBody>
        </Table>
      </PanelScrollViewport>
    </div>
  );
}

function WorkerActionMenu({
  disabled,
  onAction,
  onOpen,
  onStartAndWatch,
  onView,
  worker,
}: {
  disabled: boolean;
  onAction: (action: WorkAction) => Promise<void> | void;
  onOpen: () => void;
  onStartAndWatch: () => Promise<void> | void;
  onView: () => void;
  worker: WorkViewWorkerGridDetailed;
}) {
  const actions = getWorkerActions(worker.state);

  return (
    <DropdownMenu>
      <DropdownMenuTrigger asChild>
        <Button
          aria-label={`Open actions for ${worker.definitionName}`}
          className="size-7 text-muted-foreground"
          data-worker-row-action
          disabled={disabled}
          onClick={(event) => event.stopPropagation()}
          onPointerDown={(event) => {
            event.stopPropagation();
            onOpen();
          }}
          size="icon-sm"
          type="button"
          variant="ghost"
        >
          {disabled ? (
            <Loader2 className="size-4 animate-spin" />
          ) : (
            <MoreHorizontal className="size-4" />
          )}
        </Button>
      </DropdownMenuTrigger>
      <DropdownMenuContent
        align="end"
        onClick={(event) => event.stopPropagation()}
        onPointerDown={(event) => event.stopPropagation()}
      >
        <DropdownMenuItem
          data-worker-row-action
          onClick={(event) => event.stopPropagation()}
          onPointerDown={(event) => event.stopPropagation()}
          onSelect={(event) => {
            event.stopPropagation();
            onView();
          }}
        >
          <SquareArrowOutUpRight className="size-4" />
          View
        </DropdownMenuItem>
        {isStartableWorker(worker.state) && (
          <DropdownMenuItem
            data-worker-row-action
            onClick={(event) => event.stopPropagation()}
            onPointerDown={(event) => event.stopPropagation()}
            onSelect={(event) => {
              event.stopPropagation();
              void onStartAndWatch();
            }}
          >
            <Play className="size-4" />
            Start & View
          </DropdownMenuItem>
        )}
        {actions.map((action) => {
          const Icon = workerActionIcons[action];
          return (
            <DropdownMenuItem
              data-worker-row-action
              key={action}
              onClick={(event) => event.stopPropagation()}
              onPointerDown={(event) => event.stopPropagation()}
              onSelect={(event) => {
                event.stopPropagation();
                void onAction(action);
              }}
            >
              <Icon className="size-4" />
              {action}
            </DropdownMenuItem>
          );
        })}
      </DropdownMenuContent>
    </DropdownMenu>
  );
}

const workerActionIcons = {
  Cancel: Ban,
  Pause,
  Purge: Trash2,
  Push: Clock3,
  Start: Play,
} satisfies Record<WorkAction, typeof Play>;

export function getWorkerActions(state: WorkerState): WorkAction[] {
  switch (state) {
    case "Queued":
      return ["Start", "Pause", "Cancel"];
    case "Paused":
    case "Failed":
      return ["Start", "Cancel"];
    case "Running":
      return ["Pause", "Cancel"];
    case "Waiting":
    case "Retrying":
      return ["Pause", "Push", "Cancel"];
    case "Interrupted":
      return ["Cancel"];
    case "Canceled":
    case "Completed":
      return ["Purge"];
    case "Interrupting":
    case "Canceling":
    case "Pausing":
      return [];
  }
}

export function isStartableWorker(state: WorkerState) {
  return state === "Queued" || state === "Paused" || state === "Failed";
}

export function VirtualIterationTable({
  hasMore,
  highlightedIterationKey,
  highlightedWorkerId,
  iterations,
  loading,
  loadingMore,
  loadMore,
  onScrollPositionChange,
  onSelect,
  scrollMemory,
  scrollResetKey,
  shape,
  totalCount,
}: {
  hasMore: boolean;
  highlightedIterationKey?: string | null;
  highlightedWorkerId?: string | null;
  iterations: WorkViewIterationGridDetailed[];
  loading: boolean;
  loadingMore: boolean;
  loadMore: () => void;
  onScrollPositionChange: (scrollTop: number) => void;
  onSelect: (iteration: WorkViewIterationGridDetailed) => void;
  scrollMemory: MutableRefObject<number>;
  scrollResetKey: string;
  shape: WorkComponentShape;
  totalCount?: number;
}) {
  const detailed = shape === "detailed";
  const scrollRef = useRef<HTMLDivElement>(null);
  const lastScrollResetKeyRef = useRef(scrollResetKey);
  const relativeNow = useLiveRelativeTimeNow();
  // eslint-disable-next-line react-hooks/incompatible-library -- TanStack Virtual owns scroll measurement state.
  const rowVirtualizer = useVirtualizer({
    count: iterations.length,
    estimateSize: () => 64,
    getScrollElement: () => scrollRef.current,
    getItemKey: (index) => {
      const iteration = iterations[index];
      return iteration ? getIterationRowKey(iteration) : index;
    },
    overscan: 10,
  });
  const virtualItems = rowVirtualizer.getVirtualItems();

  useEffect(() => {
    if (scrollMemory.current > 0) {
      scrollRef.current?.scrollTo({ top: scrollMemory.current });
    }
  }, [scrollMemory]);

  useEffect(() => {
    if (
      loading &&
      !loadingMore &&
      lastScrollResetKeyRef.current !== scrollResetKey
    ) {
      lastScrollResetKeyRef.current = scrollResetKey;
      onScrollPositionChange(0);
      scrollRef.current?.scrollTo({ top: 0 });
    }
  }, [loading, loadingMore, onScrollPositionChange, scrollResetKey]);

  if (loading && iterations.length === 0) {
    if (iterations.length === 0 && totalCount === 0) {
      return <QueryTableStatus label="No iterations matched the current query." />;
    }

    return <QueryTablePlaceholder />;
  }

  if (iterations.length === 0) {
    return <QueryTableStatus label="No iterations matched the current query." />;
  }

  return (
    <div className="flex min-h-0 flex-1 flex-col overflow-hidden rounded-lg border">
      <div className="grid bg-card shadow-[0_1px_0_var(--border)]">
        <div className="flex min-h-12">
          <div className="flex h-12 flex-[2_2_22rem] items-center px-3 font-medium text-sm">Definition</div>
          <div className="flex h-12 w-24 items-center px-3 font-medium text-sm">Status</div>
          <div className="flex h-12 w-32 items-center px-3 font-medium text-sm">Worker state</div>
          {detailed && <div className="flex h-12 w-72 items-center px-3 font-medium text-sm">Subject id</div>}
          {detailed && <div className="flex h-12 flex-[2_2_20rem] items-center px-3 font-medium text-sm">Identifiers</div>}
          <div className="flex h-12 w-36 items-center px-3 font-medium text-sm">Completed</div>
          <div className="flex h-12 w-28 items-center px-3 font-medium text-sm">Duration</div>
        </div>
      </div>
      <PanelScrollViewport
        className="workable-grid-scrollbar"
        hasMore={hasMore}
        loadedCount={iterations.length}
        loading={loading}
        loadingMore={loadingMore}
        noun="iteration"
        onLoadMore={loadMore}
        onScroll={(event) => {
          onScrollPositionChange(event.currentTarget.scrollTop);
        }}
        viewportRef={scrollRef}
      >
        <Table className="grid">
          <TableBody
            className="relative grid"
            style={{ height: `${rowVirtualizer.getTotalSize()}px` }}
          >
            {virtualItems.map((virtualRow) => {
              const iteration = iterations[virtualRow.index];
              if (!iteration) {
                return null;
              }
              const iterationKey = getIterationRowKey(iteration);
              const isHighlighted = highlightedIterationKey
                ? iterationKey === highlightedIterationKey
                : iteration.workerId.value === highlightedWorkerId;

              return (
                <TableRow
                  className={`absolute flex h-16 w-full cursor-pointer overflow-hidden ${
                    isHighlighted
                      ? "bg-sky-500/10 ring-1 ring-inset ring-sky-500/40"
                      : ""
                  }`}
                  data-index={virtualRow.index}
                  key={virtualRow.key}
                  onClick={() => onSelect(iteration)}
                  style={{
                    transform: `translateY(${virtualRow.start}px)`,
                  }}
                  title={`Open iteration #${iteration.sequence}`}
                >
                  <TableCell className="min-w-0 flex-[2_2_22rem] overflow-hidden">
                    <div className="font-mono text-xs">{iteration.definitionName}</div>
                    <div className="font-mono text-muted-foreground text-xs">
                      #{iteration.sequence} / {iteration.workerId.value}
                    </div>
                  </TableCell>
                  <TableCell className="w-24 overflow-hidden">
                    <Badge className={completionTone(iteration.status)} variant="outline">
                      {iteration.status}
                    </Badge>
                  </TableCell>
                  <TableCell className="w-32 overflow-hidden">
                    <Badge className={stateTone(iteration.workerState)} variant="outline">
                      {iteration.workerState}
                    </Badge>
                  </TableCell>
                  {detailed && (
                    <TableCell className="w-72 overflow-hidden font-mono text-muted-foreground text-xs">
                      <TypedValueSummary values={iteration.subjectId ? [iteration.subjectId] : []} />
                    </TableCell>
                  )}
                  {detailed && (
                    <TableCell className="min-w-0 flex-[2_2_20rem] overflow-hidden font-mono text-muted-foreground text-xs">
                      <IdentifierSummary identifiers={iteration.identifiers} />
                    </TableCell>
                  )}
                  <TableCell className="w-36 overflow-hidden text-muted-foreground text-xs">
                    {formatRelativeTime(iteration.completedAt, relativeNow)}
                  </TableCell>
                  <TableCell className="w-28 overflow-hidden">
                    <DurationValue duration={formatExecutionDuration(iteration.executionDuration)} />
                  </TableCell>
                </TableRow>
              );
            })}
          </TableBody>
        </Table>
      </PanelScrollViewport>
    </div>
  );
}

export function QueryResultTotal({
  noun,
  totalCount,
}: {
  noun: string;
  totalCount?: number;
}) {
  if (totalCount === undefined) {
    return null;
  }

  return (
    <div className="flex h-7 shrink-0 items-center text-muted-foreground text-xs tabular-nums">
      {totalCount.toLocaleString()} {noun}{totalCount === 1 ? "" : "s"}
    </div>
  );
}

export function IdentifierSummary({ identifiers }: { identifiers?: WorkTypedValue[] | null }) {
  return <TypedValueSummary values={identifiers ?? []} />;
}

export function TypedValueSummary({ values }: { values: WorkTypedValue[] }) {
  if (values.length === 0) {
    return <span>-</span>;
  }

  return (
    <div className="grid max-w-full grid-cols-[max-content_minmax(0,1fr)] gap-x-3 gap-y-1 overflow-hidden">
      {values.slice(0, 3).map((value, index) => (
        <div className="contents" key={`${value.type}:${value.value}:${index}`}>
          <span className="truncate text-foreground">{value.type}</span>
          <span className="truncate" title={value.value}>
            {value.value}
          </span>
        </div>
      ))}
      {values.length > 3 && (
        <div className="contents">
          <span className="text-foreground">more</span>
          <span>{values.length - 3}</span>
        </div>
      )}
    </div>
  );
}

export function DurationValue({
  className = "font-mono text-xs",
  duration,
}: {
  className?: string;
  duration: DurationDisplay;
}) {
  return (
    <span className={`${className} ${duration.isWarning ? semanticTextToneClass("warning") : "text-muted-foreground"}`}>
      {duration.text}
    </span>
  );
}

export function formatWorkerDuration(worker: WorkViewWorkerGridDetailed): DurationDisplay {
  return worker.totalExecutionDuration
    ? formatExecutionDuration(worker.totalExecutionDuration)
    : { isWarning: false, text: "-" };
}
