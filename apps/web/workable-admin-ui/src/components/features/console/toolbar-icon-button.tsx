"use client";

import type { ComponentProps, ReactNode } from "react";
import { Button } from "@/components/ui/button";
import { Tooltip, TooltipContent, TooltipTrigger } from "@/components/ui/tooltip";
import { consoleIconButtonClassName } from "@/lib/ui/console";
import { cn } from "@/lib/utils";

export function ToolbarIconButton({
  children,
  className,
  label,
  side = "top",
  sideOffset = 6,
  tooltip,
  ...props
}: Omit<ComponentProps<typeof Button>, "children"> & {
  children: ReactNode;
  label: string;
  side?: ComponentProps<typeof TooltipContent>["side"];
  sideOffset?: number;
  tooltip?: ReactNode;
}) {
  return (
    <Tooltip delayDuration={500} disableHoverableContent>
      <TooltipTrigger asChild>
        <Button
          aria-label={label}
          className={cn(consoleIconButtonClassName, className)}
          size="icon-sm"
          variant="ghost"
          {...props}
        >
          {children}
        </Button>
      </TooltipTrigger>
      <TooltipContent side={side} sideOffset={sideOffset}>
        {tooltip ?? label}
      </TooltipContent>
    </Tooltip>
  );
}
