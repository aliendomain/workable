"use client";

import {
  ListFilter,
} from "lucide-react";
import { useCallback, useEffect, useRef, useState } from "react";
import type { OverviewScope } from "@/components/features/console/types";
import { Button } from "@/components/ui/button";
import { Popover, PopoverContent, PopoverTrigger } from "@/components/ui/popover";
import { Tooltip, TooltipContent, TooltipTrigger } from "@/components/ui/tooltip";
import {
  areStringArraysEqual,
  createQueryFilterDescriptions,
  getQueryFilterActiveCount,
  type QueryKeyKindFilter,
} from "@/components/workable/console/filter-data";
import {
  CatalogFilterPanel,
  FilterPanelFrame,
  FilterPanelSection,
  QueryFilterSections,
} from "@/components/workable/console/filter-panels";
import {
  type WorkableConnection,
} from "@/lib/workable";
import {
  createQueryCatalogScope,
  formatOverviewScopeLabel,
  normalizeCategoryFilter,
} from "@/components/workable/console/catalog-path";

export {
  areStringArraysEqual,
  catalogFilterPanelFrameClassName,
  createQueryFilterDescriptions,
  filterPanelSectionClassName,
  formatFilterValues,
  formatQueryKeyKindLabel,
  getQueryFilterActiveCount,
} from "@/components/workable/console/filter-data";

export type { QueryKeyKindFilter } from "@/components/workable/console/filter-data";

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
  const draftScope = createQueryCatalogScope(draftCategoryFilter, draftDefinitionFilter);

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
  const activeFilterCount = getQueryFilterActiveCount(
    catalogScope,
    facetValue,
    keyKindFilter,
    keyTypeFilter,
    keyValueFilter
  );
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
  const draftScope = createQueryCatalogScope(draftCategoryFilter, draftDefinitionFilter);

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
