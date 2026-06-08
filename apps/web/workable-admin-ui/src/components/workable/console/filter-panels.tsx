"use client";

import {
  CheckCircle2,
  FileCode2,
  Folder,
  Square,
} from "lucide-react";
import type { ReactNode } from "react";
import { ConsolePlaceholder } from "@/components/features/console/empty-state";
import type { OverviewScope } from "@/components/features/console/types";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { ScrollArea } from "@/components/ui/scroll-area";
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from "@/components/ui/select";
import {
  catalogFilterPanelFrameClassName,
  filterPanelSectionClassName,
  type QueryKeyKindFilter,
} from "@/components/workable/console/filter-data";
import {
  DefinitionCatalogBrowser,
  defaultCatalogBrowserBackButtonClassName,
  defaultCatalogBrowserHeaderClassName,
  defaultCatalogBrowserTitleClassName,
} from "@/components/workable/console/catalog-browser";
import { normalizeCategoryFilter } from "@/components/workable/console/catalog-path";
import type { WorkableConnection } from "@/lib/workable";

export function CatalogFilterPanel({
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
    <div className={catalogFilterPanelFrameClassName}>
      <ScrollArea className="min-h-0 flex-1">
        <DefinitionCatalogBrowser
          backButtonClassName={defaultCatalogBrowserBackButtonClassName()}
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
  return <ConsolePlaceholder className="mx-2 my-1 h-40 rounded-md" />;
}

export function FilterPanelFrame({
  children,
  footer,
  onClear,
}: {
  children: ReactNode;
  footer?: ReactNode;
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

export function FilterPanelSection({
  children,
  className,
  title,
}: {
  children: ReactNode;
  className?: string;
  title: string;
}) {
  return (
    <div className={`${filterPanelSectionClassName} ${className ?? ""}`.trim()}>
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

export function QueryFilterSections<TValue extends string>({
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
  footer?: ReactNode;
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
