import type { OverviewScope } from "@/components/features/console/types";

export type OverviewComponentScope = {
  category?: string;
  definitionName?: string;
  includeSubcategories?: boolean;
};

export function normalizeScopeText(value: unknown) {
  return typeof value === "string" ? value.trim() : "";
}

export function splitCatalogPath(path: unknown) {
  const value = normalizeScopeText(path);
  return value
    ? value
        .split(":")
        .map((segment) => segment.trim())
        .filter(Boolean)
    : [];
}

export function normalizeCategoryFilter(path: unknown) {
  return splitCatalogPath(path).join(":");
}

export function normalizeOverviewScope(scope: OverviewScope | null | undefined): OverviewScope | null {
  if (!scope) {
    return null;
  }

  const category = normalizeCategoryFilter(scope.category);
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

export function createQueryCatalogScope(
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

export function overviewScopesEqual(
  left: OverviewScope | null,
  right: OverviewScope | null
) {
  const normalizedLeft = normalizeOverviewScope(left);
  const normalizedRight = normalizeOverviewScope(right);
  return (
    normalizedLeft?.category === normalizedRight?.category &&
    normalizedLeft?.definitionName === normalizedRight?.definitionName &&
    normalizedLeft?.includeSubcategories === normalizedRight?.includeSubcategories
  );
}

export function createOverviewComponentScope<TEmpty extends null | undefined = null>(
  scope: OverviewScope | null | undefined,
  options?: {
    emptyValue?: TEmpty;
    includeSubcategoriesForDefinition?: boolean;
  }
): OverviewComponentScope | TEmpty {
  const normalizedScope = normalizeOverviewScope(scope);
  const category = normalizeCategoryFilter(normalizedScope?.category ?? "");
  const definitionName = normalizedScope?.definitionName ?? "";
  if (!category && !definitionName) {
    return (options && "emptyValue" in options ? options.emptyValue : null) as TEmpty;
  }

  const includeSubcategories =
    category && (!definitionName || options?.includeSubcategoriesForDefinition)
      ? scope?.includeSubcategories ?? true
      : undefined;

  return {
    category: category || undefined,
    definitionName: definitionName || undefined,
    includeSubcategories,
  };
}

export function formatOverviewScopeLabel(scope: OverviewScope | null) {
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
