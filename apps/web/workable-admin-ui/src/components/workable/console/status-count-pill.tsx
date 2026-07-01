"use client";

import type { ReactNode } from "react";
import { Badge } from "@/components/ui/badge";
import { cn } from "@/lib/utils";

type StatusCountPillProps = {
  ariaLabel?: string;
  badgeClassName?: string;
  className?: string;
  label: ReactNode;
  onClick?: () => void;
  value: ReactNode;
  valueClassName?: string;
};

export function StatusCountPill({
  ariaLabel,
  badgeClassName,
  className,
  label,
  onClick,
  value,
  valueClassName,
}: StatusCountPillProps) {
  const content = (
    <Badge
      className={cn(
        "h-8 w-full justify-center gap-2 px-3 text-sm transition-colors group-hover/status-count:border-primary/60",
        badgeClassName
      )}
      variant="outline"
    >
      <span className="whitespace-nowrap">{label}</span>
      <span className={cn("whitespace-nowrap font-mono font-medium leading-none", valueClassName)}>
        {value}
      </span>
    </Badge>
  );

  if (onClick) {
    return (
      <button
        aria-label={ariaLabel}
        className={cn(
          "group/status-count inline-flex h-8 shrink-0 cursor-pointer items-center justify-center rounded-full text-left transition-colors hover:bg-accent/40 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring focus-visible:ring-offset-2 focus-visible:ring-offset-background",
          className
        )}
        onClick={onClick}
        type="button"
      >
        {content}
      </button>
    );
  }

  return (
    <div className={cn("inline-flex h-8 shrink-0 items-center justify-center rounded-full text-left", className)}>
      {content}
    </div>
  );
}
