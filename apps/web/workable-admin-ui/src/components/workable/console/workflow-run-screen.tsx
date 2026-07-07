"use client";

import {
  ArrowRight,
  Ban,
  GitBranch,
  Loader2,
  Pause,
  Play,
  Rows4,
  Square,
  Workflow,
} from "lucide-react";
import type { ReactNode } from "react";
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
import { PanelShell } from "@/components/features/console/panel-shell";
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
import { StatusCountPill } from "@/components/workable/console/status-count-pill";
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
  runId: string;
  selectedChildWorkerId?: string | null;
  selectedChildWorkerPageIndex?: number;
  selectedStepName: string | null;
  workflowGraphScrollTop?: number;
};
type WorkflowActionResult = {
  action: WorkflowAction;
  messages?: WorkMessage[];
  run?: WorkflowRunStateOverlay | null;
  runId: string;
  status: string;
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
  const restoredWorkflowGraphScrollTop = initialUiState?.runId === workflowRunId
    ? normalizeWorkflowGraphScrollTop(initialUiState.workflowGraphScrollTop)
    : 0;
  const workflowGraphScrollRef = useRef<HTMLDivElement | null>(null);
  const workflowGraphContentRef = useRef<HTMLDivElement | null>(null);
  const workflowGraphScrollTopRef = useRef(restoredWorkflowGraphScrollTop);
  const restoredWorkflowGraphScrollKeyRef = useRef<string | null>(null);
  useEffect(() => {
    workflowGraphScrollTopRef.current = restoredWorkflowGraphScrollTop;
    restoredWorkflowGraphScrollKeyRef.current = null;
  }, [restoredWorkflowGraphScrollTop, workflowRunId]);
  useEffect(() => {
    if (!run || hiddenPanelIds.has("workflowGraph")) {
      return;
    }

    const restoreKey = `${workflowRunId}:${restoredWorkflowGraphScrollTop}`;
    if (restoredWorkflowGraphScrollKeyRef.current === restoreKey) {
      return;
    }

    const scrollContainer = workflowGraphScrollRef.current;
    if (!scrollContainer) {
      return;
    }

    const restore = () => {
      scrollContainer.scrollTop = restoredWorkflowGraphScrollTop;
      workflowGraphScrollTopRef.current = scrollContainer.scrollTop;

      if (scrollContainer.scrollTop >= restoredWorkflowGraphScrollTop) {
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
  }, [hiddenPanelIds, restoredWorkflowGraphScrollTop, run, workflowRunId]);
  const createWorkflowRunUiStateSnapshot = useCallback((state: WorkflowRunConsoleViewUiStateSnapshot) => ({
    ...state,
    workflowGraphScrollTop: workflowGraphScrollTopRef.current,
  }), []);
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
            message={workflowDetail.error}
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
            {!hiddenPanelIds.has("workflowControls") ? (
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
              />
            ) : null}
            {!hiddenPanelIds.has("workflowGraph") ? (
              <PanelShell
                onClose={() => setPanelVisible("workflowGraph", false)}
                title="Workflow Graph"
              >
                <div
                  className="workable-grid-scrollbar max-h-[70vh] overflow-auto pr-1"
                  onScroll={(event) => {
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
    onUiStateChange?.({
      autoFollowCurrentStep,
      runId: workflowRunId,
      selectedChildWorkerId,
      selectedChildWorkerPageIndex,
      selectedStepName,
    });
  }, [
    autoFollowCurrentStep,
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
  const handleWorkerPageIndexChange = useCallback((pageIndex: number) => {
    setSelectedChildWorkerPageIndex(normalizeWorkflowWorkerPageIndex(pageIndex));
  }, []);
  const handleOpenWorker = useCallback((workerId: string) => {
    const nextUiState = {
      autoFollowCurrentStep,
      runId: workflowRunId,
      selectedChildWorkerId: workerId,
      selectedChildWorkerPageIndex,
      selectedStepName,
    } satisfies WorkflowRunConsoleViewUiStateSnapshot;
    setSelectedChildWorkerId(workerId);
    onUiStateChange?.(nextUiState);
    onOpenWorker(workerId, nextUiState);
  }, [
    autoFollowCurrentStep,
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
      <div className="rounded-2xl border border-border/70 bg-[radial-gradient(circle_at_top,_rgba(56,189,248,0.08),transparent_40%),linear-gradient(to_bottom,_rgba(255,255,255,0.02),transparent)] px-4 pt-4 pb-2 shadow-sm">
        <div className="mb-4">
          <WorkflowWorkerProgressSummary
            executing={isWorkflowStepExecuting(currentActivity?.status ?? currentStepStatus)}
            summary={progressSummary}
          />
        </div>
        <div ref={graphViewportRef} className="workable-grid-scrollbar overflow-x-auto pb-2">
          <div className="w-max min-w-full pb-1">
            <WorkflowStructureSequence
              currentStepName={highlightedCurrentStepName}
              onRegisterStepElement={registerStepElement}
              onSelectStep={handleSelectStep}
              selectedStepName={selectedStepName}
              steps={steps}
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
  currentStepName,
  onRegisterStepElement,
  onSelectStep,
  selectedStepName,
  steps,
}: {
  currentStepName?: string | null;
  onRegisterStepElement: (stepName: string, element: HTMLElement | null) => void;
  onSelectStep: (stepName: string) => void;
  selectedStepName?: string | null;
  steps: WorkflowStepOperatorView[];
}) {
  const renderedSteps = useMemo(
    () => filterRenderedWorkflowSteps(steps, currentStepName),
    [currentStepName, steps]
  );

  return (
    <div className="flex w-max min-w-full items-stretch gap-3">
      {renderedSteps.map((step, index, list) => (
        <Fragment key={step.name}>
          <WorkflowFlowStepColumn
            currentStepName={currentStepName}
            onRegisterStepElement={onRegisterStepElement}
            onSelectStep={onSelectStep}
            selectedStepName={selectedStepName}
            step={step}
          />
          {index < list.length - 1 ? <WorkflowFlowConnector /> : null}
        </Fragment>
      ))}
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

function filterRenderedWorkflowSteps(
  steps: WorkflowStepOperatorView[],
  _currentStepName?: string | null
) {
  return steps.filter((step) => step.kind !== "Join");
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
) {
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
      className="flex min-w-12 self-stretch items-center justify-center text-muted-foreground"
    >
      <div className="flex items-center gap-1">
        <div className="h-px w-6 bg-border/80" />
        <ArrowRight className="size-4" />
        <div className="h-px w-6 bg-border/80" />
      </div>
    </div>
  );
}

function WorkflowFlowStepColumn({
  currentStepName,
  onRegisterStepElement,
  onSelectStep,
  selectedStepName,
  step,
}: {
  currentStepName?: string | null;
  onRegisterStepElement: (stepName: string, element: HTMLElement | null) => void;
  onSelectStep: (stepName: string) => void;
  selectedStepName?: string | null;
  step: WorkflowStepOperatorView;
}) {
  if (step.kind === "Parallel") {
    return (
      <div className="flex w-fit flex-col gap-4">
        <WorkflowParallelSummaryCard
          current={step.name === currentStepName}
          onSelect={() => onSelectStep(step.name)}
          registerElement={(element) => onRegisterStepElement(step.name, element)}
          selected={step.name === selectedStepName}
          step={step}
        />
      </div>
    );
  }

  if (step.kind === "Join") {
    return (
      <div className="flex w-fit flex-col gap-4">
        <WorkflowJoinCard
          current={step.name === currentStepName}
          onSelect={() => onSelectStep(step.name)}
          registerElement={(element) => onRegisterStepElement(step.name, element)}
          selected={step.name === selectedStepName}
          step={step}
        />
      </div>
    );
  }

  return (
    <div className="flex w-fit flex-col gap-4">
      <WorkflowStructureNodeCard
        current={step.name === currentStepName}
        onSelect={() => onSelectStep(step.name)}
        registerElement={(element) => onRegisterStepElement(step.name, element)}
        selected={step.name === selectedStepName}
        step={step}
      />
    </div>
  );
}

function WorkflowStructureNodeCard({
  current,
  onSelect,
  registerElement,
  selected,
  step,
}: {
  current: boolean;
  onSelect: () => void;
  registerElement?: (element: HTMLElement | null) => void;
  selected: boolean;
  step: WorkflowStepOperatorView;
}) {
  return (
    <WorkflowNodeCardShell
      current={current}
      icon={renderWorkflowStepKindIcon(step.kind)}
      metaRow={(
        <>
          <Badge variant="outline">
            {formatWorkflowStepKind(step.kind)}
          </Badge>
          <WorkflowNodeStatusBadge status={step.status} />
        </>
      )}
      onSelect={onSelect}
      registerElement={registerElement}
      selected={selected}
      status={step.status}
      title={step.name}
    />
  );
}

function WorkflowJoinCard({
  current,
  onSelect,
  registerElement,
  selected,
  step,
}: {
  current: boolean;
  onSelect: () => void;
  registerElement?: (element: HTMLElement | null) => void;
  selected: boolean;
  step: WorkflowStepOperatorView;
}) {
  return (
    <WorkflowNodeCardShell
      current={current}
      icon={<Workflow className="size-4" />}
      metaRow={(
        <>
          <Badge variant="outline">Join</Badge>
          <WorkflowNodeStatusBadge status={step.status} />
        </>
      )}
      onSelect={onSelect}
      registerElement={registerElement}
      selected={selected}
      status={step.status}
      title={step.name}
    />
  );
}

function WorkflowParallelSummaryCard({
  current,
  onSelect,
  registerElement,
  selected,
  step,
}: {
  current: boolean;
  onSelect: () => void;
  registerElement?: (element: HTMLElement | null) => void;
  selected: boolean;
  step: WorkflowStepOperatorView;
}) {
  const totalBranches = step.steps.length;
  const completedBranches = step.steps.filter((child) => child.status === "Completed").length;
  const showStatusBadge = step.status !== "Completed";
  const completedLabelValue = step.status === "Completed"
    ? String(completedBranches)
    : `${completedBranches}/${totalBranches}`;

  return (
    <WorkflowNodeCardShell
      current={current}
      icon={<GitBranch className="size-4" />}
      metaRow={(
        <>
          <Badge variant="outline">Parallel</Badge>
          <StatusCountPill
            badgeClassName={semanticBadgeToneClass("success")}
            label="Completed"
            value={completedLabelValue}
            valueClassName={semanticTextToneClass("success", "strong")}
          />
          {showStatusBadge ? <WorkflowNodeStatusBadge status={step.status} /> : null}
        </>
      )}
      onSelect={onSelect}
      registerElement={registerElement}
      selected={selected}
      status={step.status}
      title={step.name}
    />
  );
}

function WorkflowNodeCardShell({
  current,
  icon,
  metaRow,
  onSelect,
  registerElement,
  selected,
  status,
  title,
}: {
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
        "inline-flex min-w-[18rem] self-start rounded-2xl border bg-card/95 p-4 text-left shadow-sm backdrop-blur-sm transition-colors hover:border-border",
        workflowNodeCardSurfaceClassName(status, current, selected),
        selected
          ? "[box-shadow:inset_0_0_0_2px_rgba(56,189,248,0.75),0_8px_24px_rgba(14,165,233,0.12)]"
          : "ring-1 ring-transparent"
      )}
      onClick={onSelect}
      ref={registerElement}
      type="button"
    >
      <div className="grid grid-cols-[auto_minmax(0,1fr)] grid-rows-[minmax(1.5rem,auto)_2rem] items-center gap-x-3 gap-y-3">
        <div
          className={cn(
            "row-span-2 flex size-9 shrink-0 items-center justify-center rounded-xl border self-start",
            workflowNodeIconFrameClassName(status, current)
          )}
        >
          {icon}
        </div>
        <div className="flex min-w-0 items-center gap-2 self-center">
          <div className="truncate font-semibold text-sm">{title}</div>
          {isWorkflowStepExecuting(status) ? <WorkflowExecutingIndicator /> : null}
        </div>
        <div className="flex min-w-0 flex-nowrap items-center gap-2 self-center">
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
