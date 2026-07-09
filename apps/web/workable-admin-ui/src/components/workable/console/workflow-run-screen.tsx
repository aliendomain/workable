"use client";

import {
  ArrowRight,
  Ban,
  ChevronDown,
  ChevronRight,
  GitBranch,
  Loader2,
  Maximize2,
  Pause,
  Play,
  Rows3,
  Rows4,
  Square,
  Workflow,
} from "lucide-react";
import type { PointerEvent as ReactPointerEvent, ReactNode, RefObject } from "react";
import { Fragment, useCallback, useEffect, useMemo, useRef, useState } from "react";
import { ConsoleEmptyState } from "@/components/features/console/empty-state";
import {
  ConsolePageLayout,
  consolePanelActionGapClassName,
} from "@/components/features/console/console-primitives";
import {
  useRegisterConsoleHeaderCapabilities,
  type ConsoleHeaderCapabilities,
} from "@/components/features/console/header-capabilities";
import { PanelAggregateFrame } from "@/components/features/console/panel-aggregate-frame";
import {
  PanelShell,
  type PanelShapeOption,
  type PanelViewState,
} from "@/components/features/console/panel-shell";
import { usePanelVisibilityState } from "@/components/features/console/panel-visibility-state";
import { createRealtimePayloadMessage, type RealtimePayloadMessage } from "@/components/features/console/realtime-payload";
import { useConsoleRealtimeView } from "@/components/features/console/realtime";
import { type PanelVisibilityOption } from "@/components/features/console/panel-visibility-settings";
import { StackedSkeleton } from "@/components/features/console/stacked-skeleton";
import { createWorkComponentRequest, getWorkComponentData, getWorkComponentErrors } from "@/components/workable/console/component-results";
import {
  ConsoleActionButton,
  ExecutionStatusBadge,
  consoleActionToneClassName,
  createExecutionControlConfirmProps,
} from "@/components/workable/console/execution-status-controls";
import { ErrorBanner, ErrorPanel, FeedbackBanner, type FeedbackTone } from "@/components/workable/console/feedback-panel";
import { useLiveRelativeTimeNow } from "@/components/workable/console/live-relative-time";
import {
  workableFetch,
  type WorkComponentQueryResult,
  type WorkMessage,
  type WorkableConnection,
  type WorkflowChildWorkerSummary,
  type WorkflowStepChildWorkerQueryResult,
  type WorkflowAvailableActions,
  type WorkflowOperatorNodeStatus,
  type WorkflowRunDetailView,
  type WorkflowRunStatus,
  type WorkflowStepKind,
  type WorkflowStepOperatorView,
} from "@/lib/workable";
import {
  semanticBadgeToneClass,
  semanticIndicatorToneClass,
  semanticTextToneClass,
} from "@/lib/ui/state-tones";
import { cn } from "@/lib/utils";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { formatElapsedSince } from "@/components/workable/console/detail-screens";

type WorkflowDetailPanelId = "workflowControls" | "workflowGraph";
type WorkflowAction = "Start" | "Pause" | "Cancel";
type WorkflowRunStateOverlay = {
  availableActions: WorkflowAvailableActions;
  status: WorkflowRunStatus;
};
export type WorkflowRunConsoleViewUiStateSnapshot = {
  autoFollowCurrentStep: boolean;
  collapsedBranchIds?: string[];
  focusedBranchId?: string | null;
  runId: string;
  selectedChildWorkerId?: string | null;
  selectedChildWorkerPageIndex?: number;
  workflowGraphScrollLeft?: number;
  selectedStepName: string | null;
  workflowGraphScrollTop?: number;
  workflowGraphViewState?: Extract<PanelViewState, "detailed" | "standard">;
};
type WorkflowActionResult = {
  action: WorkflowAction;
  messages?: WorkMessage[];
  run?: WorkflowRunStateOverlay | null;
  runId: string;
  status: string;
};
export type WorkflowGraphRenderModel = {
  currentPath: string[];
  hiddenJoinNames: string[];
  selectedPath: string[];
  sequence: WorkflowGraphRenderNode[];
};
export type WorkflowGraphRenderNode = {
  branchLanes: WorkflowGraphBranchLane[];
  containsCurrent: boolean;
  containsSelected: boolean;
  current: boolean;
  kind: WorkflowStepKind;
  name: string;
  nestedSequence: WorkflowGraphRenderNode[];
  path: string[];
  selected: boolean;
  status: WorkflowOperatorNodeStatus;
};
export type WorkflowGraphBranchLane = {
  containsCurrent: boolean;
  containsSelected: boolean;
  id: string;
  index: number;
  label: string;
  parentName: string;
  parentPath: string[];
  rootStepName: string;
  status: WorkflowOperatorNodeStatus;
  steps: WorkflowGraphRenderNode[];
};
export type WorkflowGraphRenderModelOptions = {
  currentStepName?: string | null;
  selectedStepName?: string | null;
};
type WorkflowGraphBranchFocusCrumb = {
  branchId: string;
  index: number;
  parentName: string;
  rootStepName: string;
};
type WorkflowGraphBranchFocus = {
  breadcrumbs: WorkflowGraphBranchFocusCrumb[];
  lane: WorkflowGraphBranchLane;
};

type WorkflowActionButtonProps = {
  action: WorkflowAction;
  disabled?: boolean;
  executionMayStop?: boolean;
  icon: typeof Play;
  loading?: boolean;
  onAction: (action: WorkflowAction) => Promise<void>;
  tooltip?: string;
};

const emptyAvailableWorkflowActions: WorkflowAvailableActions = {
  cancel: false,
  pause: false,
  start: false,
};

const workflowNodeWorkerPageSize = 12;
const workflowGraphChildSampleSize = workflowNodeWorkerPageSize;
const workflowPanelOptions: PanelVisibilityOption<WorkflowDetailPanelId>[] = [
  {
    id: "workflowControls",
    label: "Workflow controls",
    description: "Workflow status, lifecycle controls, and run metadata.",
  },
  {
    id: "workflowGraph",
    label: "Workflow graph",
    description: "The live workflow step graph with child worker samples.",
  },
];
const workflowGraphViewStateOptions: readonly PanelShapeOption[] = [
  { icon: Rows3, label: "Standard", shape: "standard" },
  { icon: Rows4, label: "Detailed", shape: "detailed" },
];

export function createWorkflowRunDetailPath(
  workflowRunId: string,
  options?: { childSampleSize?: number }
) {
  const params = new URLSearchParams();
  if (options?.childSampleSize) {
    params.set("childSampleSize", String(options.childSampleSize));
  }

  return appendQueryString(`workflow-runs/${encodeURIComponent(workflowRunId)}`, params);
}

function createWorkflowRunActionPath(workflowRunId: string, action: WorkflowAction) {
  return `workflow-runs/${encodeURIComponent(workflowRunId)}/actions/${action.toLowerCase()}`;
}

function createWorkflowRunStepChildrenPath(
  workflowRunId: string,
  stepName: string,
  options: {
    skip: number;
    take: number;
  }
) {
  const params = new URLSearchParams({
    skip: String(Math.max(0, options.skip)),
    take: String(Math.max(1, options.take)),
  });
  return appendQueryString(
    `workflow-runs/${encodeURIComponent(workflowRunId)}/steps/${encodeURIComponent(stepName)}/children`,
    params
  );
}

export function WorkflowRunConsoleView({
  connection,
  initialUiState,
  onActiveRealtimeConnectionCountChange,
  onOpenWorker,
  onRealtimePayloadOpenChange,
  onWorkflowGraphExpandedChange,
  onUiStateChange,
  realtimePayloadCaptureEnabled,
  realtimePayloadMaxMessages,
  realtimePayloadOpen,
  refreshToken,
  workflowRunId,
}: {
  connection: WorkableConnection;
  initialUiState?: WorkflowRunConsoleViewUiStateSnapshot | null;
  onActiveRealtimeConnectionCountChange: (count: number) => void;
  onOpenWorker: (workerId: string, uiState?: WorkflowRunConsoleViewUiStateSnapshot) => void;
  onRealtimePayloadOpenChange: (open: boolean) => void;
  onWorkflowGraphExpandedChange?: (expanded: boolean) => void;
  onUiStateChange?: (state: WorkflowRunConsoleViewUiStateSnapshot) => void;
  realtimePayloadCaptureEnabled: boolean;
  realtimePayloadMaxMessages: number;
  realtimePayloadOpen: boolean;
  refreshToken: number;
  workflowRunId: string;
}) {
  const [actionFeedback, setActionFeedback] = useState<{
    message: string;
    tone: FeedbackTone;
    title?: string;
  }>();
  const [optimisticRunState, setOptimisticRunState] = useState<WorkflowRunStateOverlay | null>(null);
  const [manualRefreshToken, setManualRefreshToken] = useState(0);
  const [pendingAction, setPendingAction] = useState<WorkflowAction | null>(null);
  const {
    hiddenPanelIdList,
    hiddenPanelIds,
    resetPanelVisibility,
    setPanelVisible,
  } = usePanelVisibilityState<WorkflowDetailPanelId>();
  const refreshSeed = refreshToken + manualRefreshToken;
  const workflowDetail = useWorkableResource<WorkflowRunDetailView>(
    connection,
    createWorkflowRunDetailPath(workflowRunId, {
      childSampleSize: workflowGraphChildSampleSize,
    }),
    refreshSeed,
    {
      retainDataOnNull: true,
      resetKey: workflowRunId,
    }
  );
  const workflowRealtimeRequest = useMemo(
    () => ({
      components: [
        createWorkComponentRequest(
          "workflowRun",
          "workflowRun",
          "detailed",
          {
            childSampleSize: workflowGraphChildSampleSize,
            runId: workflowRunId,
          }
        ),
      ],
    }),
    [workflowRunId]
  );
  const workflowRealtime = useConsoleRealtimeView<WorkComponentQueryResult, RealtimePayloadMessage>({
    body: workflowRealtimeRequest,
    captureEnabled: realtimePayloadCaptureEnabled && realtimePayloadOpen,
    connection,
    connectionInstanceKey: `workflow-run:${workflowRunId}`,
    createMessage: (result, nextMessageId) => {
      const payloadJson = JSON.stringify(result);
      return createRealtimePayloadMessage(
        result,
        payloadJson,
        `workflow-run:${workflowRunId}:${nextMessageId}`,
        "workflow-run",
        `workflow-run:${workflowRunId}`,
        connection
      );
    },
    enabled: Boolean(connection.realtimeHubPath) &&
      Boolean(workflowDetail.data) &&
      !isFinalWorkflowRunStatus(workflowDetail.data?.status),
    maxMessages: realtimePayloadMaxMessages,
    subscription: `workflow-run:${workflowRunId}`,
    subscriptionInstanceKey: workflowRunId,
    subscriptionErrorMessage: "Realtime workflow run subscription failed.",
    viewName: "workflow-run",
  });
  const realtimeRun = getWorkComponentData<WorkflowRunDetailView>(
    workflowRealtime.data,
    "workflowRun"
  );
  const [stepChildPageRefreshToken, setStepChildPageRefreshToken] = useState(0);
  const realtimeErrors = useMemo(
    () => getWorkComponentErrors(workflowRealtime.data),
    [workflowRealtime.data]
  );
  useEffect(() => {
    setOptimisticRunState(null);
  }, [workflowRunId]);
  useEffect(() => {
    if (!optimisticRunState) {
      return;
    }

    if (realtimeRun?.status === optimisticRunState.status || workflowDetail.data?.status === optimisticRunState.status) {
      setOptimisticRunState(null);
    }
  }, [optimisticRunState, realtimeRun?.status, workflowDetail.data?.status]);
  useEffect(() => {
    if (!realtimeRun && !workflowDetail.data) {
      return;
    }

    setStepChildPageRefreshToken((current) => current + 1);
  }, [realtimeRun, workflowDetail.data]);
  const run = useMemo(
    () => mergeWorkflowRunDetail(realtimeRun, workflowDetail.data ?? null, optimisticRunState),
    [optimisticRunState, realtimeRun, workflowDetail.data]
  );
  const refreshWorkflowRun = useCallback(() => {
    setManualRefreshToken((current) => current + 1);
  }, []);
  const toggleRealtimePayloadOpen = useCallback(() => {
    onRealtimePayloadOpenChange(!realtimePayloadOpen);
  }, [onRealtimePayloadOpenChange, realtimePayloadOpen]);
  const headerCapabilities = useMemo<ConsoleHeaderCapabilities>(
    () => ({
      realtime: {
        connectionState: workflowRealtime.connectionState,
        enabled: workflowRealtime.enabled,
        menuItems: [
          {
            active: realtimePayloadOpen,
            icon: <Rows4 className="size-4" />,
            id: "workflow-run-realtime-payloads",
            label: "Realtime payloads",
            onSelect: toggleRealtimePayloadOpen,
          },
        ],
        title: workflowRealtime.error ?? realtimeErrors[0],
      },
      refresh: {
        disabled: workflowDetail.loading || workflowDetail.refreshing === true,
        onRefresh: refreshWorkflowRun,
        refreshing: workflowDetail.refreshing === true,
        title: "Refresh workflow run",
      },
    }),
    [
      realtimeErrors,
      realtimePayloadOpen,
      refreshWorkflowRun,
      toggleRealtimePayloadOpen,
      workflowDetail.loading,
      workflowDetail.refreshing,
      workflowRealtime.connectionState,
      workflowRealtime.enabled,
      workflowRealtime.error,
    ]
  );
  const relativeNow = useLiveRelativeTimeNow();

  useRegisterConsoleHeaderCapabilities({
    active: true,
    capabilities: headerCapabilities,
    id: "workflow-run-console",
  });

  useEffect(() => {
    const isActive = workflowRealtime.enabled &&
      workflowRealtime.connectionState !== "disabled";
    onActiveRealtimeConnectionCountChange(isActive ? 1 : 0);

    return () => onActiveRealtimeConnectionCountChange(0);
  }, [
    onActiveRealtimeConnectionCountChange,
    workflowRealtime.connectionState,
    workflowRealtime.enabled,
  ]);

  useEffect(() => {
    if (actionFeedback?.tone !== "success") {
      return;
    }

    const timer = setTimeout(() => {
      setActionFeedback((current) =>
        current?.message === actionFeedback.message && current.tone === "success"
          ? undefined
          : current
      );
    }, 2000);

    return () => clearTimeout(timer);
  }, [actionFeedback]);

  const executeAction = useCallback(async (action: WorkflowAction) => {
    setPendingAction(action);
    setActionFeedback(undefined);

    try {
      const result = await workableFetch<WorkflowActionResult>(
        connection,
        createWorkflowRunActionPath(workflowRunId, action),
        {
          body: JSON.stringify({}),
          method: "POST",
        }
      );
      const message = firstWorkflowMessage(result.messages) ??
        `${action} accepted for workflow run ${workflowRunId}.`;
      setActionFeedback({
        message,
        tone: result.status === "Accepted" ? "success" : "error",
        title: result.status === "Accepted" ? "Workflow action accepted" : "Workflow action failed",
      });
      if (result.status === "Accepted" && result.run) {
        setOptimisticRunState(result.run);
      }
      refreshWorkflowRun();
    } catch (error) {
      setActionFeedback({
        message: error instanceof Error ? error.message : `Could not ${action.toLowerCase()} the workflow run.`,
        tone: "error",
        title: "Workflow action failed",
      });
    } finally {
      setPendingAction(null);
    }
  }, [connection, refreshWorkflowRun, workflowRunId]);

  const controlsDisabled = pendingAction !== null || !run;
  const availableActions = run?.availableActions ?? emptyAvailableWorkflowActions;
  const hasError = Boolean(workflowDetail.error) && !run;
  const restoredWorkflowGraphViewState = initialUiState?.runId === workflowRunId &&
    initialUiState.workflowGraphViewState === "detailed"
    ? "detailed"
    : "standard";
  const [workflowGraphViewState, setWorkflowGraphViewState] = useState<Extract<PanelViewState, "detailed" | "standard">>(
    restoredWorkflowGraphViewState
  );
  useEffect(() => {
    setWorkflowGraphViewState(restoredWorkflowGraphViewState);
  }, [restoredWorkflowGraphViewState, workflowRunId]);
  const workflowGraphExpanded = workflowGraphViewState === "detailed";
  useEffect(() => {
    onWorkflowGraphExpandedChange?.(workflowGraphExpanded);

    return () => {
      onWorkflowGraphExpandedChange?.(false);
    };
  }, [onWorkflowGraphExpandedChange, workflowGraphExpanded]);
  const restoredWorkflowGraphScrollTop = initialUiState?.runId === workflowRunId
    ? normalizeWorkflowGraphScrollTop(initialUiState.workflowGraphScrollTop)
    : 0;
  const restoredWorkflowGraphScrollLeft = initialUiState?.runId === workflowRunId
    ? normalizeWorkflowGraphScrollTop(initialUiState.workflowGraphScrollLeft)
    : 0;
  const workflowGraphScrollRef = useRef<HTMLDivElement | null>(null);
  const workflowGraphContentRef = useRef<HTMLDivElement | null>(null);
  const workflowGraphScrollLeftRef = useRef(restoredWorkflowGraphScrollLeft);
  const workflowGraphScrollTopRef = useRef(restoredWorkflowGraphScrollTop);
  const restoredWorkflowGraphScrollKeyRef = useRef<string | null>(null);
  const workflowGraphDrag = useDraggableWorkflowGraphScroll(workflowGraphScrollRef, (scrollLeft, scrollTop) => {
    workflowGraphScrollLeftRef.current = scrollLeft;
    workflowGraphScrollTopRef.current = scrollTop;
  });
  useEffect(() => {
    workflowGraphScrollLeftRef.current = restoredWorkflowGraphScrollLeft;
    workflowGraphScrollTopRef.current = restoredWorkflowGraphScrollTop;
    restoredWorkflowGraphScrollKeyRef.current = null;
  }, [restoredWorkflowGraphScrollLeft, restoredWorkflowGraphScrollTop, workflowRunId]);
  useEffect(() => {
    if (!run || hiddenPanelIds.has("workflowGraph")) {
      return;
    }

    const restoreKey = `${workflowRunId}:${restoredWorkflowGraphScrollLeft}:${restoredWorkflowGraphScrollTop}`;
    if (restoredWorkflowGraphScrollKeyRef.current === restoreKey) {
      return;
    }

    const scrollContainer = workflowGraphScrollRef.current;
    if (!scrollContainer) {
      return;
    }

    const restore = () => {
      scrollContainer.scrollLeft = restoredWorkflowGraphScrollLeft;
      scrollContainer.scrollTop = restoredWorkflowGraphScrollTop;
      workflowGraphScrollLeftRef.current = scrollContainer.scrollLeft;
      workflowGraphScrollTopRef.current = scrollContainer.scrollTop;

      if (
        scrollContainer.scrollLeft >= restoredWorkflowGraphScrollLeft &&
        scrollContainer.scrollTop >= restoredWorkflowGraphScrollTop
      ) {
        restoredWorkflowGraphScrollKeyRef.current = restoreKey;
      }
    };

    const frame = requestAnimationFrame(restore);
    const resizeObserver = typeof ResizeObserver === "undefined"
      ? null
      : new ResizeObserver(restore);
    resizeObserver?.observe(workflowGraphContentRef.current ?? scrollContainer);

    return () => {
      cancelAnimationFrame(frame);
      resizeObserver?.disconnect();
    };
  }, [hiddenPanelIds, restoredWorkflowGraphScrollLeft, restoredWorkflowGraphScrollTop, run, workflowRunId]);
  const createWorkflowRunUiStateSnapshot = useCallback((state: WorkflowRunConsoleViewUiStateSnapshot) => ({
    ...state,
    ...(workflowGraphViewState === "detailed" ? { workflowGraphViewState } : {}),
    workflowGraphScrollLeft: workflowGraphScrollLeftRef.current,
    workflowGraphScrollTop: workflowGraphScrollTopRef.current,
  }), [workflowGraphViewState]);
  const handleWorkflowRunUiStateChange = useCallback((state: WorkflowRunConsoleViewUiStateSnapshot) => {
    onUiStateChange?.(createWorkflowRunUiStateSnapshot(state));
  }, [createWorkflowRunUiStateSnapshot, onUiStateChange]);
  const handleOpenWorkflowWorker = useCallback((
    workerId: string,
    uiState?: WorkflowRunConsoleViewUiStateSnapshot
  ) => {
    onOpenWorker(
      workerId,
      uiState ? createWorkflowRunUiStateSnapshot(uiState) : undefined
    );
  }, [createWorkflowRunUiStateSnapshot, onOpenWorker]);

  return (
    <ConsolePageLayout scrollMode="browser">
      <ErrorPanel errors={[
        workflowDetail.error,
        workflowRealtime.error,
        ...realtimeErrors,
      ]} />
      <PanelAggregateFrame
        hiddenPanelIds={hiddenPanelIdList}
        onPanelVisibilityChange={setPanelVisible}
        onResetUi={resetPanelVisibility}
        padding="tightTop"
        panelOptions={workflowPanelOptions}
        scrollMode="browser"
        settingsButtonLabel="Workflow panel settings"
        settingsDescription="Checked panels are shown on the workflow run page."
        settingsTitle="Workflow panels"
      >
        {workflowDetail.loading && !run ? <StackedSkeleton count={5} /> : null}
        {hasError ? (
          <ErrorBanner
            key={workflowDetail.error}
            message={workflowDetail.error ?? "Unable to load workflow run."}
            title="Unable to load workflow run"
          />
        ) : null}
        {!workflowDetail.loading && !run ? (
          <ConsoleEmptyState padding="spacious">
            Workflow run not found.
          </ConsoleEmptyState>
        ) : null}
        {run ? (
          <div className="relative flex min-h-0 flex-1 flex-col gap-6">
            {actionFeedback ? (
              <FeedbackBanner
                key={`${actionFeedback.tone}:${actionFeedback.message}`}
                message={actionFeedback.message}
                onDismiss={() => setActionFeedback(undefined)}
                title={actionFeedback.title ?? "Workflow action"}
                tone={actionFeedback.tone}
              />
            ) : null}
            {!workflowGraphExpanded && !hiddenPanelIds.has("workflowControls") ? (
              <PanelShell
                contentClassName="hidden"
                leadingActions={(
                  <div className={`flex min-w-0 flex-wrap items-center ${consolePanelActionGapClassName}`}>
                    <WorkflowActionButton
                      action="Start"
                      disabled={controlsDisabled || !availableActions.start}
                      icon={Play}
                      loading={pendingAction === "Start"}
                      onAction={executeAction}
                      tooltip="Resume this paused or blocked workflow run."
                    />
                    <WorkflowActionButton
                      action="Pause"
                      disabled={controlsDisabled || !availableActions.pause}
                      executionMayStop={run.status === "Running"}
                      icon={Pause}
                      loading={pendingAction === "Pause"}
                      onAction={executeAction}
                      tooltip="Pause this workflow run and pause outstanding child work where possible."
                    />
                    <WorkflowActionButton
                      action="Cancel"
                      disabled={controlsDisabled || !availableActions.cancel}
                      executionMayStop={run.status === "Running"}
                      icon={Ban}
                      loading={pendingAction === "Cancel"}
                      onAction={executeAction}
                      tooltip="Cancel the workflow and any active child work that can be canceled."
                    />
                  </div>
                )}
                onClose={() => setPanelVisible("workflowControls", false)}
                title={<WorkflowRunStatusBadge now={relativeNow} run={run} />}
              >
                {null}
              </PanelShell>
            ) : null}
            {!hiddenPanelIds.has("workflowGraph") ? (
              <PanelShell
                className={workflowGraphExpanded ? "min-h-[calc(100svh-6rem)]" : undefined}
                onClose={() => setPanelVisible("workflowGraph", false)}
                onViewStateChange={(shape) => {
                  setWorkflowGraphViewState(shape === "detailed" ? "detailed" : "standard");
                }}
                supportedViewStates={["standard", "detailed"]}
                title="Workflow Graph"
                viewState={workflowGraphViewState}
                viewStateOptions={workflowGraphViewStateOptions}
              >
                <div
                  className={cn(
                    "workable-grid-scrollbar overflow-auto pr-1 select-none",
                    workflowGraphDrag.dragging ? "cursor-grabbing" : "cursor-grab",
                    workflowGraphExpanded
                      ? "h-[calc(100svh-12rem)] min-h-[28rem]"
                      : "max-h-[70vh]"
                  )}
                  onClickCapture={workflowGraphDrag.onClickCapture}
                  onPointerCancel={workflowGraphDrag.onPointerCancel}
                  onPointerDown={workflowGraphDrag.onPointerDown}
                  onPointerMove={workflowGraphDrag.onPointerMove}
                  onPointerUp={workflowGraphDrag.onPointerUp}
                  onScroll={(event) => {
                    workflowGraphScrollLeftRef.current = event.currentTarget.scrollLeft;
                    workflowGraphScrollTopRef.current = event.currentTarget.scrollTop;
                  }}
                  ref={workflowGraphScrollRef}
                >
                  <div className="pr-1 pb-1" ref={workflowGraphContentRef}>
                    {run.steps.length === 0 ? (
                      <ConsoleEmptyState padding="compact">
                        No workflow steps have been materialized for this run yet.
                      </ConsoleEmptyState>
                    ) : (
                      <WorkflowFlowChart
                        connection={connection}
                        currentStepName={run.currentStepName}
                        currentStepStatus={run.currentStepStatus}
                        initialAutoFollowCurrentStep={initialUiState?.runId === workflowRunId
                          ? initialUiState.autoFollowCurrentStep
                          : undefined}
                        initialSelectedChildWorkerId={initialUiState?.runId === workflowRunId
                          ? initialUiState.selectedChildWorkerId ?? null
                          : null}
                        initialSelectedChildWorkerPageIndex={initialUiState?.runId === workflowRunId
                          ? initialUiState.selectedChildWorkerPageIndex
                          : undefined}
                        initialSelectedStepName={initialUiState?.runId === workflowRunId
                          ? initialUiState.selectedStepName
                          : null}
                        initialCollapsedBranchIds={initialUiState?.runId === workflowRunId
                          ? initialUiState.collapsedBranchIds
                          : undefined}
                        initialFocusedBranchId={initialUiState?.runId === workflowRunId
                          ? initialUiState.focusedBranchId
                          : undefined}
                        onOpenWorker={handleOpenWorkflowWorker}
                        onUiStateChange={handleWorkflowRunUiStateChange}
                        outstandingChildren={run.outstandingChildren}
                        runStatus={run.status}
                        stepChildPageRefreshToken={stepChildPageRefreshToken}
                        steps={run.steps}
                        workflowRunId={workflowRunId}
                      />
                    )}
                  </div>
                </div>
              </PanelShell>
            ) : null}
          </div>
        ) : null}
      </PanelAggregateFrame>
    </ConsolePageLayout>
  );
}

function WorkflowRunStatusBadge({
  now,
  run,
}: {
  now: number;
  run: WorkflowRunDetailView;
}) {
  return (
    <ExecutionStatusBadge
      label={run.status}
      timing={formatWorkflowRunStatusTiming(run, now) ?? undefined}
      toneClassName={semanticTextToneClass(workflowRunTone(run.status), "strong")}
    />
  );
}

function mergeWorkflowRunDetail(
  realtimeRun: WorkflowRunDetailView | null | undefined,
  fetchedRun: WorkflowRunDetailView | null,
  optimisticRunState: WorkflowRunStateOverlay | null
) {
  const preferredRun = choosePreferredWorkflowRun(realtimeRun, fetchedRun);
  if (!preferredRun) {
    return null;
  }

  if (!optimisticRunState) {
    return preferredRun;
  }

  if (isFinalWorkflowRunStatus(optimisticRunState.status) && !isFinalWorkflowRunStatus(preferredRun.status)) {
    return {
      ...preferredRun,
      availableActions: optimisticRunState.availableActions,
      status: optimisticRunState.status,
    } satisfies WorkflowRunDetailView;
  }

  return preferredRun;
}

function choosePreferredWorkflowRun(
  realtimeRun: WorkflowRunDetailView | null | undefined,
  fetchedRun: WorkflowRunDetailView | null
) {
  if (!realtimeRun) {
    return fetchedRun;
  }

  if (!fetchedRun) {
    return realtimeRun;
  }

  return isFinalWorkflowRunStatus(fetchedRun.status) && !isFinalWorkflowRunStatus(realtimeRun.status)
    ? fetchedRun
    : realtimeRun;
}

function WorkflowFlowChart({
  connection,
  currentStepName,
  currentStepStatus,
  initialAutoFollowCurrentStep,
  initialCollapsedBranchIds,
  initialFocusedBranchId,
  initialSelectedChildWorkerId,
  initialSelectedChildWorkerPageIndex,
  initialSelectedStepName,
  onOpenWorker,
  onUiStateChange,
  outstandingChildren,
  runStatus,
  stepChildPageRefreshToken,
  steps,
  workflowRunId,
}: {
  connection: WorkableConnection;
  currentStepName?: string | null;
  currentStepStatus?: WorkflowOperatorNodeStatus | null;
  initialAutoFollowCurrentStep?: boolean;
  initialCollapsedBranchIds?: string[];
  initialFocusedBranchId?: string | null;
  initialSelectedChildWorkerId?: string | null;
  initialSelectedChildWorkerPageIndex?: number;
  initialSelectedStepName?: string | null;
  onOpenWorker: (workerId: string, uiState?: WorkflowRunConsoleViewUiStateSnapshot) => void;
  onUiStateChange?: (state: WorkflowRunConsoleViewUiStateSnapshot) => void;
  outstandingChildren: WorkflowChildWorkerSummary;
  runStatus: WorkflowRunStatus;
  stepChildPageRefreshToken: number;
  steps: WorkflowStepOperatorView[];
  workflowRunId: string;
}) {
  const graphViewportRef = useRef<HTMLDivElement | null>(null);
  const stepElementRefs = useRef(new Map<string, HTMLElement | null>());
  const flattenedSteps = useMemo(() => flattenWorkflowSteps(steps), [steps]);
  const registerStepElement = useCallback((stepName: string, element: HTMLElement | null) => {
    if (element) {
      stepElementRefs.current.set(stepName, element);
      return;
    }

    stepElementRefs.current.delete(stepName);
  }, []);
  const currentActivity = useMemo(() => {
    if (currentStepName) {
      const matched = findWorkflowStepByName(steps, currentStepName);
      if (matched) {
        return matched;
      }
    }

    return flattenedSteps.find((step) =>
      step.status === "Running" || step.status === "WaitingOnChildren"
    ) ?? flattenedSteps[0] ?? null;
  }, [currentStepName, flattenedSteps, steps]);
  const [selectedStepName, setSelectedStepName] = useState<string | null>(
    () => resolveWorkflowInitialSelectionStepName(
      steps,
      initialSelectedStepName,
      currentActivity?.name ?? currentStepName ?? null
    )
  );
  const [selectedChildWorkerId, setSelectedChildWorkerId] = useState<string | null>(
    () => initialSelectedChildWorkerId ?? null
  );
  const [selectedChildWorkerPageIndex, setSelectedChildWorkerPageIndex] = useState(
    () => normalizeWorkflowWorkerPageIndex(initialSelectedChildWorkerPageIndex)
  );
  const [autoFollowCurrentStep, setAutoFollowCurrentStep] = useState(
    () => initialAutoFollowCurrentStep ?? true
  );
  const defaultCollapsedBranchIds = useMemo(
    () => collectWorkflowGraphBranchIdsFromSteps(steps),
    [steps]
  );
  const branchCollapseModifiedRef = useRef(initialCollapsedBranchIds !== undefined);
  const [collapsedBranchIds, setCollapsedBranchIds] = useState<string[]>(
    () => initialCollapsedBranchIds !== undefined
      ? normalizeCollapsedBranchIds(initialCollapsedBranchIds)
      : defaultCollapsedBranchIds
  );
  const collapsedBranchIdSet = useMemo(
    () => new Set(collapsedBranchIds),
    [collapsedBranchIds]
  );
  const [focusedBranchId, setFocusedBranchId] = useState<string | null>(
    () => normalizeFocusedBranchId(initialFocusedBranchId)
  );
  useEffect(() => {
    if (branchCollapseModifiedRef.current) {
      return;
    }

    setCollapsedBranchIds(defaultCollapsedBranchIds);
  }, [defaultCollapsedBranchIds]);
  const autoSelectedStepName = useMemo(
    () => resolveWorkflowSelectionStepName(steps, currentActivity?.name ?? currentStepName ?? null),
    [currentActivity?.name, currentStepName, steps]
  );
  const activeSelectionAnchor = currentActivity?.name ?? currentStepName ?? autoSelectedStepName ?? null;
  const previousActiveSelectionAnchorRef = useRef<string | null>(activeSelectionAnchor);

  useEffect(() => {
    const previousActiveSelectionAnchor = previousActiveSelectionAnchorRef.current;
    previousActiveSelectionAnchorRef.current = activeSelectionAnchor;

    const selectionMissing = selectedStepName
      ? !isWorkflowStepSelectable(findWorkflowStepByName(steps, selectedStepName))
      : true;
    const activeNodeChanged = activeSelectionAnchor !== previousActiveSelectionAnchor;

    if ((selectionMissing || (autoFollowCurrentStep && activeNodeChanged)) &&
      autoSelectedStepName !== selectedStepName) {
      setSelectedStepName(autoSelectedStepName);
    }
  }, [activeSelectionAnchor, autoFollowCurrentStep, autoSelectedStepName, selectedStepName, steps]);
  const previousSelectedStepNameRef = useRef(selectedStepName);
  useEffect(() => {
    const previousSelectedStepName = previousSelectedStepNameRef.current;
    previousSelectedStepNameRef.current = selectedStepName;

    if (previousSelectedStepName === selectedStepName) {
      return;
    }

    setSelectedChildWorkerId(null);
    setSelectedChildWorkerPageIndex(0);
  }, [selectedStepName]);
  useEffect(() => {
    onUiStateChange?.(createWorkflowFlowUiState({
      autoFollowCurrentStep,
      collapsedBranchIds,
      focusedBranchId,
      runId: workflowRunId,
      selectedChildWorkerId,
      selectedChildWorkerPageIndex,
      selectedStepName,
    }));
  }, [
    autoFollowCurrentStep,
    collapsedBranchIds,
    focusedBranchId,
    onUiStateChange,
    selectedChildWorkerId,
    selectedChildWorkerPageIndex,
    selectedStepName,
    workflowRunId,
  ]);
  const selectedStep = useMemo(
    () => selectedStepName ? findWorkflowStepByName(steps, selectedStepName) : null,
    [selectedStepName, steps]
  );
  const shouldFollowCurrentActivity = autoFollowCurrentStep &&
    runStatus === "Running" &&
    isWorkflowStepExecuting(currentActivity?.status ?? currentStepStatus);
  const highlightedCurrentStepName = shouldFollowCurrentActivity
    ? currentActivity?.name ?? currentStepName ?? null
    : null;
  const renderModel = useMemo(
    () => createWorkflowGraphRenderModel(steps, {
      currentStepName: highlightedCurrentStepName,
      selectedStepName,
    }),
    [highlightedCurrentStepName, selectedStepName, steps]
  );
  const focusedBranch = useMemo(
    () => focusedBranchId
      ? findWorkflowGraphBranchFocus(renderModel.sequence, focusedBranchId)
      : null,
    [focusedBranchId, renderModel.sequence]
  );
  useEffect(() => {
    if (focusedBranchId && !focusedBranch) {
      setFocusedBranchId(null);
    }
  }, [focusedBranch, focusedBranchId]);
  const progressSummary = useMemo(
    () => summarizeWorkflowWorkerProgress(steps, outstandingChildren),
    [outstandingChildren, steps]
  );

  const handleSelectStep = useCallback((stepName: string) => {
    setAutoFollowCurrentStep(false);
    setSelectedStepName(stepName);
    setSelectedChildWorkerId(null);
    setSelectedChildWorkerPageIndex(0);
  }, []);
  const handleToggleBranchCollapse = useCallback((branchId: string) => {
    branchCollapseModifiedRef.current = true;
    setCollapsedBranchIds((current) => current.includes(branchId)
      ? current.filter((id) => id !== branchId)
      : [...current, branchId].sort((left, right) => left.localeCompare(right)));
  }, []);
  const handleFocusBranch = useCallback((lane: WorkflowGraphBranchLane) => {
    setAutoFollowCurrentStep(false);
    setFocusedBranchId(lane.id);
    setSelectedStepName(lane.rootStepName);
    setSelectedChildWorkerId(null);
    setSelectedChildWorkerPageIndex(0);
  }, []);
  const handleFocusBranchId = useCallback((branchId: string | null) => {
    setFocusedBranchId(branchId);
  }, []);
  const handleWorkerPageIndexChange = useCallback((pageIndex: number) => {
    setSelectedChildWorkerPageIndex(normalizeWorkflowWorkerPageIndex(pageIndex));
  }, []);
  const handleOpenWorker = useCallback((workerId: string) => {
    const nextUiState = createWorkflowFlowUiState({
      autoFollowCurrentStep,
      collapsedBranchIds,
      focusedBranchId,
      runId: workflowRunId,
      selectedChildWorkerId: workerId,
      selectedChildWorkerPageIndex,
      selectedStepName,
    });
    setSelectedChildWorkerId(workerId);
    onUiStateChange?.(nextUiState);
    onOpenWorker(workerId, nextUiState);
  }, [
    autoFollowCurrentStep,
    collapsedBranchIds,
    focusedBranchId,
    onOpenWorker,
    onUiStateChange,
    selectedChildWorkerPageIndex,
    selectedStepName,
    workflowRunId,
  ]);

  useEffect(() => {
    if (!shouldFollowCurrentActivity) {
      return;
    }

    const activeStepName = currentActivity?.name ?? currentStepName ?? null;
    if (!activeStepName) {
      return;
    }

    const viewport = graphViewportRef.current;
    if (!viewport) {
      return;
    }

    let frame = 0;
    frame = requestAnimationFrame(() => {
      const activeStepElement = stepElementRefs.current.get(activeStepName);
      activeStepElement?.scrollIntoView({
        behavior: "smooth",
        block: "nearest",
        inline: "center",
      });
    });

    return () => cancelAnimationFrame(frame);
  }, [
    currentActivity?.name,
    currentStepName,
    shouldFollowCurrentActivity,
  ]);

  return (
    <div className="space-y-4 pr-1">
      <div className="w-max min-w-full rounded-2xl border border-border/70 bg-[radial-gradient(circle_at_top,_rgba(56,189,248,0.08),transparent_40%),linear-gradient(to_bottom,_rgba(255,255,255,0.02),transparent)] px-4 pt-4 pb-2 shadow-sm">
        <div className="mb-4">
          <WorkflowWorkerProgressSummary
            executing={isWorkflowStepExecuting(currentActivity?.status ?? currentStepStatus)}
            summary={progressSummary}
          />
        </div>
        <div ref={graphViewportRef} className="pb-2">
          <div className="w-max min-w-full pb-1">
              <WorkflowStructureSequence
              collapsedBranchIds={collapsedBranchIdSet}
              focusedBranch={focusedBranch}
              model={renderModel}
              onFocusBranch={handleFocusBranch}
              onFocusBranchId={handleFocusBranchId}
              onRegisterStepElement={registerStepElement}
              onSelectStep={handleSelectStep}
              onToggleBranchCollapse={handleToggleBranchCollapse}
            />
          </div>
        </div>
      </div>
      <div className="rounded-2xl border border-border/70 bg-card/90 p-4 shadow-sm">
        {selectedStep ? (
          <WorkflowSelectedNodeInspector
            connection={connection}
            onOpenWorker={handleOpenWorker}
            onPageIndexChange={handleWorkerPageIndexChange}
            pageIndex={selectedChildWorkerPageIndex}
            refreshToken={stepChildPageRefreshToken}
            selectedWorkerId={selectedChildWorkerId}
            step={selectedStep}
            workflowRunId={workflowRunId}
          />
        ) : (
          <ConsoleEmptyState padding="compact">
            Select a workflow node to inspect it.
          </ConsoleEmptyState>
        )}
      </div>
    </div>
  );
}

function WorkflowStructureSequence({
  collapsedBranchIds,
  focusedBranch,
  model,
  onFocusBranch,
  onFocusBranchId,
  onRegisterStepElement,
  onSelectStep,
  onToggleBranchCollapse,
}: {
  collapsedBranchIds: ReadonlySet<string>;
  focusedBranch: WorkflowGraphBranchFocus | null;
  model: WorkflowGraphRenderModel;
  onFocusBranch: (lane: WorkflowGraphBranchLane) => void;
  onFocusBranchId: (branchId: string | null) => void;
  onRegisterStepElement: (stepName: string, element: HTMLElement | null) => void;
  onSelectStep: (stepName: string) => void;
  onToggleBranchCollapse: (branchId: string) => void;
}) {
  const sequence = focusedBranch?.lane.steps ?? model.sequence;

  return (
    <div className="space-y-3">
      {focusedBranch ? (
        <WorkflowGraphFocusBreadcrumb
          focusedBranch={focusedBranch}
          onFocusBranchId={onFocusBranchId}
        />
      ) : null}
      <div className="flex w-max min-w-full items-stretch gap-3">
        {sequence.map((node, index, list) => (
          <Fragment key={node.name}>
            <WorkflowGraphNodeColumn
              collapsedBranchIds={collapsedBranchIds}
              node={node}
              onFocusBranch={onFocusBranch}
              onRegisterStepElement={onRegisterStepElement}
              onSelectStep={onSelectStep}
              onToggleBranchCollapse={onToggleBranchCollapse}
            />
            {index < list.length - 1 ? <WorkflowFlowConnector /> : null}
          </Fragment>
        ))}
      </div>
    </div>
  );
}

function WorkflowGraphFocusBreadcrumb({
  focusedBranch,
  onFocusBranchId,
}: {
  focusedBranch: WorkflowGraphBranchFocus;
  onFocusBranchId: (branchId: string | null) => void;
}) {
  return (
    <div
      aria-label="Workflow branch focus"
      className="flex min-w-0 flex-wrap items-center gap-1.5 rounded-lg border border-sky-500/20 bg-sky-500/[0.05] px-2.5 py-2 text-sm"
    >
      <Button
        className="h-6 px-2 text-xs"
        onClick={() => onFocusBranchId(null)}
        size="xs"
        type="button"
        variant="ghost"
      >
        Workflow
      </Button>
      {focusedBranch.breadcrumbs.map((crumb, index, list) => {
        const current = index === list.length - 1;
        const label = `Branch ${crumb.index + 1}: ${crumb.rootStepName}`;

        return (
          <Fragment key={crumb.branchId}>
            <ChevronRight aria-hidden="true" className="size-3.5 shrink-0 text-muted-foreground" />
            {current ? (
              <span
                aria-current="page"
                className="min-w-0 max-w-80 truncate rounded-md bg-sky-500/10 px-2 py-1 font-medium text-sky-100 text-xs"
                title={`${crumb.parentName} / ${label}`}
              >
                {label}
              </span>
            ) : (
              <Button
                className="h-6 max-w-80 px-2 text-xs"
                onClick={() => onFocusBranchId(crumb.branchId)}
                size="xs"
                title={`${crumb.parentName} / ${label}`}
                type="button"
                variant="ghost"
              >
                <span className="truncate">{label}</span>
              </Button>
            )}
          </Fragment>
        );
      })}
    </div>
  );
}

function WorkflowSelectedNodeInspector({
  connection,
  onOpenWorker,
  onPageIndexChange,
  pageIndex,
  refreshToken,
  selectedWorkerId,
  step,
  workflowRunId,
}: {
  connection: WorkableConnection;
  onOpenWorker: (workerId: string) => void;
  onPageIndexChange: (pageIndex: number) => void;
  pageIndex: number;
  refreshToken: number;
  selectedWorkerId?: string | null;
  step: WorkflowStepOperatorView;
  workflowRunId: string;
}) {
  const pageSkip = pageIndex * workflowNodeWorkerPageSize;
  const initialPageSampleCount = Math.min(workflowNodeWorkerPageSize, step.children.total);
  const shouldLoadPagedChildren = step.children.total > 0 && (
    pageIndex > 0 ||
    step.childSample.length < initialPageSampleCount
  );
  const childWorkersPage = useWorkableResource<WorkflowStepChildWorkerQueryResult>(
    connection,
    shouldLoadPagedChildren
      ? createWorkflowRunStepChildrenPath(workflowRunId, step.name, {
          skip: pageSkip,
          take: workflowNodeWorkerPageSize,
        })
      : null,
    refreshToken + pageIndex,
    {
      resetKey: `${workflowRunId}:${step.name}:${pageIndex}`,
    }
  );
  const pagedWorkers = shouldLoadPagedChildren
    ? (childWorkersPage.data?.workers ?? [])
    : step.childSample.slice(0, workflowNodeWorkerPageSize);
  const totalWorkers = shouldLoadPagedChildren
    ? (childWorkersPage.data?.totalCount ?? step.children.total)
    : step.children.total;
  const pageStart = totalWorkers > 0 ? pageSkip + 1 : 0;
  const pageEnd = totalWorkers > 0
    ? Math.min(pageSkip + pagedWorkers.length, totalWorkers)
    : 0;
  const hasPreviousPage = pageIndex > 0;
  const hasNextPage = pageSkip + pagedWorkers.length < totalWorkers;

  return (
    <div className="space-y-4">
      <div className="space-y-2">
        <div className="flex items-center gap-2 text-muted-foreground text-xs uppercase tracking-[0.18em]">
          <Workflow className="size-3.5" />
          Current node details
        </div>
        <div className="flex flex-wrap items-center gap-2">
          <span className="font-semibold text-lg">{step.name}</span>
          <Badge variant="outline">{formatWorkflowStepKind(step.kind)}</Badge>
          <WorkflowNodeStatusBadge status={step.status} />
          {isWorkflowStepExecuting(step.status) ? (
            <WorkflowExecutingIndicator />
          ) : null}
        </div>
      </div>
      {totalWorkers > 0 ? (
        <div className="space-y-2">
          <div className="flex flex-wrap items-center justify-between gap-2 text-muted-foreground text-sm">
            <span>
              {pageStart}-{pageEnd} of {totalWorkers} worker{totalWorkers === 1 ? "" : "s"}
            </span>
            {totalWorkers > workflowNodeWorkerPageSize ? (
              <div className="flex items-center gap-2">
                <Button
                  disabled={!hasPreviousPage}
                  onClick={() => onPageIndexChange(Math.max(0, pageIndex - 1))}
                  size="sm"
                  type="button"
                  variant="outline"
                >
                  Previous
                </Button>
                <Button
                  disabled={!hasNextPage}
                  onClick={() => onPageIndexChange(pageIndex + 1)}
                  size="sm"
                  type="button"
                  variant="outline"
                >
                  Next
                </Button>
              </div>
            ) : null}
          </div>
          {shouldLoadPagedChildren && childWorkersPage.error ? (
            <p className="text-destructive text-sm">
              {childWorkersPage.error}
            </p>
          ) : null}
          {shouldLoadPagedChildren && childWorkersPage.loading && !childWorkersPage.data ? (
            <p className="text-muted-foreground text-sm">
              Loading associated workers...
            </p>
          ) : null}
          {pagedWorkers.map((worker) => {
            const selected = worker.workerId === selectedWorkerId;

            return (
              <Button
                aria-pressed={selected}
                key={worker.workerId}
                className={cn(
                  "h-auto min-h-12 w-full min-w-0 justify-between gap-3 overflow-hidden rounded-xl px-3 py-3 text-left",
                  selected
                    ? "border-primary bg-primary/10 text-foreground shadow-sm ring-2 ring-primary/45 hover:border-primary hover:bg-primary/15"
                    : ""
                )}
                onClick={() => onOpenWorker(worker.workerId)}
                title={`${worker.definitionName} · ${worker.workerId}`}
                type="button"
                variant="outline"
              >
                <span className="min-w-0 flex-1">
                  <span className="block truncate font-medium text-sm">{worker.definitionName}</span>
                  <span className={cn(
                    "block truncate font-mono text-xs",
                    selected ? "text-foreground/75" : "text-muted-foreground"
                  )}>
                    {worker.workerId}
                  </span>
                </span>
                <span className="flex shrink-0 items-center gap-2">
                  {isWorkflowWorkerExecuting(worker.state) ? <WorkflowExecutingIndicator /> : null}
                  <Badge className={semanticBadgeToneClass(workerStateTone(worker.state))} variant="secondary">
                    {worker.state}
                  </Badge>
                </span>
              </Button>
            );
          })}
        </div>
      ) : step.children.total > 0 ? (
        <p className="text-muted-foreground text-sm">
          This node has associated workers, but none are currently available to display.
        </p>
      ) : null}
      {step.children.total === 0 ? (
        <p className="text-muted-foreground text-sm">
          This node has not dispatched any workers.
        </p>
      ) : null}
    </div>
  );
}

function normalizeWorkflowWorkerPageIndex(pageIndex: number | null | undefined) {
  return Math.max(0, Math.floor(pageIndex ?? 0));
}

function normalizeWorkflowGraphScrollTop(scrollTop: number | null | undefined) {
  if (typeof scrollTop !== "number" || !Number.isFinite(scrollTop)) {
    return 0;
  }

  return Math.max(0, Math.floor(scrollTop));
}

function useDraggableWorkflowGraphScroll(
  viewportRef: RefObject<HTMLDivElement | null>,
  onScrollPositionChange: (scrollLeft: number, scrollTop: number) => void
) {
  const dragStateRef = useRef<{
    moved: boolean;
    pointerId: number;
    scrollLeft: number;
    scrollTop: number;
    startX: number;
    startY: number;
  } | null>(null);
  const suppressNextClickRef = useRef(false);
  const [dragging, setDragging] = useState(false);

  const finishDrag = useCallback((event: ReactPointerEvent<HTMLDivElement>) => {
    const dragState = dragStateRef.current;
    if (!dragState || dragState.pointerId !== event.pointerId) {
      return;
    }

    dragStateRef.current = null;
    suppressNextClickRef.current = dragState.moved;
    setDragging(false);
    try {
      event.currentTarget.releasePointerCapture(event.pointerId);
    } catch {
      // Pointer capture is best-effort across DOM test and browser environments.
    }
  }, []);

  return {
    dragging,
    onClickCapture: useCallback((event: ReactPointerEvent<HTMLDivElement>) => {
      if (!suppressNextClickRef.current) {
        return;
      }

      suppressNextClickRef.current = false;
      event.preventDefault();
      event.stopPropagation();
    }, []),
    onPointerCancel: finishDrag,
    onPointerDown: useCallback((event: ReactPointerEvent<HTMLDivElement>) => {
      if (event.button !== 0 || !event.isPrimary) {
        return;
      }

      if (isWorkflowGraphDragInteractiveTarget(event.target)) {
        return;
      }

      const viewport = viewportRef.current;
      if (!viewport) {
        return;
      }

      dragStateRef.current = {
        moved: false,
        pointerId: event.pointerId,
        scrollLeft: viewport.scrollLeft,
        scrollTop: viewport.scrollTop,
        startX: event.clientX,
        startY: event.clientY,
      };
      suppressNextClickRef.current = false;
      setDragging(true);
      try {
        event.currentTarget.setPointerCapture(event.pointerId);
      } catch {
        // Pointer capture is best-effort across DOM test and browser environments.
      }
    }, [viewportRef]),
    onPointerMove: useCallback((event: ReactPointerEvent<HTMLDivElement>) => {
      const dragState = dragStateRef.current;
      const viewport = viewportRef.current;
      if (!dragState || !viewport || dragState.pointerId !== event.pointerId) {
        return;
      }

      const deltaX = event.clientX - dragState.startX;
      const deltaY = event.clientY - dragState.startY;
      if (!dragState.moved && Math.hypot(deltaX, deltaY) < 4) {
        return;
      }

      dragState.moved = true;
      event.preventDefault();
      viewport.scrollLeft = Math.max(0, dragState.scrollLeft - deltaX);
      viewport.scrollTop = Math.max(0, dragState.scrollTop - deltaY);
      onScrollPositionChange(viewport.scrollLeft, viewport.scrollTop);
    }, [onScrollPositionChange, viewportRef]),
    onPointerUp: finishDrag,
  };
}

function isWorkflowGraphDragInteractiveTarget(target: EventTarget | null) {
  return target instanceof Element &&
    Boolean(target.closest("button,a,input,select,textarea,[role='button'],[role='link']"));
}

function normalizeCollapsedBranchIds(branchIds: string[] | null | undefined) {
  return [...new Set((branchIds ?? []).filter((branchId) => branchId.trim().length > 0))]
    .sort((left, right) => left.localeCompare(right));
}

function normalizeFocusedBranchId(branchId: string | null | undefined) {
  const normalized = branchId?.trim();
  return normalized && normalized.length > 0 ? normalized : null;
}

function createWorkflowFlowUiState(
  state: WorkflowRunConsoleViewUiStateSnapshot & { collapsedBranchIds?: string[] }
): WorkflowRunConsoleViewUiStateSnapshot {
  const collapsedBranchIds = normalizeCollapsedBranchIds(state.collapsedBranchIds);
  const focusedBranchId = normalizeFocusedBranchId(state.focusedBranchId);

  return {
    autoFollowCurrentStep: state.autoFollowCurrentStep,
    collapsedBranchIds,
    ...(focusedBranchId ? { focusedBranchId } : {}),
    runId: state.runId,
    selectedChildWorkerId: state.selectedChildWorkerId,
    selectedChildWorkerPageIndex: state.selectedChildWorkerPageIndex,
    selectedStepName: state.selectedStepName,
    ...(state.workflowGraphScrollLeft !== undefined ? { workflowGraphScrollLeft: state.workflowGraphScrollLeft } : {}),
    ...(state.workflowGraphScrollTop !== undefined ? { workflowGraphScrollTop: state.workflowGraphScrollTop } : {}),
    ...(state.workflowGraphViewState === "detailed" ? { workflowGraphViewState: state.workflowGraphViewState } : {}),
  };
}

export function createWorkflowGraphRenderModel(
  steps: WorkflowStepOperatorView[],
  options: WorkflowGraphRenderModelOptions = {}
): WorkflowGraphRenderModel {
  const hiddenJoinNames: string[] = [];
  const sequence = createWorkflowGraphRenderNodes(
    steps,
    [],
    options,
    hiddenJoinNames
  );

  return {
    currentPath: findWorkflowGraphRenderPath(sequence, (node) => node.current),
    hiddenJoinNames,
    selectedPath: findWorkflowGraphRenderPath(sequence, (node) => node.selected),
    sequence,
  };
}

function createWorkflowGraphRenderNodes(
  steps: WorkflowStepOperatorView[],
  parentPath: string[],
  options: WorkflowGraphRenderModelOptions,
  hiddenJoinNames: string[]
): WorkflowGraphRenderNode[] {
  const nodes: WorkflowGraphRenderNode[] = [];

  for (const step of steps) {
    if (step.kind === "Join") {
      hiddenJoinNames.push(step.name);
      continue;
    }

    const path = [...parentPath, step.name];
    const nestedSequence = step.kind === "Parallel"
      ? []
      : createWorkflowGraphRenderNodes(step.steps, path, options, hiddenJoinNames);
    const branchLanes = step.kind === "Parallel"
      ? createWorkflowGraphBranchLanes(step, path, options, hiddenJoinNames)
      : [];
    const selected = step.name === options.selectedStepName;
    const current = step.name === options.currentStepName;
    const containsSelected = selected ||
      nestedSequence.some((node) => node.containsSelected) ||
      branchLanes.some((lane) => lane.containsSelected);
    const containsCurrent = current ||
      nestedSequence.some((node) => node.containsCurrent) ||
      branchLanes.some((lane) => lane.containsCurrent);

    nodes.push({
      branchLanes,
      containsCurrent,
      containsSelected,
      current,
      kind: step.kind,
      name: step.name,
      nestedSequence,
      path,
      selected,
      status: step.status,
    });
  }

  return nodes;
}

function createWorkflowGraphBranchLanes(
  step: WorkflowStepOperatorView,
  parentPath: string[],
  options: WorkflowGraphRenderModelOptions,
  hiddenJoinNames: string[]
): WorkflowGraphBranchLane[] {
  return step.steps.flatMap((branchStep, index) => {
    const branchNodes = createWorkflowGraphRenderNodes(
      [branchStep],
      parentPath,
      options,
      hiddenJoinNames
    );
    if (branchNodes.length === 0) {
      return [];
    }

    const rootNode = branchNodes[0];
    return [{
      containsCurrent: rootNode.containsCurrent,
      containsSelected: rootNode.containsSelected,
      id: `${step.name}:branch:${index}:${rootNode.name}`,
      index,
      label: rootNode.name,
      parentName: step.name,
      parentPath,
      rootStepName: rootNode.name,
      status: rootNode.status,
      steps: branchNodes,
    }];
  });
}

function findWorkflowGraphRenderPath(
  nodes: WorkflowGraphRenderNode[],
  predicate: (node: WorkflowGraphRenderNode) => boolean
): string[] {
  for (const node of nodes) {
    if (predicate(node)) {
      return node.path;
    }

    const nestedMatch = findWorkflowGraphRenderPath(node.nestedSequence, predicate);
    if (nestedMatch.length > 0) {
      return nestedMatch;
    }

    for (const lane of node.branchLanes) {
      const branchMatch = findWorkflowGraphRenderPath(lane.steps, predicate);
      if (branchMatch.length > 0) {
        return branchMatch;
      }
    }
  }

  return [];
}

function findWorkflowGraphBranchFocus(
  nodes: WorkflowGraphRenderNode[],
  branchId: string,
  breadcrumbs: WorkflowGraphBranchFocusCrumb[] = []
): WorkflowGraphBranchFocus | null {
  for (const node of nodes) {
    for (const lane of node.branchLanes) {
      const crumb = {
        branchId: lane.id,
        index: lane.index,
        parentName: lane.parentName,
        rootStepName: lane.rootStepName,
      };
      const nextBreadcrumbs = [...breadcrumbs, crumb];
      if (lane.id === branchId) {
        return {
          breadcrumbs: nextBreadcrumbs,
          lane,
        };
      }

      const branchMatch = findWorkflowGraphBranchFocus(lane.steps, branchId, nextBreadcrumbs);
      if (branchMatch) {
        return branchMatch;
      }
    }

    const nestedMatch = findWorkflowGraphBranchFocus(node.nestedSequence, branchId, breadcrumbs);
    if (nestedMatch) {
      return nestedMatch;
    }
  }

  return null;
}

function collectWorkflowGraphBranchIdsFromSteps(steps: WorkflowStepOperatorView[]): string[] {
  const model = createWorkflowGraphRenderModel(steps);
  const branchIds: string[] = [];
  collectWorkflowGraphBranchIds(model.sequence, branchIds);
  return branchIds.sort((left, right) => left.localeCompare(right));
}

function collectWorkflowGraphBranchIds(
  nodes: WorkflowGraphRenderNode[],
  branchIds: string[]
) {
  for (const node of nodes) {
    collectWorkflowGraphBranchIds(node.nestedSequence, branchIds);
    for (const lane of node.branchLanes) {
      branchIds.push(lane.id);
      collectWorkflowGraphBranchIds(lane.steps, branchIds);
    }
  }
}

function summarizeWorkflowWorkerProgress(
  steps: WorkflowStepOperatorView[],
  fallback: WorkflowChildWorkerSummary
): WorkflowChildWorkerSummary {
  let total = 0;
  let active = 0;
  let final = 0;

  const visit = (currentSteps: WorkflowStepOperatorView[]) => {
    for (const step of currentSteps) {
      if (step.kind === "DispatchWork" || step.kind === "DispatchEach") {
        total += step.children.total;
        active += step.children.active;
        final += step.children.final;
      }

      if (step.steps.length > 0) {
        visit(step.steps);
      }
    }
  };

  visit(steps);

  if (total <= 0) {
    return fallback;
  }

  return {
    active,
    final,
    total,
  };
}

function findFirstDispatchWorkflowStep(
  steps: WorkflowStepOperatorView[]
): WorkflowStepOperatorView | null {
  for (const step of steps) {
    if (step.kind === "DispatchWork") {
      return step;
    }

    const nested = findFirstDispatchWorkflowStep(step.steps);
    if (nested) {
      return nested;
    }
  }

  return null;
}

function resolveWorkflowSelectionStepName(
  steps: WorkflowStepOperatorView[],
  preferredStepName?: string | null
) {
  const preferredStep = preferredStepName
    ? findWorkflowStepByName(steps, preferredStepName)
    : null;
  if (isWorkflowStepSelectable(preferredStep)) {
    return preferredStep.name;
  }

  return findFirstDispatchWorkflowStep(steps)?.name ?? steps[0]?.name ?? null;
}

function resolveWorkflowInitialSelectionStepName(
  steps: WorkflowStepOperatorView[],
  initialSelectedStepName?: string | null,
  preferredStepName?: string | null
) {
  const initialSelectedStep = initialSelectedStepName
    ? findWorkflowStepByName(steps, initialSelectedStepName)
    : null;
  if (isWorkflowStepSelectable(initialSelectedStep)) {
    return initialSelectedStep.name;
  }

  return resolveWorkflowSelectionStepName(steps, preferredStepName);
}

function isWorkflowStepSelectable(step: WorkflowStepOperatorView | null | undefined): step is WorkflowStepOperatorView {
  return Boolean(step && step.kind !== "Join");
}

function findWorkflowStepByName(
  steps: WorkflowStepOperatorView[],
  stepName: string
): WorkflowStepOperatorView | null {
  for (const step of steps) {
    if (step.name === stepName) {
      return step;
    }

    const nested = findWorkflowStepByName(step.steps, stepName);
    if (nested) {
      return nested;
    }
  }

  return null;
}

function flattenWorkflowSteps(steps: WorkflowStepOperatorView[]) {
  const flattened: WorkflowStepOperatorView[] = [];
  const visit = (currentSteps: WorkflowStepOperatorView[]) => {
    for (const step of currentSteps) {
      flattened.push(step);
      if (step.steps.length > 0) {
        visit(step.steps);
      }
    }
  };

  visit(steps);
  return flattened;
}

function WorkflowFlowConnector() {
  return (
    <div
      aria-hidden="true"
      className="flex min-w-12 shrink-0 items-start justify-center pt-8 text-muted-foreground"
    >
      <div className="flex items-center gap-1">
        <div className="h-px w-6 bg-border/80" />
        <ArrowRight className="size-4" />
        <div className="h-px w-6 bg-border/80" />
      </div>
    </div>
  );
}

function WorkflowGraphNodeColumn({
  collapsedBranchIds,
  node,
  onFocusBranch,
  onRegisterStepElement,
  onSelectStep,
  onToggleBranchCollapse,
}: {
  collapsedBranchIds: ReadonlySet<string>;
  node: WorkflowGraphRenderNode;
  onFocusBranch: (lane: WorkflowGraphBranchLane) => void;
  onRegisterStepElement: (stepName: string, element: HTMLElement | null) => void;
  onSelectStep: (stepName: string) => void;
  onToggleBranchCollapse: (branchId: string) => void;
}) {
  return (
    <div className="flex w-fit flex-col gap-3">
      <WorkflowGraphRenderNodeCard
        node={node}
        onSelect={() => onSelectStep(node.name)}
        registerElement={(element) => onRegisterStepElement(node.name, element)}
        size="full"
      />
      {node.branchLanes.length > 0 ? (
        <WorkflowParallelBranchLanes
          collapsedBranchIds={collapsedBranchIds}
          lanes={node.branchLanes}
          onFocusBranch={onFocusBranch}
          onRegisterStepElement={onRegisterStepElement}
          onSelectStep={onSelectStep}
          onToggleBranchCollapse={onToggleBranchCollapse}
        />
      ) : null}
      {node.nestedSequence.length > 0 ? (
        <WorkflowNestedStepSequence
          collapsedBranchIds={collapsedBranchIds}
          nodes={node.nestedSequence}
          onFocusBranch={onFocusBranch}
          onRegisterStepElement={onRegisterStepElement}
          onSelectStep={onSelectStep}
          onToggleBranchCollapse={onToggleBranchCollapse}
        />
      ) : null}
    </div>
  );
}

function WorkflowNestedStepSequence({
  collapsedBranchIds,
  nodes,
  onFocusBranch,
  onRegisterStepElement,
  onSelectStep,
  onToggleBranchCollapse,
}: {
  collapsedBranchIds: ReadonlySet<string>;
  nodes: WorkflowGraphRenderNode[];
  onFocusBranch: (lane: WorkflowGraphBranchLane) => void;
  onRegisterStepElement: (stepName: string, element: HTMLElement | null) => void;
  onSelectStep: (stepName: string) => void;
  onToggleBranchCollapse: (branchId: string) => void;
}) {
  return (
    <div className="flex min-w-0 items-stretch gap-2">
      {nodes.map((node, index, list) => (
        <Fragment key={node.name}>
          <div className="flex min-w-0 flex-col gap-2">
            <WorkflowGraphRenderNodeCard
              node={node}
              onSelect={() => onSelectStep(node.name)}
              registerElement={(element) => onRegisterStepElement(node.name, element)}
              size="compact"
            />
            {node.branchLanes.length > 0 ? (
              <WorkflowParallelBranchLanes
                collapsedBranchIds={collapsedBranchIds}
                compact
                lanes={node.branchLanes}
                onFocusBranch={onFocusBranch}
                onRegisterStepElement={onRegisterStepElement}
                onSelectStep={onSelectStep}
                onToggleBranchCollapse={onToggleBranchCollapse}
              />
            ) : null}
            {node.nestedSequence.length > 0 ? (
              <WorkflowNestedStepSequence
                collapsedBranchIds={collapsedBranchIds}
                nodes={node.nestedSequence}
                onFocusBranch={onFocusBranch}
                onRegisterStepElement={onRegisterStepElement}
                onSelectStep={onSelectStep}
                onToggleBranchCollapse={onToggleBranchCollapse}
              />
            ) : null}
          </div>
          {index < list.length - 1 ? <WorkflowCompactConnector /> : null}
        </Fragment>
      ))}
    </div>
  );
}

function WorkflowParallelBranchLanes({
  collapsedBranchIds,
  compact = false,
  lanes,
  onFocusBranch,
  onRegisterStepElement,
  onSelectStep,
  onToggleBranchCollapse,
}: {
  collapsedBranchIds: ReadonlySet<string>;
  compact?: boolean;
  lanes: WorkflowGraphBranchLane[];
  onFocusBranch: (lane: WorkflowGraphBranchLane) => void;
  onRegisterStepElement: (stepName: string, element: HTMLElement | null) => void;
  onSelectStep: (stepName: string) => void;
  onToggleBranchCollapse: (branchId: string) => void;
}) {
  return (
    <div className={cn(
      "rounded-xl border border-sky-500/25 bg-sky-500/[0.04] p-3 shadow-inner",
      compact ? "max-w-[30rem]" : "min-w-[22rem]"
    )}>
      <div className="mb-3 flex min-w-0 items-center gap-2 text-sky-200 text-xs uppercase tracking-[0.14em]">
        <GitBranch className="size-3.5 shrink-0" />
        <span className="font-semibold">Branches</span>
        <Badge className="h-5 border-sky-500/30 bg-sky-500/10 px-1.5 text-sky-100" variant="outline">
          {lanes.length}
        </Badge>
      </div>
      <div className="space-y-3">
        {lanes.map((lane, index) => {
          const collapsed = collapsedBranchIds.has(lane.id);
          const CollapseIcon = collapsed ? ChevronRight : ChevronDown;

          return (
            <div
              className={cn(
                "relative overflow-hidden rounded-lg border bg-card p-3 shadow-sm",
                lane.containsSelected
                  ? "border-sky-400/70 bg-sky-500/10"
                  : lane.containsCurrent
                    ? "border-emerald-500/60 bg-emerald-500/10"
                    : "border-sky-500/25"
              )}
              key={lane.id}
            >
              <div className={cn(
                "absolute inset-y-2 left-0 w-1 rounded-r-full",
                lane.containsSelected
                  ? "bg-sky-400"
                  : lane.containsCurrent
                    ? "bg-emerald-400"
                    : "bg-sky-500/45"
              )} />
              <div className="mb-3 flex min-w-0 items-center gap-2 pl-2 text-xs">
                <Button
                  aria-expanded={!collapsed}
                  aria-label={`${collapsed ? "Expand" : "Collapse"} branch ${lane.rootStepName}`}
                  className="size-6 shrink-0 rounded-md border-sky-500/30 bg-sky-500/10 p-0 text-sky-100 hover:bg-sky-500/20"
                  onClick={() => onToggleBranchCollapse(lane.id)}
                  size="icon-sm"
                  title={`${collapsed ? "Expand" : "Collapse"} branch ${lane.rootStepName}`}
                  type="button"
                  variant="outline"
                >
                  <CollapseIcon className="size-3.5" />
                </Button>
                <Button
                  aria-label={`Focus branch ${lane.rootStepName}`}
                  className="size-6 shrink-0 rounded-md border-sky-500/30 bg-sky-500/10 p-0 text-sky-100 hover:bg-sky-500/20"
                  onClick={() => onFocusBranch(lane)}
                  size="icon-sm"
                  title={`Focus branch ${lane.rootStepName}`}
                  type="button"
                  variant="outline"
                >
                  <Maximize2 className="size-3.5" />
                </Button>
                <Badge className="border-sky-500/30 bg-sky-500/10 text-sky-100" variant="outline">
                  Branch {index + 1}
                </Badge>
                <button
                  className="min-w-0 truncate rounded px-1 py-0.5 font-mono text-muted-foreground text-xs hover:bg-sky-500/10 hover:text-foreground"
                  onClick={() => onSelectStep(lane.rootStepName)}
                  title={`Select ${lane.rootStepName}`}
                  type="button"
                >
                  {lane.rootStepName}
                </button>
                {lane.status === "Running" ? <WorkflowExecutingIndicator /> : null}
              </div>
              {!collapsed ? (
                <WorkflowNestedStepSequence
                  collapsedBranchIds={collapsedBranchIds}
                  nodes={lane.steps}
                  onFocusBranch={onFocusBranch}
                  onRegisterStepElement={onRegisterStepElement}
                  onSelectStep={onSelectStep}
                  onToggleBranchCollapse={onToggleBranchCollapse}
                />
              ) : null}
            </div>
          );
        })}
      </div>
    </div>
  );
}

function WorkflowCompactConnector() {
  return (
    <div
      aria-hidden="true"
      className="flex min-w-5 shrink-0 items-start justify-center pt-4 text-muted-foreground"
    >
      <ArrowRight className="size-3.5" />
    </div>
  );
}

function WorkflowGraphRenderNodeCard({
  node,
  onSelect,
  registerElement,
  size,
}: {
  onSelect: () => void;
  node: WorkflowGraphRenderNode;
  registerElement?: (element: HTMLElement | null) => void;
  size: "compact" | "full";
}) {
  const compact = size === "compact";
  const showStatusBadge = node.kind !== "Parallel" || node.status !== "Completed";

  return (
    <WorkflowNodeCardShell
      compact={compact}
      current={node.current}
      icon={renderWorkflowStepKindIcon(node.kind)}
      metaRow={(
        <>
          <Badge variant="outline">
            {formatWorkflowStepKind(node.kind)}
          </Badge>
          {showStatusBadge ? <WorkflowNodeStatusBadge status={node.status} /> : null}
        </>
      )}
      onSelect={onSelect}
      registerElement={registerElement}
      selected={node.selected}
      status={node.status}
      title={node.name}
    />
  );
}

function WorkflowNodeCardShell({
  compact = false,
  current,
  icon,
  metaRow,
  onSelect,
  registerElement,
  selected,
  status,
  title,
}: {
  compact?: boolean;
  current: boolean;
  icon: ReactNode;
  metaRow: ReactNode;
  onSelect: () => void;
  registerElement?: (element: HTMLElement | null) => void;
  selected: boolean;
  status: WorkflowOperatorNodeStatus;
  title: string;
}) {
  return (
    <button
      aria-current={current ? "step" : undefined}
      aria-pressed={selected}
      className={cn(
        "inline-flex self-start rounded-2xl border bg-card/95 text-left shadow-sm backdrop-blur-sm transition-colors hover:border-border",
        compact ? "min-w-[10rem] max-w-[14rem] p-2.5" : "min-w-[18rem] p-4",
        workflowNodeCardSurfaceClassName(status, current, selected),
        selected
          ? "[box-shadow:inset_0_0_0_2px_rgba(56,189,248,0.75),0_8px_24px_rgba(14,165,233,0.12)]"
          : "ring-1 ring-transparent"
      )}
      onClick={onSelect}
      ref={registerElement}
      type="button"
    >
      <div className={cn(
        "grid grid-cols-[auto_minmax(0,1fr)] items-start",
        compact
          ? "grid-rows-[minmax(1.25rem,auto)_minmax(1.75rem,auto)] gap-x-2 gap-y-1.5"
          : "grid-rows-[minmax(1.5rem,auto)_minmax(2rem,auto)] gap-x-3 gap-y-3"
      )}>
        <div
          className={cn(
            "row-span-2 flex shrink-0 items-center justify-center rounded-xl border self-start",
            compact ? "size-7" : "size-9",
            workflowNodeIconFrameClassName(status, current)
          )}
        >
          {icon}
        </div>
        <div className="flex min-w-0 items-center gap-2 self-center">
          <div className={cn("truncate font-semibold", compact ? "text-xs" : "text-sm")}>{title}</div>
          {isWorkflowStepExecuting(status) ? <WorkflowExecutingIndicator /> : null}
        </div>
        <div className="flex min-w-0 flex-wrap items-center gap-2 self-center">
          {metaRow}
        </div>
      </div>
    </button>
  );
}

function WorkflowWorkerProgressSummary({
  executing,
  summary,
}: {
  executing: boolean;
  summary: WorkflowChildWorkerSummary;
}) {
  if (summary.total <= 0) {
    return executing ? <WorkflowExecutingIndicator /> : null;
  }

  const finalCount = Math.min(summary.final, summary.total);
  const activeCount = Math.min(summary.active, Math.max(0, summary.total - finalCount));
  const completedPercent = summary.total > 0 ? (finalCount / summary.total) * 100 : 0;
  const activePercent = summary.total > 0 ? (activeCount / summary.total) * 100 : 0;

  return (
    <div className="flex w-full items-center gap-3">
      <div
        aria-label={`Workflow worker progress (${finalCount}/${summary.total}${activeCount > 0 ? `, ${activeCount} active` : ""})`}
        className="relative h-3 flex-1 overflow-hidden rounded-full border border-border/70 bg-[var(--status-neutral-soft)]"
        role="img"
      >
        {finalCount > 0 ? (
          <div
            className={cn("absolute inset-y-0 left-0", semanticIndicatorToneClass("success"))}
            style={{ width: `${completedPercent}%` }}
          />
        ) : null}
        {activeCount > 0 ? (
          <div
            className={cn("absolute inset-y-0", semanticIndicatorToneClass("info"))}
            style={{ left: `${completedPercent}%`, width: `${activePercent}%` }}
          />
        ) : null}
        {summary.total > 1 ? (
          <div
            aria-hidden="true"
            className="absolute inset-0 grid divide-x divide-background/50"
            style={{ gridTemplateColumns: `repeat(${summary.total}, minmax(0, 1fr))` }}
          >
            {Array.from({ length: summary.total }).map((_, index) => (
              <span key={index} />
            ))}
          </div>
        ) : null}
      </div>
      {executing ? <WorkflowExecutingIndicator /> : null}
    </div>
  );
}

function WorkflowExecutingIndicator() {
  return (
    <span
      aria-label="Executing"
      className={cn(
        "inline-flex size-5 shrink-0 items-center justify-center rounded-full border",
        semanticBadgeToneClass("info")
      )}
      title="Executing"
    >
      <Loader2 aria-hidden="true" className="size-3 animate-spin" />
    </span>
  );
}

function renderWorkflowStepKindIcon(kind: WorkflowStepKind) {
  switch (kind) {
    case "DispatchEach":
    case "Parallel":
    case "Branch":
      return <GitBranch className="size-4" />;
    case "Join":
      return <Workflow className="size-4" />;
    default:
      return <Square className="size-4" />;
  }
}

function workflowNodeIconFrameClassName(
  status: WorkflowOperatorNodeStatus,
  current = false
) {
  if (current) {
    return "border-emerald-500/30 bg-emerald-500/10 text-emerald-300";
  }

  switch (status) {
    case "Completed":
      return "border-emerald-500/30 bg-emerald-500/10 text-emerald-300";
    case "Blocked":
    case "Failed":
      return "border-red-500/30 bg-red-500/10 text-red-300";
    case "Paused":
    case "Canceled":
      return "border-amber-500/30 bg-amber-500/10 text-amber-300";
    case "Running":
    case "WaitingOnChildren":
      return "border-sky-500/30 bg-sky-500/10 text-sky-300";
    default:
      return "border-border/70 bg-muted/40 text-muted-foreground";
  }
}

function workflowNodeCardSurfaceClassName(
  status: WorkflowOperatorNodeStatus,
  current = false,
  selected = false
) {
  if (selected) {
    return "border-sky-400/55 bg-sky-500/6 shadow-sky-500/20";
  }

  if (current) {
    return "border-emerald-500/50 bg-emerald-500/5 shadow-emerald-500/10";
  }

  switch (status) {
    case "Blocked":
    case "Failed":
      return "border-red-500/45 shadow-red-500/10";
    case "Paused":
    case "Canceled":
      return "border-amber-500/45 shadow-amber-500/10";
    case "Running":
    case "WaitingOnChildren":
      return "border-sky-500/45 shadow-sky-500/10";
    default:
      return "border-border/80";
  }
}

function isWorkflowStepExecuting(status?: WorkflowOperatorNodeStatus | null) {
  return status === "Running" || status === "WaitingOnChildren";
}

function WorkflowNodeStatusBadge({ status }: { status: WorkflowOperatorNodeStatus }) {
  if (status === "WaitingOnChildren") {
    return null;
  }

  return (
    <Badge className={semanticBadgeToneClass(workflowNodeTone(status))} variant="secondary">
      {status}
    </Badge>
  );
}

function workflowRunTone(status: WorkflowRunStatus) {
  switch (status) {
    case "Completed":
      return "success";
    case "Blocked":
    case "Failed":
    case "Invalid":
    case "NotFound":
    case "Unauthorized":
      return "danger";
    case "Paused":
    case "Canceled":
      return "warning";
    default:
      return "info";
  }
}

function workflowNodeTone(status: WorkflowOperatorNodeStatus) {
  switch (status) {
    case "Completed":
      return "success";
    case "Blocked":
    case "Failed":
      return "danger";
    case "Paused":
    case "Canceled":
      return "warning";
    case "Running":
    case "WaitingOnChildren":
      return "info";
    default:
      return "neutral";
  }
}

function workerStateTone(state: string) {
  switch (state) {
    case "Completed":
      return "success";
    case "Failed":
    case "Interrupted":
      return "danger";
    case "Canceled":
    case "Canceling":
    case "Paused":
    case "Pausing":
      return "warning";
    default:
      return "info";
  }
}

function isWorkflowWorkerExecuting(state: string) {
  switch (state) {
    case "Queued":
    case "Running":
    case "Waiting":
    case "Retrying":
    case "Pausing":
    case "Interrupting":
    case "Canceling":
      return true;
    default:
      return false;
  }
}

function WorkflowActionButton({
  action,
  disabled,
  executionMayStop,
  icon: Icon,
  loading,
  onAction,
  tooltip,
}: WorkflowActionButtonProps) {
  const toneClassName = consoleActionToneClassName(disabled === true);
  const confirmProps = createExecutionControlConfirmProps(action, "workflow", executionMayStop);

  return (
    <ConsoleActionButton
      className={toneClassName}
      disabled={disabled}
      icon={Icon}
      label={action}
      loading={loading}
      onAction={() => onAction(action)}
      tooltip={tooltip}
      {...confirmProps}
    />
  );
}

function formatWorkflowStepKind(kind?: WorkflowStepKind | null) {
  switch (kind) {
    case "DispatchWork":
      return "Dispatch";
    case "DispatchEach":
      return "Dispatch Each";
    case "Parallel":
      return "Parallel";
    case "Branch":
      return "Branch";
    case "Join":
      return "Join";
    default:
      return "Step";
  }
}

function formatWorkflowRunStatusTiming(run: WorkflowRunDetailView, now: number) {
  switch (run.status) {
    case "Running":
    case "Paused":
    case "Blocked":
      return formatElapsedSince(run.startedAt ?? run.createdAt, now);
    case "Failed":
      return formatElapsedSince(run.completedAt ?? run.startedAt ?? run.createdAt, now);
    default:
      return null;
  }
}


function firstWorkflowMessage(messages?: WorkMessage[] | null) {
  return messages?.find((message) => Boolean(message.text))?.text;
}

function isFinalWorkflowRunStatus(status?: WorkflowRunStatus | null) {
  return status === "Completed" || status === "Failed" || status === "Canceled";
}

function appendQueryString(path: string, params: URLSearchParams) {
  const query = params.toString();
  return query ? `${path}?${query}` : path;
}

function useWorkableResource<T>(
  connection: WorkableConnection,
  path: string | null,
  refreshToken: number,
  options?: {
    retainDataOnNull?: boolean;
    resetKey?: string | number | null;
  }
) {
  const [state, setState] = useState<{
    data?: T;
    error?: string;
    errorCause?: unknown;
    loading: boolean;
    refreshing?: boolean;
  }>({ loading: !!path });
  const apiUrl = connection.apiUrl;
  const systemName = connection.systemName;
  const retainDataOnNull = options?.retainDataOnNull === true;
  const resetKey = options?.resetKey ?? null;
  const lastResetKeyRef = useRef<string | number | null>(resetKey);

  useEffect(() => {
    if (!path) {
      queueMicrotask(() => setState((current) => {
        const sameResetKey = lastResetKeyRef.current === resetKey;
        lastResetKeyRef.current = resetKey;

        if (retainDataOnNull && sameResetKey && current.data !== undefined) {
          return {
            data: current.data,
            errorCause: undefined,
            loading: false,
            refreshing: false,
          };
        }

        return { loading: false };
      }));
      return;
    }

    let canceled = false;
    lastResetKeyRef.current = resetKey;
    queueMicrotask(() => {
      if (!canceled) {
        setState((current) => ({
          ...current,
          error: undefined,
          errorCause: undefined,
          loading: current.data === undefined,
          refreshing: current.data !== undefined,
        }));
      }
    });

    workableFetch<T>({ apiUrl, systemName }, path)
      .then((data) => {
        if (!canceled) {
          setState({ data, errorCause: undefined, loading: false, refreshing: false });
        }
      })
      .catch((error) => {
        if (!canceled) {
          const detail = error instanceof Error ? error.message : "Request failed.";
          setState((current) => ({
            data: current.data,
            error: detail,
            errorCause: error,
            loading: false,
            refreshing: false,
          }));
        }
      });

    return () => {
      canceled = true;
    };
  }, [apiUrl, path, refreshToken, resetKey, retainDataOnNull, systemName]);

  return state;
}
