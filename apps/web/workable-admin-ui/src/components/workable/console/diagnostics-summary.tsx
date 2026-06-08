"use client";

import { ChevronRight, Loader2 } from "lucide-react";
import type { ReactNode } from "react";
import { cn } from "@/lib/utils";
import { formatLocalTime } from "@/components/workable/console/console-format";
import { semanticBadgeToneClass } from "@/lib/ui/state-tones";

const diagnosticsDetailCardToneClassNames = {
  muted: "border-border bg-muted/10",
  warning: semanticBadgeToneClass("warning"),
} as const;

export function DiagnosticsSummarySection({
  children,
  expanded,
  lastUpdatedAt,
  onExpandedChange,
  summary,
  title,
}: {
  children: ReactNode;
  expanded: boolean;
  lastUpdatedAt?: Date;
  onExpandedChange: (expanded: boolean) => void;
  summary: ReactNode;
  title: string;
}) {
  return (
    <div className="border-b p-3 last:border-b-0">
      <button
        className="flex w-full items-center justify-between gap-3 text-left"
        onClick={() => onExpandedChange(!expanded)}
        type="button"
      >
        <div className="flex min-w-0 items-center gap-2">
          <ChevronRight className={cn("size-4 shrink-0 transition-transform", expanded && "rotate-90")} />
          <div className="min-w-0">
            <div className="font-medium text-sm">{title}</div>
            <div className="truncate text-muted-foreground text-xs">
              {summary}
            </div>
          </div>
        </div>
        <div className="shrink-0 text-muted-foreground text-xs">
          {getDiagnosticsSectionStatus(expanded, lastUpdatedAt)}
        </div>
      </button>
      {expanded ? (
        <div className="mt-3 space-y-2">
          {children}
        </div>
      ) : null}
    </div>
  );
}

export function DiagnosticsLoadingState({
  children,
}: {
  children: ReactNode;
}) {
  return (
    <div className="flex items-center gap-2 rounded-md border border-border px-3 py-2 text-muted-foreground text-sm">
      <Loader2 className="size-4 animate-spin" />
      {children}
    </div>
  );
}

export function DiagnosticsEmptyState({
  children,
}: {
  children: ReactNode;
}) {
  return (
    <div className="rounded-md border border-border bg-muted/20 px-3 py-2 text-muted-foreground text-sm">
      {children}
    </div>
  );
}

export function DiagnosticsDetailCard({
  children,
  className,
  tone,
}: {
  children: ReactNode;
  className?: string;
  tone?: "muted" | "warning";
}) {
  return (
    <div
      className={cn(
        "rounded-md border px-3 py-2",
        tone ? diagnosticsDetailCardToneClassNames[tone] : "border-border",
        className
      )}
    >
      {children}
    </div>
  );
}

export function getDiagnosticsSectionStatus(expanded: boolean, lastUpdatedAt?: Date) {
  if (!expanded) {
    return "Collapsed";
  }

  return lastUpdatedAt ? formatLocalTime(lastUpdatedAt) : "Waiting";
}
