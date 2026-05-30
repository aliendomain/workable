"use client";

import {
  CheckCircle2,
  FileCode2,
  Folder,
  ListFilter,
  Square,
} from "lucide-react";
import { useCallback, useEffect, useRef, useState } from "react";
import type { OverviewScope } from "@/components/features/console/types";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Popover, PopoverContent, PopoverTrigger } from "@/components/ui/popover";
import { ScrollArea } from "@/components/ui/scroll-area";
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from "@/components/ui/select";
import { Tooltip, TooltipContent, TooltipTrigger } from "@/components/ui/tooltip";
import {
  type WorkKeyKind,
  type WorkableConnection,
} from "@/lib/workable";
import {
  DefinitionCatalogBrowser,
  defaultCatalogBrowserBackButtonClassName,
  defaultCatalogBrowserHeaderClassName,
  defaultCatalogBrowserTitleClassName,
} from "@/components/workable/console/catalog-browser";
import {
  normalizeCategoryFilter,
  splitCatalogPath,
} from "@/components/workable/console/catalog-browser-data";

type QueryKeyKindFilter = WorkKeyKind | "Any";

export function OverviewCatalogFilter({
  connection,
  onClear,
  onSelectCategory,
  onSelectDefinition,
  refreshToken,
  scope,
  tooltipLabel = "Filter overview by category and definition",
}: {
  connection: WorkableConnection;
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
  const [draftCategoryFilter, setDraftCategoryFilter] = useState(scope?.category ?? "");
  const [draftDefinitionFilter, setDraftDefinitionFilter] = useState(scope?.definitionName ?? "");
  const [path, setPath] = useState(scope?.category ?? "");
  const activeFilterCount = scope ? 1 : 0;
  const scopeLabel = formatOverviewScopeLabel(scope);
  const filterTooltip = scopeLabel
    ? `Filtered by catalog: ${scopeLabel}`
    : tooltipLabel;

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
      setDraftCategoryFilter(scope?.category ?? "");
      setDraftDefinitionFilter(scope?.definitionName ?? "");
      setPath(scope?.category ?? "");
    }
    setOpen(nextOpen);
  };

  const clearAll = () => {
    closeTooltip();
    setDraftCategoryFilter("");
    setDraftDefinitionFilter("");
    setPath("");
    onClear();
    setOpen(false);
  };
  const hasDraftChanges =
    normalizeCategoryFilter(draftCategoryFilter) !== normalizeCategoryFilter(scope?.category ?? "") ||
    draftDefinitionFilter.trim() !== (scope?.definitionName ?? "").trim();
  const draftScope = createDraftQueryCatalogScope(draftCategoryFilter, draftDefinitionFilter);

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
        <FilterPanelFrame
          footer={(
            <div className="flex justify-end">
              <Button
                disabled={!hasDraftChanges}
                onClick={() => {
                  if (draftDefinitionFilter.trim()) {
                    onSelectDefinition(draftDefinitionFilter.trim(), draftCategoryFilter);
                  } else if (normalizeCategoryFilter(draftCategoryFilter)) {
                    onSelectCategory(draftCategoryFilter);
                  } else {
                    onClear();
                  }
                  setOpen(false);
                }}
                size="sm"
                type="button"
              >
                Apply
              </Button>
            </div>
          )}
          onClear={clearAll}
        >
          <FilterPanelSection title="Catalog">
            <CatalogFilterPanel
              connection={connection}
              enabled={open}
              onClear={() => {
                setDraftCategoryFilter("");
                setDraftDefinitionFilter("");
                setPath("");
              }}
              onSelectCategory={(category) => {
                setDraftCategoryFilter(category);
                setDraftDefinitionFilter("");
              }}
              onSelectDefinition={(definitionName, category) => {
                setDraftCategoryFilter(category);
                setDraftDefinitionFilter(definitionName);
              }}
              path={path}
              refreshToken={refreshToken}
              scope={draftScope}
              setPath={setPath}
            />
          </FilterPanelSection>
        </FilterPanelFrame>
      </PopoverContent>
    </Popover>
  );
}

function CatalogFilterPanel({
  connection,
  enabled = true,
  onClear,
  onClose,
  onSelectCategory,
  onSelectDefinition,
  path,
  refreshToken = 0,
  scope,
  setPath,
}: {
  connection: WorkableConnection;
  enabled?: boolean;
  onClear: () => void;
  onClose?: () => void;
  onSelectCategory: (category: string) => void;
  onSelectDefinition: (definitionName: string, category: string) => void;
  path: string;
  refreshToken?: number;
  scope: OverviewScope | null;
  setPath: (path: string) => void;
}) {
  const selectCategory = (category: string) => {
    setPath(category);
    onSelectCategory(category);
  };

  const clear = () => {
    setPath("");
    onClear();
  };

  return (
    <div className="flex h-[22rem] min-h-0 flex-col">
      <ScrollArea className="min-h-0 flex-1">
        <DefinitionCatalogBrowser
          backButtonClassName={defaultCatalogBrowserBackButtonClassName()}
          bodyClassName="py-1"
          connection={connection}
          emptyState={(
            <div className="px-3 py-3 text-muted-foreground text-sm">
              No catalog entries.
            </div>
          )}
          enabled={enabled}
          headerClassName={defaultCatalogBrowserHeaderClassName()}
          headerRight={(
            <Button onClick={clear} size="sm" variant="ghost">
              All
            </Button>
          )}
          loadingState={<CatalogFilterPlaceholder />}
          onNavigate={selectCategory}
          path={path}
          refreshToken={refreshToken}
          renderCategory={(category) => {
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
          }}
          renderDefinition={(definition) => {
            const isActive = definition.name === scope?.definitionName;

            return (
              <button
                className={
                  isActive
                    ? "flex h-8 w-full min-w-0 items-center gap-2 bg-accent px-2 text-left text-accent-foreground text-sm"
                    : "flex h-8 w-full min-w-0 items-center gap-2 px-2 text-left text-sm hover:bg-accent hover:text-accent-foreground"
                }
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
          }}
          titleClassName={defaultCatalogBrowserTitleClassName()}
        />
      </ScrollArea>
    </div>
  );
}

function CatalogFilterPlaceholder() {
  return <div className="mx-2 my-1 h-40 rounded-md border border-dashed" />;
}

function FilterPanelFrame({
  children,
  footer,
  onClear,
}: {
  children: React.ReactNode;
  footer?: React.ReactNode;
  onClear: () => void;
}) {
  return (
    <>
      <div className="flex h-10 items-center justify-between border-b px-3">
        <span className="font-medium text-sm">Filters</span>
        <Button onClick={onClear} size="sm" variant="ghost">
          Clear
        </Button>
      </div>
      <ScrollArea className="max-h-[70vh]">
        <div className="grid gap-3 p-3">
          {children}
        </div>
      </ScrollArea>
      {footer ? (
        <div className="border-t px-3 py-3">
          {footer}
        </div>
      ) : null}
    </>
  );
}

function FilterPanelSection({
  children,
  className,
  title,
}: {
  children: React.ReactNode;
  className?: string;
  title: string;
}) {
  return (
    <div className={`overflow-hidden rounded-lg border ${className ?? ""}`.trim()}>
      <div className="border-b px-3 py-2 font-medium text-muted-foreground text-xs">
        {title}
      </div>
      {children}
    </div>
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

function QueryFilterSections<TValue extends string>({
  allFacetLabel,
  catalogScope,
  connection,
  clearAll,
  catalogEnabled = true,
  facetLabel,
  facetOptions,
  facetValue,
  footer,
  keyKindFilter,
  keyTypeFilter,
  keyValueFilter,
  onClose,
  onFacetChange,
  onKeyKindFilterChange,
  onKeyTypeFilterChange,
  onKeyValueFilterChange,
  onSelectCategory,
  onSelectDefinition,
  path,
  refreshToken,
  setPath,
}: {
  allFacetLabel: string;
  catalogScope: OverviewScope | null;
  connection: WorkableConnection;
  catalogEnabled?: boolean;
  clearAll: () => void;
  facetLabel: string;
  facetOptions: TValue[];
  facetValue: TValue[];
  footer?: React.ReactNode;
  keyKindFilter: QueryKeyKindFilter;
  keyTypeFilter: string;
  keyValueFilter: string;
  onClose?: () => void;
  onFacetChange: (value: TValue[]) => void;
  onKeyKindFilterChange: (keyKind: QueryKeyKindFilter) => void;
  onKeyTypeFilterChange: (keyType: string) => void;
  onKeyValueFilterChange: (keyValue: string) => void;
  onSelectCategory: (category: string) => void;
  onSelectDefinition: (definitionName: string, category: string) => void;
  path: string;
  refreshToken: number;
  setPath: (path: string) => void;
}) {
  return (
    <FilterPanelFrame footer={footer} onClear={clearAll}>
      <div className="grid gap-3 lg:grid-cols-[minmax(0,1.35fr)_minmax(14rem,0.9fr)_minmax(16rem,1fr)]">
        <FilterPanelSection title="Catalog">
          <CatalogFilterPanel
            connection={connection}
            enabled={catalogEnabled}
            onClear={clearAll}
            onClose={onClose}
            onSelectCategory={onSelectCategory}
            onSelectDefinition={onSelectDefinition}
            path={path}
            refreshToken={refreshToken}
            scope={catalogScope}
            setPath={setPath}
          />
        </FilterPanelSection>
        <FilterPanelSection title={facetLabel}>
          <QueryFacetPanel
            allLabel={allFacetLabel}
            onChange={onFacetChange}
            options={facetOptions}
            value={facetValue}
          />
        </FilterPanelSection>
        <FilterPanelSection title="Key">
          <div className="flex h-10 items-center border-b">
            <Select
              onValueChange={(value) => onKeyKindFilterChange(value as QueryKeyKindFilter)}
              value={keyKindFilter}
            >
              <SelectTrigger className="h-10 w-full rounded-none border-0 bg-transparent px-3 shadow-none focus:ring-0 focus:ring-offset-0">
                <SelectValue placeholder="Any" />
              </SelectTrigger>
              <SelectContent>
                <SelectItem value="Any">Any</SelectItem>
                <SelectItem value="Subject">Subject</SelectItem>
                <SelectItem value="ConcurrencyKey">Concurrency</SelectItem>
                <SelectItem value="Identifier">Identity</SelectItem>
              </SelectContent>
            </Select>
          </div>
          <div className="grid gap-3 p-3">
            <Input
              className="h-9"
              onChange={(event) => onKeyTypeFilterChange(event.target.value)}
              placeholder="Any key type"
              value={keyTypeFilter}
            />
            <Input
              className="h-9"
              onChange={(event) => onKeyValueFilterChange(event.target.value)}
              placeholder="Any key value"
              value={keyValueFilter}
            />
          </div>
        </FilterPanelSection>
      </div>
    </FilterPanelFrame>
  );
}

export function QueryFilterPopover<TValue extends string>({
  allFacetLabel,
  catalogScope,
  connection,
  facetLabel,
  facetOptions,
  facetValue,
  keyKindFilter,
  keyTypeFilter,
  keyValueFilter,
  onClearCatalog,
  onFacetChange,
  onKeyKindFilterChange,
  onKeyTypeFilterChange,
  onKeyValueFilterChange,
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
  keyKindFilter: QueryKeyKindFilter;
  keyTypeFilter: string;
  keyValueFilter: string;
  onClearCatalog: () => void;
  onFacetChange: (value: TValue[]) => void;
  onKeyKindFilterChange: (keyKind: QueryKeyKindFilter) => void;
  onKeyTypeFilterChange: (keyType: string) => void;
  onKeyValueFilterChange: (keyValue: string) => void;
  onSelectCategory: (category: string) => void;
  onSelectDefinition: (definitionName: string, category: string) => void;
  refreshToken: number;
}) {
  const [open, setOpen] = useState(false);
  const [path, setPath] = useState(catalogScope?.category ?? "");
  const activeFilterCount =
    (catalogScope ? 1 : 0) +
    (keyKindFilter !== "Any" ? 1 : 0) +
    (keyTypeFilter.trim() ? 1 : 0) +
    (keyValueFilter.trim() ? 1 : 0) +
    (facetValue.length > 0 ? 1 : 0);
  const filterDescriptions = createQueryFilterDescriptions(
    catalogScope,
    facetLabel,
    facetValue,
    keyKindFilter,
    keyTypeFilter,
    keyValueFilter
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
    onKeyKindFilterChange("Any");
    onKeyTypeFilterChange("");
    onKeyValueFilterChange("");
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
        <QueryFilterSections
          allFacetLabel={allFacetLabel}
          catalogScope={catalogScope}
          connection={connection}
          catalogEnabled={open}
          clearAll={clearAll}
          facetLabel={facetLabel}
          facetOptions={facetOptions}
          facetValue={facetValue}
          keyKindFilter={keyKindFilter}
          keyTypeFilter={keyTypeFilter}
          keyValueFilter={keyValueFilter}
          onClose={() => setOpen(false)}
          onFacetChange={onFacetChange}
          onKeyKindFilterChange={onKeyKindFilterChange}
          onKeyTypeFilterChange={onKeyTypeFilterChange}
          onKeyValueFilterChange={onKeyValueFilterChange}
          onSelectCategory={onSelectCategory}
          onSelectDefinition={onSelectDefinition}
          path={path}
          refreshToken={refreshToken}
          setPath={setPath}
        />
      </PopoverContent>
    </Popover>
  );
}

export function getQueryFilterActiveCount<TValue extends string>(
  catalogScope: OverviewScope | null,
  facetValue: TValue[],
  keyKindFilter: QueryKeyKindFilter,
  keyTypeFilter: string,
  keyValueFilter: string
) {
  return (catalogScope ? 1 : 0) +
    (facetValue.length > 0 ? 1 : 0) +
    (keyKindFilter !== "Any" ? 1 : 0) +
    (keyTypeFilter.trim() ? 1 : 0) +
    (keyValueFilter.trim() ? 1 : 0);
}

export function QueryFilterPanelContent<TValue extends string>({
  allFacetLabel,
  catalogScope,
  connection,
  facetLabel,
  facetOptions,
  facetValue,
  isOpen,
  keyKindFilter,
  keyTypeFilter,
  keyValueFilter,
  onApply,
  onDismiss,
  refreshToken,
}: {
  allFacetLabel: string;
  catalogScope: OverviewScope | null;
  connection: WorkableConnection;
  facetLabel: string;
  facetOptions: TValue[];
  facetValue: TValue[];
  isOpen: boolean;
  keyKindFilter: QueryKeyKindFilter;
  keyTypeFilter: string;
  keyValueFilter: string;
  onApply: (next: {
    categoryFilter: string;
    definitionFilter: string;
    facetValue: TValue[];
    keyKindFilter: QueryKeyKindFilter;
    keyTypeFilter: string;
    keyValueFilter: string;
  }) => void;
  onDismiss?: () => void;
  refreshToken: number;
}) {
  const appliedCategoryFilter = catalogScope?.category ?? "";
  const appliedDefinitionFilter = catalogScope?.definitionName ?? "";
  const [draftCategoryFilter, setDraftCategoryFilter] = useState(appliedCategoryFilter);
  const [draftDefinitionFilter, setDraftDefinitionFilter] = useState(appliedDefinitionFilter);
  const [draftFacetValue, setDraftFacetValue] = useState<TValue[]>(facetValue);
  const [draftKeyKindFilter, setDraftKeyKindFilter] = useState<QueryKeyKindFilter>(keyKindFilter);
  const [draftKeyTypeFilter, setDraftKeyTypeFilter] = useState(keyTypeFilter);
  const [draftKeyValueFilter, setDraftKeyValueFilter] = useState(keyValueFilter);
  const [path, setPath] = useState(appliedCategoryFilter);
  const wasOpenRef = useRef(isOpen);
  const draftScope = createDraftQueryCatalogScope(draftCategoryFilter, draftDefinitionFilter);

  useEffect(() => {
    if (isOpen && !wasOpenRef.current) {
      setDraftCategoryFilter(appliedCategoryFilter);
      setDraftDefinitionFilter(appliedDefinitionFilter);
      setDraftFacetValue(facetValue);
      setDraftKeyKindFilter(keyKindFilter);
      setDraftKeyTypeFilter(keyTypeFilter);
      setDraftKeyValueFilter(keyValueFilter);
      setPath(appliedCategoryFilter);
    }

    wasOpenRef.current = isOpen;
  }, [
    appliedCategoryFilter,
    appliedDefinitionFilter,
    facetValue,
    isOpen,
    keyKindFilter,
    keyTypeFilter,
    keyValueFilter,
  ]);

  const clearAll = () => {
    setDraftCategoryFilter("");
    setDraftDefinitionFilter("");
    setDraftFacetValue([]);
    setDraftKeyKindFilter("Any");
    setDraftKeyTypeFilter("");
    setDraftKeyValueFilter("");
    setPath("");
    onApply({
      categoryFilter: "",
      definitionFilter: "",
      facetValue: [],
      keyKindFilter: "Any",
      keyTypeFilter: "",
      keyValueFilter: "",
    });
    onDismiss?.();
  };
  const hasDraftChanges =
    normalizeCategoryFilter(draftCategoryFilter) !== normalizeCategoryFilter(appliedCategoryFilter) ||
    draftDefinitionFilter.trim() !== appliedDefinitionFilter.trim() ||
    draftKeyKindFilter !== keyKindFilter ||
    draftKeyTypeFilter.trim() !== keyTypeFilter.trim() ||
    draftKeyValueFilter.trim() !== keyValueFilter.trim() ||
    !areStringArraysEqual(draftFacetValue, facetValue);

  return (
    <QueryFilterSections
      allFacetLabel={allFacetLabel}
      catalogScope={draftScope}
      connection={connection}
      catalogEnabled={isOpen}
      clearAll={clearAll}
      facetLabel={facetLabel}
      facetOptions={facetOptions}
      facetValue={draftFacetValue}
      footer={(
        <div className="flex justify-end">
          <Button
            disabled={!hasDraftChanges}
            onClick={() => {
              onApply({
                categoryFilter: draftCategoryFilter,
                definitionFilter: draftDefinitionFilter,
                facetValue: draftFacetValue,
                keyKindFilter: draftKeyKindFilter,
                keyTypeFilter: draftKeyTypeFilter,
                keyValueFilter: draftKeyValueFilter,
              });
              onDismiss?.();
            }}
            size="sm"
            type="button"
          >
            Apply
          </Button>
        </div>
      )}
      keyKindFilter={draftKeyKindFilter}
      keyTypeFilter={draftKeyTypeFilter}
      keyValueFilter={draftKeyValueFilter}
      onFacetChange={setDraftFacetValue}
      onKeyKindFilterChange={setDraftKeyKindFilter}
      onKeyTypeFilterChange={setDraftKeyTypeFilter}
      onKeyValueFilterChange={setDraftKeyValueFilter}
      onSelectCategory={(category) => {
        setDraftCategoryFilter(category);
        setDraftDefinitionFilter("");
      }}
      onSelectDefinition={(definitionName, category) => {
        setDraftCategoryFilter(category);
        setDraftDefinitionFilter(definitionName);
      }}
      path={path}
      refreshToken={refreshToken}
      setPath={setPath}
    />
  );
}

function createDraftQueryCatalogScope(
  categoryFilter: string,
  definitionFilter: string
): OverviewScope | null {
  const category = normalizeCategoryFilter(categoryFilter);
  const definitionName = definitionFilter.trim();
  if (!category && !definitionName) {
    return null;
  }

  return {
    category: category || undefined,
    definitionName: definitionName || undefined,
    includeSubcategories: true,
  };
}

function areStringArraysEqual(left: readonly string[], right: readonly string[]) {
  return left.length === right.length &&
    left.every((value, index) => value === right[index]);
}

function createQueryFilterDescriptions<TValue extends string>(
  catalogScope: OverviewScope | null,
  facetLabel: string,
  facetValue: TValue[],
  keyKindFilter: QueryKeyKindFilter,
  keyTypeFilter: string,
  keyValueFilter: string
) {
  const descriptions: string[] = [];
  const catalogLabel = formatOverviewScopeLabel(catalogScope);
  if (catalogLabel) {
    descriptions.push(`catalog: ${catalogLabel}`);
  }
  if (facetValue.length > 0) {
    descriptions.push(`${facetLabel.toLowerCase()}: ${formatFilterValues(facetValue)}`);
  }
  if (keyKindFilter !== "Any") {
    descriptions.push(`key kind: ${formatQueryKeyKindLabel(keyKindFilter)}`);
  }
  if (keyTypeFilter.trim()) {
    descriptions.push(`key type: ${keyTypeFilter.trim()}`);
  }
  if (keyValueFilter.trim()) {
    descriptions.push(`key value: ${keyValueFilter.trim()}`);
  }

  return descriptions;
}

function formatQueryKeyKindLabel(value: QueryKeyKindFilter) {
  switch (value) {
    case "Subject":
      return "subject";
    case "ConcurrencyKey":
      return "concurrency";
    case "Identifier":
      return "identity";
    default:
      return "none";
  }
}

function formatFilterValues(values: readonly string[]) {
  const visible = values.slice(0, 3);
  const suffix = values.length > visible.length
    ? `, +${values.length - visible.length} more`
    : "";
  return `${visible.join(", ")}${suffix}`;
}

function normalizeScopeText(value: unknown) {
  return typeof value === "string" ? value.trim() : "";
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
