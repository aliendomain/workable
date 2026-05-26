"use client";

import type { HTMLAttributes, ReactNode } from "react";
import { ConsoleViewFrame } from "@/components/features/console/console-primitives";
import {
  PanelVisibilitySettings,
  type PanelVisibilityOption,
} from "@/components/features/console/panel-visibility-settings";
import { cn } from "@/lib/utils";

export type PanelAggregateFrameScrollMode = "browser" | "panel";

export function PanelAggregateFrame<TId extends string>({
  children,
  className,
  controls,
  fill = false,
  hiddenPanelIds,
  onPanelVisibilityChange,
  onResetUi,
  padding = "default",
  panelOptions,
  scrollMode = "browser",
  settingsButtonLabel,
  settingsDescription,
  settingsTitle,
  ...props
}: HTMLAttributes<HTMLDivElement> & {
  children: ReactNode;
  controls?: ReactNode;
  fill?: boolean;
  hiddenPanelIds: readonly TId[];
  onPanelVisibilityChange: (panelId: TId, visible: boolean) => void;
  onResetUi?: () => void;
  padding?: "default" | "tightTop";
  panelOptions: readonly PanelVisibilityOption<TId>[];
  scrollMode?: PanelAggregateFrameScrollMode;
  settingsButtonLabel: string;
  settingsDescription: string;
  settingsTitle: string;
}) {
  return (
    <ConsoleViewFrame
      className={cn(
        "space-y-2.5",
        fill && "flex min-h-0 flex-1 flex-col",
        fill && scrollMode === "panel" && "overflow-hidden",
        className
      )}
      padding={padding}
      {...props}
    >
      <div className="flex min-w-0 items-center justify-end gap-1">
        {controls}
        <PanelVisibilitySettings
          buttonLabel={settingsButtonLabel}
          description={settingsDescription}
          hiddenPanelIds={hiddenPanelIds}
          onPanelVisibilityChange={onPanelVisibilityChange}
          onResetUi={onResetUi}
          panelOptions={panelOptions}
          title={settingsTitle}
        />
      </div>
      <div
        className={cn(
          fill && "flex min-h-0 flex-1 flex-col",
          fill && scrollMode === "panel" && "overflow-hidden"
        )}
      >
        {children}
      </div>
    </ConsoleViewFrame>
  );
}
