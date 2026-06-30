"use client";

import { RotateCcw, Settings } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Popover, PopoverContent, PopoverTrigger } from "@/components/ui/popover";
import { Tooltip, TooltipContent, TooltipTrigger } from "@/components/ui/tooltip";
import { consoleIconButtonClassName } from "@/lib/ui/console";

export type PanelVisibilityOption<TPanelId extends string> = {
  description: string;
  id: TPanelId;
  label: string;
};

export function PanelVisibilitySettings<TPanelId extends string>({
  buttonLabel = "Panel settings",
  description = "Checked panels are shown on this page.",
  hiddenPanelIds,
  onPanelVisibilityChange,
  onResetUi,
  panelOptions,
  resetLabel = "Reset UI to defaults",
  title = "Panels",
}: {
  buttonLabel?: string;
  description?: string;
  hiddenPanelIds: readonly TPanelId[];
  onPanelVisibilityChange: (panelId: TPanelId, visible: boolean) => void;
  onResetUi?: () => void;
  panelOptions: readonly PanelVisibilityOption<TPanelId>[];
  resetLabel?: string;
  title?: string;
}) {
  return (
    <Popover>
      <Tooltip delayDuration={500} disableHoverableContent>
        <TooltipTrigger asChild>
          <PopoverTrigger asChild>
            <Button
              aria-label={buttonLabel}
              className={consoleIconButtonClassName}
              size="icon-sm"
              variant="ghost"
            >
              <Settings className="size-4" />
            </Button>
          </PopoverTrigger>
        </TooltipTrigger>
        <TooltipContent side="bottom" sideOffset={6}>
          {buttonLabel}
        </TooltipContent>
      </Tooltip>
      <PopoverContent align="end" className="w-80 p-0">
        <div className="flex items-start justify-between gap-3 border-b px-3 py-2">
          <div className="min-w-0">
            <div className="font-medium text-sm">{title}</div>
            <div className="text-muted-foreground text-xs">{description}</div>
          </div>
          <Button
            className="h-6 shrink-0 px-2 text-xs"
            onClick={() => panelOptions.forEach((panel) => onPanelVisibilityChange(panel.id, true))}
            size="sm"
            variant="ghost"
          >
            All
          </Button>
        </div>
        <div className="space-y-1 p-2">
          {panelOptions.map((panel) => {
            const visible = isPanelVisibleInSettings(hiddenPanelIds, panel.id);

            return (
              <label
                className="flex cursor-pointer items-start gap-3 rounded-md px-2 py-2 transition-colors hover:bg-accent/40"
                key={panel.id}
              >
                <input
                  checked={visible}
                  className="mt-0.5 size-4 accent-primary"
                  onChange={(event) =>
                    onPanelVisibilityChange(panel.id, event.currentTarget.checked)
                  }
                  type="checkbox"
                />
                <span className="min-w-0">
                  <span className="block font-medium text-sm">{panel.label}</span>
                  <span className="block text-muted-foreground text-xs">
                    {panel.description}
                  </span>
                </span>
              </label>
            );
          })}
        </div>
        {onResetUi ? (
          <div className="border-t p-2">
            <Button
              className="h-9 w-full justify-start gap-2 text-muted-foreground"
              onClick={() => onResetUi()}
              size="sm"
              variant="ghost"
            >
              <RotateCcw className="size-4" />
              {resetLabel}
            </Button>
          </div>
        ) : null}
      </PopoverContent>
    </Popover>
  );
}

export function isPanelVisibleInSettings<TPanelId extends string>(
  hiddenPanelIds: readonly TPanelId[],
  panelId: TPanelId
) {
  return !hiddenPanelIds.includes(panelId);
}
