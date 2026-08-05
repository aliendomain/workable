"use client";

import { Check, ChevronRight, ChevronsUpDown, Copy, DatabaseSearch, DatabaseZap, GitBranchMinus, GitBranchPlus, Globe2, Info, Maximize2, Minimize2, SquareTerminal } from "lucide-react";
import { Fragment, type ReactNode, useCallback, useDeferredValue, useEffect, useMemo, useRef, useState } from "react";
import { PanelScrollViewport, PanelShell } from "@/components/features/console/panel-shell";
import { ToolbarIconButton } from "@/components/features/console/toolbar-icon-button";
import { ConsoleEmptyState } from "@/components/features/console/empty-state";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { ButtonGroup } from "@/components/ui/button-group";
import {
  Command,
  CommandEmpty,
  CommandGroup,
  CommandInput,
  CommandItem,
  CommandList,
} from "@/components/ui/command";
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from "@/components/ui/dialog";
import { Input } from "@/components/ui/input";
import {
  Popover,
  PopoverContent,
  PopoverTrigger,
} from "@/components/ui/popover";
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from "@/components/ui/select";
import {
  Tooltip,
  TooltipContent,
  TooltipTrigger,
} from "@/components/ui/tooltip";
import { semanticBadgeToneClass } from "@/lib/ui/state-tones";
import { cn } from "@/lib/utils";
import type {
  WorkCompletionStatus,
  WorkComponentShape,
  WorkProfileMetricType,
  WorkProfileSnapshot,
  WorkProfileSnapshotNode,
} from "@/lib/workable";

export type WorkProfileSummary = {
  maxDepth: number;
  metricCounts: Record<WorkProfileMetricType, number>;
  nodeCount: number;
  totalNodeMilliseconds: number;
  totalTreeMilliseconds: number;
};

export type WorkProfileHotspotMode = "off" | "tree" | "node";
export type WorkProfileHotspotThreshold = "pct10" | "pct25" | "ms25" | "ms50" | "top5";
type WorkProfileInstrumentationFilter = "all" | "http" | "sql";

export type WorkProfileSearchResult = {
  expandableNodeIds: string[];
  matchedNodeIds: ReadonlySet<string>;
  matchedNodeCount: number;
  visibleNodeIds: ReadonlySet<string>;
};

export type WorkProfileHotspotResult = {
  matchedNodeCount: number;
  matchedNodeIds: ReadonlySet<string>;
  matchesByNodeId: ReadonlyMap<string, WorkProfileHotspotMatch>;
};

export type WorkProfileSqlBatch = {
  originalExecutionBatch: string;
  parameterCount: number;
  replayableBatch: string;
  redactedParameterCount: number;
  statementCount: number;
};

type WorkProfileSqlBatchMode = "replayable" | "original";

const sqlBatchUnavailableMessage = "SQL batch is unavailable for this profile because it does not contain captured SQL commands.";
const sqlBatchOpenTooltip = "Open the SQL batch viewer for any captured SQL commands in this profile.";
const sqlProfilingUnavailableTooltip = "SQL profiling is not available for this system. Enable it by calling AddWorkableSqlServerProfiling() in the host's Workable SQL Server configuration.";
const httpClientProfilingUnavailableTooltip = "HTTP client profiling is not available for this system. Enable it by calling AddWorkableHttpClientProfiling() in the host's Workable configuration.";

type WorkProfileMethodScopeOption = {
  label: string;
  value: string;
};

type WorkProfileMethodScopeEntry = WorkProfileMethodScopeOption & {
  shortLabel: string;
};

type WorkProfileMethodScopeSelection = {
  nodeIds: readonly string[];
};

type WorkProfileAvailability = {
  label: string;
  message: string;
};

type WorkProfileHotspotMatch = {
  milliseconds: number;
  percentOfTotal: number;
};

const emptyMetricCounts = (): Record<WorkProfileMetricType, number> => ({
  MethodScope: 0,
  Scope: 0,
  Timing: 0,
  Metric: 0,
});

const hotspotModes = [
  { id: "off", label: "Off" },
  { id: "tree", label: "Tree time" },
  { id: "node", label: "Node time" },
] as const satisfies readonly { id: WorkProfileHotspotMode; label: string }[];

const hotspotThresholds = [
  { id: "pct10", label: ">= 10% total" },
  { id: "pct25", label: ">= 25% total" },
  { id: "ms25", label: ">= 25ms" },
  { id: "ms50", label: ">= 50ms" },
  { id: "top5", label: "Top 5" },
] as const satisfies readonly { id: WorkProfileHotspotThreshold; label: string }[];

type WorkProfileSqlTokenKind =
  | "comment"
  | "function"
  | "identifier"
  | "keyword"
  | "number"
  | "parameter"
  | "plain"
  | "string"
  | "string-delimiter"
  | "type";

type WorkProfileSqlToken = {
  kind: WorkProfileSqlTokenKind;
  text: string;
};

const workProfileSqlHighlightMaxCharacters = 24_000;
const workProfileSqlHighlightMaxLines = 400;

const workProfileSqlKeywords = new Set([
  "ADD",
  "ALL",
  "ALTER",
  "AND",
  "AS",
  "ASC",
  "BEGIN",
  "BETWEEN",
  "BY",
  "CASE",
  "CAST",
  "COMMIT",
  "CONVERT",
  "COUNT",
  "CREATE",
  "CROSS",
  "DECLARE",
  "DELETE",
  "DESC",
  "DISTINCT",
  "DROP",
  "ELSE",
  "END",
  "EXEC",
  "EXECUTE",
  "EXISTS",
  "FROM",
  "FULL",
  "GO",
  "GROUP",
  "HAVING",
  "IN",
  "INNER",
  "INSERT",
  "INTO",
  "IS",
  "JOIN",
  "LEFT",
  "LIKE",
  "MERGE",
  "NOT",
  "NULL",
  "ON",
  "OR",
  "ORDER",
  "OUTER",
  "OUTPUT",
  "RIGHT",
  "ROLLBACK",
  "SELECT",
  "SET",
  "SP_EXECUTESQL",
  "THEN",
  "TOP",
  "TRAN",
  "TRANSACTION",
  "UNION",
  "UPDATE",
  "VALUES",
  "WHEN",
  "WHERE",
]);

const workProfileSqlTypes = new Set([
  "BIGINT",
  "BINARY",
  "BIT",
  "CHAR",
  "DATE",
  "DATETIME",
  "DATETIME2",
  "DATETIMEOFFSET",
  "DECIMAL",
  "FLOAT",
  "IMAGE",
  "INT",
  "MONEY",
  "NCHAR",
  "NTEXT",
  "NUMERIC",
  "NVARCHAR",
  "REAL",
  "SMALLDATETIME",
  "SMALLINT",
  "SMALLMONEY",
  "TEXT",
  "TIME",
  "TIMESTAMP",
  "TINYINT",
  "UNIQUEIDENTIFIER",
  "VARBINARY",
  "VARCHAR",
  "XML",
]);

export function summarizeWorkProfile(profile?: WorkProfileSnapshot | null): WorkProfileSummary | null {
  if (!profile) {
    return null;
  }

  const metricCounts = emptyMetricCounts();
  let maxDepth = 0;
  let nodeCount = 0;

  const visit = (node: WorkProfileSnapshotNode, depth: number) => {
    nodeCount += 1;
    metricCounts[node.metricType] += 1;
    maxDepth = Math.max(maxDepth, depth);
    for (const child of node.children) {
      visit(child, depth + 1);
    }
  };

  visit(profile.root, 1);

  return {
    maxDepth,
    metricCounts,
    nodeCount,
    totalNodeMilliseconds: profile.root.nodeMilliseconds,
    totalTreeMilliseconds: profile.root.treeMilliseconds,
  };
}

export function collectWorkProfileExpandableNodeIds(
  node: WorkProfileSnapshotNode,
  nodeId = "root"
): string[] {
  const expandableIds: string[] = [];

  const visit = (current: WorkProfileSnapshotNode, currentNodeId: string) => {
    if (isWorkProfileNodeExpandable(current)) {
      expandableIds.push(currentNodeId);
    }

    current.children.forEach((child, index) => {
      visit(child, `${currentNodeId}.${index}`);
    });
  };

  visit(node, nodeId);
  return expandableIds;
}

export function createDefaultExpandedWorkProfileNodeIds(
  profile?: WorkProfileSnapshot | null
): string[] {
  return profile ? ["root"] : [];
}

export function searchWorkProfile(
  profile: WorkProfileSnapshot | null | undefined,
  query: string,
  options?: {
    keepAncestors?: boolean;
  }
): WorkProfileSearchResult | null {
  return filterWorkProfile(profile, query, {
    hotspotActive: false,
    instrumentationFilter: "all",
    keepAncestors: options?.keepAncestors ?? false,
  });
}

export function findWorkProfileHotspots(
  profile: WorkProfileSnapshot | null | undefined,
  mode: WorkProfileHotspotMode,
  threshold: WorkProfileHotspotThreshold
): WorkProfileHotspotResult | null {
  if (!profile || mode === "off") {
    return null;
  }

  const totalMilliseconds = Math.max(profile.root.treeMilliseconds, 0);
  const candidates: Array<WorkProfileHotspotMatch & { nodeId: string }> = [];

  const visit = (node: WorkProfileSnapshotNode, nodeId: string) => {
    if (nodeId !== "root") {
      const milliseconds = mode === "tree" ? node.treeMilliseconds : node.nodeMilliseconds;
      candidates.push({
        milliseconds,
        nodeId,
        percentOfTotal: totalMilliseconds > 0 ? milliseconds / totalMilliseconds : 0,
      });
    }

    node.children.forEach((child, index) => {
      visit(child, `${nodeId}.${index}`);
    });
  };

  visit(profile.root, "root");

  const nonZeroCandidates = candidates.filter((candidate) => candidate.milliseconds > 0);
  const matchedCandidates = threshold === "top5"
    ? [...nonZeroCandidates]
      .sort((left, right) => {
        if (right.milliseconds !== left.milliseconds) {
          return right.milliseconds - left.milliseconds;
        }

        return left.nodeId.localeCompare(right.nodeId);
      })
      .slice(0, 5)
    : nonZeroCandidates.filter((candidate) => candidate.milliseconds >= resolveHotspotThresholdMilliseconds(
      threshold,
      totalMilliseconds
    ));
  const matchedNodeIds = new Set<string>();
  const matchesByNodeId = new Map<string, WorkProfileHotspotMatch>();

  matchedCandidates.forEach(({ milliseconds, nodeId, percentOfTotal }) => {
    matchedNodeIds.add(nodeId);
    matchesByNodeId.set(nodeId, {
      milliseconds,
      percentOfTotal,
    });
  });

  return {
    matchedNodeCount: matchedNodeIds.size,
    matchedNodeIds,
    matchesByNodeId,
  };
}

export function WorkProfilePanel({
  httpClientProfilingAvailable = true,
  iterationIsFinal,
  iterationStatus,
  onClose,
  onViewStateChange,
  profile,
  profilingEnabled,
  sqlProfilingAvailable = true,
  viewState,
}: {
  httpClientProfilingAvailable?: boolean;
  iterationIsFinal?: boolean;
  iterationStatus?: WorkCompletionStatus | null;
  onClose: () => void;
  onViewStateChange: (shape: WorkComponentShape) => void;
  profile?: WorkProfileSnapshot | null;
  profilingEnabled?: boolean | null;
  sqlProfilingAvailable?: boolean;
  viewState: WorkComponentShape;
}) {
  const summary = useMemo(() => summarizeWorkProfile(profile), [profile]);
  const availability = useMemo(
    () => resolveWorkProfileAvailability({
      iterationIsFinal,
      iterationStatus,
      profile,
      profilingEnabled,
    }),
    [iterationIsFinal, iterationStatus, profile, profilingEnabled]
  );
  const expandableNodeIds = useMemo(
    () => profile ? collectWorkProfileExpandableNodeIds(profile.root) : [],
    [profile]
  );
  const methodScopeOptions = useMemo(
    () => collectWorkProfileMethodScopeOptions(profile),
    [profile]
  );
  const [hotspotMode, setHotspotMode] = useState<WorkProfileHotspotMode>("off");
  const [hotspotThreshold, setHotspotThreshold] = useState<WorkProfileHotspotThreshold>("pct10");
  const [selectedMethodScopeIdentity, setSelectedMethodScopeIdentity] = useState("");
  const [instrumentationFilter, setInstrumentationFilter] = useState<WorkProfileInstrumentationFilter>("all");
  const [sqlBatchOpen, setSqlBatchOpen] = useState(false);
  const [keepAncestors, setKeepAncestors] = useState(false);
  const [searchText, setSearchText] = useState("");
  const deferredSearchText = useDeferredValue(searchText);
  const activeSearchText = deferredSearchText.trim();
  const selectedMethodScope = useMemo(
    () => findWorkProfileMethodScopeSelection(profile, selectedMethodScopeIdentity),
    [profile, selectedMethodScopeIdentity]
  );
  const hotspotResult = useMemo(
    () => findWorkProfileHotspots(profile, hotspotMode, hotspotThreshold),
    [hotspotMode, hotspotThreshold, profile]
  );
  const sqlBatch = useMemo(
    () => sqlBatchOpen ? createWorkProfileSqlBatch(profile) : null,
    [profile, sqlBatchOpen]
  );
  const filterResult = useMemo(
    () => filterWorkProfile(profile, deferredSearchText, {
      hotspotActive: hotspotMode !== "off",
      hotspotNodeIds: hotspotResult?.matchedNodeIds,
      instrumentationFilter,
      keepAncestors,
      methodScopeSelection: selectedMethodScope,
    }),
    [deferredSearchText, hotspotMode, hotspotResult, instrumentationFilter, keepAncestors, profile, selectedMethodScope]
  );
  const profileKey = `${profile?.startedAt ?? "none"}:${profile?.capturedAt ?? "none"}:${profile?.root.label ?? "none"}`;
  const lastProfileKeyRef = useRef(profileKey);
  const previousViewStateRef = useRef<WorkComponentShape | null>(null);
  const previousFilterActiveRef = useRef(Boolean(filterResult));
  const filterRestoreExpandedNodeIdsRef = useRef<ReadonlySet<string> | null>(null);
  const [expandedNodeIds, setExpandedNodeIds] = useState<ReadonlySet<string>>(
    () => new Set(createDefaultExpandedWorkProfileNodeIds(profile))
  );
  const expandedNodeIdsRef = useRef(expandedNodeIds);
  const httpActionsDisabled = !httpClientProfilingAvailable;
  const sqlActionsDisabled = !sqlProfilingAvailable;
  const hotspotActive = hotspotMode !== "off";
  const methodScopeActive = selectedMethodScopeIdentity.length > 0;
  const httpOnly = instrumentationFilter === "http";
  const sqlOnly = instrumentationFilter === "sql";
  const selectedMethodScopeLabel = methodScopeOptions.find((option) => option.value === selectedMethodScopeIdentity)?.label ?? null;
  const filterCountLabel = sqlOnly && activeSearchText.length === 0 && !hotspotActive && !methodScopeActive
    ? "SQL nodes"
    : httpOnly && activeSearchText.length === 0 && !hotspotActive && !methodScopeActive
      ? "HTTP requests"
      : hotspotActive && activeSearchText.length === 0 && !methodScopeActive && instrumentationFilter === "all"
        ? "Hotspots"
        : methodScopeActive && activeSearchText.length === 0 && !hotspotActive && instrumentationFilter === "all"
          ? "Method scopes"
          : "Matches";
  const sqlOnlyTooltip = sqlActionsDisabled
    ? sqlProfilingUnavailableTooltip
    : sqlOnly
      ? "SQL-only filter is enabled. The profile tree is currently limited to captured Microsoft.Data.SqlClient command nodes."
      : "Filter the profile tree down to captured Microsoft.Data.SqlClient command nodes.";
  const sqlOnlyTitle = sqlActionsDisabled
    ? sqlProfilingUnavailableTooltip
    : sqlOnly
      ? "SQL-only filter is on. The profile tree is limited to captured SQL command nodes."
      : "SQL-only filter is off. Show only captured SQL command nodes.";
  const sqlBatchTooltip = sqlActionsDisabled
    ? sqlProfilingUnavailableTooltip
    : sqlBatchOpenTooltip;
  const httpOnlyTooltip = httpActionsDisabled
    ? httpClientProfilingUnavailableTooltip
    : httpOnly
      ? "HTTP-only filter is enabled. The profile tree is currently limited to captured System.Net.Http request nodes."
      : "Filter the profile tree down to captured System.Net.Http request nodes.";
  const httpOnlyTitle = httpActionsDisabled
    ? httpClientProfilingUnavailableTooltip
    : httpOnly
      ? "HTTP-only filter is on. The profile tree is limited to captured HTTP request nodes."
      : "HTTP-only filter is off. Show only captured HTTP request nodes.";

  useEffect(() => {
    expandedNodeIdsRef.current = expandedNodeIds;
  }, [expandedNodeIds]);

  useEffect(() => {
    if (lastProfileKeyRef.current !== profileKey) {
      lastProfileKeyRef.current = profileKey;
      previousFilterActiveRef.current = false;
      filterRestoreExpandedNodeIdsRef.current = null;
      setSelectedMethodScopeIdentity("");
      setSearchText("");
      setExpandedNodeIds(new Set(
        viewState === "detailed" && !hotspotActive
          ? expandableNodeIds
          : createDefaultExpandedWorkProfileNodeIds(profile)
      ));
      return;
    }

    const activeFilterResult = filterResult;
    const filterActive = Boolean(activeFilterResult);
    const wasFilterActive = previousFilterActiveRef.current;
    previousFilterActiveRef.current = filterActive;

    if (activeFilterResult) {
      if (!wasFilterActive) {
        filterRestoreExpandedNodeIdsRef.current = new Set(expandedNodeIdsRef.current);
      }

      setExpandedNodeIds((current) => {
        const next = new Set(activeFilterResult.expandableNodeIds);
        return areWorkProfileNodeIdSetsEqual(current, next) ? current : next;
      });
      return;
    }

    if (wasFilterActive) {
      const restore = filterRestoreExpandedNodeIdsRef.current;
      filterRestoreExpandedNodeIdsRef.current = null;
      setExpandedNodeIds(new Set(restore ?? createDefaultExpandedWorkProfileNodeIds(profile)));
    }
  }, [expandableNodeIds, filterResult, hotspotActive, profile, profileKey, viewState]);

  useEffect(() => {
    if (selectedMethodScopeIdentity.length === 0) {
      return;
    }

    const selectionStillExists = methodScopeOptions.some((option) => option.value === selectedMethodScopeIdentity);
    if (!selectionStillExists) {
      setSelectedMethodScopeIdentity("");
    }
  }, [methodScopeOptions, selectedMethodScopeIdentity]);

  useEffect(() => {
    const previousViewState = previousViewStateRef.current;
    previousViewStateRef.current = viewState;

    if (viewState !== "detailed" || previousViewState === "detailed" || filterResult) {
      return;
    }

    setExpandedNodeIds((current) => {
      const next = new Set(expandableNodeIds);
      return areWorkProfileNodeIdSetsEqual(current, next) ? current : next;
    });
  }, [expandableNodeIds, filterResult, viewState]);

  useEffect(() => {
    if (!sqlProfilingAvailable) {
      setInstrumentationFilter((current) => current === "sql" ? "all" : current);
      setSqlBatchOpen(false);
    }
  }, [sqlProfilingAvailable]);

  useEffect(() => {
    if (!httpClientProfilingAvailable) {
      queueMicrotask(() => {
        setInstrumentationFilter((current) => current === "http" ? "all" : current);
      });
    }
  }, [httpClientProfilingAvailable]);

  const expandAll = useCallback(() => {
    setExpandedNodeIds(new Set(filterResult?.expandableNodeIds ?? expandableNodeIds));
  }, [expandableNodeIds, filterResult]);

  const collapseAll = useCallback(() => {
    setExpandedNodeIds(new Set(createDefaultExpandedWorkProfileNodeIds(profile)));
  }, [profile]);

  const toggleNode = useCallback((nodeId: string) => {
    setExpandedNodeIds((current) => {
      const next = new Set(current);
      if (next.has(nodeId)) {
        next.delete(nodeId);
      } else {
        next.add(nodeId);
      }

      return next;
    });
  }, []);

  return (
    <>
      <PanelShell
        className="flex min-h-0 flex-1 flex-col overflow-hidden"
        contentClassName={viewState === "compact"
          ? "hidden"
          : "mt-4 flex min-h-0 flex-1 flex-col overflow-hidden"}
        description={undefined}
        actions={viewState !== "compact" && profile ? (
          <WorkProfilePanelActions
            onCollapseAll={collapseAll}
            onExpandAll={expandAll}
          />
        ) : null}
        onClose={onClose}
        onViewStateChange={onViewStateChange}
        supportedViewStates={["compact", "standard", "detailed"]}
        title={(
          <WorkProfilePanelTitle
            availability={availability}
            profile={profile}
            summary={summary}
            viewState={viewState}
          />
        )}
        viewState={viewState}
      >
        <section
          className={cn(
            "flex min-h-0 flex-1 flex-col rounded-xl border bg-muted/10 p-4",
            viewState === "standard" && "min-h-[24rem] max-h-[70vh]",
            viewState === "detailed" && "max-h-[calc(100svh-11rem)]"
          )}
        >
          {!profile ? (
            <ConsoleEmptyState className="flex min-h-0 flex-1 items-center justify-center" fill padding="spacious">
              {availability?.message}
            </ConsoleEmptyState>
          ) : (
            <PanelScrollViewport
              className="rounded-xl border bg-background/60 p-4"
              hasMore={false}
              loadedCount={summary?.nodeCount ?? 0}
              loading={false}
              loadingMore={false}
              noun="profile node"
              onLoadMore={() => undefined}
              showLoadedCount={false}
            >
              <div className="space-y-3">
                <div className="flex flex-col gap-3">
                  <div className="flex min-h-8 flex-wrap items-center gap-2 text-muted-foreground text-xs">
                    <ProfileSummaryPill label="Started" value={formatProfileTimestamp(profile.startedAt)} />
                    <ProfileSummaryPill label="Captured" value={formatProfileTimestamp(profile.capturedAt)} />
                    <ProfileSummaryPill label="Tree" value={formatProfileMilliseconds(profile.root.treeMilliseconds)} />
                    <ProfileSummaryPill label="Nodes" value={(summary?.nodeCount ?? 0).toLocaleString()} />
                    {filterResult ? (
                      <ProfileSummaryPill
                        label={filterCountLabel}
                        value={filterResult.matchedNodeCount.toLocaleString()}
                      />
                    ) : null}
                  </div>
                  <div className="flex flex-wrap items-center gap-2 rounded-xl border bg-muted/10 p-3">
                    <div className="flex min-w-0 flex-wrap items-center gap-2">
                      <ButtonGroup className="flex-wrap">
                        {hotspotModes.map((mode) => (
                          <Tooltip key={mode.id}>
                            <TooltipTrigger asChild>
                              <Button
                                aria-pressed={hotspotMode === mode.id}
                                onClick={() => setHotspotMode(mode.id)}
                                size="default"
                                title={describeWorkProfileHotspotMode(mode.id)}
                                type="button"
                                variant={hotspotMode === mode.id ? "secondary" : "outline"}
                              >
                                {mode.label}
                              </Button>
                            </TooltipTrigger>
                            <TooltipContent sideOffset={6}>
                              {describeWorkProfileHotspotMode(mode.id)}
                            </TooltipContent>
                          </Tooltip>
                        ))}
                      </ButtonGroup>
                      <Select
                        disabled={!hotspotActive}
                        onValueChange={(value) => setHotspotThreshold(value as WorkProfileHotspotThreshold)}
                        value={hotspotThreshold}
                      >
                        <SelectTrigger
                          aria-label="Hotspot threshold"
                          className="min-w-36"
                          size="default"
                          title={describeWorkProfileHotspotThreshold(hotspotMode, hotspotThreshold)}
                        >
                          <SelectValue />
                        </SelectTrigger>
                        <SelectContent>
                          {hotspotThresholds.map((threshold) => (
                            <SelectItem key={threshold.id} value={threshold.id}>
                              {threshold.label}
                            </SelectItem>
                          ))}
                        </SelectContent>
                      </Select>
                      {methodScopeOptions.length > 0 ? (
                        <WorkProfileMethodScopePicker
                          onValueChange={setSelectedMethodScopeIdentity}
                          options={methodScopeOptions}
                          value={selectedMethodScopeIdentity}
                        />
                      ) : null}
                    </div>
                    <div className="ml-auto flex min-w-0 flex-1 flex-wrap items-center justify-end gap-2">
                      <div className="min-w-40 flex-1 max-w-56">
                        <Input
                          aria-label="Search profile nodes"
                          onChange={(event) => setSearchText(event.target.value)}
                          placeholder="Filter profile nodes"
                          type="search"
                          value={searchText}
                        />
                      </div>
                      <div className="flex flex-wrap items-center gap-2">
                        <Tooltip>
                          <TooltipTrigger asChild>
                            {sqlActionsDisabled ? (
                              <span className="inline-flex" tabIndex={0}>
                                <Button
                                  aria-label="SQL nodes only"
                                  aria-pressed={false}
                                  disabled
                                  size="icon-sm"
                                  title={sqlOnlyTitle}
                                  type="button"
                                  variant="outline"
                                >
                                  <DatabaseSearch className="size-3.5" />
                                </Button>
                              </span>
                            ) : (
                              <Button
                                aria-label="SQL nodes only"
                                aria-pressed={sqlOnly}
                                onClick={() => setInstrumentationFilter(sqlOnly ? "all" : "sql")}
                                size="icon-sm"
                                title={sqlOnlyTitle}
                                type="button"
                                variant={sqlOnly ? "secondary" : "outline"}
                              >
                                {sqlOnly ? (
                                  <DatabaseZap className="size-3.5" />
                                ) : (
                                  <DatabaseSearch className="size-3.5" />
                                )}
                              </Button>
                            )}
                          </TooltipTrigger>
                          <TooltipContent sideOffset={6}>
                            {sqlOnlyTooltip}
                          </TooltipContent>
                        </Tooltip>
                        <Tooltip>
                          <TooltipTrigger asChild>
                            {httpActionsDisabled ? (
                              <span className="inline-flex" tabIndex={0}>
                                <Button
                                  aria-label="HTTP request nodes only"
                                  aria-pressed={false}
                                  disabled
                                  size="icon-sm"
                                  title={httpOnlyTitle}
                                  type="button"
                                  variant="outline"
                                >
                                  <Globe2 className="size-3.5" />
                                </Button>
                              </span>
                            ) : (
                              <Button
                                aria-label="HTTP request nodes only"
                                aria-pressed={httpOnly}
                                onClick={() => setInstrumentationFilter(httpOnly ? "all" : "http")}
                                size="icon-sm"
                                title={httpOnlyTitle}
                                type="button"
                                variant={httpOnly ? "secondary" : "outline"}
                              >
                                <Globe2 className="size-3.5" />
                              </Button>
                            )}
                          </TooltipTrigger>
                          <TooltipContent sideOffset={6}>
                            {httpOnlyTooltip}
                          </TooltipContent>
                        </Tooltip>
                        <Tooltip>
                          <TooltipTrigger asChild>
                            {sqlActionsDisabled ? (
                              <span className="inline-flex" tabIndex={0}>
                                <Button
                                  aria-label="Open SQL batch"
                                  className="whitespace-nowrap"
                                  disabled
                                  size="icon-sm"
                                  title={sqlBatchTooltip}
                                  type="button"
                                  variant="outline"
                                >
                                  <SquareTerminal className="size-3.5" />
                                </Button>
                              </span>
                            ) : (
                              <Button
                                aria-label="Open SQL batch"
                                className="whitespace-nowrap"
                                onClick={() => setSqlBatchOpen(true)}
                                size="icon-sm"
                                title={sqlBatchTooltip}
                                type="button"
                                variant="outline"
                              >
                                <SquareTerminal className="size-3.5" />
                              </Button>
                            )}
                          </TooltipTrigger>
                          <TooltipContent sideOffset={6}>
                            {sqlBatchTooltip}
                          </TooltipContent>
                        </Tooltip>
                        <Tooltip>
                          <TooltipTrigger asChild>
                            <Button
                              aria-label="Ancestor context"
                              aria-pressed={keepAncestors}
                              className="whitespace-nowrap"
                              onClick={() => setKeepAncestors((current) => !current)}
                              size="icon-sm"
                              type="button"
                              variant={keepAncestors ? "secondary" : "outline"}
                            >
                              {keepAncestors ? (
                                <GitBranchPlus className="size-3.5" />
                              ) : (
                                <GitBranchMinus className="size-3.5" />
                              )}
                            </Button>
                          </TooltipTrigger>
                          <TooltipContent sideOffset={6}>
                            {keepAncestors
                              ? "Ancestor nodes are currently visible while filtering so matches stay in context."
                              : "Ancestor nodes are currently hidden so the filtered view only shows direct matches in the active scope."}
                          </TooltipContent>
                        </Tooltip>
                      </div>
                    </div>
                  </div>
                </div>
                {filterResult && filterResult.matchedNodeCount === 0 ? (
                  <ConsoleEmptyState className="rounded-xl border bg-muted/10" fill padding="spacious">
                    {createWorkProfileEmptyStateMessage(activeSearchText, hotspotActive, selectedMethodScopeLabel, instrumentationFilter)}
                  </ConsoleEmptyState>
                ) : profile?.root ? (
                  <WorkProfileTreeNode
                    depth={0}
                    expandedNodeIds={expandedNodeIds}
                    forceExpandContext={activeSearchText.length > 0}
                    hotspotMatchesByNodeId={hotspotResult?.matchesByNodeId}
                    matchedNodeIds={filterResult?.matchedNodeIds}
                    node={profile.root}
                    nodeId="root"
                    onToggle={toggleNode}
                    searchQuery={activeSearchText}
                    visibleNodeIds={filterResult?.visibleNodeIds}
                  />
                ) : null}
              </div>
            </PanelScrollViewport>
          )}
        </section>
      </PanelShell>
      <WorkProfileSqlBatchDialog
        batch={sqlBatch}
        onOpenChange={setSqlBatchOpen}
        open={sqlBatchOpen}
      />
    </>
  );
}

function WorkProfilePanelTitle({
  availability,
  profile,
  summary,
  viewState,
}: {
  availability: WorkProfileAvailability | null;
  profile?: WorkProfileSnapshot | null;
  summary: WorkProfileSummary | null;
  viewState: WorkComponentShape;
}) {
  return (
    <>
      <span>Profile</span>
      {viewState !== "compact" ? (
        <Tooltip delayDuration={500} disableHoverableContent>
          <TooltipTrigger asChild>
            <button
              aria-label="Profile: Per-iteration profile tree with timings, scopes, and captured context."
              className="group inline-flex size-5 items-center justify-center rounded-sm text-muted-foreground transition-colors hover:text-foreground focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring focus-visible:ring-offset-2 focus-visible:ring-offset-background"
              type="button"
            >
              <Info className="size-3.5 shrink-0" />
            </button>
          </TooltipTrigger>
          <TooltipContent className="max-w-64 whitespace-normal text-left" side="top" sideOffset={6}>
            Per-iteration profile tree with timings, scopes, and captured context.
          </TooltipContent>
        </Tooltip>
      ) : null}
      {viewState === "compact" ? (
        profile && summary ? (
          <>
            <ProfileSummaryPill label="Tree" value={formatProfileMilliseconds(summary.totalTreeMilliseconds)} />
            <ProfileSummaryPill label="Nodes" value={summary.nodeCount.toLocaleString()} />
            <ProfileSummaryPill label="Depth" value={summary.maxDepth.toString()} />
          </>
        ) : (
          <ProfileSummaryPill label="State" value={availability?.label ?? "Unavailable"} />
        )
      ) : null}
    </>
  );
}

function resolveWorkProfileAvailability({
  iterationIsFinal,
  iterationStatus,
  profile,
  profilingEnabled,
}: {
  iterationIsFinal?: boolean;
  iterationStatus?: WorkCompletionStatus | null;
  profile?: WorkProfileSnapshot | null;
  profilingEnabled?: boolean | null;
}): WorkProfileAvailability | null {
  if (profile) {
    return null;
  }

  if (profilingEnabled === false) {
    return {
      label: "Disabled",
      message: "Profiling was disabled for this iteration, so no profile tree was captured.",
    };
  }

  if (iterationIsFinal === false || iterationStatus === "Executing") {
    return {
      label: "Pending",
      message: "This iteration is still executing. Profiling is enabled, and the profile tree will appear after the iteration finishes.",
    };
  }

  if (profilingEnabled === true) {
    return {
      label: "Unavailable",
      message: "Profiling was enabled for this iteration, but no profile snapshot is available. The snapshot may have been omitted from this response or could not be captured.",
    };
  }

  return {
    label: "Unavailable",
    message: "No profile snapshot is available for this iteration.",
  };
}

function WorkProfilePanelActions({
  onCollapseAll,
  onExpandAll,
}: {
  onCollapseAll: () => void;
  onExpandAll: () => void;
}) {
  return (
    <>
      <ToolbarIconButton
        label="Expand all profile nodes"
        onClick={onExpandAll}
        type="button"
        tooltip="Expand the full profile tree"
      >
        <Maximize2 className="size-3.5" />
      </ToolbarIconButton>
      <ToolbarIconButton
        label="Collapse profile nodes"
        onClick={onCollapseAll}
        type="button"
        tooltip="Collapse the profile tree back to the root"
      >
        <Minimize2 className="size-3.5" />
      </ToolbarIconButton>
    </>
  );
}

function WorkProfileSqlBatchDialog({
  batch,
  onOpenChange,
  open,
}: {
  batch: WorkProfileSqlBatch | null;
  onOpenChange: (open: boolean) => void;
  open: boolean;
}) {
  const [copied, setCopied] = useState(false);
  const [mode, setMode] = useState<WorkProfileSqlBatchMode>("replayable");

  const activeBatch = batch
    ? mode === "replayable"
      ? batch.replayableBatch
      : batch.originalExecutionBatch
    : "";

  useEffect(() => {
    if (!open) {
      setCopied(false);
      setMode("replayable");
      return;
    }

    if (!copied) {
      return;
    }

    const timeoutId = window.setTimeout(() => setCopied(false), 2000);
    return () => window.clearTimeout(timeoutId);
  }, [copied, open]);

  const handleCopy = useCallback(async () => {
    if (!batch) {
      return;
    }

    const writeText = navigator.clipboard?.writeText?.bind(navigator.clipboard);
    if (!writeText) {
      setCopied(false);
      return;
    }

    try {
      await writeText(activeBatch);
      setCopied(true);
    } catch {
      setCopied(false);
    }
  }, [activeBatch, batch]);

  return (
    <Dialog onOpenChange={onOpenChange} open={open}>
      <DialogContent className="flex max-h-[calc(100vh-2rem)] flex-col overflow-hidden sm:max-h-[min(85vh,56rem)] sm:max-w-4xl">
        <DialogHeader className="shrink-0">
          <DialogTitle>SQL batch</DialogTitle>
          <DialogDescription>
            {batch
              ? "Choose a replayable script or a parameterized view that keeps the captured execution shape. Redacted values may need to be replaced before replaying either view."
              : "This profile does not contain captured SQL commands, so there is no SQL batch to display."}
          </DialogDescription>
          {batch ? (
            <div className="flex flex-wrap items-center gap-2 pt-1 text-muted-foreground text-xs">
              <ProfileSummaryPill label="Statements" value={batch.statementCount.toLocaleString()} />
              <ProfileSummaryPill label="Parameters" value={batch.parameterCount.toLocaleString()} />
              <ProfileSummaryPill label="Redacted" value={batch.redactedParameterCount.toLocaleString()} />
            </div>
          ) : null}
        </DialogHeader>
        <div className="flex min-h-0 flex-1 flex-col">
          {batch ? (
            <>
              <div
                aria-label="SQL batch mode"
                className="mb-3 inline-flex w-full rounded-lg bg-muted p-[3px] sm:w-[26rem]"
                role="tablist"
              >
                <button
                  aria-selected={mode === "replayable"}
                  className={cn(
                    "inline-flex h-8 flex-1 items-center justify-center rounded-md px-3 text-sm font-medium transition-colors",
                    mode === "replayable"
                      ? "bg-background text-foreground shadow-sm"
                      : "text-foreground/60 hover:text-foreground"
                  )}
                  onClick={() => setMode("replayable")}
                  role="tab"
                  type="button"
                >
                  Replayable
                </button>
                <button
                  aria-selected={mode === "original"}
                  className={cn(
                    "inline-flex h-8 flex-1 items-center justify-center rounded-md px-3 text-sm font-medium transition-colors",
                    mode === "original"
                      ? "bg-background text-foreground shadow-sm"
                      : "text-foreground/60 hover:text-foreground"
                  )}
                  onClick={() => setMode("original")}
                  role="tab"
                  type="button"
                >
                  Parameterized view
                </button>
              </div>
              <WorkProfileSqlBatchViewer
                batch={activeBatch}
                label={mode === "replayable" ? "Replayable SQL batch" : "Parameterized SQL batch"}
              />
            </>
          ) : (
            <ConsoleEmptyState className="flex min-h-[14rem] flex-1 items-center justify-center rounded-lg border bg-muted/10" fill padding="spacious">
              {sqlBatchUnavailableMessage}
            </ConsoleEmptyState>
          )}
        </div>
        <DialogFooter className="shrink-0" showCloseButton>
          {batch ? (
            <Button
              onClick={() => void handleCopy()}
              title="Copy the active SQL batch view."
              type="button"
              variant={copied ? "secondary" : "outline"}
            >
              {copied ? (
                <Check className="size-4" />
              ) : (
                <Copy className="size-4" />
              )}
              {copied ? "Copied" : "Copy SQL"}
            </Button>
          ) : null}
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}

function WorkProfileSqlBatchViewer({
  batch,
  label,
}: {
  batch: string;
  label: string;
}) {
  const highlightFallbackReason = useMemo(
    () => getWorkProfileSqlHighlightFallbackReason(batch),
    [batch]
  );
  const tokens = useMemo(
    () => highlightFallbackReason ? [] : tokenizeWorkProfileSql(batch),
    [batch, highlightFallbackReason]
  );

  return (
    <div
      aria-label={label}
      className="min-h-[14rem] flex-1 overflow-auto rounded-lg border bg-slate-950/90 shadow-inner"
      role="region"
      tabIndex={0}
    >
      {highlightFallbackReason ? (
        <div className="border-slate-800/80 border-b px-3 py-2 text-[11px] text-slate-400">
          {highlightFallbackReason}
        </div>
      ) : null}
      <pre className="min-w-max p-3 font-mono text-xs leading-5 text-slate-100">
        {highlightFallbackReason
          ? batch
          : tokens.map((token, index) => (
            <span
              className={workProfileSqlTokenClassName(token.kind)}
              key={`${index}:${token.kind}:${token.text.length}`}
            >
              {token.text}
            </span>
          ))}
      </pre>
    </div>
  );
}

function tokenizeWorkProfileSql(value: string): WorkProfileSqlToken[] {
  return tokenizeWorkProfileSqlSegment(value);
}

function tokenizeWorkProfileSqlSegment(
  value: string,
  options?: {
    embeddedStringMode?: "parameter-definition" | "statement" | null;
  }
): WorkProfileSqlToken[] {
  const tokens: WorkProfileSqlToken[] = [];
  let index = 0;
  let embeddedStringMode = options?.embeddedStringMode ?? null;

  while (index < value.length) {
    const character = value[index];

    if (character === "-" && value[index + 1] === "-") {
      let commentEnd = index + 2;
      while (commentEnd < value.length && value[commentEnd] !== "\n") {
        commentEnd += 1;
      }

      pushWorkProfileSqlToken(tokens, "comment", value.slice(index, commentEnd));
      index = commentEnd;
      continue;
    }

    if ((character === "N" || character === "n") && value[index + 1] === "'") {
      const prefixEnd = index + 2;
      const stringEnd = findWorkProfileSqlStringEnd(value, prefixEnd);
      pushWorkProfileSqlStringTokens(
        tokens,
        value.slice(index, prefixEnd),
        value.slice(prefixEnd, stringEnd - 1),
        "'",
        embeddedStringMode
      );
      embeddedStringMode = embeddedStringMode === "statement" ? "parameter-definition" : null;
      index = stringEnd;
      continue;
    }

    if (character === "'") {
      const prefixEnd = index + 1;
      const stringEnd = findWorkProfileSqlStringEnd(value, prefixEnd);
      pushWorkProfileSqlStringTokens(
        tokens,
        "'",
        value.slice(prefixEnd, stringEnd - 1),
        "'",
        embeddedStringMode
      );
      embeddedStringMode = null;
      index = stringEnd;
      continue;
    }

    if (character === "@") {
      let parameterEnd = index + 1;
      while (parameterEnd < value.length && /[@A-Za-z0-9_#$]/.test(value[parameterEnd])) {
        parameterEnd += 1;
      }

      pushWorkProfileSqlToken(tokens, "parameter", value.slice(index, parameterEnd));
      index = parameterEnd;
      continue;
    }

    if (character === "0" && (value[index + 1] === "x" || value[index + 1] === "X")) {
      let hexEnd = index + 2;
      while (hexEnd < value.length && /[0-9A-Fa-f]/.test(value[hexEnd])) {
        hexEnd += 1;
      }

      pushWorkProfileSqlToken(tokens, "number", value.slice(index, hexEnd));
      index = hexEnd;
      continue;
    }

    if (/[0-9]/.test(character)) {
      let numberEnd = index + 1;
      while (numberEnd < value.length && /[0-9._]/.test(value[numberEnd])) {
        numberEnd += 1;
      }

      pushWorkProfileSqlToken(tokens, "number", value.slice(index, numberEnd));
      index = numberEnd;
      continue;
    }

    if (character === "[") {
      let identifierEnd = index + 1;
      while (identifierEnd < value.length) {
        if (value[identifierEnd] === "]") {
          if (value[identifierEnd + 1] === "]") {
            identifierEnd += 2;
            continue;
          }

          identifierEnd += 1;
          break;
        }

        identifierEnd += 1;
      }

      pushWorkProfileSqlToken(tokens, "identifier", value.slice(index, identifierEnd));
      index = identifierEnd;
      continue;
    }

    if (/[A-Za-z_]/.test(character)) {
      let wordEnd = index + 1;
      while (wordEnd < value.length && /[A-Za-z0-9_.$#]/.test(value[wordEnd])) {
        wordEnd += 1;
      }

      const text = value.slice(index, wordEnd);
      const upperText = text.toUpperCase();
      const nextSignificantCharacter = findNextNonWhitespaceCharacter(value, wordEnd);
      const kind = workProfileSqlTypes.has(upperText)
        ? "type"
        : isWorkProfileSqlFunctionToken(upperText, nextSignificantCharacter)
        ? "function"
        : workProfileSqlKeywords.has(upperText)
        ? "keyword"
        : "plain";
      pushWorkProfileSqlToken(tokens, kind, text);
      if (upperText === "SP_EXECUTESQL") {
        embeddedStringMode = "statement";
      }
      index = wordEnd;
      continue;
    }

    if (character === ";" && embeddedStringMode === "parameter-definition") {
      embeddedStringMode = null;
    }

    pushWorkProfileSqlToken(tokens, "plain", character);
    index += 1;
  }

  return tokens;
}

function findWorkProfileSqlStringEnd(value: string, index: number): number {
  let current = index;

  while (current < value.length) {
    if (value[current] !== "'") {
      current += 1;
      continue;
    }

    if (value[current + 1] === "'") {
      current += 2;
      continue;
    }

    return current + 1;
  }

  return current;
}

function pushWorkProfileSqlStringTokens(
  tokens: WorkProfileSqlToken[],
  prefix: string,
  rawBody: string,
  suffix: string,
  embeddedStringMode: "parameter-definition" | "statement" | null
) {
  if (!embeddedStringMode) {
    pushWorkProfileSqlToken(tokens, "string", `${prefix}${rawBody}${suffix}`);
    return;
  }

  pushWorkProfileSqlToken(tokens, "string-delimiter", prefix);
  appendWorkProfileSqlTokens(
    tokens,
    tokenizeWorkProfileSqlSegment(
      decodeWorkProfileSqlEmbeddedString(rawBody),
      { embeddedStringMode: null }
    )
  );
  pushWorkProfileSqlToken(tokens, "string-delimiter", suffix);
}

function decodeWorkProfileSqlEmbeddedString(value: string): string {
  return value.replaceAll("''", "'");
}

function findNextNonWhitespaceCharacter(value: string, index: number): string | null {
  let current = index;
  while (current < value.length) {
    if (!/\s/.test(value[current])) {
      return value[current];
    }

    current += 1;
  }

  return null;
}

function isWorkProfileSqlFunctionToken(text: string, nextSignificantCharacter: string | null): boolean {
  return nextSignificantCharacter === "(" &&
    !workProfileSqlKeywords.has(text) &&
    !workProfileSqlTypes.has(text);
}

function getWorkProfileSqlHighlightFallbackReason(value: string): string | null {
  const lineCount = value.length === 0 ? 0 : value.split("\n").length;
  if (value.length > workProfileSqlHighlightMaxCharacters || lineCount > workProfileSqlHighlightMaxLines) {
    return "Syntax highlighting is disabled for large SQL batches so the viewer stays responsive.";
  }

  return null;
}

function appendWorkProfileSqlTokens(
  tokens: WorkProfileSqlToken[],
  values: readonly WorkProfileSqlToken[]
) {
  values.forEach((value) => {
    pushWorkProfileSqlToken(tokens, value.kind, value.text);
  });
}

function pushWorkProfileSqlToken(
  tokens: WorkProfileSqlToken[],
  kind: WorkProfileSqlTokenKind,
  text: string
) {
  if (text.length === 0) {
    return;
  }

  const previous = tokens[tokens.length - 1];
  if (previous && previous.kind === kind) {
    previous.text += text;
    return;
  }

  tokens.push({ kind, text });
}

function workProfileSqlTokenClassName(kind: WorkProfileSqlTokenKind): string {
  switch (kind) {
    case "comment":
      return "italic text-slate-500";
    case "function":
      return "text-rose-300";
    case "identifier":
      return "text-cyan-200";
    case "keyword":
      return "font-semibold text-violet-300";
    case "number":
      return "text-amber-300";
    case "parameter":
      return "text-sky-300";
    case "string-delimiter":
      return "text-emerald-500";
    case "string":
      return "text-emerald-300";
    case "type":
      return "text-cyan-300";
    case "plain":
      return "text-slate-100";
  }
}

function WorkProfileTreeNode({
  depth,
  expandedNodeIds,
  forceExpandContext = false,
  hotspotMatchesByNodeId,
  matchedNodeIds,
  node,
  nodeId,
  onToggle,
  searchQuery,
  visibleNodeIds,
}: {
  depth: number;
  expandedNodeIds: ReadonlySet<string>;
  forceExpandContext?: boolean;
  hotspotMatchesByNodeId?: ReadonlyMap<string, WorkProfileHotspotMatch>;
  matchedNodeIds?: ReadonlySet<string>;
  node: WorkProfileSnapshotNode;
  nodeId: string;
  onToggle: (nodeId: string) => void;
  searchQuery?: string;
  visibleNodeIds?: ReadonlySet<string>;
}) {
  if (visibleNodeIds && !visibleNodeIds.has(nodeId)) {
    return (
      <>
        {node.children.map((child, index) => (
          <WorkProfileTreeNode
            depth={depth}
            expandedNodeIds={expandedNodeIds}
            forceExpandContext={forceExpandContext}
            hotspotMatchesByNodeId={hotspotMatchesByNodeId}
            key={`${nodeId}.${index}`}
            matchedNodeIds={matchedNodeIds}
            node={child}
            nodeId={`${nodeId}.${index}`}
            onToggle={onToggle}
            searchQuery={searchQuery}
            visibleNodeIds={visibleNodeIds}
          />
        ))}
      </>
    );
  }

  const expandable = isWorkProfileNodeExpandable(node);
  const expanded = expandable && expandedNodeIds.has(nodeId);
  const collapsedSummary = [
    node.children.length > 0 ? `${node.children.length} child node${node.children.length === 1 ? "" : "s"}` : null,
    summarizeWorkProfileContext(node.context),
  ].filter(Boolean).join(" • ");
  const contextVisible = shouldRenderWorkProfileContext(node.context);
  const hasTimingMetrics = node.metricType !== "Metric";
  const hotspotMatch = hotspotMatchesByNodeId?.get(nodeId);
  const matched = matchedNodeIds?.has(nodeId) ?? false;

  return (
    <div className="space-y-3">
      <div className={cn(
        "rounded-xl border bg-background/80 shadow-sm",
        matched && "border-primary/80 bg-primary/10 ring-2 ring-primary/40"
      )}>
        <div
          className="flex min-w-0 items-start gap-3 p-3"
          style={{ paddingLeft: `${0.75 + depth * 1.1}rem` }}
        >
          {expandable ? (
            <button
              aria-label={`${expanded ? "Collapse" : "Expand"} ${node.label}`}
              className="mt-0.5 inline-flex size-6 shrink-0 items-center justify-center rounded-md text-muted-foreground transition-colors hover:bg-accent hover:text-foreground"
              onClick={() => onToggle(nodeId)}
              type="button"
            >
              <ChevronRight className={cn("size-4 transition-transform", expanded && "rotate-90")} />
            </button>
          ) : (
            <span className="size-6 shrink-0" />
          )}
          <div className="min-w-0 flex-1 space-y-2">
            <div className="flex min-w-0 flex-wrap items-center gap-2">
              <span className="min-w-0 truncate font-medium text-sm">
                {renderHighlightedText(node.label, searchQuery)}
              </span>
              <Badge className={profileMetricTone(node.metricType)} variant="outline">
                {renderHighlightedText(formatProfileMetricType(node.metricType), searchQuery)}
              </Badge>
              {hasTimingMetrics ? (
                <>
                  <ProfileSummaryPill label="Tree" value={formatProfileMilliseconds(node.treeMilliseconds)} />
                  <ProfileSummaryPill label="Node" value={formatProfileMilliseconds(node.nodeMilliseconds)} />
                </>
              ) : null}
              {hotspotMatch ? (
                <ProfileSummaryPill label="Total" value={formatProfilePercentage(hotspotMatch.percentOfTotal)} />
              ) : null}
            </div>
            {!expanded && collapsedSummary ? (
              <div className="text-muted-foreground text-xs">
                {renderHighlightedText(collapsedSummary, searchQuery)}
              </div>
            ) : null}
          </div>
        </div>
        {expanded ? (
          <div className="space-y-3 border-t px-3 py-3">
            {contextVisible ? (
              <WorkProfileContextBlock
                context={node.context}
                forceExpanded={forceExpandContext}
                searchQuery={searchQuery}
              />
            ) : null}
            {node.children.length > 0 ? (
              <div className="space-y-3 border-l border-dashed pl-3">
                {node.children.map((child, index) => (
                  <WorkProfileTreeNode
                    depth={depth + 1}
                    expandedNodeIds={expandedNodeIds}
                    forceExpandContext={forceExpandContext}
                    hotspotMatchesByNodeId={hotspotMatchesByNodeId}
                    key={`${nodeId}.${index}`}
                    matchedNodeIds={matchedNodeIds}
                    node={child}
                    nodeId={`${nodeId}.${index}`}
                    onToggle={onToggle}
                    searchQuery={searchQuery}
                    visibleNodeIds={visibleNodeIds}
                  />
                ))}
              </div>
            ) : null}
          </div>
        ) : null}
      </div>
    </div>
  );
}

function WorkProfileContextBlock({
  context,
  forceExpanded,
  searchQuery,
}: {
  context: unknown;
  forceExpanded: boolean;
  searchQuery?: string;
}) {
  const [expanded, setExpanded] = useState(true);
  const displayContext = useMemo(() => createWorkProfileContextDisplayValue(context), [context]);
  const normalizedContext = useMemo(
    () => normalizeProfileJsonValue(displayContext),
    [displayContext]
  );
  const formattedContext = useMemo(() => formatProfileContext(displayContext), [displayContext]);
  const expandedRef = useRef(expanded);
  const previousForceExpandedRef = useRef(forceExpanded);
  const searchRestoreExpandedRef = useRef<boolean | null>(null);

  useEffect(() => {
    expandedRef.current = expanded;
  }, [expanded]);

  useEffect(() => {
    setExpanded(true);
    previousForceExpandedRef.current = forceExpanded;
    searchRestoreExpandedRef.current = null;
  }, [formattedContext]);
  useEffect(() => {
    const wasForceExpanded = previousForceExpandedRef.current;
    previousForceExpandedRef.current = forceExpanded;

    if (forceExpanded) {
      if (!wasForceExpanded) {
        searchRestoreExpandedRef.current = expandedRef.current;
      }

      setExpanded(true);
      return;
    }

    if (wasForceExpanded) {
      setExpanded(searchRestoreExpandedRef.current ?? true);
      searchRestoreExpandedRef.current = null;
    }
  }, [forceExpanded]);

  return (
    <div className="space-y-2 rounded-lg border bg-muted/20 p-3">
      <button
        aria-label={`${expanded ? "Collapse" : "Expand"} context JSON`}
        className="flex w-full items-center justify-between gap-3 rounded-md text-left transition-colors hover:text-foreground"
        onClick={() => setExpanded((current) => !current)}
        type="button"
      >
        <div className="font-medium text-xs uppercase tracking-wide text-muted-foreground">
          Context JSON
        </div>
        <div className="flex items-center gap-2 text-muted-foreground text-xs">
          <span>{summarizeWorkProfileContext(displayContext) ?? "context"}</span>
          <ChevronRight className={cn("size-4 transition-transform", expanded && "rotate-90")} />
        </div>
      </button>
      {expanded ? (
        <pre className="overflow-x-auto whitespace-pre-wrap break-words rounded-md bg-background/70 p-3 font-mono text-xs leading-5 text-foreground">
          {normalizedContext.ok ? (
            <ProfileJsonValue searchQuery={searchQuery} value={normalizedContext.value} />
          ) : (
            renderHighlightedText(formattedContext, searchQuery)
          )}
        </pre>
      ) : null}
    </div>
  );
}

function WorkProfileMethodScopePicker({
  onValueChange,
  options,
  value,
}: {
  onValueChange: (value: string) => void;
  options: readonly WorkProfileMethodScopeOption[];
  value: string;
}) {
  const [open, setOpen] = useState(false);
  const selectedOption = options.find((option) => option.value === value) ?? null;

  return (
    <Popover onOpenChange={setOpen} open={open}>
      <PopoverTrigger asChild>
        <Button
          aria-expanded={open}
          aria-label="Profile method scope"
          className="w-full justify-between sm:w-72"
          role="combobox"
          title="Filter to profiled method-scope entries. This only lists nodes created as method scopes."
          type="button"
          variant="outline"
        >
          <span className="min-w-0 flex-1 truncate text-left">
            {selectedOption?.label ?? "All method scopes"}
          </span>
          <ChevronsUpDown className="ml-2 size-4 shrink-0 text-muted-foreground" />
        </Button>
      </PopoverTrigger>
      <PopoverContent align="end" className="w-[22rem] p-0">
        <Command>
          <CommandInput aria-label="Search method scopes" placeholder="Search method scopes" />
          <CommandList>
            <CommandEmpty>No method scopes found.</CommandEmpty>
            <CommandGroup>
              <CommandItem
                aria-selected={value.length === 0}
                data-checked={value.length === 0}
                onSelect={() => {
                  onValueChange("");
                  setOpen(false);
                }}
                role="option"
                value="all method scopes"
              >
                All method scopes
              </CommandItem>
              {options.map((option) => (
                <CommandItem
                  aria-selected={value === option.value}
                  data-checked={value === option.value}
                  key={option.value}
                  onSelect={() => {
                    onValueChange(option.value);
                    setOpen(false);
                  }}
                  role="option"
                  value={`${option.label} ${option.value}`}
                >
                  {option.label}
                </CommandItem>
              ))}
            </CommandGroup>
          </CommandList>
        </Command>
      </PopoverContent>
    </Popover>
  );
}

function ProfileSummaryPill({
  label,
  value,
}: {
  label: string;
  value: string;
}) {
  return (
    <span className="inline-flex h-8 items-center gap-1 rounded-full border bg-background/80 px-2.5 py-1 font-mono text-[11px] text-foreground">
      <span className="text-muted-foreground">{label}</span>
      <span>{value}</span>
    </span>
  );
}

function shouldRenderWorkProfileContext(context: unknown): boolean {
  return context !== null && context !== undefined && typeof context !== "string";
}

function isWorkProfileNodeExpandable(node: WorkProfileSnapshotNode): boolean {
  return node.children.length > 0 || shouldRenderWorkProfileContext(node.context);
}

function summarizeWorkProfileContext(context: unknown): string | null {
  const displayContext = createWorkProfileContextDisplayValue(context);
  if (!shouldRenderWorkProfileContext(displayContext)) {
    return null;
  }

  if (Array.isArray(displayContext)) {
    return `context: ${displayContext.length} item${displayContext.length === 1 ? "" : "s"}`;
  }

  if (typeof displayContext === "object") {
    const keys = Object.keys(displayContext as Record<string, unknown>);
    return `context: ${keys.length} key${keys.length === 1 ? "" : "s"}`;
  }

  return `context: ${String(displayContext)}`;
}

function normalizeWorkProfileSearchQuery(value: string): string {
  return value.trim().toLowerCase();
}

function filterWorkProfile(
  profile: WorkProfileSnapshot | null | undefined,
  query: string,
  options: {
    hotspotActive: boolean;
    hotspotNodeIds?: ReadonlySet<string>;
    instrumentationFilter: WorkProfileInstrumentationFilter;
    keepAncestors: boolean;
    methodScopeSelection?: WorkProfileMethodScopeSelection | null;
  }
): WorkProfileSearchResult | null {
  const normalizedQuery = normalizeWorkProfileSearchQuery(query);
  const searchActive = normalizedQuery.length > 0;
  const methodScopeRootNodeIds = options.methodScopeSelection?.nodeIds ?? [];
  const methodScopeActive = methodScopeRootNodeIds.length > 0;
  const instrumentationFilterActive = options.instrumentationFilter !== "all";
  const scopeOnlyFilter = methodScopeActive && !searchActive && !options.hotspotActive && !instrumentationFilterActive;
  if (!profile || (!searchActive && !options.hotspotActive && !methodScopeActive && !instrumentationFilterActive)) {
    return null;
  }

  const expandableNodeIds: string[] = [];
  const matchedNodeIds = new Set<string>();
  const visibleNodeIds = new Set<string>();
  let matchedNodeCount = 0;
  const visit = (node: WorkProfileSnapshotNode, nodeId: string): boolean => {
    const searchMatched = searchActive && createWorkProfileNodeSearchText(node).includes(normalizedQuery);
    const hotspotMatched = options.hotspotActive && options.hotspotNodeIds?.has(nodeId) === true;
    const instrumentationMatched = options.instrumentationFilter === "all"
      || (options.instrumentationFilter === "sql" && isWorkProfileSqlNode(node))
      || (options.instrumentationFilter === "http" && isWorkProfileHttpNode(node));
    const inSelectedSubtree = !methodScopeActive || methodScopeRootNodeIds.some((selectedNodeId) =>
      isWorkProfileNodeWithinSelectedScope(nodeId, selectedNodeId)
    );
    const onSelectedPath = methodScopeActive && methodScopeRootNodeIds.some((selectedNodeId) =>
      isWorkProfileNodeOnSelectedScopePath(nodeId, selectedNodeId)
    );
    const matchedSelf = scopeOnlyFilter
      ? inSelectedSubtree
      : inSelectedSubtree
        && (!searchActive || searchMatched)
        && (!options.hotspotActive || hotspotMatched)
        && instrumentationMatched;
    let hasVisibleDescendant = false;

    node.children.forEach((child, index) => {
      if (visit(child, `${nodeId}.${index}`)) {
        hasVisibleDescendant = true;
      }
    });

    const visibleSelf = scopeOnlyFilter ? inSelectedSubtree : matchedSelf;
    const visibleNode = visibleSelf || (
      options.keepAncestors
      && hasVisibleDescendant
      && (inSelectedSubtree || onSelectedPath)
    );

    if (!visibleNode && !hasVisibleDescendant) {
      return false;
    }

    if (visibleNode) {
      visibleNodeIds.add(nodeId);
    }

    if (matchedSelf) {
      matchedNodeCount += 1;
      matchedNodeIds.add(nodeId);
    }

    if (visibleNode && isWorkProfileNodeExpandable(node)) {
      expandableNodeIds.push(nodeId);
    }

    return visibleNode || hasVisibleDescendant;
  };

  visit(profile.root, "root");

  return {
    expandableNodeIds,
    matchedNodeCount,
    matchedNodeIds,
    visibleNodeIds,
  };
}

function collectWorkProfileMethodScopeOptions(
  profile: WorkProfileSnapshot | null | undefined
): WorkProfileMethodScopeOption[] {
  if (!profile) {
    return [];
  }

  const identitiesByShortLabel = new Map<string, Set<string>>();
  const methodScopesByIdentity = new Map<string, WorkProfileMethodScopeEntry>();

  const visit = (node: WorkProfileSnapshotNode, nodeId: string) => {
    if (nodeId !== "root") {
      const methodScope = getWorkProfileMethodScope(node);
      if (methodScope) {
        let identities = identitiesByShortLabel.get(methodScope.shortLabel);
        if (!identities) {
          identities = new Set<string>();
          identitiesByShortLabel.set(methodScope.shortLabel, identities);
        }

        identities.add(methodScope.value);
        methodScopesByIdentity.set(methodScope.value, methodScope);
      }
    }

    node.children.forEach((child, index) => {
      visit(child, `${nodeId}.${index}`);
    });
  };

  visit(profile.root, "root");

  return [...methodScopesByIdentity.values()]
    .map((methodScope) => ({
      label: (identitiesByShortLabel.get(methodScope.shortLabel)?.size ?? 0) > 1
        ? methodScope.value
        : methodScope.shortLabel,
      value: methodScope.value,
    }))
    .sort((left, right) => left.label.localeCompare(right.label));
}

function findWorkProfileMethodScopeSelection(
  profile: WorkProfileSnapshot | null | undefined,
  identity: string
): WorkProfileMethodScopeSelection | null {
  if (!profile || identity.length === 0) {
    return null;
  }

  const nodeIds: string[] = [];

  const visit = (node: WorkProfileSnapshotNode, nodeId: string) => {
    if (nodeId !== "root") {
      const methodScope = getWorkProfileMethodScope(node);
      if (methodScope?.value === identity) {
        nodeIds.push(nodeId);
      }
    }

    node.children.forEach((child, index) => {
      visit(child, `${nodeId}.${index}`);
    });
  };

  visit(profile.root, "root");
  if (nodeIds.length === 0) {
    return null;
  }

  return {
    nodeIds,
  };
}

function isWorkProfileNodeWithinSelectedScope(nodeId: string, selectedNodeId: string): boolean {
  return nodeId === selectedNodeId || nodeId.startsWith(`${selectedNodeId}.`);
}

function isWorkProfileNodeOnSelectedScopePath(nodeId: string, selectedNodeId: string): boolean {
  return nodeId === selectedNodeId || selectedNodeId.startsWith(`${nodeId}.`);
}

function createWorkProfileSearchTerms(value: string): string[] {
  const normalizedValue = normalizeWorkProfileSearchQuery(value);
  if (!normalizedValue) {
    return [];
  }

  return [...new Set(normalizedValue.split(/\s+/).filter(Boolean))]
    .sort((left, right) => right.length - left.length);
}

function areWorkProfileNodeIdSetsEqual(
  left: ReadonlySet<string>,
  right: ReadonlySet<string>
) {
  if (left.size !== right.size) {
    return false;
  }

  for (const value of left) {
    if (!right.has(value)) {
      return false;
    }
  }

  return true;
}

function createWorkProfileNodeSearchText(node: WorkProfileSnapshotNode): string {
  const parts = [node.label, formatProfileMetricType(node.metricType)];
  if (shouldRenderWorkProfileContext(node.context)) {
    parts.push(formatProfileContext(node.context));
  }

  return parts.join("\n").toLowerCase();
}

type WorkProfileSqlCommandContext = {
  CommandType?: unknown;
  Database?: unknown;
  HasTransaction?: unknown;
  Operation?: unknown;
  ParameterCount?: unknown;
  Parameters?: unknown;
  Provider?: unknown;
  Statement?: unknown;
  StatementKind?: unknown;
};

type WorkProfileSqlParameterContext = {
  Direction?: unknown;
  IsRedacted?: unknown;
  Name?: unknown;
  Type?: unknown;
  Value?: unknown;
};

type NormalizedWorkProfileSqlCommand = {
  database: string | null;
  hasTransaction: boolean;
  label: string;
  nodeMilliseconds: number;
  operation: string;
  parameters: NormalizedWorkProfileSqlParameter[];
  statement: string;
  statementKind: string;
  treeMilliseconds: number;
};

type NormalizedWorkProfileSqlParameter = {
  direction: string;
  isRedacted: boolean;
  name: string;
  type: string;
  value: unknown;
};

export function createWorkProfileSqlBatch(
  profile: WorkProfileSnapshot | null | undefined
): WorkProfileSqlBatch | null {
  if (!profile) {
    return null;
  }

  const commands = collectWorkProfileSqlCommands(profile.root);
  if (commands.length === 0) {
    return null;
  }

  const parameterCount = commands.reduce((count, command) => count + command.parameters.length, 0);
  const redactedParameterCount = commands.reduce(
    (count, command) => count + command.parameters.filter((parameter) => parameter.isRedacted).length,
    0
  );
  const replayableLines = [
    "-- Generated from Workable SQL profile nodes.",
    "-- Replace redacted values before replaying this batch if exact fidelity matters.",
    "-- Statements are separated with GO so captured parameter names can be declared directly.",
    "SET NOCOUNT ON;",
  ];
  const originalExecutionLines = [
    "-- Generated from Workable SQL profile nodes.",
    "-- This view preserves the captured parameterized execution shape for each command.",
    "SET NOCOUNT ON;",
  ];

  commands.forEach((command, commandIndex) => {
    replayableLines.push("");
    replayableLines.push(...createWorkProfileReplayableSqlBatchSection(command, commandIndex));
    originalExecutionLines.push("");
    originalExecutionLines.push(...createWorkProfileOriginalSqlBatchSection(command, commandIndex));
  });

  return {
    originalExecutionBatch: originalExecutionLines.join("\n"),
    parameterCount,
    replayableBatch: replayableLines.join("\n"),
    redactedParameterCount,
    statementCount: commands.length,
  };
}

function collectWorkProfileSqlCommands(node: WorkProfileSnapshotNode): NormalizedWorkProfileSqlCommand[] {
  const commands: NormalizedWorkProfileSqlCommand[] = [];

  const visit = (current: WorkProfileSnapshotNode) => {
    const command = normalizeWorkProfileSqlCommand(current);
    if (command) {
      commands.push(command);
    }

    current.children.forEach(visit);
  };

  visit(node);
  return commands;
}

function normalizeWorkProfileSqlCommand(
  node: WorkProfileSnapshotNode
): NormalizedWorkProfileSqlCommand | null {
  if (!isWorkProfileSqlNode(node)) {
    return null;
  }

  const context = findWorkProfileSqlCommandContext(node.context);
  if (!context) {
    return null;
  }

  const operation = getWorkProfileObjectString(context, "Operation");
  const statement = getWorkProfileObjectString(context, "Statement");
  const statementKind = getWorkProfileObjectString(context, "StatementKind");

  return {
    database: getWorkProfileObjectString(context, "Database"),
    hasTransaction: getWorkProfileObjectBoolean(context, "HasTransaction"),
    label: node.label,
    nodeMilliseconds: node.nodeMilliseconds,
    operation: operation ?? "Command",
    parameters: normalizeWorkProfileSqlParameters(getWorkProfileObjectValue(context, "Parameters")),
    statement: statement ?? "<empty>",
    statementKind: statementKind ?? "UNKNOWN",
    treeMilliseconds: node.treeMilliseconds,
  };
}

function findWorkProfileSqlCommandContext(
  value: unknown
): WorkProfileSqlCommandContext | null {
  if (!value || typeof value !== "object") {
    return null;
  }

  if (Array.isArray(value)) {
    for (const item of value) {
      const match = findWorkProfileSqlCommandContext(item);
      if (match) {
        return match;
      }
    }

    return null;
  }

  const context = value as WorkProfileSqlCommandContext & Record<string, unknown>;
  const provider = getWorkProfileObjectString(context, "Provider");
  const statement = getWorkProfileObjectString(context, "Statement");
  if (provider === "Microsoft.Data.SqlClient" && statement !== null) {
    return context;
  }

  for (const nestedValue of Object.values(context)) {
    const match = findWorkProfileSqlCommandContext(nestedValue);
    if (match) {
      return match;
    }
  }

  return null;
}

function normalizeWorkProfileSqlParameters(value: unknown): NormalizedWorkProfileSqlParameter[] {
  if (!Array.isArray(value)) {
    return [];
  }

  return value
    .map((parameter, index) => normalizeWorkProfileSqlParameter(parameter, index))
    .filter((parameter): parameter is NormalizedWorkProfileSqlParameter => parameter !== null);
}

function normalizeWorkProfileSqlParameter(
  value: unknown,
  index: number
): NormalizedWorkProfileSqlParameter | null {
  if (!value || typeof value !== "object" || Array.isArray(value)) {
    return null;
  }

  const parameter = value as WorkProfileSqlParameterContext;
  const direction = getWorkProfileObjectString(parameter, "Direction");
  const type = getWorkProfileObjectString(parameter, "Type");
  const parameterValue = getWorkProfileObjectValue(parameter, "Value");
  return {
    direction: direction ?? "Input",
    isRedacted: getWorkProfileObjectBoolean(parameter, "IsRedacted"),
    name: normalizeWorkProfileSqlParameterName(getWorkProfileObjectValue(parameter, "Name"), index),
    type: type ?? inferWorkProfileSqlParameterType(parameterValue),
    value: parameterValue,
  };
}

function normalizeWorkProfileSqlParameterName(value: unknown, index: number): string {
  if (typeof value !== "string" || value.trim().length === 0) {
    return `@Parameter${index + 1}`;
  }

  return value.trim().startsWith("@")
    ? value.trim()
    : `@${value.trim()}`;
}

function createWorkProfileContextDisplayValue(context: unknown): unknown {
  if (!context || typeof context !== "object") {
    return context;
  }

  if (Array.isArray(context)) {
    return context.map((item) => createWorkProfileContextDisplayValue(item));
  }

  const record = context as Record<string, unknown>;
  if (isDirectWorkProfileSqlCommandContext(record)) {
    return createWorkProfileSqlDisplayContext(record);
  }

  return Object.fromEntries(
    Object.entries(record).map(([key, value]) => [key, createWorkProfileContextDisplayValue(value)])
  );
}

function isDirectWorkProfileSqlCommandContext(value: Record<string, unknown>): boolean {
  return getWorkProfileObjectString(value, "Provider") === "Microsoft.Data.SqlClient" &&
    getWorkProfileObjectString(value, "Statement") !== null;
}

function createWorkProfileSqlDisplayContext(
  context: Record<string, unknown>
): Record<string, unknown> {
  const statement = getWorkProfileObjectString(context, "Statement") ?? "<empty>";
  const commandType = getWorkProfileObjectString(context, "CommandType") ?? "Text";
  const parameters = normalizeWorkProfileSqlParameters(getWorkProfileObjectValue(context, "Parameters"));
  const displayStatement = createWorkProfileSqlDisplayStatement(statement, commandType, parameters);

  return Object.fromEntries(
    Object.entries(context)
      .filter(([key]) => !isWorkProfileObjectKey(key, "Parameters"))
      .map(([key, value]) => [
        key,
        isWorkProfileObjectKey(key, "Statement")
          ? displayStatement
          : createWorkProfileContextDisplayValue(value),
      ])
  );
}

function createWorkProfileSqlDisplayStatement(
  statement: string,
  commandType: string,
  parameters: readonly NormalizedWorkProfileSqlParameter[]
): string {
  const trimmedStatement = statement.trimEnd();
  const baseStatement = trimmedStatement.length > 0 ? trimmedStatement : "<empty>";

  if (commandType.toLowerCase() === "text") {
    return inlineWorkProfileSqlStatementParameters(baseStatement, parameters);
  }

  return createWorkProfileExecutableSqlDisplayStatement(baseStatement, parameters);
}

function inlineWorkProfileSqlStatementParameters(
  statement: string,
  parameters: readonly NormalizedWorkProfileSqlParameter[]
): string {
  const inlinedParameters = [...parameters]
    .filter((parameter) => !isWorkProfileSqlOutputParameter(parameter))
    .sort((left, right) => right.name.length - left.name.length);

  let result = statement;
  inlinedParameters.forEach((parameter) => {
    result = replaceWorkProfileSqlParameterReferences(
      result,
      parameter.name,
      formatWorkProfileSqlLiteral(parameter)
    );
  });

  return appendWorkProfileSqlOutputComments(result, parameters);
}

function replaceWorkProfileSqlParameterReferences(
  statement: string,
  parameterName: string,
  replacement: string
): string {
  const pattern = new RegExp(
    `(^|[^@A-Za-z0-9_#$])(${escapeRegularExpression(parameterName)})(?![A-Za-z0-9_#$])(?!\\s*(?:=|OUTPUT\\b|OUT\\b))`,
    "gi"
  );

  return statement.replace(pattern, (_match, prefix: string) => `${prefix}${replacement}`);
}

function createWorkProfileExecutableSqlDisplayStatement(
  statement: string,
  parameters: readonly NormalizedWorkProfileSqlParameter[]
): string {
  if (parameters.length === 0) {
    return ensureWorkProfileSqlStatementTerminated(`EXEC ${statement}`);
  }

  const lines = [`EXEC ${statement}`];
  parameters.forEach((parameter, index) => {
    const suffix = index < parameters.length - 1 ? "," : ";";
    const outputComment = isWorkProfileSqlOutputParameter(parameter)
      ? ` /* ${parameter.direction} parameter */`
      : "";
    lines.push(
      `  ${parameter.name} = ${formatWorkProfileSqlLiteral(parameter)}${outputComment}${suffix}`
    );
  });

  return lines.join("\n");
}

function appendWorkProfileSqlOutputComments(
  statement: string,
  parameters: readonly NormalizedWorkProfileSqlParameter[]
): string {
  const outputComments = parameters
    .filter((parameter) => isWorkProfileSqlOutputParameter(parameter))
    .map((parameter) => createWorkProfileSqlOutputParameterComment(parameter));

  if (outputComments.length === 0) {
    return statement;
  }

  return `${statement}${statement.endsWith("\n") ? "" : "\n"}${outputComments.join("\n")}`;
}

function createWorkProfileSqlOutputParameterComment(
  parameter: NormalizedWorkProfileSqlParameter
): string {
  return `-- ${parameter.name} is an ${parameter.direction} parameter and remains parameterized in this preview.`;
}

function isWorkProfileObjectKey(actualKey: string, key: string): boolean {
  return actualKey === key || actualKey === toWorkProfileCamelCaseKey(key);
}

function toWorkProfileCamelCaseKey(key: string): string {
  return key.length > 0
    ? `${key.charAt(0).toLowerCase()}${key.slice(1)}`
    : key;
}

function inferWorkProfileSqlParameterType(value: unknown): string {
  if (typeof value === "number") {
    return Number.isInteger(value) ? "Int" : "Decimal";
  }

  if (typeof value === "boolean") {
    return "Bit";
  }

  return "NVarChar";
}

function createWorkProfileReplayableSqlBatchSection(
  command: NormalizedWorkProfileSqlCommand,
  commandIndex: number
): string[] {
  const lines = [
    `-- ${command.label} #${commandIndex + 1} | ${command.statementKind} | ${formatProfileMilliseconds(command.treeMilliseconds)} tree | ${formatProfileMilliseconds(command.nodeMilliseconds)} node${command.database ? ` | ${command.database}` : ""}${command.hasTransaction ? " | transaction" : ""}`,
  ];

  if (command.parameters.length > 0) {
    command.parameters.forEach((parameter) => {
      lines.push(createWorkProfileReplayableSqlVariableDeclaration(parameter));
    });
    lines.push("");
  }

  lines.push(ensureWorkProfileSqlStatementTerminated(command.statement));
  lines.push("GO");

  return lines;
}

function createWorkProfileReplayableSqlVariableDeclaration(
  parameter: NormalizedWorkProfileSqlParameter
): string {
  const sqlType = mapWorkProfileSqlTypeToDeclaration(parameter.type);
  return `DECLARE ${parameter.name} ${sqlType} = ${formatWorkProfileSqlLiteral(parameter)};`;
}

function ensureWorkProfileSqlStatementTerminated(statement: string): string {
  const trimmed = statement.trimEnd();
  if (trimmed.length === 0) {
    return "<empty>";
  }

  return trimmed.endsWith(";")
    ? trimmed
    : `${trimmed};`;
}

function createWorkProfileOriginalSqlBatchSection(
  command: NormalizedWorkProfileSqlCommand,
  commandIndex: number
): string[] {
  const lines = [
    `-- ${command.label} #${commandIndex + 1} | ${command.statementKind} | ${formatProfileMilliseconds(command.treeMilliseconds)} tree | ${formatProfileMilliseconds(command.nodeMilliseconds)} node${command.database ? ` | ${command.database}` : ""}${command.hasTransaction ? " | transaction" : ""}`,
  ];

  if (command.parameters.length > 0) {
    command.parameters.forEach((parameter, parameterIndex) => {
      lines.push(createWorkProfileOriginalSqlVariableDeclaration(parameter, commandIndex, parameterIndex));
    });
    lines.push("");
  }

  lines.push("EXEC sp_executesql");
  if (command.parameters.length === 0) {
    lines.push(`    ${formatSqlUnicodeStringLiteral(ensureWorkProfileSqlStatementTerminated(command.statement))};`);
    return lines;
  }

  lines.push(`    ${formatSqlUnicodeStringLiteral(ensureWorkProfileSqlStatementTerminated(command.statement))},`);
  lines.push(`    ${formatSqlUnicodeStringLiteral(createWorkProfileSqlParameterDefinitionList(command.parameters))},`);

  const assignments = command.parameters.map((parameter, parameterIndex) =>
    `    ${parameter.name} = ${createWorkProfileOriginalSqlVariableName(commandIndex, parameter, parameterIndex)}${isWorkProfileSqlOutputParameter(parameter) ? " OUTPUT" : ""}`
  );

  assignments.forEach((assignment, assignmentIndex) => {
    lines.push(`${assignment}${assignmentIndex < assignments.length - 1 ? "," : ";"}`);
  });

  return lines;
}

function createWorkProfileOriginalSqlVariableDeclaration(
  parameter: NormalizedWorkProfileSqlParameter,
  commandIndex: number,
  parameterIndex: number
): string {
  const sqlType = mapWorkProfileSqlTypeToDeclaration(parameter.type);
  return `DECLARE ${createWorkProfileOriginalSqlVariableName(commandIndex, parameter, parameterIndex)} ${sqlType} = ${formatWorkProfileSqlLiteral(parameter)};`;
}

function createWorkProfileOriginalSqlVariableName(
  commandIndex: number,
  parameter: NormalizedWorkProfileSqlParameter,
  parameterIndex: number
): string {
  const rawName = parameter.name.startsWith("@") ? parameter.name.slice(1) : parameter.name;
  const sanitized = rawName.replace(/[^A-Za-z0-9_]/g, "");
  const suffix = sanitized.length > 0
    ? (/^[0-9]/.test(sanitized) ? `p${sanitized}` : sanitized)
    : `Parameter${parameterIndex + 1}`;
  return `@cmd${commandIndex + 1}_${suffix}`;
}

function createWorkProfileSqlParameterDefinitionList(
  parameters: readonly NormalizedWorkProfileSqlParameter[]
): string {
  return parameters
    .map((parameter) =>
      `${parameter.name} ${mapWorkProfileSqlTypeToDeclaration(parameter.type)}${isWorkProfileSqlOutputParameter(parameter) ? " OUTPUT" : ""}`)
    .join(", ");
}

function isWorkProfileSqlOutputParameter(parameter: NormalizedWorkProfileSqlParameter): boolean {
  return parameter.direction === "Output" ||
    parameter.direction === "InputOutput" ||
    parameter.direction === "ReturnValue";
}

function mapWorkProfileSqlTypeToDeclaration(type: string): string {
  switch (type) {
    case "BigInt":
      return "bigint";
    case "Binary":
    case "Image":
    case "Timestamp":
    case "VarBinary":
      return "varbinary(max)";
    case "Bit":
      return "bit";
    case "Date":
      return "date";
    case "DateTime":
      return "datetime";
    case "DateTime2":
      return "datetime2(7)";
    case "DateTimeOffset":
      return "datetimeoffset(7)";
    case "Decimal":
    case "Money":
    case "SmallMoney":
      return "decimal(38, 10)";
    case "Float":
      return "float";
    case "Int":
      return "int";
    case "NChar":
    case "NText":
    case "NVarChar":
    case "Xml":
      return "nvarchar(max)";
    case "Real":
      return "real";
    case "SmallDateTime":
      return "smalldatetime";
    case "SmallInt":
      return "smallint";
    case "Text":
    case "VarChar":
      return "varchar(max)";
    case "Time":
      return "time(7)";
    case "TinyInt":
      return "tinyint";
    case "UniqueIdentifier":
      return "uniqueidentifier";
    default:
      return "nvarchar(max)";
  }
}

function formatWorkProfileSqlLiteral(parameter: NormalizedWorkProfileSqlParameter): string {
  if (parameter.isRedacted) {
    return isWorkProfileSqlTextualType(parameter.type)
      ? "N'<redacted>' /* redacted in profile */"
      : "NULL /* redacted in profile */";
  }

  if (parameter.value === null || parameter.value === undefined) {
    return "NULL";
  }

  if (typeof parameter.value === "number") {
    return Number.isFinite(parameter.value) ? parameter.value.toString() : "NULL";
  }

  if (typeof parameter.value === "boolean") {
    return parameter.value ? "1" : "0";
  }

  if (typeof parameter.value === "string") {
    if (isWorkProfileSqlBinaryType(parameter.type)) {
      if (/^0x[0-9A-F]+$/i.test(parameter.value)) {
        return parameter.value;
      }

      return `NULL /* binary parameter value could not be reconstructed from profile: ${parameter.value.replace(/\*\//g, "* /")} */`;
    }

    if (isWorkProfileSqlQuotedValueType(parameter.type)) {
      return `'${escapeWorkProfileSqlLiteral(parameter.value)}'`;
    }

    return formatSqlUnicodeStringLiteral(parameter.value);
  }

  return formatSqlUnicodeStringLiteral(String(parameter.value));
}

function isWorkProfileSqlTextualType(type: string): boolean {
  return type === "Char" ||
    type === "NChar" ||
    type === "NText" ||
    type === "NVarChar" ||
    type === "Text" ||
    type === "VarChar" ||
    type === "Xml";
}

function isWorkProfileSqlQuotedValueType(type: string): boolean {
  return type === "Date" ||
    type === "DateTime" ||
    type === "DateTime2" ||
    type === "DateTimeOffset" ||
    type === "SmallDateTime" ||
    type === "Time" ||
    type === "UniqueIdentifier";
}

function isWorkProfileSqlBinaryType(type: string): boolean {
  return type === "Binary" ||
    type === "Image" ||
    type === "Timestamp" ||
    type === "VarBinary";
}

function formatSqlUnicodeStringLiteral(value: string): string {
  return `N'${escapeWorkProfileSqlLiteral(value)}'`;
}

function escapeWorkProfileSqlLiteral(value: string): string {
  return value.replace(/'/g, "''");
}

function isWorkProfileSqlNode(node: WorkProfileSnapshotNode): boolean {
  return node.instrumentation === "sql.client";
}

function isWorkProfileHttpNode(node: WorkProfileSnapshotNode): boolean {
  return node.instrumentation === "http.client";
}

function getWorkProfileObjectValue<TRecord extends Record<string, unknown>>(
  record: TRecord,
  key: string
): unknown {
  if (key in record) {
    return record[key];
  }

  const camelKey = toWorkProfileCamelCaseKey(key);
  return camelKey in record
    ? record[camelKey]
    : undefined;
}

function getWorkProfileObjectString<TRecord extends Record<string, unknown>>(
  record: TRecord,
  key: string
): string | null {
  const value = getWorkProfileObjectValue(record, key);
  return typeof value === "string" && value.length > 0
    ? value
    : null;
}

function getWorkProfileObjectBoolean<TRecord extends Record<string, unknown>>(
  record: TRecord,
  key: string
): boolean {
  return getWorkProfileObjectValue(record, key) === true;
}

function getWorkProfileMethodScope(node: WorkProfileSnapshotNode): WorkProfileMethodScopeEntry | null {
  if (node.metricType !== "MethodScope") {
    return null;
  }

  const identity = node.label.startsWith("Executing ")
    ? node.label.slice("Executing ".length)
    : node.label;
  const methodSeparatorIndex = identity.lastIndexOf(".");
  if (methodSeparatorIndex <= 0 || methodSeparatorIndex >= identity.length - 1) {
    return null;
  }

  const typeName = identity.slice(0, methodSeparatorIndex);
  const methodName = identity.slice(methodSeparatorIndex + 1);
  const shortTypeName = typeName.split(/[.+]/).at(-1) ?? typeName;
  const shortLabel = `${shortTypeName}.${methodName}`;
  return {
    label: shortLabel,
    shortLabel,
    value: identity,
  };
}

function formatProfileContext(context: unknown): string {
  const displayContext = createWorkProfileContextDisplayValue(context);
  if (typeof displayContext === "string") {
    return displayContext;
  }

  if (typeof displayContext === "number" || typeof displayContext === "boolean") {
    return String(displayContext);
  }

  try {
    return JSON.stringify(displayContext, null, 2) ?? "null";
  } catch {
    return String(displayContext);
  }
}

function createWorkProfileEmptyStateMessage(
  searchQuery: string,
  hotspotActive: boolean,
  selectedMethodScopeLabel: string | null,
  instrumentationFilter: WorkProfileInstrumentationFilter
): string {
  const filteredNodeLabel = instrumentationFilter === "sql"
    ? "SQL profile nodes"
    : instrumentationFilter === "http"
      ? "HTTP request profile nodes"
      : null;

  if (filteredNodeLabel) {
    if (searchQuery.length > 0 && hotspotActive && selectedMethodScopeLabel) {
      return `No ${filteredNodeLabel} matched "${searchQuery}" within the selected hotspots for ${selectedMethodScopeLabel}.`;
    }

    if (searchQuery.length > 0 && hotspotActive) {
      return `No ${filteredNodeLabel} matched "${searchQuery}" within the selected hotspots.`;
    }

    if (searchQuery.length > 0 && selectedMethodScopeLabel) {
      return `No ${filteredNodeLabel} matched "${searchQuery}" within ${selectedMethodScopeLabel}.`;
    }

    if (searchQuery.length > 0) {
      return `No ${filteredNodeLabel} matched "${searchQuery}".`;
    }

    if (hotspotActive && selectedMethodScopeLabel) {
      return `No ${filteredNodeLabel} matched the selected hotspots within ${selectedMethodScopeLabel}.`;
    }

    if (hotspotActive) {
      return `No ${filteredNodeLabel} matched the selected hotspots.`;
    }

    if (selectedMethodScopeLabel) {
      return `No ${filteredNodeLabel} matched within ${selectedMethodScopeLabel}.`;
    }

    return `No ${filteredNodeLabel} matched the active filters.`;
  }

  if (searchQuery.length > 0 && hotspotActive && selectedMethodScopeLabel) {
    return `No profile nodes matched "${searchQuery}" within the selected hotspots for ${selectedMethodScopeLabel}.`;
  }

  if (searchQuery.length > 0 && hotspotActive) {
    return `No profile nodes matched "${searchQuery}" within the selected hotspots.`;
  }

  if (searchQuery.length > 0 && selectedMethodScopeLabel) {
    return `No profile nodes matched "${searchQuery}" within ${selectedMethodScopeLabel}.`;
  }

  if (searchQuery.length > 0) {
    return `No profile nodes matched "${searchQuery}".`;
  }

  if (hotspotActive && selectedMethodScopeLabel) {
    return `No profile nodes matched the selected hotspots within ${selectedMethodScopeLabel}.`;
  }

  if (hotspotActive) {
    return "No profile nodes matched the selected hotspots.";
  }

  if (selectedMethodScopeLabel) {
    return `No profile nodes matched within ${selectedMethodScopeLabel}.`;
  }

  return "No profile nodes matched the active filters.";
}

function describeWorkProfileHotspotMode(mode: WorkProfileHotspotMode): string {
  switch (mode) {
    case "off":
      return "Turn off hotspot filtering and show the full profile tree.";
    case "tree":
      return "Tree time uses a node's total time including all descendant nodes. Use it to find slow regions of work.";
    case "node":
      return "Node time uses only the time spent in the node itself, excluding descendants. Use it to find slow individual steps.";
  }
}

function describeWorkProfileHotspotThreshold(
  mode: WorkProfileHotspotMode,
  threshold: WorkProfileHotspotThreshold
): string {
  if (mode === "off") {
    return "Enable Tree time or Node time to filter the profile down to slow sections.";
  }

  const thresholdLabel = hotspotThresholds.find((option) => option.id === threshold)?.label ?? threshold;
  return `Show ${mode === "tree" ? "slow regions" : "slow individual steps"} that match ${thresholdLabel}.`;
}

function resolveHotspotThresholdMilliseconds(
  threshold: WorkProfileHotspotThreshold,
  totalMilliseconds: number
) {
  switch (threshold) {
    case "pct10":
      return totalMilliseconds * 0.1;
    case "pct25":
      return totalMilliseconds * 0.25;
    case "ms25":
      return 25;
    case "ms50":
      return 50;
    case "top5":
      return 0;
  }
}

function normalizeProfileJsonValue(context: unknown): { ok: true; value: unknown } | { ok: false } {
  try {
    return {
      ok: true,
      value: JSON.parse(JSON.stringify(context)) as unknown,
    };
  } catch {
    return { ok: false };
  }
}

function ProfileJsonValue({
  indent = 0,
  searchQuery,
  value,
}: {
  indent?: number;
  searchQuery?: string;
  value: unknown;
}) {
  if (value === null || value === undefined) {
    return <span className="text-muted-foreground">null</span>;
  }

  if (typeof value === "string") {
    return (
      <span className="text-emerald-300">
        {renderHighlightedText(JSON.stringify(value), searchQuery)}
      </span>
    );
  }

  if (typeof value === "number") {
    return (
      <span className="text-amber-300">
        {renderHighlightedText(String(value), searchQuery)}
      </span>
    );
  }

  if (typeof value === "boolean") {
    return (
      <span className="text-sky-300">
        {renderHighlightedText(String(value), searchQuery)}
      </span>
    );
  }

  if (Array.isArray(value)) {
    if (value.length === 0) {
      return <span>[]</span>;
    }

    return (
      <>
        <span>[</span>
        {"\n"}
        {value.map((item, index) => (
          <Fragment key={index}>
            {profileJsonIndent(indent + 1)}
            <ProfileJsonValue indent={indent + 1} searchQuery={searchQuery} value={item} />
            {index < value.length - 1 && <span>,</span>}
            {"\n"}
          </Fragment>
        ))}
        {profileJsonIndent(indent)}
        <span>]</span>
      </>
    );
  }

  if (typeof value === "object") {
    const entries = Object.entries(value as Record<string, unknown>);
    if (entries.length === 0) {
      return <span>{"{}"}</span>;
    }

    return (
      <>
        <span>{"{"}</span>
        {"\n"}
        {entries.map(([key, item], index) => (
          <Fragment key={key}>
            {profileJsonIndent(indent + 1)}
            <span className="text-violet-300">
              {renderHighlightedText(JSON.stringify(key), searchQuery)}
            </span>
            <span>: </span>
            <ProfileJsonValue indent={indent + 1} searchQuery={searchQuery} value={item} />
            {index < entries.length - 1 && <span>,</span>}
            {"\n"}
          </Fragment>
        ))}
        {profileJsonIndent(indent)}
        <span>{"}"}</span>
      </>
    );
  }

  return <span>{renderHighlightedText(JSON.stringify(value), searchQuery)}</span>;
}

function profileJsonIndent(level: number) {
  return "  ".repeat(level);
}

function renderHighlightedText(value: string, query?: string): ReactNode {
  const searchTerms = createWorkProfileSearchTerms(query ?? "");
  if (value.length === 0 || searchTerms.length === 0) {
    return value;
  }

  const pattern = new RegExp(searchTerms.map(escapeRegularExpression).join("|"), "gi");
  const nodes: ReactNode[] = [];
  let match: RegExpExecArray | null;
  let lastIndex = 0;

  while ((match = pattern.exec(value)) !== null) {
    const matchIndex = match.index;
    if (matchIndex > lastIndex) {
      nodes.push(value.slice(lastIndex, matchIndex));
    }

    nodes.push(
      <mark
        className="rounded-sm bg-primary/25 px-0.5 text-inherit ring-1 ring-primary/35"
        key={`${matchIndex}:${match[0]}`}
      >
        {value.slice(matchIndex, matchIndex + match[0].length)}
      </mark>
    );
    lastIndex = matchIndex + match[0].length;
  }

  if (nodes.length === 0) {
    return value;
  }

  if (lastIndex < value.length) {
    nodes.push(value.slice(lastIndex));
  }

  return nodes;
}

function escapeRegularExpression(value: string) {
  return value.replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
}

function formatProfileMetricType(metricType: WorkProfileMetricType): string {
  switch (metricType) {
    case "MethodScope":
      return "Method";
    case "Scope":
      return "Scope";
    case "Timing":
      return "Timing";
    case "Metric":
      return "Metric";
  }
}

function formatProfileMilliseconds(value: number): string {
  if (value >= 1000) {
    const seconds = value / 1000;
    return `${seconds % 1 === 0 ? seconds.toFixed(0) : seconds.toFixed(1)}s`;
  }

  return `${value}ms`;
}

function formatProfilePercentage(value: number): string {
  const percent = value * 100;
  if (!Number.isFinite(percent)) {
    return "0%";
  }

  if (percent >= 10 || percent % 1 === 0) {
    return `${percent.toFixed(0)}%`;
  }

  return `${percent.toFixed(1)}%`;
}

function formatProfileTimestamp(value: string): string {
  const date = new Date(value);
  if (Number.isNaN(date.valueOf())) {
    return value;
  }

  return date.toLocaleString(undefined, {
    dateStyle: "medium",
    timeStyle: "short",
  });
}

function profileMetricTone(metricType: WorkProfileMetricType): string {
  switch (metricType) {
    case "MethodScope":
      return semanticBadgeToneClass("info");
    case "Scope":
      return semanticBadgeToneClass("neutral");
    case "Timing":
      return semanticBadgeToneClass("warning");
    case "Metric":
      return semanticBadgeToneClass("success");
  }
}
