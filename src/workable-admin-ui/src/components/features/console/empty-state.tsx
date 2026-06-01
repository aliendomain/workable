"use client";

import type { ReactNode } from "react";
import { cn } from "@/lib/utils";

export function ConsoleEmptyState({
  children,
  className,
  fill = false,
  padding = "default",
}: {
  children: ReactNode;
  className?: string;
  fill?: boolean;
  padding?: "compact" | "default" | "spacious";
}) {
  return (
    <div
      className={cn(
        "rounded-lg border border-dashed text-center text-muted-foreground text-sm",
        fill && "flex min-h-0 flex-1 items-center justify-center",
        padding === "compact" && "p-4",
        padding === "default" && "p-6",
        padding === "spacious" && "p-8",
        className
      )}
    >
      {children}
    </div>
  );
}

export function ConsolePlaceholder({
  className,
  fill = false,
}: {
  className?: string;
  fill?: boolean;
}) {
  return (
    <div
      className={cn(
        "rounded-lg border border-dashed",
        fill && "flex min-h-0 flex-1",
        className
      )}
    />
  );
}
