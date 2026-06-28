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
    <>
      <Badge className={cn("justify-center", badgeClassName)} variant="outline">
        {label}
      </Badge>
      <span className={cn("whitespace-nowrap font-mono font-medium text-sm leading-none", valueClassName)}>
        {value}
      </span>
    </>
  );

  if (onClick) {
    return (
      <button
        aria-label={ariaLabel}
        className={cn(
          "inline-flex h-8 shrink-0 cursor-pointer items-center gap-2 rounded-full border bg-muted/25 px-3 text-left transition-colors hover:border-primary/60 hover:bg-accent/50 hover:text-primary focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring focus-visible:ring-offset-2 focus-visible:ring-offset-background",
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
    <div className={cn("inline-flex h-8 shrink-0 items-center gap-2 rounded-full border bg-muted/25 px-3 text-left", className)}>
      {content}
    </div>
  );
}
