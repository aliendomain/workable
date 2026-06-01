import type { OverviewScope } from "@/components/features/console/types";
import { formatOverviewScopeLabel } from "@/components/workable/console/catalog-path";
import type { WorkKeyKind } from "@/lib/workable";

export type QueryKeyKindFilter = WorkKeyKind | "Any";

export const catalogFilterPanelFrameClassName = "flex min-h-[22rem] flex-1 flex-col";
export const filterPanelSectionClassName = "flex min-h-0 flex-col overflow-hidden rounded-lg border";

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

export function areStringArraysEqual(left: readonly string[], right: readonly string[]) {
  return left.length === right.length &&
    left.every((value, index) => value === right[index]);
}

export function createQueryFilterDescriptions<TValue extends string>(
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

export function formatQueryKeyKindLabel(value: QueryKeyKindFilter) {
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

export function formatFilterValues(values: readonly string[]) {
  const visible = values.slice(0, 3);
  const suffix = values.length > visible.length
    ? `, +${values.length - visible.length} more`
    : "";
  return `${visible.join(", ")}${suffix}`;
}
