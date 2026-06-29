"use client";

import { Loader2, type LucideIcon } from "lucide-react";
import type { ReactNode } from "react";
import { useState } from "react";
import {
  AlertDialog,
  AlertDialogAction,
  AlertDialogCancel,
  AlertDialogContent,
  AlertDialogDescription,
  AlertDialogFooter,
  AlertDialogHeader,
  AlertDialogTitle,
} from "@/components/ui/alert-dialog";
import { Button } from "@/components/ui/button";
import { Tooltip, TooltipContent, TooltipTrigger } from "@/components/ui/tooltip";
import { cn } from "@/lib/utils";

export function consoleActionToneClassName(disabled: boolean) {
  if (disabled) {
    return "";
  }

  return "border-border bg-muted/20 text-foreground hover:bg-muted/35";
}

export type ExecutionControlSubject = "worker" | "workflow";

type ExecutionControlConfirmProps = {
  cancelLabel?: string;
  confirmClassName?: string;
  confirmDescription?: ReactNode;
  confirmLabel?: string;
  confirmTitle?: string;
};

export function createExecutionControlConfirmProps(
  action: string,
  subject: ExecutionControlSubject,
  executionMayStop = false
): ExecutionControlConfirmProps {
  if (action !== "Pause" && action !== "Cancel") {
    return {};
  }

  const targetLabel = subject === "worker" ? "worker" : "workflow";
  const inFlightLabel = subject === "worker" ? "execution" : "child work";

  if (action === "Cancel") {
    return {
      cancelLabel: "Keep running",
      confirmClassName:
        "bg-[var(--status-danger-solid)] text-[var(--status-danger-contrast)] hover:bg-[var(--status-danger-text)] focus-visible:ring-[var(--status-danger-border)]",
      confirmDescription: (
        <>
          This will request cancellation for the current {targetLabel}.
          {executionMayStop
            ? ` Any in-flight ${inFlightLabel} may stop as soon as the ${subject} observes the cancellation.`
            : ""}
          {" "}Cancellation is final and cannot be undone.
        </>
      ),
      confirmLabel: `Cancel ${targetLabel}`,
      confirmTitle: `Cancel ${targetLabel}?`,
    };
  }

  return {
    cancelLabel: "Keep executing",
    confirmClassName:
      "!bg-[var(--status-warning-solid)] !text-[var(--status-warning-contrast)] hover:!bg-[var(--status-warning-text)] focus-visible:ring-[var(--status-warning-border)]",
    confirmDescription: executionMayStop
      ? `This will request that the current ${targetLabel} pause. Any in-flight ${inFlightLabel} may stop when the ${subject} observes the pause request, and the ${targetLabel} can be resumed later.`
      : `This will move the current ${targetLabel} into the paused state, and it can be resumed later.`,
    confirmLabel: `Pause ${targetLabel}`,
    confirmTitle: `Pause ${targetLabel}?`,
  };
}

export function ExecutionStatusBadge({
  label,
  timing,
  toneClassName,
}: {
  label: ReactNode;
  timing?: ReactNode;
  toneClassName: string;
}) {
  return (
    <div className={cn("inline-flex min-w-[6rem] flex-col items-center justify-center gap-0.5 text-[11px] leading-none", toneClassName)}>
      <span className="inline-flex items-center justify-center font-medium leading-none">{label}</span>
      {timing ? (
        <span className="inline-flex items-center justify-center tabular-nums leading-none">{timing}</span>
      ) : null}
    </div>
  );
}

export function ConsoleActionButton({
  cancelLabel,
  className,
  confirmClassName,
  confirmDescription,
  confirmLabel,
  confirmTitle,
  disabled,
  icon: Icon,
  label,
  loading = false,
  onAction,
  tooltip,
}: {
  cancelLabel?: string;
  className?: string;
  confirmClassName?: string;
  confirmDescription?: ReactNode;
  confirmLabel?: string;
  confirmTitle?: string;
  disabled?: boolean;
  icon: LucideIcon;
  label: string;
  loading?: boolean;
  onAction: () => Promise<void> | void;
  tooltip?: string;
}) {
  const [confirmOpen, setConfirmOpen] = useState(false);

  const button = (
    <Button
      className={className}
      disabled={disabled}
      onClick={() => {
        if (confirmTitle && confirmDescription && confirmLabel && cancelLabel) {
          setConfirmOpen(true);
          return;
        }

        void onAction();
      }}
      size="sm"
      type="button"
      variant="outline"
    >
      {loading ? <Loader2 className="size-4 animate-spin" /> : <Icon className="size-4" />}
      {label}
    </Button>
  );

  const buttonWithTooltip = tooltip ? (
    <Tooltip delayDuration={250}>
      <TooltipTrigger asChild>
        {button}
      </TooltipTrigger>
      <TooltipContent side="top" sideOffset={6}>
        {tooltip}
      </TooltipContent>
    </Tooltip>
  ) : button;

  if (!(confirmTitle && confirmDescription && confirmLabel && cancelLabel)) {
    return buttonWithTooltip;
  }

  return (
    <AlertDialog onOpenChange={setConfirmOpen} open={confirmOpen}>
      {buttonWithTooltip}
      <AlertDialogContent>
        <AlertDialogHeader>
          <AlertDialogTitle>{confirmTitle}</AlertDialogTitle>
          <AlertDialogDescription>
            {confirmDescription}
          </AlertDialogDescription>
        </AlertDialogHeader>
        <AlertDialogFooter>
          <AlertDialogCancel>{cancelLabel}</AlertDialogCancel>
          <AlertDialogAction
            variant="default"
            className={confirmClassName}
            onClick={() => {
              setConfirmOpen(false);
              void onAction();
            }}
          >
            {confirmLabel}
          </AlertDialogAction>
        </AlertDialogFooter>
      </AlertDialogContent>
    </AlertDialog>
  );
}
