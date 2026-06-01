"use client";

import type { ChangeEventHandler, ReactNode } from "react";
import { cn } from "@/lib/utils";

const realtimePanelHeaderVariantClassNames = {
  "compact-title": "flex items-center justify-between gap-2 border-b px-2 py-1.5",
  title: "flex items-center justify-between gap-2 border-b px-3 py-2",
  toolbar: "grid gap-2 border-b bg-muted/30 px-2 py-2",
} as const;

export function RealtimeToolbarSurface({
  children,
  className,
}: {
  children: ReactNode;
  className?: string;
}) {
  return (
    <div className={cn("grid gap-2 rounded-md border bg-muted/30 px-2 py-2", className)}>
      {children}
    </div>
  );
}

export function RealtimePanelFrame({
  children,
  className,
  defaultRows = true,
}: {
  children: ReactNode;
  className?: string;
  defaultRows?: boolean;
}) {
  return (
    <div
      className={cn(
        "grid min-h-0 overflow-hidden rounded-md border",
        defaultRows && "grid-rows-[auto_minmax(0,1fr)]",
        className
      )}
    >
      {children}
    </div>
  );
}

export function RealtimePanelHeader({
  children,
  className,
  variant = "toolbar",
}: {
  children: ReactNode;
  className?: string;
  variant?: "compact-title" | "title" | "toolbar";
}) {
  return (
    <div
      className={cn(
        realtimePanelHeaderVariantClassNames[variant],
        className
      )}
    >
      {children}
    </div>
  );
}

export function RealtimeToolbar({
  children,
  className,
}: {
  children: ReactNode;
  className?: string;
}) {
  return (
    <div className={cn("flex min-w-0 flex-wrap items-center gap-1", className)}>
      {children}
    </div>
  );
}

export function RealtimeToolbarSearchInput({
  className,
  onChange,
  placeholder,
  value,
}: {
  className?: string;
  onChange: (value: string) => void;
  placeholder: string;
  value: string;
}) {
  const handleChange: ChangeEventHandler<HTMLInputElement> = (event) => {
    onChange(event.currentTarget.value);
  };

  return (
    <input
      className={cn(
        "h-7 min-w-48 flex-1 rounded-md border bg-background px-2 text-foreground text-xs",
        className
      )}
      onChange={handleChange}
      placeholder={placeholder}
      value={value}
    />
  );
}

export function RealtimeMessageLimitField({
  className,
  label = "Max",
  onChange,
  value,
}: {
  className?: string;
  label?: string;
  onChange: (value: number) => void;
  value: number;
}) {
  const handleChange: ChangeEventHandler<HTMLInputElement> = (event) => {
    onChange(normalizeRealtimeMessageLimit(event.currentTarget.value));
  };

  return (
    <label className={cn("flex h-7 items-center gap-1.5 rounded-md border bg-background px-2 text-xs", className)}>
      <span className="text-muted-foreground">{label}</span>
      <input
        className="w-14 bg-transparent font-mono text-foreground outline-none"
        max={1000}
        min={1}
        onChange={handleChange}
        type="number"
        value={value}
      />
    </label>
  );
}

export function RealtimeCollapsedRail({
  children,
  className,
}: {
  children: ReactNode;
  className?: string;
}) {
  return (
    <div className={cn("flex min-h-0 items-start justify-center overflow-hidden py-2", className)}>
      <div className="font-mono text-muted-foreground text-xs [writing-mode:vertical-rl]">
        {children}
      </div>
    </div>
  );
}

export function normalizeRealtimeMessageLimit(value: string) {
  const parsed = Number.parseInt(value, 10);
  if (!Number.isFinite(parsed)) {
    return 100;
  }

  return Math.min(1000, Math.max(1, parsed));
}
