"use client";

import type { HTMLAttributes, ReactNode } from "react";
import { cn } from "@/lib/utils";

export const consolePageLayoutClassName = "flex min-h-0 flex-col gap-6";
export const consoleToolbarLaneClassName =
  "-mb-2 flex min-h-9 min-w-0 -translate-y-2 items-center justify-end gap-1";
export const consolePanelSurfaceClassName = "rounded-xl bg-card p-4 ring-1 ring-foreground/10";
export const consolePanelHeaderClassName =
  "flex min-w-0 items-center justify-between gap-3";
export const consolePanelBodyClassName = "mt-4";
export const consolePanelTitleTextClassName = "text-base";
export const consolePanelTitleClassName =
  `truncate font-semibold ${consolePanelTitleTextClassName}`;
export const consolePanelDescriptionClassName =
  "mt-0.5 block text-muted-foreground text-xs";
export const consoleBreadcrumbTextClassName = consolePanelTitleTextClassName;
export const consoleBreadcrumbRootItemClassName = "inline-flex items-center font-semibold";
export const consoleBreadcrumbLinkClassName = "max-w-56 truncate";
export const consoleBreadcrumbCurrentClassName = "max-w-56 truncate";
export const consoleBreadcrumbDefinitionClassName = "max-w-80 truncate font-mono";
export const consolePanelSectionGapClassName = "gap-4";
export const consolePanelClusterGapClassName = "gap-3";
export const consolePanelInlineGapClassName = "gap-1";
export const consolePanelActionGapClassName = "gap-2";

export function ViewActionLane({ children }: { children?: ReactNode }) {
  return (
    <div
      aria-hidden={children ? undefined : true}
      className={consoleToolbarLaneClassName}
    >
      {children}
    </div>
  );
}

export function ConsolePageLayout({
  children,
  className,
  reserveToolbar = false,
  toolbar,
  ...props
}: HTMLAttributes<HTMLDivElement> & {
  children: ReactNode;
  reserveToolbar?: boolean;
  toolbar?: ReactNode;
}) {
  return (
    <div className={cn(consolePageLayoutClassName, className)} {...props}>
      {(reserveToolbar || toolbar) ? <ViewActionLane>{toolbar}</ViewActionLane> : null}
      {children}
    </div>
  );
}

export function ConsolePanelSurface({
  children,
  className,
  ...props
}: HTMLAttributes<HTMLElement> & {
  children: ReactNode;
}) {
  return (
    <section className={cn(consolePanelSurfaceClassName, className)} {...props}>
      {children}
    </section>
  );
}

export function ConsolePanelHeader({
  children,
  className,
  ...props
}: HTMLAttributes<HTMLDivElement> & {
  children: ReactNode;
}) {
  return (
    <div className={cn(consolePanelHeaderClassName, className)} {...props}>
      {children}
    </div>
  );
}

export function ConsolePanelBody({
  children,
  className,
  ...props
}: HTMLAttributes<HTMLDivElement> & {
  children: ReactNode;
}) {
  return (
    <div className={cn(consolePanelBodyClassName, className)} {...props}>
      {children}
    </div>
  );
}

export function ConsolePanelTitle({
  children,
  className,
  ...props
}: HTMLAttributes<HTMLElement> & {
  children: ReactNode;
}) {
  return (
    <span className={cn(consolePanelTitleClassName, className)} {...props}>
      {children}
    </span>
  );
}

export function ConsolePanelDescription({
  children,
  className,
  ...props
}: HTMLAttributes<HTMLElement> & {
  children: ReactNode;
}) {
  return (
    <span className={cn(consolePanelDescriptionClassName, className)} {...props}>
      {children}
    </span>
  );
}
