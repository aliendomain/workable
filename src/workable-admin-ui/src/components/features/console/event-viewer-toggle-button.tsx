"use client";

import { Send } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Tooltip, TooltipContent, TooltipTrigger } from "@/components/ui/tooltip";
import { consoleIconButtonClassName } from "@/lib/ui/console";
import { cn } from "@/lib/utils";

export function EventViewerToggleButton({
  open,
  onOpenChange,
}: {
  open: boolean;
  onOpenChange: (open: boolean) => void;
}) {
  return (
    <Tooltip delayDuration={500} disableHoverableContent>
      <TooltipTrigger asChild>
        <Button
          aria-label={open ? "Close event viewer" : "Open event viewer"}
          className={cn(
            consoleIconButtonClassName,
            open && "text-foreground"
          )}
          onClick={() => onOpenChange(!open)}
          size="icon-sm"
          variant="ghost"
        >
          <Send className="size-4" />
        </Button>
      </TooltipTrigger>
      <TooltipContent side="bottom" sideOffset={6}>
        {open ? "Event viewer open" : "Event viewer"}
      </TooltipContent>
    </Tooltip>
  );
}
