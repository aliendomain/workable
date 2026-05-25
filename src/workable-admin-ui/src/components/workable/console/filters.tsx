"use client";

import {
  CheckCircle2,
  ChevronLeft,
  FileCode2,
  Folder,
  Home,
  ListFilter,
  Square,
} from "lucide-react";
import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import type { Loadable, OverviewScope } from "@/components/features/console/types";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { Popover, PopoverContent, PopoverTrigger } from "@/components/ui/popover";
import { ScrollArea } from "@/components/ui/scroll-area";
import { Skeleton } from "@/components/ui/skeleton";
import { Tooltip, TooltipContent, TooltipTrigger } from "@/components/ui/tooltip";
import {
  workableFetch,
  type WorkComponentQueryResult,
  type WorkComponentResult,
  type WorkDefinition,
  type WorkOverviewCatalogCategoryItem,
  type WorkSystemOverview,
  type WorkableConnection,
} from "@/lib/workable";

type WorkOverviewCatalogComponent = Pick<WorkSystemOverview, "catalogCategories" | "catalogDefinitions">;
type DefinitionCatalogLevel = {
  categories: WorkOverviewCatalogCategoryItem[];
  definitions: WorkDefinition[];
};
type CatalogFilterDefinitionItem = Pick<WorkDefinition, "id" | "name"> & {
  category?: string | null;
};

export function OverviewCatalogFilter({
  connection,
  loading,
  onClear,
  onSelectCategory,
  onSelectDefinition,
  refreshToken,
  scope,
  tooltipLabel = "Filter overview by category and definition",
}: {
  connection: WorkableConnection;
  loading: boolean;
  onClear: () => void;
  onSelectCategory: (category: string) => void;
  onSelectDefinition: (definitionName: string, category: string) => void;
  refreshToken: number;
  scope: OverviewScope | null;
  tooltipLabel?: string;
}) {
  const [open, setOpen] = useState(false);
  const [tooltipOpen, setTooltipOpen] = useState(false);
  const tooltipOpenTimer = useRef<ReturnType<typeof setTimeout> | null>(null);
  const [path, setPath] = useState(scope?.category ?? "");
  const activeFilterCount = scope ? 1 : 0;
  const scopeLabel = formatOverviewScopeLabel(scope);
  const filterTooltip = scopeLabel
    ? `Filtered by catalog: ${scopeLabel}`
    : tooltipLabel;
  const catalog = useWorkableResource<DefinitionCatalogLevel>(
    connection,
    open ? createDefinitionCatalogLevelPath(path) : null,
    refreshToken
  );

  const closeTooltip = useCallback(() => {
    if (tooltipOpenTimer.current) {
      clearTimeout(tooltipOpenTimer.current);
      tooltipOpenTimer.current = null;
    }
    setTooltipOpen(false);
  }, []);

  const scheduleTooltip = useCallback(() => {
    closeTooltip();
    if (open) {
      return;
    }

    tooltipOpenTimer.current = setTimeout(() => {
      setTooltipOpen(true);
      tooltipOpenTimer.current = null;
    }, 500);
  }, [closeTooltip, open]);

  useEffect(() => () => {
    if (tooltipOpenTimer.current) {
      clearTimeout(tooltipOpenTimer.current);
    }
  }, []);


  const handleOpenChange = (nextOpen: boolean) => {
    closeTooltip();
    if (nextOpen) {
      setPath(scope?.category ?? "");
    }
    setOpen(nextOpen);
  };

  const clearAll = () => {
    closeTooltip();
    setPath("");
    onClear();
  };

  return (
    <Popover open={open} onOpenChange={handleOpenChange}>
      <Tooltip
        disableHoverableContent
        open={tooltipOpen}
      >
        <TooltipTrigger asChild>
          <PopoverTrigger asChild>
            <Button
              aria-label="Filter overview"
              className="relative text-muted-foreground hover:bg-transparent hover:text-foreground aria-expanded:bg-transparent aria-expanded:text-foreground dark:hover:bg-transparent"
              onBlur={closeTooltip}
              onClick={closeTooltip}
              onFocus={closeTooltip}
              onPointerEnter={scheduleTooltip}
              onPointerLeave={closeTooltip}
              size="icon-sm"
              variant="ghost"
            >
              <ListFilter className="size-4" />
              {activeFilterCount > 0 && (
                <span className="-right-0.5 -top-0.5 absolute flex size-4 items-center justify-center rounded-full bg-primary font-medium text-[10px] text-primary-foreground">
                  {activeFilterCount}
                </span>
              )}
            </Button>
          </PopoverTrigger>
        </TooltipTrigger>
        <TooltipContent
          className="max-w-80 whitespace-normal text-left"
          side="bottom"
          sideOffset={6}
        >
          {filterTooltip}
        </TooltipContent>
      </Tooltip>
      <PopoverContent align="end" className="w-[26rem] p-0">
        <div className="flex h-10 items-center justify-between border-b px-3">
          <span className="font-medium text-sm">Filters</span>
          <Button onClick={clearAll} size="sm" variant="ghost">
            Clear
          </Button>
        </div>
        <ScrollArea className="max-h-[70vh]">
          <div className="p-3">
            <div className="overflow-hidden rounded-lg border">
              <div className="border-b px-3 py-2 font-medium text-muted-foreground text-xs">
                Catalog
              </div>
              <CatalogFilterPanel
                categories={catalog.data?.categories ?? []}
                definitions={catalog.data?.definitions ?? []}
                loading={loading || catalog.loading || !!catalog.refreshing}
                onClear={clearAll}
                onClose={() => setOpen(false)}
                onSelectCategory={(category) => {
                  closeTooltip();
                  onSelectCategory(category);
                }}
                onSelectDefinition={(definitionName, category) => {
                  closeTooltip();
                  onSelectDefinition(definitionName, category);
                }}
                path={path}
                scope={scope}
                setPath={setPath}
              />
            </div>
          </div>
        </ScrollArea>
      </PopoverContent>
    </Popover>
  );
}

function CatalogFilterPanel({
  categories,
  definitions,
  loading,
  onClear,
  onClose,
  onSelectCategory,
  onSelectDefinition,
  path,
  scope,
  setPath,
}: {
  categories: WorkOverviewCatalogCategoryItem[];
  definitions: CatalogFilterDefinitionItem[];
  loading: boolean;
  onClear: () => void;
  onClose?: () => void;
  onSelectCategory: (category: string) => void;
  onSelectDefinition: (definitionName: string, category: string) => void;
  path: string;
  scope: OverviewScope | null;
  setPath: (path: string) => void;
}) {
  const pathSegments = splitCatalogPath(path);
  const currentLabel = pathSegments.at(-1) ?? "All categories";
  const canGoBack = pathSegments.length > 0;

  const selectCategory = (category: string) => {
    setPath(category);
    onSelectCategory(category);
  };

  const clear = () => {
    setPath("");
    onClear();
  };

  const goBack = () => {
    selectCategory(pathSegments.slice(0, -1).join(":"));
  };

  return (
    <>
      <div className="flex h-10 min-w-0 items-center gap-1 border-b px-2">
        <button
          aria-label={canGoBack ? "Back to parent category" : "Catalog root"}
          className="flex size-7 shrink-0 items-center justify-center rounded-md hover:bg-accent hover:text-accent-foreground disabled:pointer-events-none disabled:opacity-40"
          disabled={!canGoBack}
          onClick={goBack}
          type="button"
        >
          {canGoBack ? <ChevronLeft className="size-4" /> : <Home className="size-4" />}
        </button>
        <span className="min-w-0 flex-1 truncate font-medium text-sm">
          {currentLabel}
        </span>
        <Button onClick={clear} size="sm" variant="ghost">
          All
        </Button>
      </div>
      <ScrollArea className="max-h-80">
        <div className="py-1">
          {loading ? (
            Array.from({ length: 5 }).map((_, index) => (
              <Skeleton className="mx-2 my-1 h-8" key={index} />
            ))
          ) : (
            <>
              {categories.map((category) => {
                const isActive =
                  !scope?.definitionName &&
                  normalizeCategoryFilter(scope?.category ?? "") ===
                    normalizeCategoryFilter(category.path);

                return (
                  <button
                    className={
                      isActive
                        ? "flex h-8 w-full min-w-0 items-center gap-2 bg-accent px-2 text-left text-accent-foreground text-sm"
                        : "flex h-8 w-full min-w-0 items-center gap-2 px-2 text-left text-sm hover:bg-accent hover:text-accent-foreground"
                    }
                    key={category.path}
                    onClick={() => selectCategory(category.path)}
                    type="button"
                  >
                    <Folder className="size-4 shrink-0 text-muted-foreground" />
                    <span className="min-w-0 flex-1 truncate">{category.label}</span>
                    <span className="shrink-0 text-muted-foreground text-xs tabular-nums">
                      {category.count}
                    </span>
                  </button>
                );
              })}
              {definitions.map((definition) => {
                const isActive = definition.name === scope?.definitionName;

                return (
                  <button
                    className={
                      isActive
                        ? "flex h-8 w-full min-w-0 items-center gap-2 bg-accent px-2 text-left text-accent-foreground text-sm"
                        : "flex h-8 w-full min-w-0 items-center gap-2 px-2 text-left text-sm hover:bg-accent hover:text-accent-foreground"
                    }
                    key={definition.id.value}
                    onClick={() => {
                      onSelectDefinition(
                        definition.name,
                        definition.category ?? path
                      );
                      onClose?.();
                    }}
                    type="button"
                  >
                    <FileCode2 className="size-4 shrink-0 text-muted-foreground" />
                    <span className="min-w-0 flex-1 truncate font-mono">
                      {definition.name}
                    </span>
                  </button>
                );
              })}
              {categories.length === 0 && definitions.length === 0 && (
                <div className="px-3 py-3 text-muted-foreground text-sm">
                  No catalog entries.
                </div>
              )}
            </>
          )}
        </div>
      </ScrollArea>
    </>
  );
}

function QueryFacetPanel<TValue extends string>({
  allLabel,
  onChange,
  options,
  value,
}: {
  allLabel: string;
  onChange: (value: TValue[]) => void;
  options: TValue[];
  value: TValue[];
}) {
  const selected = new Set(value);
  const selectedLabel =
    value.length === 0
      ? allLabel
      : value.length === 1
        ? value[0]
        : `${value.length} selected`;

  const setEnabled = (option: TValue, enabled: boolean) => {
    const next = new Set(selected);
    if (enabled) {
      next.add(option);
    } else {
      next.delete(option);
    }
    onChange(options.filter((item) => next.has(item)));
  };

  return (
    <div>
      <div className="flex h-10 items-center justify-between border-b px-3">
        <span className="truncate font-medium text-sm">{selectedLabel}</span>
        <Button onClick={() => onChange([])} size="sm" variant="ghost">
          All
        </Button>
      </div>
      <div className="py-1">
        {options.map((option) => {
          const isSelected = selected.has(option);

          return (
            <button
              className={
                isSelected
                  ? "flex h-8 w-full items-center gap-2 bg-accent px-3 text-accent-foreground text-sm"
                  : "flex h-8 w-full items-center gap-2 px-3 text-sm hover:bg-accent hover:text-accent-foreground"
              }
              key={option}
              onClick={() => setEnabled(option, !isSelected)}
              type="button"
            >
              {isSelected ? (
                <CheckCircle2 className="size-4 shrink-0 text-primary" />
              ) : (
                <Square className="size-4 shrink-0 text-muted-foreground" />
              )}
              <span>{option}</span>
            </button>
          );
        })}
      </div>
    </div>
  );
}

export function QueryFilterPopover<TValue extends string>({
  allFacetLabel,
  catalogScope,
  connection,
  facetLabel,
  facetOptions,
  facetValue,
  keyTypeFilter,
  onClearCatalog,
  onFacetChange,
  onKeyTypeFilterChange,
  onSelectCategory,
  onSelectDefinition,
  refreshToken,
}: {
  allFacetLabel: string;
  catalogScope: OverviewScope | null;
  connection: WorkableConnection;
  facetLabel: string;
  facetOptions: TValue[];
  facetValue: TValue[];
  keyTypeFilter: string;
  onClearCatalog: () => void;
  onFacetChange: (value: TValue[]) => void;
  onKeyTypeFilterChange: (keyType: string) => void;
  onSelectCategory: (category: string) => void;
  onSelectDefinition: (definitionName: string, category: string) => void;
  refreshToken: number;
}) {
  const [open, setOpen] = useState(false);
  const [path, setPath] = useState(catalogScope?.category ?? "");
  const catalogRequest = useMemo(
    () => ({
      components: [{ id: "catalog", type: "catalog" }],
      scope: createOverviewComponentScope(catalogScope),
    }),
    [catalogScope]
  );
  const catalog = useWorkablePostResource<WorkComponentQueryResult>(
    connection,
    open ? "components/query" : null,
    catalogRequest,
    refreshToken
  );
  const catalogComponent = getWorkComponentData<WorkOverviewCatalogComponent>(
    open ? catalog.data : undefined,
    "catalog"
  );
  const activeFilterCount =
    (catalogScope ? 1 : 0) +
    (keyTypeFilter.trim() ? 1 : 0) +
    (facetValue.length > 0 ? 1 : 0);
  const filterDescriptions = createQueryFilterDescriptions(
    catalogScope,
    facetLabel,
    facetValue,
    keyTypeFilter
  );
  const filterTooltip =
    filterDescriptions.length > 0
      ? `Filtered by ${filterDescriptions.join("; ")}`
      : "Filter query";

  const handleOpenChange = (nextOpen: boolean) => {
    if (nextOpen) {
      setPath(catalogScope?.category ?? "");
    }
    setOpen(nextOpen);
  };

  const clearAll = () => {
    onClearCatalog();
    onKeyTypeFilterChange("");
    onFacetChange([]);
    setPath("");
  };

  return (
    <Popover open={open} onOpenChange={handleOpenChange}>
      <Tooltip delayDuration={500} disableHoverableContent>
        <TooltipTrigger asChild>
          <PopoverTrigger asChild>
            <Button
              aria-label="Filter query"
              className="relative text-muted-foreground hover:bg-transparent hover:text-foreground aria-expanded:bg-transparent aria-expanded:text-foreground dark:hover:bg-transparent"
              size="icon-sm"
              variant="ghost"
            >
              <ListFilter className="size-4" />
              {activeFilterCount > 0 && (
                <span className="-right-0.5 -top-0.5 absolute flex size-4 items-center justify-center rounded-full bg-primary font-medium text-[10px] text-primary-foreground">
                  {activeFilterCount}
                </span>
              )}
            </Button>
          </PopoverTrigger>
        </TooltipTrigger>
        <TooltipContent
          className="max-w-80 whitespace-normal text-left"
          side="bottom"
          sideOffset={6}
        >
          {filterTooltip}
        </TooltipContent>
      </Tooltip>
      <PopoverContent align="end" className="w-[26rem] p-0">
        <div className="flex h-10 items-center justify-between border-b px-3">
          <span className="font-medium text-sm">Filters</span>
          <Button onClick={clearAll} size="sm" variant="ghost">
            Clear
          </Button>
        </div>
        <ScrollArea className="max-h-[70vh]">
          <div className="grid gap-3 p-3">
            <div className="overflow-hidden rounded-lg border">
              <div className="border-b px-3 py-2 font-medium text-muted-foreground text-xs">
                Catalog
              </div>
              <CatalogFilterPanel
                categories={catalogComponent?.catalogCategories ?? []}
                definitions={catalogComponent?.catalogDefinitions ?? []}
                loading={catalog.loading || !!catalog.refreshing}
                onClear={onClearCatalog}
                onSelectCategory={onSelectCategory}
                onSelectDefinition={(definitionName, category) => {
                  onSelectDefinition(definitionName, category);
                  setOpen(false);
                }}
                path={path}
                scope={catalogScope}
                setPath={setPath}
              />
            </div>
            <div className="rounded-lg border">
              <div className="border-b px-3 py-2 font-medium text-muted-foreground text-xs">
                {facetLabel}
              </div>
              <QueryFacetPanel
                allLabel={allFacetLabel}
                onChange={onFacetChange}
                options={facetOptions}
                value={facetValue}
              />
            </div>
            <div className="grid gap-2 rounded-lg border p-3">
              <Label className="text-muted-foreground text-xs">Key type</Label>
              <Input
                className="h-8"
                onChange={(event) => onKeyTypeFilterChange(event.target.value)}
                placeholder="Any key type"
                value={keyTypeFilter}
              />
            </div>
          </div>
        </ScrollArea>
      </PopoverContent>
    </Popover>
  );
}

function createQueryFilterDescriptions<TValue extends string>(
  catalogScope: OverviewScope | null,
  facetLabel: string,
  facetValue: TValue[],
  keyTypeFilter: string
) {
  const descriptions: string[] = [];
  const catalogLabel = formatOverviewScopeLabel(catalogScope);
  if (catalogLabel) {
    descriptions.push(`catalog: ${catalogLabel}`);
  }
  if (facetValue.length > 0) {
    descriptions.push(`${facetLabel.toLowerCase()}: ${formatFilterValues(facetValue)}`);
  }
  if (keyTypeFilter.trim()) {
    descriptions.push(`key type: ${keyTypeFilter.trim()}`);
  }

  return descriptions;
}

function formatFilterValues(values: readonly string[]) {
  const visible = values.slice(0, 3);
  const suffix = values.length > visible.length
    ? `, +${values.length - visible.length} more`
    : "";
  return `${visible.join(", ")}${suffix}`;
}

function getWorkComponentData<T>(
  result: WorkComponentQueryResult | undefined,
  id: string
): T | undefined {
  const component = result?.components[id] as WorkComponentResult<T> | undefined;
  return component?.status?.toLowerCase() === "ok" ? component.data : undefined;
}

function createOverviewComponentScope(scope: OverviewScope | null) {
  const normalizedScope = normalizeOverviewScope(scope);
  const category = normalizeCategoryFilter(normalizedScope?.category ?? "");
  const definitionName = normalizedScope?.definitionName ?? "";
  if (!category && !definitionName) {
    return null;
  }

  return {
    category: category || undefined,
    definitionName: definitionName || undefined,
    includeSubcategories: category && !definitionName
      ? scope?.includeSubcategories ?? true
      : undefined,
  };
}

function normalizeOverviewScope(scope: OverviewScope | null | undefined): OverviewScope | null {
  if (!scope) {
    return null;
  }

  const category = normalizeScopeText(scope.category);
  const definitionName = normalizeScopeText(scope.definitionName);
  if (!category && !definitionName) {
    return null;
  }

  return {
    category: category || undefined,
    definitionName: definitionName || undefined,
    includeSubcategories: category && !definitionName
      ? scope.includeSubcategories ?? true
      : undefined,
  };
}

function normalizeScopeText(value: unknown) {
  return typeof value === "string" ? value.trim() : "";
}

function createDefinitionCatalogLevelPath(category: string) {
  const query = new URLSearchParams({ level: "true" });
  const normalizedCategory = normalizeCategoryFilter(category);
  if (normalizedCategory) {
    query.set("category", normalizedCategory);
  }

  return `definitions?${query.toString()}`;
}

function splitCatalogPath(path: unknown) {
  const value = normalizeScopeText(path);
  return value
    ? value
        .split(":")
        .map((segment) => segment.trim())
        .filter(Boolean)
    : [];
}

function normalizeCategoryFilter(path: unknown) {
  return splitCatalogPath(path).join(":");
}

function formatOverviewScopeLabel(scope: OverviewScope | null) {
  if (!scope) {
    return "";
  }

  const categoryLabel = splitCatalogPath(scope.category ?? "").join(" / ");
  if (scope.definitionName) {
    return categoryLabel
      ? `${categoryLabel} / ${scope.definitionName}`
      : scope.definitionName;
  }

  return categoryLabel;
}

function useWorkableResource<T>(
  connection: WorkableConnection,
  path: string | null,
  refreshToken: number
): Loadable<T> {
  const [state, setState] = useState<Loadable<T>>({ loading: !!path });
  const apiUrl = connection.apiUrl;
  const systemName = connection.systemName;

  useEffect(() => {
    if (!path) {
      queueMicrotask(() => setState({ loading: false }));
      return;
    }

    let canceled = false;
    queueMicrotask(() => {
      if (!canceled) {
        setState((current) => ({
          ...current,
          error: undefined,
          loading: current.data === undefined,
          refreshing: current.data !== undefined,
        }));
      }
    });

    const requestConnection = { apiUrl, systemName };
    workableFetch<T>(requestConnection, path)
      .then((data) => {
        if (!canceled) {
          setState({ data, loading: false, refreshing: false });
        }
      })
      .catch((error) => {
        if (!canceled) {
          const detail = error instanceof Error ? error.message : "Request failed.";
          setState((current) =>
            current.error === detail && !current.loading && !current.refreshing
              ? current
              : {
                  data: current.data,
                  error: detail,
                  loading: false,
                  refreshing: false,
                }
          );
        }
      });

    return () => {
      canceled = true;
    };
  }, [apiUrl, systemName, path, refreshToken]);

  return state;
}

function useWorkablePostResource<T>(
  connection: WorkableConnection,
  path: string | null,
  body: unknown,
  refreshToken: number
): Loadable<T> {
  const [state, setState] = useState<Loadable<T>>({ loading: !!path });
  const apiUrl = connection.apiUrl;
  const systemName = connection.systemName;
  const bodyKey = JSON.stringify(body);
  const requestKey = `${apiUrl}\n${systemName ?? ""}\n${path ?? ""}\n${bodyKey}`;
  const previousRequestKey = useRef<string | null>(null);

  useEffect(() => {
    if (!path) {
      previousRequestKey.current = requestKey;
      queueMicrotask(() => setState({ loading: false }));
      return;
    }

    let canceled = false;
    const requestChanged = previousRequestKey.current !== requestKey;
    previousRequestKey.current = requestKey;
    queueMicrotask(() => {
      if (!canceled) {
        setState((current) => ({
          ...(requestChanged ? {} : current),
          error: undefined,
          loading: requestChanged || current.data === undefined,
          refreshing: !requestChanged && current.data !== undefined,
        }));
      }
    });

    const requestConnection = { apiUrl, systemName };
    workableFetch<T>(requestConnection, path, {
      method: "POST",
      body: bodyKey,
    })
      .then((data) => {
        if (!canceled) {
          setState({ data, loading: false, refreshing: false });
        }
      })
      .catch((error) => {
        if (!canceled) {
          const detail = error instanceof Error ? error.message : "Request failed.";
          setState((current) =>
            current.error === detail && !current.loading && !current.refreshing
              ? current
              : {
                  data: current.data,
                  error: detail,
                  loading: false,
                  refreshing: false,
                }
          );
        }
      });

    return () => {
      canceled = true;
    };
  }, [apiUrl, bodyKey, path, refreshToken, requestKey, systemName]);

  return state;
}
