"use client";

import { FileJson, Rows4, Wrench } from "lucide-react";
import { useState } from "react";
import { Button } from "@/components/ui/button";
import { Popover, PopoverContent, PopoverTrigger } from "@/components/ui/popover";
import { Tooltip, TooltipContent, TooltipTrigger } from "@/components/ui/tooltip";
import {
  consoleIconButtonClassName,
  consoleMenuItemClassName,
  consoleMenuItemDisabledClassName,
  consoleMutedMetaClassName,
} from "@/lib/ui/console";

export function SystemToolsMenu({
  canUseRealtimePayloads,
  eventViewerOpen,
  onEventViewerOpenChange,
  onRealtimePayloadOpenChange,
  realtimePayloadOpen,
}: {
  canUseRealtimePayloads: boolean;
  eventViewerOpen: boolean;
  onEventViewerOpenChange: (open: boolean) => void;
  onRealtimePayloadOpenChange: (open: boolean) => void;
  realtimePayloadOpen: boolean;
}) {
  const [open, setOpen] = useState(false);

  return (
    <Popover open={open} onOpenChange={setOpen}>
      <Tooltip delayDuration={500} disableHoverableContent>
        <TooltipTrigger asChild>
          <PopoverTrigger asChild>
            <Button
              aria-label="System tools"
              className={consoleIconButtonClassName}
              size="icon-sm"
              variant="ghost"
            >
              <Wrench className="size-4" />
            </Button>
          </PopoverTrigger>
        </TooltipTrigger>
        <TooltipContent side="bottom" sideOffset={6}>
          System tools
        </TooltipContent>
      </Tooltip>
      <PopoverContent align="end" className="w-56 p-1">
        <button
          className={consoleMenuItemClassName}
          onClick={() => {
            onEventViewerOpenChange(!eventViewerOpen);
            setOpen(false);
          }}
          type="button"
        >
          <FileJson className="size-4" />
          <span className="flex-1">Event viewer</span>
          <span className={consoleMutedMetaClassName}>{eventViewerOpen ? "Open" : ""}</span>
        </button>
        <Tooltip delayDuration={250}>
          <TooltipTrigger asChild>
            <button
              className={consoleMenuItemDisabledClassName}
              disabled={!canUseRealtimePayloads}
              onClick={() => {
                onRealtimePayloadOpenChange(!realtimePayloadOpen);
                setOpen(false);
              }}
              type="button"
            >
              <Rows4 className="size-4" />
              <span className="flex-1">Realtime payloads</span>
              <span className={consoleMutedMetaClassName}>
                {realtimePayloadOpen ? "Open" : ""}
              </span>
            </button>
          </TooltipTrigger>
          {!canUseRealtimePayloads ? (
            <TooltipContent side="left" sideOffset={6}>
              Realtime payloads are only available on the worker detail view when realtime is configured.
            </TooltipContent>
          ) : null}
        </Tooltip>
      </PopoverContent>
    </Popover>
  );
}
