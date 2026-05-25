"use client";

import type { ReactNode } from "react";
import { MoreHorizontal, X, type LucideIcon } from "lucide-react";
import { Button } from "@/components/ui/button";
import {
  DropdownMenu,
  DropdownMenuContent,
  DropdownMenuItem,
  DropdownMenuTrigger,
} from "@/components/ui/dropdown-menu";
import { Tooltip, TooltipContent, TooltipTrigger } from "@/components/ui/tooltip";
import {
  ConsolePanelBody,
  ConsolePanelDescription,
  ConsolePanelHeader,
  ConsolePanelSurface,
  ConsolePanelTitle,
} from "@/components/features/console/console-primitives";
import { consoleIconButtonClassName } from "@/lib/ui/console";
import { cn } from "@/lib/utils";
import type { WorkComponentShape } from "@/lib/workable";

export type PanelShapeOption = {
  icon: LucideIcon;
  label: string;
  shape: WorkComponentShape;
};

export function PanelShell({
  actions,
  centerActions = false,
  children,
  className,
  contentClassName,
  description,
  menuLabel = "Panel options",
  onClose,
  onShapeChange,
  shape,
  shapeOptions,
  supportedShapes,
  title,
}: {
  actions?: ReactNode;
  centerActions?: boolean;
  children: ReactNode;
  className?: string;
  contentClassName?: string;
  description?: string;
  menuLabel?: string;
  onClose?: () => void;
  onShapeChange?: (shape: WorkComponentShape) => void;
  shape?: WorkComponentShape;
  shapeOptions?: readonly PanelShapeOption[];
  supportedShapes?: readonly WorkComponentShape[];
  title: ReactNode;
}) {
  const hasMenu = Boolean((shape && onShapeChange && supportedShapes && shapeOptions?.length) || onClose);

  return (
    <ConsolePanelSurface className={className}>
      <ConsolePanelHeader
        className={centerActions ? "grid grid-cols-[minmax(0,1fr)_auto_minmax(0,1fr)]" : undefined}
      >
        <div className="flex min-w-0 items-center gap-2">
          <span className="min-w-0">
            <ConsolePanelTitle className="flex min-w-0 flex-wrap items-center gap-2">
              {title}
            </ConsolePanelTitle>
            {description ? (
              <ConsolePanelDescription>{description}</ConsolePanelDescription>
            ) : null}
          </span>
        </div>
        {centerActions ? (
          <>
            <div className="flex min-w-0 flex-wrap items-center justify-center gap-1.5">
              {actions}
            </div>
            <div className="flex min-w-0 items-center justify-end">
              {hasMenu ? (
                <PanelOptionsMenu
                  label={menuLabel}
                  onClose={onClose}
                  onShapeChange={onShapeChange}
                  shape={shape}
                  shapeOptions={shapeOptions}
                  supportedShapes={supportedShapes}
                />
              ) : null}
            </div>
          </>
        ) : (
          <div className="flex shrink-0 flex-wrap items-center justify-end gap-1.5">
            {actions}
            {hasMenu ? (
              <PanelOptionsMenu
                label={menuLabel}
                onClose={onClose}
                onShapeChange={onShapeChange}
                shape={shape}
                shapeOptions={shapeOptions}
                supportedShapes={supportedShapes}
              />
            ) : null}
          </div>
        )}
      </ConsolePanelHeader>
      <ConsolePanelBody className={contentClassName ?? "space-y-4"}>
        {children}
      </ConsolePanelBody>
    </ConsolePanelSurface>
  );
}

function PanelOptionsMenu({
  label,
  onClose,
  onShapeChange,
  shape,
  shapeOptions,
  supportedShapes,
}: {
  label: string;
  onClose?: () => void;
  onShapeChange?: (shape: WorkComponentShape) => void;
  shape?: WorkComponentShape;
  shapeOptions?: readonly PanelShapeOption[];
  supportedShapes?: readonly WorkComponentShape[];
}) {
  const canChangeShape = Boolean(shape && onShapeChange && supportedShapes && shapeOptions?.length);

  return (
    <DropdownMenu modal={false}>
      <Tooltip delayDuration={500} disableHoverableContent>
        <TooltipTrigger asChild>
          <DropdownMenuTrigger asChild>
            <Button
              aria-label={label}
              className={cn(consoleIconButtonClassName, "size-7")}
              size="icon-sm"
              variant="ghost"
            >
              <MoreHorizontal className="size-4" />
            </Button>
          </DropdownMenuTrigger>
        </TooltipTrigger>
        <TooltipContent side="top" sideOffset={6}>
          {label}
        </TooltipContent>
      </Tooltip>
      <DropdownMenuContent align="end" className="w-44">
        {canChangeShape
          ? shapeOptions?.map((option) => {
              const Icon = option.icon;
              const supported = supportedShapes?.includes(option.shape) ?? false;
              const active = shape === option.shape;

              return (
                <DropdownMenuItem
                  className={active ? "bg-accent/60" : undefined}
                  disabled={!supported}
                  key={option.shape}
                  onSelect={() => {
                    if (supported) {
                      onShapeChange?.(option.shape);
                    }
                  }}
                >
                  <Icon className="size-4" />
                  <span>{option.label}</span>
                  {!supported ? (
                    <span className="ml-auto text-muted-foreground text-xs">Unavailable</span>
                  ) : active ? (
                    <span className="ml-auto text-muted-foreground text-xs">Current</span>
                  ) : null}
                </DropdownMenuItem>
              );
            })
          : null}
        {onClose ? (
          <DropdownMenuItem
            className={cn(canChangeShape && "border-t")}
            onSelect={() => {
              onClose();
            }}
          >
            <X className="size-4" />
            <span>Hide panel</span>
          </DropdownMenuItem>
        ) : null}
      </DropdownMenuContent>
    </DropdownMenu>
  );
}
