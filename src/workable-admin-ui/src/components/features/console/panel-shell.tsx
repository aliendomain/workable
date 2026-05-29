"use client";

import type { ReactNode, RefObject, UIEvent } from "react";
import { useEffect, useRef } from "react";
import { ListFilter, MoreHorizontal, Rows2, Rows3, Rows4, X, type LucideIcon } from "lucide-react";
import { Button } from "@/components/ui/button";
import {
  DropdownMenu,
  DropdownMenuContent,
  DropdownMenuItem,
  DropdownMenuTrigger,
} from "@/components/ui/dropdown-menu";
import { Popover, PopoverContent, PopoverTrigger } from "@/components/ui/popover";
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

export type PanelViewState = WorkComponentShape;
export type PanelShapeOption = {
  icon: LucideIcon;
  label: string;
  shape: PanelViewState;
};
export type PanelFilterControl = {
  activeCount: number;
  content: ReactNode;
  contentClassName?: string;
  label: string;
  onOpenChange?: (open: boolean) => void;
};

export const panelViewStateOptions: readonly PanelShapeOption[] = [
  { icon: Rows2, label: "Compact", shape: "compact" },
  { icon: Rows3, label: "Standard", shape: "standard" },
  { icon: Rows4, label: "Detailed", shape: "detailed" },
];

export function PanelShell({
  actions,
  centerActions = false,
  children,
  className,
  contentClassName,
  description,
  filterControl,
  hideTitle = false,
  leadingActions,
  menuLabel = "Panel options",
  onClose,
  onViewStateChange,
  title,
  viewState,
  viewStateOptions = panelViewStateOptions,
  supportedViewStates,
}: {
  actions?: ReactNode;
  centerActions?: boolean;
  children: ReactNode;
  className?: string;
  contentClassName?: string;
  description?: string;
  filterControl?: PanelFilterControl;
  hideTitle?: boolean;
  leadingActions?: ReactNode;
  menuLabel?: string;
  onClose?: () => void;
  onViewStateChange?: (shape: PanelViewState) => void;
  supportedViewStates?: readonly PanelViewState[];
  title: ReactNode;
  viewState?: PanelViewState;
  viewStateOptions?: readonly PanelShapeOption[];
}) {
  const hasMenu = Boolean(
    (viewState && onViewStateChange && supportedViewStates && viewStateOptions.length > 0) || onClose
  );
  const normalizedSupportedViewStates = getNormalizedPanelViewStates(supportedViewStates);
  const supportsSyntheticCompact = Boolean(
    viewState === "compact" &&
    supportedViewStates &&
    !supportedViewStates.includes("compact")
  );
  const supportedOptions = getSupportedPanelShapeOptions(normalizedSupportedViewStates, viewStateOptions);
  const nextViewStateOption = getNextPanelShapeOption(supportedOptions, viewState);
  const canCycleViewState = Boolean(nextViewStateOption && onViewStateChange);
  const mergedContentClassName = supportsSyntheticCompact
    ? "hidden"
    : contentClassName ?? "space-y-4";

  return (
    <ConsolePanelSurface className={className}>
      <ConsolePanelHeader
        className={centerActions ? "grid grid-cols-[minmax(0,1fr)_auto_minmax(0,1fr)]" : undefined}
      >
        <div className="flex min-w-0 items-center gap-2">
          {!hideTitle ? (
            <span className="min-w-0">
              <ConsolePanelTitle className="flex min-w-0 flex-wrap items-center gap-2">
                {title}
              </ConsolePanelTitle>
              {description ? (
                <ConsolePanelDescription>{description}</ConsolePanelDescription>
              ) : null}
            </span>
          ) : null}
          {leadingActions ? (
            <div className="flex min-w-0 flex-wrap items-center gap-1.5">
              {leadingActions}
            </div>
          ) : null}
        </div>
        {centerActions ? (
          <>
            <div className="flex min-w-0 flex-wrap items-center justify-center gap-1.5">
              {actions}
            </div>
            <div className="flex min-w-0 items-center justify-end">
              {filterControl ? (
                <PanelFilterButton filterControl={filterControl} />
              ) : null}
              {canCycleViewState && nextViewStateOption ? (
                <NextViewStateButton
                  onViewStateChange={onViewStateChange!}
                  option={nextViewStateOption}
                />
              ) : null}
              {hasMenu ? (
                <PanelOptionsMenu
                  label={menuLabel}
                  onClose={onClose}
                  onViewStateChange={onViewStateChange}
                  supportedViewStates={normalizedSupportedViewStates}
                  viewState={viewState}
                  viewStateOptions={viewStateOptions}
                />
              ) : null}
            </div>
          </>
        ) : (
          <div className="flex shrink-0 flex-wrap items-center justify-end gap-1.5">
            {actions}
            {(filterControl || canCycleViewState || hasMenu) ? (
              <div className="flex items-center gap-0.5">
                {filterControl ? (
                  <PanelFilterButton filterControl={filterControl} />
                ) : null}
                {canCycleViewState && nextViewStateOption ? (
                  <NextViewStateButton
                    onViewStateChange={onViewStateChange!}
                    option={nextViewStateOption}
                  />
                ) : null}
                {hasMenu ? (
                  <PanelOptionsMenu
                    label={menuLabel}
                    onClose={onClose}
                    onViewStateChange={onViewStateChange}
                    supportedViewStates={normalizedSupportedViewStates}
                    viewState={viewState}
                    viewStateOptions={viewStateOptions}
                  />
                ) : null}
              </div>
            ) : null}
          </div>
        )}
      </ConsolePanelHeader>
      <ConsolePanelBody className={mergedContentClassName}>
        {children}
      </ConsolePanelBody>
    </ConsolePanelSurface>
  );
}

export function PanelScrollViewport({
  children,
  className,
  footerClassName,
  hasMore,
  loadedCount,
  loading,
  loadingMore,
  noun,
  onLoadMore,
  onScroll,
  showLoadedCount = true,
  viewportRef,
}: {
  children: ReactNode;
  className?: string;
  footerClassName?: string;
  hasMore: boolean;
  loadedCount: number;
  loading: boolean;
  loadingMore: boolean;
  noun: string;
  onLoadMore: () => void;
  onScroll?: (event: UIEvent<HTMLDivElement>) => void;
  showLoadedCount?: boolean;
  viewportRef?: RefObject<HTMLDivElement | null>;
}) {
  const internalViewportRef = useRef<HTMLDivElement | null>(null);
  const sentinelRef = useRef<HTMLDivElement | null>(null);
  const scrollRef = viewportRef ?? internalViewportRef;
  const shouldContainOverscroll = hasMore || loading || loadingMore;

  usePanelLoadMoreSentinel(
    scrollRef,
    sentinelRef,
    hasMore,
    loading,
    loadingMore,
    onLoadMore
  );

  return (
    <div
      className={cn(
        "workable-grid-scrollbar min-h-0 flex-1 overflow-auto",
        shouldContainOverscroll ? "overscroll-contain" : "overscroll-auto",
        className
      )}
      onScroll={(event) => {
        if (
          hasMore &&
          !loading &&
          !loadingMore &&
          isNearPanelScrollBottom(event.currentTarget)
        ) {
          onLoadMore();
        }

        onScroll?.(event);
      }}
      ref={scrollRef}
    >
      {children}
      {(hasMore || loading || loadingMore || showLoadedCount) ? (
        <PanelInfiniteFooter
          className={footerClassName}
          hasMore={hasMore}
          loadedCount={loadedCount}
          loading={loading}
          loadingMore={loadingMore}
          noun={noun}
          sentinelRef={sentinelRef}
          showLoadedCount={showLoadedCount}
        />
      ) : null}
    </div>
  );
}

export function PanelInfiniteFooter({
  className,
  hasMore,
  loadedCount,
  loading,
  loadingMore,
  noun,
  sentinelRef,
  showLoadedCount = true,
}: {
  className?: string;
  hasMore: boolean;
  loadedCount: number;
  loading: boolean;
  loadingMore: boolean;
  noun: string;
  sentinelRef: RefObject<HTMLDivElement | null>;
  showLoadedCount?: boolean;
}) {
  if (!loading && !loadingMore && !hasMore && !showLoadedCount) {
    return null;
  }

  return (
    <div
      className={cn(
        "flex h-12 items-center justify-center border-t text-xs text-muted-foreground",
        className
      )}
      ref={sentinelRef}
    >
      {loadingMore ? (
        <span>Loading more...</span>
      ) : loading ? (
        <span>Refreshing...</span>
      ) : hasMore ? (
        <span>Scroll to load more</span>
      ) : (
        <span>Showing {loadedCount.toLocaleString()} {noun}{loadedCount === 1 ? "" : "s"}</span>
      )}
    </div>
  );
}

export function usePanelLoadMoreSentinel(
  scrollRef: RefObject<HTMLElement | null>,
  sentinelRef: RefObject<HTMLElement | null>,
  hasMore: boolean,
  loading: boolean,
  loadingMore: boolean,
  loadMore: () => void
) {
  useEffect(() => {
    const root = scrollRef.current;
    const sentinel = sentinelRef.current;
    if (!root || !sentinel || !hasMore || loading || loadingMore) {
      return;
    }

    const observer = new IntersectionObserver(
      ([entry]) => {
        if (entry?.isIntersecting) {
          loadMore();
        }
      },
      {
        root,
        rootMargin: "96px 0px",
      }
    );

    observer.observe(sentinel);
    return () => observer.disconnect();
  }, [hasMore, loadMore, loading, loadingMore, scrollRef, sentinelRef]);
}

function PanelFilterButton({ filterControl }: { filterControl: PanelFilterControl }) {
  const { activeCount, content, contentClassName, label, onOpenChange } = filterControl;

  return (
    <Popover onOpenChange={onOpenChange}>
      <Tooltip delayDuration={500} disableHoverableContent>
        <TooltipTrigger asChild>
          <PopoverTrigger asChild>
            <Button
              aria-label={activeCount > 0 ? `${label}, ${activeCount} active` : label}
              className={cn(
                consoleIconButtonClassName,
                "relative size-7",
                activeCount > 0 && "text-sky-700 dark:text-sky-200"
              )}
              size="icon-sm"
              variant="ghost"
            >
              <ListFilter className="size-4" />
              {activeCount > 0 ? (
                <span className="-right-0.5 -top-0.5 absolute flex size-4 items-center justify-center rounded-full bg-primary font-medium text-[10px] text-primary-foreground">
                  {activeCount}
                </span>
              ) : null}
            </Button>
          </PopoverTrigger>
        </TooltipTrigger>
        <TooltipContent side="top" sideOffset={6}>
          {label}
        </TooltipContent>
      </Tooltip>
      <PopoverContent align="end" className={cn("w-64 p-0", contentClassName)}>
        {content}
      </PopoverContent>
    </Popover>
  );
}

function NextViewStateButton({
  onViewStateChange,
  option,
}: {
  onViewStateChange: (shape: PanelViewState) => void;
  option: PanelShapeOption;
}) {
  const label = `Next view: ${option.label}`;
  const NextViewIcon = option.icon;

  return (
    <Tooltip delayDuration={500} disableHoverableContent>
      <TooltipTrigger asChild>
        <Button
          aria-label={label}
          className={cn(consoleIconButtonClassName, "size-7")}
          onClick={() => onViewStateChange(option.shape)}
          size="icon-sm"
          type="button"
          variant="ghost"
        >
          <span className="sr-only">{label}</span>
          <NextViewIcon className="size-4" />
        </Button>
      </TooltipTrigger>
      <TooltipContent side="top" sideOffset={6}>
        {label}
      </TooltipContent>
    </Tooltip>
  );
}

function PanelOptionsMenu({
  label,
  onClose,
  onViewStateChange,
  supportedViewStates,
  viewState,
  viewStateOptions,
}: {
  label: string;
  onClose?: () => void;
  onViewStateChange?: (shape: PanelViewState) => void;
  supportedViewStates?: readonly PanelViewState[];
  viewState?: PanelViewState;
  viewStateOptions: readonly PanelShapeOption[];
}) {
  const canChangeShape = Boolean(viewState && onViewStateChange && supportedViewStates && viewStateOptions.length > 0);

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
          ? viewStateOptions.map((option) => {
              const Icon = option.icon;
              const supported = supportedViewStates?.includes(option.shape) ?? false;
              const active = viewState === option.shape;

              return (
                <DropdownMenuItem
                  className={active ? "bg-accent/60" : undefined}
                  disabled={!supported}
                  key={option.shape}
                  onSelect={() => {
                    if (supported) {
                      onViewStateChange?.(option.shape);
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

function getSupportedPanelShapeOptions(
  supportedViewStates: readonly PanelViewState[] | undefined,
  viewStateOptions: readonly PanelShapeOption[]
) {
  return viewStateOptions.filter((option) =>
    supportedViewStates?.includes(option.shape) ?? false
  );
}

function getNextPanelShapeOption(
  supportedOptions: readonly PanelShapeOption[],
  viewState: PanelViewState | undefined
) {
  if (!viewState || supportedOptions.length < 2) {
    return null;
  }

  const currentIndex = supportedOptions.findIndex((option) => option.shape === viewState);
  if (currentIndex < 0) {
    return supportedOptions[0] ?? null;
  }

  return supportedOptions[(currentIndex + 1) % supportedOptions.length] ?? null;
}

function getNormalizedPanelViewStates(supportedViewStates: readonly PanelViewState[] | undefined) {
  if (!supportedViewStates || supportedViewStates.includes("compact")) {
    return supportedViewStates;
  }

  return ["compact", ...supportedViewStates] as const;
}

function isNearPanelScrollBottom(element: HTMLElement) {
  return element.scrollHeight - element.scrollTop - element.clientHeight <= 96;
}
