"use client";

import { Radio, RefreshCw } from "lucide-react";
import type { ConsoleHeaderCapabilities } from "@/components/features/console/header-capabilities";
import { Button } from "@/components/ui/button";
import {
  DropdownMenu,
  DropdownMenuContent,
  DropdownMenuItem,
  DropdownMenuTrigger,
} from "@/components/ui/dropdown-menu";
import { Tooltip, TooltipContent, TooltipTrigger } from "@/components/ui/tooltip";
import {
  consoleIconButtonClassName,
  consoleMenuItemDisabledClassName,
  consoleMutedMetaClassName,
} from "@/lib/ui/console";
import { cn } from "@/lib/utils";

export function ConsoleHeaderCapabilityControls({
  capabilities,
}: {
  capabilities: ConsoleHeaderCapabilities | null;
}) {
  const realtime = capabilities?.realtime ?? null;
  const refresh = capabilities?.refresh ?? null;
  const canRefresh = Boolean(!refresh?.hidden && refresh?.onRefresh && refresh?.title);
  const realtimeMenuItems = realtime?.menuItems ?? [];
  const hasRealtimeMenu = realtimeMenuItems.length > 0;
  const singleRealtimeAction = realtimeMenuItems.length === 1 ? realtimeMenuItems[0] : null;
  const shouldSpinRefresh =
    Boolean(refresh?.refreshing) &&
    (!realtime || realtime.connectionState === "connected");

  if (!realtime && !canRefresh) {
    return null;
  }

  return (
    <div className="flex items-center gap-1">
      {realtime && (
        singleRealtimeAction ? (
          <Tooltip delayDuration={500} disableHoverableContent>
            <TooltipTrigger asChild>
              <Button
                aria-label={formatRealtimeActionLabel(realtime, singleRealtimeAction)}
                className={cn(
                  consoleIconButtonClassName,
                  singleRealtimeAction.active && "text-foreground"
                )}
                onClick={singleRealtimeAction.onSelect}
                size="icon-sm"
                variant="ghost"
              >
                <Radio
                  className={cn("size-4", realtimeConnectionTone(realtime.connectionState, realtime.enabled))}
                />
              </Button>
            </TooltipTrigger>
            <TooltipContent side="bottom" sideOffset={6}>
              {formatRealtimeActionLabel(realtime, singleRealtimeAction)}
            </TooltipContent>
          </Tooltip>
        ) : hasRealtimeMenu ? (
          <DropdownMenu modal={false}>
            <Tooltip delayDuration={500} disableHoverableContent>
              <TooltipTrigger asChild>
                <DropdownMenuTrigger asChild>
                  <Button
                    aria-label={formatRealtimeConnectionState(realtime.connectionState, realtime.enabled)}
                    className={consoleIconButtonClassName}
                    size="icon-sm"
                    variant="ghost"
                  >
                    <Radio
                      className={cn("size-4", realtimeConnectionTone(realtime.connectionState, realtime.enabled))}
                    />
                  </Button>
                </DropdownMenuTrigger>
              </TooltipTrigger>
              <TooltipContent side="bottom" sideOffset={6}>
                {formatRealtimeConnectionState(realtime.connectionState, realtime.enabled)}
              </TooltipContent>
            </Tooltip>
            <DropdownMenuContent align="end" className="w-56 p-1">
              {realtimeMenuItems.map((item) => (
                <DropdownMenuItem
                  className={consoleMenuItemDisabledClassName}
                  disabled={item.disabled}
                  key={item.id}
                  onClick={item.onSelect}
                >
                  {item.icon}
                  <span className="flex-1">{item.label}</span>
                  <span className={consoleMutedMetaClassName}>{item.active ? "Open" : ""}</span>
                </DropdownMenuItem>
              ))}
            </DropdownMenuContent>
          </DropdownMenu>
        ) : (
          <Tooltip delayDuration={500} disableHoverableContent>
            <TooltipTrigger asChild>
              <span
                aria-label={formatRealtimeConnectionState(realtime.connectionState, realtime.enabled)}
                className="inline-flex size-8 items-center justify-center"
                role="img"
              >
                <Radio className={cn("size-4", realtimeConnectionTone(realtime.connectionState, realtime.enabled))} />
              </span>
            </TooltipTrigger>
            <TooltipContent side="bottom" sideOffset={6}>
              {formatRealtimeConnectionState(realtime.connectionState, realtime.enabled)}
            </TooltipContent>
          </Tooltip>
        )
      )}
      {canRefresh && refresh?.onRefresh && refresh.title && (
        <Tooltip delayDuration={500} disableHoverableContent>
          <TooltipTrigger asChild>
            <Button
              aria-label={refresh.ariaLabel ?? refresh.title}
              className="text-muted-foreground hover:bg-transparent hover:text-foreground dark:hover:bg-transparent"
              disabled={refresh.disabled}
              onClick={refresh.onRefresh}
              size="icon-sm"
              variant="ghost"
            >
              <RefreshCw className={cn("size-4", shouldSpinRefresh && "animate-spin")} />
            </Button>
          </TooltipTrigger>
          <TooltipContent side="bottom" sideOffset={6}>
            {refresh.title}
          </TooltipContent>
        </Tooltip>
      )}
    </div>
  );
}

function formatRealtimeConnectionState(connectionState: string, enabled: boolean) {
  if (!enabled) {
    return "Realtime unavailable";
  }

  switch (connectionState) {
    case "connected":
      return "Realtime enabled";
    case "connecting":
      return "Realtime connecting";
    case "reconnecting":
      return "Realtime reconnecting";
    case "error":
      return "Realtime error";
    case "disconnected":
      return "Realtime disconnected";
    default:
      return connectionState;
  }
}

function realtimeConnectionTone(connectionState: string, enabled: boolean) {
  if (!enabled) {
    return "text-slate-500 dark:text-slate-300";
  }

  switch (connectionState) {
    case "connected":
      return "text-emerald-500 dark:text-emerald-300";
    case "connecting":
      return "text-sky-500 dark:text-sky-300";
    case "reconnecting":
      return "text-amber-500 dark:text-amber-300";
    case "error":
    case "disconnected":
      return "text-red-500 dark:text-red-300";
    default:
      return "text-slate-500 dark:text-slate-300";
  }
}

function formatRealtimeActionLabel(
  realtime: NonNullable<ConsoleHeaderCapabilities["realtime"]>,
  item: NonNullable<NonNullable<ConsoleHeaderCapabilities["realtime"]>["menuItems"]>[number]
) {
  const action = item.active ? "Hide" : "Show";
  return `${formatRealtimeConnectionState(realtime.connectionState, realtime.enabled)}. ${action} ${item.label.toLowerCase()}`;
}
