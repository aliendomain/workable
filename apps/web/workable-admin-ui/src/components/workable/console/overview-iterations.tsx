"use client";

import { ChevronRight } from "lucide-react";
import { useEffect, useRef, useState } from "react";
import { ConsoleEmptyState } from "@/components/features/console/empty-state";
import { PanelShell } from "@/components/features/console/panel-shell";
import { StackedSkeleton } from "@/components/features/console/stacked-skeleton";
import { Badge } from "@/components/ui/badge";
import { Skeleton } from "@/components/ui/skeleton";
import {
  Table,
  TableBody,
  TableCell,
  TableHead,
  TableHeader,
  TableRow,
} from "@/components/ui/table";
import { Tooltip, TooltipContent, TooltipTrigger } from "@/components/ui/tooltip";
import {
  completionTone,
  formatExecutionDuration,
  type DurationDisplay,
} from "@/components/workable/console/console-format";
import {
  formatRelativeTime,
  useLiveRelativeTimeNow,
} from "@/components/workable/console/live-relative-time";
import { IdentifierSummary, TypedValueSummary } from "@/components/workable/console/query-screens";
import {
  stateTone,
  type WorkCompletionStatus,
  type WorkComponentShape,
  type WorkIterationKeyTypeFacet,
  type WorkOverviewIteration,
} from "@/lib/workable";
import {
  semanticTextToneClass,
  semanticToneForStateName,
} from "@/lib/ui/state-tones";
import { StatusCountPill } from "@/components/workable/console/status-count-pill";

const iterationStatuses: WorkCompletionStatus[] = [
  "Executing",
  "Completed",
  "Failed",
  "Interrupted",
  "Canceled",
  "Paused",
];
const subtleClickableTileClass = "transition-colors hover:border-primary/60 hover:bg-accent/40";

export type WorkOverviewIterationsComponent = {
  commonKeyTypes?: WorkIterationKeyTypeFacet[];
  iterationCountByStatus: Partial<Record<WorkCompletionStatus, number>>;
};

export function IterationStatusStrip({
  counts,
  loading,
  onSelectStatus,
  statuses = iterationStatuses,
}: {
  counts: Partial<Record<WorkCompletionStatus, number>>;
  loading: boolean;
  onSelectStatus: (status: WorkCompletionStatus) => void;
  statuses?: WorkCompletionStatus[];
}) {
  if (loading) {
    return (
      <div className="workable-grid-scrollbar flex gap-2 overflow-x-auto pb-1">
        {statuses.map((status) => (
          <Skeleton className="h-8 min-w-28 flex-1 rounded-full" key={status} />
        ))}
      </div>
    );
  }

  return (
    <div className="workable-grid-scrollbar flex gap-2 overflow-x-auto pb-1">
      {statuses.map((status) => (
        <StatusCountPill
          ariaLabel={`Open iterations filtered by ${status}`}
          badgeClassName={completionTone(status)}
          className={`min-w-28 flex-1 justify-center text-center ${subtleClickableTileClass}`}
          key={status}
          label={status}
          onClick={() => onSelectStatus(status)}
          value={counts[status] ?? 0}
          valueClassName={semanticTextToneClass(
            semanticToneForStateName(status),
            "strong"
          )}
        />
      ))}
    </div>
  );
}

export function CompactIterationStrip({
  counts,
  loading,
  onSelectStatus,
  statuses,
}: {
  counts: Partial<Record<WorkCompletionStatus, number>>;
  loading: boolean;
  onSelectStatus: (status: WorkCompletionStatus) => void;
  statuses: WorkCompletionStatus[];
}) {
  if (loading) {
    return (
      <div className="workable-grid-scrollbar flex h-8 items-center gap-2 overflow-x-auto">
        {statuses.map((status) => (
          <Skeleton className="h-7 w-30 rounded-full" key={status} />
        ))}
      </div>
    );
  }

  return (
    <div className="workable-grid-scrollbar flex min-h-8 items-center gap-2 overflow-x-auto">
      {statuses.map((status) => (
        <CompactIterationStripItem
          key={status}
          label={status}
          onClick={() => onSelectStatus(status)}
          value={counts[status] ?? 0}
          valueClassName={semanticTextToneClass(
            semanticToneForStateName(status),
            "strong"
          )}
        />
      ))}
    </div>
  );
}

function CompactIterationStripItem({
  label,
  onClick,
  value,
  valueClassName,
}: {
  label: string;
  onClick: () => void;
  value: number | string;
  valueClassName?: string;
}) {
  return (
    <button
      aria-label={`Open ${label.toLowerCase()}`}
      className="inline-flex h-8 shrink-0 cursor-pointer items-center gap-2 rounded-full border border-foreground/10 bg-muted/25 px-3 text-left transition-colors hover:border-primary/60 hover:bg-accent/50 hover:text-primary focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring focus-visible:ring-offset-2 focus-visible:ring-offset-background"
      onClick={onClick}
      type="button"
    >
      <span className="whitespace-nowrap text-muted-foreground text-xs">{label}</span>
      <span className={`whitespace-nowrap font-mono font-medium text-sm leading-none ${valueClassName ?? ""}`}>
        {value}
      </span>
    </button>
  );
}

export function TopKeyTypePanel({
  keys,
  loading,
  onShowMore,
  onSelectKeyType,
}: {
  keys: WorkIterationKeyTypeFacet[];
  loading: boolean;
  onShowMore: () => void;
  onSelectKeyType: (keyType: string) => void;
}) {
  const [visibleCount, setVisibleCount] = useState(keys.length);
  const measureRef = useRef<HTMLDivElement>(null);
  const visibleKeys = keys.slice(0, visibleCount);
  const hiddenKeys = keys.slice(visibleCount);

  useEffect(() => {
    if (loading) {
      return;
    }

    const measure = () => {
      const root = measureRef.current;
      if (!root) {
        return;
      }

      const width = root.clientWidth;
      const pillWidths = Array.from(
        root.querySelectorAll<HTMLElement>("[data-key-type-pill]")
      ).map((element) => element.offsetWidth);
      const moreWidth = root
        .querySelector<HTMLElement>("[data-key-type-more]")
        ?.offsetWidth ?? 0;
      const gap = 8;

      let used = 0;
      let nextVisibleCount = pillWidths.length;

      for (let index = 0; index < pillWidths.length; index += 1) {
        const remaining = pillWidths.length - index - 1;
        const itemWidth = pillWidths[index] + (index > 0 ? gap : 0);
        const reserveMoreWidth = remaining > 0
          ? moreWidth + (index >= 0 ? gap : 0)
          : 0;

        if (used + itemWidth + reserveMoreWidth > width) {
          nextVisibleCount = index;
          break;
        }

        used += itemWidth;
      }

      setVisibleCount(Math.max(0, nextVisibleCount));
    };

    measure();
    const observer = new ResizeObserver(measure);
    if (measureRef.current) {
      observer.observe(measureRef.current);
    }

    return () => observer.disconnect();
  }, [keys, loading]);

  if (!loading && keys.length === 0) {
    return null;
  }

  return (
    <section className="space-y-2">
      {loading ? (
        <div className="flex flex-wrap gap-2">
          {Array.from({ length: 6 }).map((_, index) => (
            <Skeleton className="h-8 w-32 shrink-0 rounded-full" key={index} />
          ))}
        </div>
      ) : (
        <div className="relative">
          <div
            aria-hidden="true"
            className="pointer-events-none invisible absolute inset-x-0 top-0 flex h-8 gap-2 overflow-hidden"
            ref={measureRef}
          >
            {keys.map((key) => (
              <span
                className="inline-flex h-8 shrink-0 items-center rounded-full border px-3 font-mono text-sm"
                data-key-type-pill
                key={key.type}
              >
                {key.type}
              </span>
            ))}
            <span className="inline-flex h-8 shrink-0 items-center rounded-full border px-3 text-sm" data-key-type-more>
              +{Math.max(1, keys.length)} more
            </span>
          </div>
          <div className="flex gap-2 overflow-hidden">
            {visibleKeys.map((key) => (
              <Tooltip delayDuration={500} disableHoverableContent key={key.type}>
                <TooltipTrigger asChild>
                  <button
                    aria-label={`Open iterations for key type ${key.type}`}
                    className={`inline-flex h-8 shrink-0 cursor-pointer items-center rounded-full border bg-muted/25 px-3 text-left ${subtleClickableTileClass} focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring focus-visible:ring-offset-2 focus-visible:ring-offset-background`}
                    onClick={() => onSelectKeyType(key.type)}
                    type="button"
                  >
                    <span className="truncate font-mono text-sm">{key.type}</span>
                  </button>
                </TooltipTrigger>
                <TooltipContent
                  className="max-w-56 whitespace-normal text-left"
                  side="top"
                  sideOffset={6}
                >
                  <KeyTypeTooltipContent keyType={key} />
                </TooltipContent>
              </Tooltip>
            ))}
            {hiddenKeys.length > 0 && (
              <Tooltip delayDuration={500} disableHoverableContent>
                <TooltipTrigger asChild>
                  <button
                    aria-label={`Open iterations to view ${hiddenKeys.length} more relationship types`}
                    className={`inline-flex h-8 shrink-0 cursor-pointer items-center rounded-full border bg-muted/25 px-3 text-sm ${subtleClickableTileClass} focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring focus-visible:ring-offset-2 focus-visible:ring-offset-background`}
                    onClick={onShowMore}
                    type="button"
                  >
                    +{hiddenKeys.length} more
                  </button>
                </TooltipTrigger>
                <TooltipContent
                  className="max-w-64 whitespace-normal text-left"
                  side="top"
                  sideOffset={6}
                >
                  <div className="space-y-1">
                    <div className="font-medium">More relationship types</div>
                    <div className="text-muted-foreground">
                      {hiddenKeys.map((key) => key.type).join(", ")}
                    </div>
                  </div>
                </TooltipContent>
              </Tooltip>
            )}
          </div>
        </div>
      )}
    </section>
  );
}

function KeyTypeTooltipContent({ keyType }: { keyType: WorkIterationKeyTypeFacet }) {
  return (
    <div className="space-y-1">
      <div className="font-medium">Open iterations filtered to this key type.</div>
      <div>{formatIterationCount(keyType.iterationCount)} with this relationship type.</div>
      <div className="grid grid-cols-[auto_auto] gap-x-3 gap-y-0.5 text-muted-foreground">
        <span>Subjects</span>
        <span className="text-right font-mono">{keyType.iterationCountByKind.Subject ?? 0}</span>
        <span>Concurrency keys</span>
        <span className="text-right font-mono">{keyType.iterationCountByKind.ConcurrencyKey ?? 0}</span>
        <span>Identifiers</span>
        <span className="text-right font-mono">{keyType.iterationCountByKind.Identifier ?? 0}</span>
      </div>
    </div>
  );
}

export function OverviewIterationList({
  emptyText,
  loading,
  onClose,
  onShapeChange,
  onOpenWorker,
  onViewState,
  panelClassName,
  shape,
  status,
  supportedShapes,
  title,
  iterations,
}: {
  emptyText: string;
  loading: boolean;
  onClose: () => void;
  onShapeChange: (shape: WorkComponentShape) => void;
  onOpenWorker: (workerId: string) => void;
  onViewState: () => void;
  panelClassName?: string;
  shape: WorkComponentShape;
  status: WorkCompletionStatus;
  supportedShapes: WorkComponentShape[];
  title: string;
  iterations: WorkOverviewIteration[];
}) {
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
          <Badge className={`justify-center ${completionTone(status)}`} variant="outline">
            {status}
          </Badge>
        </>
      }
    >
      {loading ? (
        <StackedSkeleton count={4} />
      ) : iterations.length === 0 ? (
        <ConsoleEmptyState>
          {emptyText}
        </ConsoleEmptyState>
      ) : shape === "detailed" ? (
        <OverviewIterationTable
          iterations={iterations}
          timestampLabel={status}
          onOpenWorker={onOpenWorker}
        />
      ) : (
        <OverviewIterationTable
          compact
          iterations={iterations}
          timestampLabel={status}
          onOpenWorker={onOpenWorker}
        />
      )}
    </PanelShell>
  );
}

function OverviewIterationTable({
  compact = false,
  iterations,
  onOpenWorker,
  timestampLabel,
}: {
  compact?: boolean;
  iterations: WorkOverviewIteration[];
  onOpenWorker: (workerId: string) => void;
  timestampLabel: string;
}) {
  const relativeNow = useLiveRelativeTimeNow();

  return (
    <div className="overflow-hidden rounded-lg border">
      <Table>
        <TableHeader>
          <TableRow>
            <TableHead>Definition</TableHead>
            {!compact && <TableHead>Worker state</TableHead>}
            {!compact && <TableHead>Subject id</TableHead>}
            {!compact && <TableHead>Identifiers</TableHead>}
            <TableHead>{timestampLabel}</TableHead>
            <TableHead>Duration</TableHead>
          </TableRow>
        </TableHeader>
        <TableBody>
          {iterations.map((iteration) => (
            <TableRow
              className="cursor-pointer"
              key={`${iteration.workerId.value}:${iteration.sequence}`}
              onClick={() => onOpenWorker(iteration.workerId.value)}
            >
              <TableCell>
                <div className="font-mono text-xs">{iteration.definitionName}</div>
                {!compact && (
                  <div className="font-mono text-muted-foreground text-xs">
                    {iteration.workerId.value} / #{iteration.sequence}
                  </div>
                )}
              </TableCell>
              {!compact && (
                <TableCell>
                  {"workerState" in iteration ? (
                    <Badge className={stateTone(iteration.workerState)} variant="outline">
                      {iteration.workerState}
                    </Badge>
                  ) : (
                    <span className="text-muted-foreground">-</span>
                  )}
                </TableCell>
              )}
              {!compact && (
                <TableCell className="max-w-72 font-mono text-muted-foreground text-xs">
                  <TypedValueSummary values={"subjectId" in iteration && iteration.subjectId ? [iteration.subjectId] : []} />
                </TableCell>
              )}
              {!compact && (
                <TableCell className="max-w-72 font-mono text-muted-foreground text-xs">
                  <IdentifierSummary identifiers={"identifiers" in iteration ? iteration.identifiers : []} />
                </TableCell>
              )}
              <TableCell className="text-muted-foreground text-xs">
                {formatRelativeTime(iteration.completedAt, relativeNow)}
              </TableCell>
              <TableCell>
                <DurationValue
                  className="font-mono text-xs"
                  duration={formatExecutionDuration(iteration.executionDuration)}
                  muted
                />
              </TableCell>
            </TableRow>
          ))}
        </TableBody>
      </Table>
    </div>
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

export function formatIterationCount(count: number) {
  return `${count} ${count === 1 ? "iteration" : "iterations"}`;
}
