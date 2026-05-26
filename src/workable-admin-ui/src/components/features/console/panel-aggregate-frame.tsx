"use client";

import type { HTMLAttributes, ReactNode } from "react";
import { ConsoleViewFrame } from "@/components/features/console/console-primitives";
import {
  PanelVisibilitySettings,
  type PanelVisibilityOption,
} from "@/components/features/console/panel-visibility-settings";
import { cn } from "@/lib/utils";

export function PanelAggregateFrame<TId extends string>({
  children,
  className,
  controls,
  hiddenPanelIds,
  onPanelVisibilityChange,
  onResetUi,
  padding = "default",
  panelOptions,
  settingsButtonLabel,
  settingsDescription,
  settingsTitle,
  ...props
}: HTMLAttributes<HTMLDivElement> & {
  children: ReactNode;
  controls?: ReactNode;
  hiddenPanelIds: readonly TId[];
  onPanelVisibilityChange: (panelId: TId, visible: boolean) => void;
  onResetUi?: () => void;
  padding?: "default" | "tightTop";
  panelOptions: readonly PanelVisibilityOption<TId>[];
  settingsButtonLabel: string;
  settingsDescription: string;
  settingsTitle: string;
}) {
  return (
    <ConsoleViewFrame
      className={cn("space-y-2.5", className)}
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
      {children}
    </ConsoleViewFrame>
  );
}
