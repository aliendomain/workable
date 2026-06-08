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
import {
  semanticIndicatorToneClass,
  semanticTextToneClass,
  semanticToneForRealtimeConnectionState,
} from "@/lib/ui/state-tones";
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
                <RealtimeStatusIcon realtime={realtime} />
              </Button>
            </TooltipTrigger>
            <TooltipContent side="bottom" sideOffset={6}>
              {formatRealtimeTooltip(realtime)}
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
                  <RealtimeStatusIcon realtime={realtime} />
                </Button>
              </DropdownMenuTrigger>
            </TooltipTrigger>
              <TooltipContent side="bottom" sideOffset={6}>
                {formatRealtimeTooltip(realtime)}
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
                <RealtimeStatusIcon realtime={realtime} />
              </span>
            </TooltipTrigger>
            <TooltipContent side="bottom" sideOffset={6}>
              {formatRealtimeTooltip(realtime)}
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

function RealtimeStatusIcon({
  realtime,
}: {
  realtime: NonNullable<ConsoleHeaderCapabilities["realtime"]>;
}) {
  const showDisconnectedBadge =
    realtime.enabled &&
    realtime.connectionState === "disconnected";

  return (
    <span className="relative inline-flex size-4 items-center justify-center">
      <Radio className={cn("size-4", realtimeConnectionTone(realtime.connectionState, realtime.enabled))} />
      {showDisconnectedBadge && (
        <span className={cn(
          "-right-1 -top-1 absolute flex size-3 items-center justify-center rounded-full font-semibold text-[9px] leading-none",
          semanticIndicatorToneClass("danger")
        )}>
          !
        </span>
      )}
    </span>
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

function formatRealtimeTooltip(
  realtime: NonNullable<ConsoleHeaderCapabilities["realtime"]>
) {
  return realtime.title ?? formatRealtimeConnectionState(realtime.connectionState, realtime.enabled);
}

function realtimeConnectionTone(connectionState: string, enabled: boolean) {
  return semanticTextToneClass(
    semanticToneForRealtimeConnectionState(connectionState, enabled)
  );
}

function formatRealtimeActionLabel(
  realtime: NonNullable<ConsoleHeaderCapabilities["realtime"]>,
  item: NonNullable<NonNullable<ConsoleHeaderCapabilities["realtime"]>["menuItems"]>[number]
) {
  const action = item.active ? "Hide" : "Show";
  return `${formatRealtimeConnectionState(realtime.connectionState, realtime.enabled)}. ${action} ${item.label.toLowerCase()}`;
}
