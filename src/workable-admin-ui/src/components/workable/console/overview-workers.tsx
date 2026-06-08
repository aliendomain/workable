"use client";

import {
  Ban,
  ChevronRight,
  Loader2,
  MoreHorizontal,
  Play,
} from "lucide-react";
import { useState } from "react";
import { ConsoleEmptyState } from "@/components/features/console/empty-state";
import { PanelShell } from "@/components/features/console/panel-shell";
import { StackedSkeleton } from "@/components/features/console/stacked-skeleton";
import { Badge } from "@/components/ui/badge";
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
  TableHead,
  TableHeader,
  TableRow,
} from "@/components/ui/table";
import {
  formatExecutionDuration,
  type DurationDisplay,
} from "@/components/workable/console/console-format";
import { semanticTextToneClass } from "@/lib/ui/state-tones";
import {
  formatRelativeTime,
  useLiveRelativeTimeNow,
} from "@/components/workable/console/live-relative-time";
import { IdentifierSummary, TypedValueSummary } from "@/components/workable/console/query-screens";
import {
  stateTone,
  type WorkAction,
  type WorkComponentShape,
  type WorkOverviewFailedWorker,
  type WorkOverviewFailedWorkerDetailed,
  type WorkerOverviewItem,
  type WorkerState,
} from "@/lib/workable";

export type WorkerActionTarget = Pick<
  WorkerOverviewItem,
  "definitionName" | "id" | "revision" | "state"
>;

export function OverviewWorkerList({
  emptyText,
  loading,
  onClose,
  onShapeChange,
  onOpenWorker,
  onViewState,
  onWorkerAction,
  panelClassName,
  pendingActionWorkerId,
  shape,
  showActions,
  state,
  supportedShapes,
  title,
  workers,
}: {
  emptyText: string;
  loading: boolean;
  onClose: () => void;
  onShapeChange: (shape: WorkComponentShape) => void;
  onOpenWorker: (workerId: string) => void;
  onViewState: () => void;
  onWorkerAction: (worker: WorkerActionTarget, action: WorkAction) => Promise<void>;
  panelClassName?: string;
  pendingActionWorkerId: string | null;
  shape: WorkComponentShape;
  showActions: boolean;
  state: WorkerState;
  supportedShapes: WorkComponentShape[];
  title: string;
  workers: WorkOverviewFailedWorker[];
}) {
  const detailedWorkers = workers.filter(isDetailedWorkerOverviewItem);
  const [selectedWorkerId, setSelectedWorkerId] = useState<string | null>(null);

  return (
    <PanelShell
      actions={
        <button
          className="inline-flex cursor-pointer items-center gap-1 rounded-md border border-transparent px-2 py-1 text-muted-foreground text-sm transition-colors hover:border-primary/60 hover:bg-accent/40 hover:text-primary focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring focus-visible:ring-offset-2 focus-visible:ring-offset-background"
          onClick={onViewState}
          type="button"
        >
          View
          <ChevronRight className="size-4" />
        </button>
      }
      className={panelClassName}
      onClose={onClose}
      onViewStateChange={onShapeChange}
      supportedViewStates={supportedShapes}
      viewState={shape}
      title={
        <>
          {title}
          <Badge className={`justify-center ${stateTone(state)}`} variant="outline">
            {state}
          </Badge>
        </>
      }
    >
      {shape === "detailed" ? (
        <WorkerTable
          emptyText={emptyText}
          hideState
          showIdentifiers
          showSubjectSummary
          loading={loading}
          onAction={onWorkerAction}
          onActionMenuOpen={(worker) => setSelectedWorkerId(worker.id.value)}
          onSelect={(worker) => onOpenWorker(worker.id.value)}
          pendingActionWorkerId={pendingActionWorkerId}
          selectedWorkerId={selectedWorkerId}
          showActions={showActions}
          workers={detailedWorkers}
        />
      ) : (
        <FailedWorkerTable
          emptyText={emptyText}
          loading={loading}
          onAction={onWorkerAction}
          onActionMenuOpen={(worker) => setSelectedWorkerId(worker.id.value)}
          onSelect={(worker) => onOpenWorker(worker.id.value)}
          pendingActionWorkerId={pendingActionWorkerId}
          selectedWorkerId={selectedWorkerId}
          showActions={showActions}
          workers={workers}
        />
      )}
    </PanelShell>
  );
}

function FailedWorkerTable({
  emptyText,
  loading,
  onAction,
  onActionMenuOpen,
  onSelect,
  pendingActionWorkerId,
  selectedWorkerId,
  showActions,
  workers,
}: {
  emptyText: string;
  loading: boolean;
  onAction: (worker: WorkerActionTarget, action: WorkAction) => Promise<void>;
  onActionMenuOpen: (worker: WorkOverviewFailedWorker) => void;
  onSelect: (worker: WorkOverviewFailedWorker) => void;
  pendingActionWorkerId: string | null;
  selectedWorkerId: string | null;
  showActions: boolean;
  workers: WorkOverviewFailedWorker[];
}) {
  const relativeNow = useLiveRelativeTimeNow();

  if (loading) {
    return <StackedSkeleton count={5} />;
  }

  if (workers.length === 0) {
    return (
      <ConsoleEmptyState padding="spacious">
        {emptyText}
      </ConsoleEmptyState>
    );
  }

  return (
    <div className="overflow-hidden rounded-lg border">
      <Table>
        <TableHeader>
          <TableRow>
            <TableHead>Definition</TableHead>
            <TableHead>Updated</TableHead>
            <TableHead>Duration</TableHead>
            {showActions && <TableHead className="w-12" />}
          </TableRow>
        </TableHeader>
        <TableBody>
          {workers.map((worker) => {
            const isSelected = selectedWorkerId === worker.id.value;

            return (
              <TableRow
                className={`cursor-pointer ${
                  isSelected ? "bg-sky-500/10 ring-1 ring-inset ring-sky-500/40" : ""
                }`}
                key={worker.id.value}
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
              >
                <TableCell>
                  <div className="font-mono text-xs">{worker.definitionName}</div>
                </TableCell>
                <TableCell className="text-muted-foreground text-xs">
                  {formatRelativeTime(worker.updatedAt, relativeNow)}
                </TableCell>
                <TableCell>
                  <DurationValue
                    className="font-mono text-xs"
                    duration={formatFailedWorkerDuration(worker)}
                    muted
                  />
                </TableCell>
                {showActions && (
                  <TableCell data-worker-row-action>
                    <WorkerRowActionMenu
                      disabled={pendingActionWorkerId === worker.id.value}
                      onAction={(action) => onAction(toFailedWorkerActionTarget(worker), action)}
                      onOpen={() => onActionMenuOpen(worker)}
                      worker={toFailedWorkerActionTarget(worker)}
                    />
                  </TableCell>
                )}
              </TableRow>
            );
          })}
        </TableBody>
      </Table>
    </div>
  );
}

function WorkerTable({
  emptyText,
  hideState = false,
  loading,
  onAction,
  onActionMenuOpen,
  onSelect,
  pendingActionWorkerId,
  selectedWorkerId,
  showIdentifiers = false,
  showActions,
  showSubjectSummary = false,
  workers,
}: {
  emptyText: string;
  hideState?: boolean;
  loading: boolean;
  onAction: (worker: WorkerActionTarget, action: WorkAction) => Promise<void>;
  onActionMenuOpen: (worker: WorkOverviewFailedWorkerDetailed | WorkerOverviewItem) => void;
  onSelect: (worker: WorkOverviewFailedWorkerDetailed | WorkerOverviewItem) => void;
  pendingActionWorkerId: string | null;
  selectedWorkerId: string | null;
  showActions: boolean;
  showIdentifiers?: boolean;
  showSubjectSummary?: boolean;
  workers: Array<WorkOverviewFailedWorkerDetailed | WorkerOverviewItem>;
}) {
  const relativeNow = useLiveRelativeTimeNow();

  if (loading) {
    return <StackedSkeleton count={5} />;
  }

  if (workers.length === 0) {
    return (
      <ConsoleEmptyState padding="spacious">
        {emptyText}
      </ConsoleEmptyState>
    );
  }

  return (
    <div className="overflow-hidden rounded-lg border">
      <Table>
        <TableHeader>
          <TableRow>
            <TableHead>Definition</TableHead>
            {!hideState && <TableHead>State</TableHead>}
            {showSubjectSummary && <TableHead>Subject id</TableHead>}
            {showIdentifiers && <TableHead>Identifiers</TableHead>}
            <TableHead>Updated</TableHead>
            <TableHead>Duration</TableHead>
            {showActions && <TableHead className="w-12" />}
          </TableRow>
        </TableHeader>
        <TableBody>
          {workers.map((worker) => {
            const isSelected = selectedWorkerId === worker.id.value;

            return (
              <TableRow
                className={`cursor-pointer ${
                  isSelected ? "bg-sky-500/10 ring-1 ring-inset ring-sky-500/40" : ""
                }`}
                key={worker.id.value}
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
              >
                <TableCell>
                  <div className="font-mono text-xs">{worker.definitionName}</div>
                  <div className="font-mono text-muted-foreground text-xs">{worker.id.value}</div>
                </TableCell>
                {!hideState && (
                  <TableCell>
                    <Badge className={stateTone(worker.state)} variant="outline">
                      {worker.state}
                    </Badge>
                  </TableCell>
                )}
                {showSubjectSummary && (
                  <TableCell className="max-w-72 font-mono text-muted-foreground text-xs">
                    <TypedValueSummary values={worker.subjectId ? [worker.subjectId] : []} />
                  </TableCell>
                )}
                {showIdentifiers && (
                  <TableCell className="max-w-72 font-mono text-muted-foreground text-xs">
                    <IdentifierSummary identifiers={worker.identifiers} />
                  </TableCell>
                )}
                <TableCell className="text-muted-foreground text-xs">
                  {formatRelativeTime(worker.updatedAt, relativeNow)}
                </TableCell>
                <TableCell>
                  <DurationValue
                    className="font-mono text-xs"
                    duration={formatFailedWorkerDuration(worker)}
                    muted
                  />
                </TableCell>
                {showActions && (
                  <TableCell data-worker-row-action>
                    <WorkerRowActionMenu
                      disabled={pendingActionWorkerId === worker.id.value}
                      onAction={(action) => onAction(worker, action)}
                      onOpen={() => onActionMenuOpen(worker)}
                      worker={worker}
                    />
                  </TableCell>
                )}
              </TableRow>
            );
          })}
        </TableBody>
      </Table>
    </div>
  );
}

function WorkerRowActionMenu({
  disabled,
  onAction,
  onOpen,
  worker,
}: {
  disabled: boolean;
  onAction: (action: WorkAction) => Promise<void> | void;
  onOpen: () => void;
  worker: WorkerActionTarget;
}) {
  const actions = getWorkerRowActions(worker);
  if (actions.length === 0) {
    return null;
  }

  return (
    <DropdownMenu>
      <DropdownMenuTrigger asChild>
        <button
          aria-label={`Open actions for ${worker.definitionName}`}
          className="flex size-7 cursor-pointer items-center justify-center rounded-md text-muted-foreground transition-colors hover:bg-accent hover:text-foreground focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring focus-visible:ring-offset-2 focus-visible:ring-offset-background disabled:cursor-wait disabled:opacity-60"
          data-worker-row-action
          disabled={disabled}
          onClick={(event) => event.stopPropagation()}
          onPointerDown={(event) => {
            event.stopPropagation();
            onOpen();
          }}
          type="button"
        >
          {disabled ? (
            <Loader2 className="size-4 animate-spin" />
          ) : (
            <MoreHorizontal className="size-4" />
          )}
        </button>
      </DropdownMenuTrigger>
      <DropdownMenuContent
        align="end"
        onClick={(event) => event.stopPropagation()}
        onPointerDown={(event) => event.stopPropagation()}
      >
        {actions.map((action) => (
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
            {action === "Start" ? (
              <Play className="size-4" />
            ) : (
              <Ban className="size-4" />
            )}
            {action}
          </DropdownMenuItem>
        ))}
      </DropdownMenuContent>
    </DropdownMenu>
  );
}

function DurationValue({
  className = "text-xs",
  duration,
  muted = false,
}: {
  className?: string;
  duration: DurationDisplay;
  muted?: boolean;
}) {
  const tone = duration.isWarning
    ? semanticTextToneClass("warning")
    : muted
      ? "text-muted-foreground"
      : "";

  return (
    <span className={`${className} ${tone}`}>
      {duration.text}
    </span>
  );
}

export function getWorkerRowActions(worker: WorkerActionTarget): WorkAction[] {
  if (worker.state === "Failed" || worker.state === "Paused" || worker.state === "Queued") {
    return ["Start", "Cancel"];
  }

  if (worker.state === "Running" || worker.state === "Waiting" || worker.state === "Retrying") {
    return ["Cancel"];
  }

  return [];
}

export function toFailedWorkerActionTarget(worker: WorkOverviewFailedWorker): WorkerActionTarget {
  return {
    definitionName: worker.definitionName,
    id: worker.id,
    revision: worker.revision,
    state: "Failed",
  };
}

export function isDetailedWorkerOverviewItem(
  worker: WorkOverviewFailedWorker
): worker is WorkOverviewFailedWorkerDetailed | WorkerOverviewItem {
  return "subjectId" in worker || "identifiers" in worker;
}

export function formatFailedWorkerDuration(worker: WorkOverviewFailedWorker): DurationDisplay {
  return worker.totalExecutionDuration
    ? formatExecutionDuration(worker.totalExecutionDuration)
    : { isWarning: false, text: "-" };
}
