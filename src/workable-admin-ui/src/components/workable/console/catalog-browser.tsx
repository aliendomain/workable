"use client";

import { ChevronLeft, Home } from "lucide-react";
import type { ReactNode } from "react";
import { useMemo } from "react";
import { cn } from "@/lib/utils";
import type { Loadable } from "@/components/features/console/types";
import type { WorkInfo, WorkOverviewCatalogCategoryItem, WorkOverviewDefinitionItem, WorkableConnection } from "@/lib/workable";
import {
  createDefinitionCatalogLevelPath,
  fetchDefinitionCatalogInfo,
  invalidateDefinitionCatalogLevelCache,
  type DefinitionCatalogLevel,
  useDefinitionCatalogLevel,
} from "@/components/workable/console/catalog-browser-data";
import { splitCatalogPath } from "@/components/workable/console/catalog-path";

export function CatalogBrowser<TDefinition>({
  backButtonClassName,
  backIconClassName = "size-4",
  bodyClassName,
  categories,
  definitions,
  emptyState,
  getDefinitionKey,
  headerClassName,
  headerRight,
  loading,
  loadingState,
  onNavigate,
  path,
  renderCategory,
  renderDefinition,
  rootLabel = "All categories",
  titleClassName,
  wrapperClassName,
}: {
  backButtonClassName?: string;
  backIconClassName?: string;
  bodyClassName?: string;
  categories: readonly WorkOverviewCatalogCategoryItem[];
  definitions: readonly TDefinition[];
  emptyState: ReactNode;
  getDefinitionKey: (definition: TDefinition) => string;
  headerClassName?: string;
  headerRight?: ReactNode;
  loading: boolean;
  loadingState: ReactNode;
  onNavigate: (path: string) => void;
  path: string;
  renderCategory: (category: WorkOverviewCatalogCategoryItem) => ReactNode;
  renderDefinition: (definition: TDefinition) => ReactNode;
  rootLabel?: string;
  titleClassName?: string;
  wrapperClassName?: string;
}) {
  const pathSegments = splitCatalogPath(path);
  const currentLabel = pathSegments.at(-1) ?? rootLabel;
  const canGoBack = pathSegments.length > 0;

  return (
    <div className={wrapperClassName}>
      <div className={headerClassName}>
        <button
          aria-label={canGoBack ? "Back to parent category" : "Catalog root"}
          className={backButtonClassName}
          disabled={!canGoBack}
          onClick={() => onNavigate(pathSegments.slice(0, -1).join(":"))}
          type="button"
        >
          {canGoBack ? (
            <ChevronLeft className={backIconClassName} />
          ) : (
            <Home className={backIconClassName} />
          )}
        </button>
        <span className={titleClassName}>
          {currentLabel}
        </span>
        {headerRight}
      </div>
      <div className={defaultCatalogBrowserBodyClassName(bodyClassName)}>
        {loading ? (
          loadingState
        ) : (
          <>
            {categories.map((category) => (
              <div key={category.path}>
                {renderCategory(category)}
              </div>
            ))}
            {definitions.map((definition) => (
              <div key={getDefinitionKey(definition)}>
                {renderDefinition(definition)}
              </div>
            ))}
            {categories.length === 0 && definitions.length === 0 ? emptyState : null}
          </>
        )}
      </div>
    </div>
  );
}

export type DefinitionCatalogBrowserContext = {
  connection: WorkableConnection | null;
  invalidate: () => void;
  level: Loadable<DefinitionCatalogLevel>;
  loadDefinitionInfo: (definitionId: string) => Promise<WorkInfo>;
  navigate: (path: string) => void;
  path: string;
};

export function DefinitionCatalogBrowser({
  backButtonClassName,
  backIconClassName = "size-4",
  bodyClassName,
  connection,
  enabled = true,
  emptyState,
  headerClassName,
  headerRight,
  loadingState,
  onNavigate,
  path,
  refreshToken = 0,
  renderCategory,
  renderDefinition,
  renderError,
  rootLabel = "All categories",
  titleClassName,
  wrapperClassName,
}: {
  backButtonClassName?: string;
  backIconClassName?: string;
  bodyClassName?: string;
  connection: WorkableConnection | null;
  enabled?: boolean;
  emptyState: ReactNode;
  headerClassName?: string;
  headerRight?: ReactNode | ((context: DefinitionCatalogBrowserContext) => ReactNode);
  loadingState: ReactNode;
  onNavigate: (path: string) => void;
  path: string;
  refreshToken?: number;
  renderCategory: (category: WorkOverviewCatalogCategoryItem, context: DefinitionCatalogBrowserContext) => ReactNode;
  renderDefinition: (definition: WorkOverviewDefinitionItem, context: DefinitionCatalogBrowserContext) => ReactNode;
  renderError?: (error: string, context: DefinitionCatalogBrowserContext) => ReactNode;
  rootLabel?: string;
  titleClassName?: string;
  wrapperClassName?: string;
}) {
  const level = useDefinitionCatalogLevel(
    connection,
    connection && enabled ? createDefinitionCatalogLevelPath(path) : null,
    refreshToken
  );
  const context = useMemo<DefinitionCatalogBrowserContext>(
    () => ({
      connection,
      invalidate: () => {
        if (connection) {
          invalidateDefinitionCatalogLevelCache(connection);
        }
      },
      level,
      loadDefinitionInfo: async (definitionId: string) => {
        if (!connection) {
          throw new Error("Catalog connection is unavailable.");
        }
        return fetchDefinitionCatalogInfo(connection, definitionId);
      },
      navigate: onNavigate,
      path,
    }),
    [connection, level, onNavigate, path]
  );
  const resolvedHeaderRight =
    typeof headerRight === "function"
      ? headerRight(context)
      : headerRight;

  return (
    <div className={wrapperClassName}>
      {level.error && renderError ? renderError(level.error, context) : null}
      <CatalogBrowser
        backButtonClassName={backButtonClassName}
        backIconClassName={backIconClassName}
        bodyClassName={bodyClassName}
        categories={level.data?.categories ?? []}
        definitions={level.data?.definitions ?? []}
        emptyState={emptyState}
        getDefinitionKey={(definition) => definition.id.value}
        headerClassName={headerClassName}
        headerRight={resolvedHeaderRight}
        loading={!level.data && (level.loading || !!level.refreshing)}
        loadingState={loadingState}
        onNavigate={onNavigate}
        path={path}
        renderCategory={(category) => renderCategory(category, context)}
        renderDefinition={(definition) => renderDefinition(definition, context)}
        rootLabel={rootLabel}
        titleClassName={titleClassName}
      />
    </div>
  );
}

export function defaultCatalogBrowserHeaderClassName(className?: string) {
  return cn("flex h-10 min-w-0 items-center gap-1 border-b px-2", className);
}

export function defaultCatalogBrowserBackButtonClassName(className?: string) {
  return cn(
    "flex size-7 shrink-0 items-center justify-center rounded-md hover:bg-accent hover:text-accent-foreground disabled:pointer-events-none disabled:opacity-40",
    className
  );
}

export function defaultCatalogBrowserBodyClassName(className?: string) {
  return cn("pt-1 pb-8", className);
}

export function defaultCatalogBrowserTitleClassName(className?: string) {
  return cn("min-w-0 flex-1 truncate font-medium text-sm", className);
}
