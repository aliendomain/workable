"use client";

import { Info } from "lucide-react";
import type { ReactNode } from "react";
import { Label } from "@/components/ui/label";
import { Tooltip, TooltipContent, TooltipTrigger } from "@/components/ui/tooltip";
import { cn } from "@/lib/utils";

export function FormField({
  children,
  className,
  description,
  details,
  htmlFor,
  label,
  labelClassName,
  maxWidth = "md",
}: {
  children: ReactNode;
  className?: string;
  description?: ReactNode;
  details?: ReactNode;
  htmlFor?: string;
  label?: ReactNode;
  labelClassName?: string;
  maxWidth?: "none" | "md";
}) {
  return (
    <div
      className={cn(
        "grid gap-2",
        maxWidth === "md" && "w-full max-w-md",
        className
      )}
    >
      {label ? (
        <FormFieldHeader
          description={description}
          details={details}
          htmlFor={htmlFor}
          label={label}
          labelClassName={labelClassName}
        />
      ) : null}
      {children}
    </div>
  );
}

export function FormFieldHeader({
  description,
  details,
  htmlFor,
  label,
  labelClassName,
}: {
  description?: ReactNode;
  details?: ReactNode;
  htmlFor?: string;
  label: ReactNode;
  labelClassName?: string;
}) {
  const hasTooltip = Boolean(description || details);

  if (!hasTooltip) {
    return (
      <Label className={labelClassName} htmlFor={htmlFor}>
        {label}
      </Label>
    );
  }

  return (
    <div className="flex min-w-0 items-center">
      <Tooltip delayDuration={500} disableHoverableContent>
        <TooltipTrigger asChild>
          <button
            aria-label={`${stringifyLabel(label)} field details`}
            className={cn(
              "group flex min-w-0 items-center gap-1.5 rounded-sm text-left focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring focus-visible:ring-offset-2 focus-visible:ring-offset-background",
              labelClassName
            )}
            type="button"
          >
            <span className="min-w-0 truncate font-medium text-foreground text-sm leading-none">
              {label}
            </span>
            <Info className="size-3.5 shrink-0 text-muted-foreground transition-colors group-hover:text-foreground" />
          </button>
        </TooltipTrigger>
        <TooltipContent
          className="max-w-[min(18rem,calc(100vw-2rem))] items-start whitespace-normal break-words text-left"
          collisionPadding={12}
          sideOffset={6}
        >
          <div className="min-w-0 space-y-1.5">
            {description ? (
              <p className="font-sans text-background text-xs leading-snug break-words">
                {description}
              </p>
            ) : null}
            {details}
          </div>
        </TooltipContent>
      </Tooltip>
    </div>
  );
}

export function FormEmptyState({
  children,
  className,
  padding = "default",
}: {
  children: ReactNode;
  className?: string;
  padding?: "compact" | "default";
}) {
  return (
    <div
      className={cn(
        "rounded-lg border border-dashed text-muted-foreground text-sm",
        padding === "compact" ? "p-4" : "p-6",
        className
      )}
    >
      {children}
    </div>
  );
}

export function ReadonlyFormValue({
  children,
  className,
}: {
  children: ReactNode;
  className?: string;
}) {
  return (
    <div className={cn("rounded-lg border bg-muted/30 px-3 py-2 font-mono text-sm", className)}>
      {children}
    </div>
  );
}

function stringifyLabel(label: ReactNode) {
  return typeof label === "string" || typeof label === "number"
    ? String(label)
    : "Form";
}
