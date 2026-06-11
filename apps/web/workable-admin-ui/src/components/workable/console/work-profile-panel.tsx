"use client";

import { ChevronRight, ChevronsUpDown, Maximize2, Minimize2 } from "lucide-react";
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

type WorkProfileMethodScopeOption = {
  label: string;
  value: string;
};

type WorkProfileMethodScopeEntry = WorkProfileMethodScopeOption & {
  shortLabel: string;
};

type WorkProfileMethodScopeSelection = {
  node: WorkProfileSnapshotNode;
  nodeId: string;
  option: WorkProfileMethodScopeEntry;
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
  onClose,
  onViewStateChange,
  profile,
  viewState,
}: {
  onClose: () => void;
  onViewStateChange: (shape: WorkComponentShape) => void;
  profile?: WorkProfileSnapshot | null;
  viewState: WorkComponentShape;
}) {
  const summary = useMemo(() => summarizeWorkProfile(profile), [profile]);
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
  const filterResult = useMemo(
    () => filterWorkProfile(profile, deferredSearchText, {
      hotspotActive: hotspotMode !== "off",
      hotspotNodeIds: hotspotResult?.matchedNodeIds,
      keepAncestors,
      methodScopeSelection: selectedMethodScope,
    }),
    [deferredSearchText, hotspotMode, hotspotResult, keepAncestors, profile, selectedMethodScope]
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
  const hotspotActive = hotspotMode !== "off";
  const methodScopeActive = selectedMethodScopeIdentity.length > 0;
  const selectedMethodScopeLabel = methodScopeOptions.find((option) => option.value === selectedMethodScopeIdentity)?.label ?? null;
  const keepAncestorsLabel = keepAncestors ? "Ancestors shown" : "Ancestors hidden";
  const filterCountLabel = hotspotActive && activeSearchText.length === 0 && !methodScopeActive
    ? "Hotspots"
    : methodScopeActive && activeSearchText.length === 0 && !hotspotActive
      ? "Method scopes"
      : "Matches";

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
    <PanelShell
      className="flex min-h-0 flex-1 flex-col overflow-hidden"
      contentClassName={viewState === "compact"
        ? "hidden"
        : "mt-4 flex min-h-0 flex-1 flex-col overflow-hidden"}
      description="Per-iteration profile tree with timings, scopes, and captured context."
      actions={viewState !== "compact" && profile ? (
        <WorkProfilePanelActions
          onCollapseAll={collapseAll}
          onExpandAll={expandAll}
        />
      ) : null}
      onClose={onClose}
      onViewStateChange={onViewStateChange}
      supportedViewStates={["compact", "standard", "detailed"]}
      title={<WorkProfilePanelTitle profile={profile} summary={summary} viewState={viewState} />}
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
            Profiling was not enabled for this iteration, so no profile tree was captured.
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
                <div className="flex flex-col gap-3 rounded-xl border bg-muted/10 p-3 lg:flex-row lg:flex-wrap lg:items-center">
                  <div className="flex flex-col gap-2 sm:flex-row sm:flex-wrap sm:items-center">
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
                  <div className="flex w-full flex-col gap-2 sm:flex-row lg:ml-auto lg:w-auto lg:min-w-[24rem] lg:max-w-[34rem] lg:flex-1 lg:justify-end">
                    <div className="w-full lg:max-w-sm lg:flex-1 lg:min-w-[16rem]">
                      <Input
                        aria-label="Search profile nodes"
                        onChange={(event) => setSearchText(event.target.value)}
                        placeholder="Filter profile nodes"
                        type="search"
                        value={searchText}
                      />
                    </div>
                    <Button
                      aria-pressed={keepAncestors}
                      className="whitespace-nowrap sm:self-start lg:ml-auto"
                      onClick={() => setKeepAncestors((current) => !current)}
                      size="default"
                      title={keepAncestors
                        ? "Ancestor nodes are currently visible while filtering so matches stay in context."
                        : "Ancestor nodes are currently hidden so the filtered view only shows direct matches in the active scope."}
                      type="button"
                      variant={keepAncestors ? "secondary" : "outline"}
                    >
                      {keepAncestorsLabel}
                    </Button>
                  </div>
                </div>
              </div>
              {filterResult && filterResult.matchedNodeCount === 0 ? (
                <ConsoleEmptyState className="rounded-xl border bg-muted/10" fill padding="spacious">
                  {createWorkProfileEmptyStateMessage(activeSearchText, hotspotActive, selectedMethodScopeLabel)}
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
  );
}

function WorkProfilePanelTitle({
  profile,
  summary,
  viewState,
}: {
  profile?: WorkProfileSnapshot | null;
  summary: WorkProfileSummary | null;
  viewState: WorkComponentShape;
}) {
  return (
    <>
      <span>Profile</span>
      {viewState === "compact" ? (
        profile && summary ? (
          <>
            <ProfileSummaryPill label="Tree" value={formatProfileMilliseconds(summary.totalTreeMilliseconds)} />
            <ProfileSummaryPill label="Nodes" value={summary.nodeCount.toLocaleString()} />
            <ProfileSummaryPill label="Depth" value={summary.maxDepth.toString()} />
          </>
        ) : (
          <ProfileSummaryPill label="State" value="Unavailable" />
        )
      ) : null}
    </>
  );
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
  const normalizedContext = useMemo(() => normalizeProfileJsonValue(context), [context]);
  const formattedContext = useMemo(() => formatProfileContext(context), [context]);
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
          <span>{summarizeWorkProfileContext(context) ?? "context"}</span>
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
  if (!shouldRenderWorkProfileContext(context)) {
    return null;
  }

  if (Array.isArray(context)) {
    return `context: ${context.length} item${context.length === 1 ? "" : "s"}`;
  }

  if (typeof context === "object") {
    const keys = Object.keys(context as Record<string, unknown>);
    return `context: ${keys.length} key${keys.length === 1 ? "" : "s"}`;
  }

  return `context: ${String(context)}`;
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
    keepAncestors: boolean;
    methodScopeSelection?: WorkProfileMethodScopeSelection | null;
  }
): WorkProfileSearchResult | null {
  const normalizedQuery = normalizeWorkProfileSearchQuery(query);
  const searchActive = normalizedQuery.length > 0;
  const methodScopeRootNodeId = options.methodScopeSelection?.nodeId ?? null;
  const methodScopeActive = Boolean(methodScopeRootNodeId);
  const scopeOnlyFilter = methodScopeActive && !searchActive && !options.hotspotActive;
  if (!profile || (!searchActive && !options.hotspotActive && !methodScopeActive)) {
    return null;
  }

  const expandableNodeIds: string[] = [];
  const matchedNodeIds = new Set<string>();
  const visibleNodeIds = new Set<string>();
  let matchedNodeCount = 0;
  const visit = (node: WorkProfileSnapshotNode, nodeId: string): boolean => {
    const searchMatched = searchActive && createWorkProfileNodeSearchText(node).includes(normalizedQuery);
    const hotspotMatched = options.hotspotActive && options.hotspotNodeIds?.has(nodeId) === true;
    const inSelectedSubtree = methodScopeRootNodeId === null
      || nodeId === methodScopeRootNodeId
      || nodeId.startsWith(`${methodScopeRootNodeId}.`);
    const onSelectedPath = methodScopeRootNodeId !== null
      && (nodeId === methodScopeRootNodeId || methodScopeRootNodeId.startsWith(`${nodeId}.`));
    const matchedSelf = scopeOnlyFilter
      ? nodeId === methodScopeRootNodeId
      : inSelectedSubtree && (!searchActive || searchMatched) && (!options.hotspotActive || hotspotMatched);
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

  let selection: WorkProfileMethodScopeSelection | null = null;

  const visit = (node: WorkProfileSnapshotNode, nodeId: string) => {
    if (selection) {
      return;
    }

    if (nodeId !== "root") {
      const methodScope = getWorkProfileMethodScope(node);
      if (methodScope?.value === identity) {
        selection = {
          node,
          nodeId,
          option: methodScope,
        };
        return;
      }
    }

    node.children.forEach((child, index) => {
      visit(child, `${nodeId}.${index}`);
    });
  };

  visit(profile.root, "root");
  return selection;
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
  if (typeof context === "string") {
    return context;
  }

  if (typeof context === "number" || typeof context === "boolean") {
    return String(context);
  }

  try {
    return JSON.stringify(context, null, 2) ?? "null";
  } catch {
    return String(context);
  }
}

function createWorkProfileEmptyStateMessage(
  searchQuery: string,
  hotspotActive: boolean,
  selectedMethodScopeLabel: string | null
): string {
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
