export const DEFAULT_WORKABLE_API_URL = "http://localhost:61932/workable";

export type WorkableConnection = {
  apiUrl: string;
  executionDiagnosticsPersistenceAvailable?: boolean;
  realtimeHubPath?: string | null;
  systemName?: string;
};

export type WorkableRealtimeEventCriteria = {
  definitionNames?: string[] | null;
  eventTypes?: string[] | null;
  keys?: WorkableRealtimeEventKeyCriteria[] | null;
};

export type WorkableRealtimeEventKeyCriteria = {
  kind?: WorkKeyKind | null;
  type: string;
  value: string;
};

export type WorkableRealtimeOriginActor = {
  id?: string | null;
  name?: string | null;
  email?: string | null;
};

export type WorkableRealtimeOrigin = {
  channel: string;
  surface?: string | null;
  actor?: WorkableRealtimeOriginActor | null;
  description?: string | null;
  url?: string | null;
};

export type WorkableRealtimeEvent = {
  occurredAt: string;
  workSystemName?: string | null;
  workerId?: { value: string } | null;
  workDefinitionName?: string | null;
  subjectId?: WorkTypedValue | null;
  concurrencyKey?: WorkTypedValue | null;
  identifiers: WorkTypedValue[];
  eventType: string;
  data?: unknown;
};

export type WorkableRealtimeEventBatch = {
  sentAt: string;
  events: WorkableRealtimeEvent[];
};

export type WorkRealtimeCapability = {
  enabled: boolean;
  transport?: string | null;
  hubPath?: string | null;
};

export type WorkableHttpHostCapabilities = {
  realtime: WorkRealtimeCapability;
};

export type WorkableHttpSystemCapabilities = {
  executionDiagnosticsPersistenceAvailable?: boolean;
  httpClientProfilingAvailable: boolean;
  persistentCoordinationAvailable: boolean;
  sqlProfilingAvailable: boolean;
};

export function createDefaultWorkableHttpSystemCapabilities(): WorkableHttpSystemCapabilities {
  return {
    executionDiagnosticsPersistenceAvailable: false,
    httpClientProfilingAvailable: false,
    persistentCoordinationAvailable: false,
    sqlProfilingAvailable: false,
  };
}

export function normalizeWorkableHttpSystemCapabilities(
  value?: Partial<WorkableHttpSystemCapabilities> | null
): WorkableHttpSystemCapabilities {
  return {
    executionDiagnosticsPersistenceAvailable: Boolean(value?.executionDiagnosticsPersistenceAvailable),
    httpClientProfilingAvailable: Boolean(value?.httpClientProfilingAvailable),
    persistentCoordinationAvailable: Boolean(value?.persistentCoordinationAvailable),
    sqlProfilingAvailable: Boolean(value?.sqlProfilingAvailable),
  };
}

export type WorkSystemAccessSummary = {
  isSystemAdministrator: boolean;
  isWorkAdministrator: boolean;
  canViewDiagnostics: boolean;
  canControlSystem: boolean;
  canReadAllWork: boolean;
  canOperateAllWork: boolean;
  totalDefinitionCount: number;
  readableDefinitionCount: number;
  operableDefinitionCount: number;
};

export type WorkableHttpHostDescriptor = {
  capabilities: WorkableHttpHostCapabilities;
  systems: WorkableHttpSystemDescriptor[];
};

export type WorkableHttpSystemDescriptor = {
  name?: string | null;
  state: string;
  isDefault: boolean;
  capabilities: WorkableHttpSystemCapabilities;
  access: WorkSystemAccessSummary;
};

export type WorkableHttpSystemDiagnostics = {
  name?: string | null;
  state: string;
  queue: WorkSystemQueueDiagnostics;
  readModel: WorkSystemReadModelDiagnostics;
  retention: WorkSystemRetentionDiagnostics;
  concurrency: WorkSystemConcurrencyDiagnostics;
  durability: WorkSystemDurabilityDiagnostics;
  idempotency: WorkSystemIdempotencyDiagnostics;
};

export type WorkSystemQueueDiagnostics = {
  rejectedWorkCount: number;
  lastRejectedAt?: string | null;
  lastRejectedStatus?: string | null;
  lastRejectedCode?: string | null;
  lastRejectedMessage?: string | null;
  alertableRejectedWorkCount: number;
  lastAlertableRejectedCode?: string | null;
  lastAlertableRejectedMessage?: string | null;
};

export type WorkSystemReadModelDiagnostics = {
  enqueuedSequence: number;
  appliedSequence: number;
  appliedUpdateCount: number;
  publishedSnapshotCount: number;
  lastBatchSize: number;
  lastProjectionDuration: string;
  lastProjectedAt?: string | null;
  projectorFailureType?: string | null;
  projectorFailureMessage?: string | null;
  pendingUpdateCount: number;
  hasProjectorFailure: boolean;
};

export type WorkSystemRetentionDiagnostics = {
  trackedFinalWorkerCount: number;
  scheduledPurgeCount: number;
  scheduledPurgeHighWaterMark: number;
  oldestScheduledPurgeDueAt?: string | null;
  oldestDuePurgeAge: string;
  pendingCountRetentionDefinitionCount: number;
  systemCountRetentionPending: boolean;
  lastRunAt?: string | null;
  lastRunDuration: string;
  lastPurgedCount: number;
  totalPurgedCount: number;
  schedulerFailureType?: string | null;
  schedulerFailureMessage?: string | null;
  hasSchedulerFailure: boolean;
};

export type WorkSystemConcurrencyDiagnostics = {
  deferredStartCount: number;
  oldestDeferredStartAge: string;
  lastDrainReleasedCount: number;
};

export type WorkSystemDurabilityDiagnostics = {
  acceptedWaiterCount: number;
  oldestAcceptedWaiterAge: string;
  pendingCleanupCount: number;
  oldestPendingCleanupAge: string;
  readerFailureType?: string | null;
  readerFailureMessage?: string | null;
  leaseRenewalFailureType?: string | null;
  leaseRenewalFailureMessage?: string | null;
  cleanupFailureType?: string | null;
  cleanupFailureMessage?: string | null;
  hasReaderFailure: boolean;
  hasLeaseRenewalFailure: boolean;
  hasCleanupFailure: boolean;
};

export type WorkSystemIdempotencyDiagnostics = {
  duplicateRejectionCount: number;
  lastDuplicateRejectedStorage?: string | null;
};

export type WorkDefinition = {
  id: { value: string };
  name: string;
  category?: string | null;
  description?: string | null;
  inputSchema?: WorkSchema | null;
  outputSchema?: WorkSchema | null;
  defaultOptions?: WorkerOptions | null;
  configuration?: WorkConfiguration | null;
  metadata?: Record<string, unknown> | null;
  revision: number;
};

export type WorkSchema = {
  jsonSchema?: string | null;
  contentType?: string | null;
  schemaDialect?: string | null;
};

export type JsonSchemaNode = {
  $schema?: string;
  type?: string | string[];
  format?: string;
  enum?: Array<string | number | boolean | null>;
  default?: unknown;
  properties?: Record<string, JsonSchemaNode>;
  required?: string[];
  items?: JsonSchemaNode;
  anyOf?: JsonSchemaNode[];
  oneOf?: JsonSchemaNode[];
  additionalProperties?: boolean | JsonSchemaNode;
  description?: string;
  title?: string;
  pattern?: string;
};

export type WorkMessage = {
  code: string;
  occurredAt: string;
  severity: string;
  text: string;
  target?: string | null;
  metadata?: Record<string, unknown> | null;
};

export type WorkIterationMessageSummary = {
  total: number;
  critical: number;
  error: number;
  errors: number;
  warning: number;
  warnings: number;
  information: number;
  debug: number;
  trace: number;
};

export type WorkIterationMessagePage = {
  items: WorkMessage[];
  hasMore: boolean;
  cursor?: string | null;
};

export type WorkIterationMessageSection = {
  summary: WorkIterationMessageSummary;
  page: WorkIterationMessagePage;
};

export type WorkerIterationFailure = {
  kind: "Failure" | "Exception";
  message: string;
  code?: string | null;
  target?: string | null;
  exceptionType?: string | null;
  stackTrace?: string | null;
  declaredByWork: boolean;
};

export type WorkerSummary = {
  id: { value: string };
  revision: number;
  stateSequence: number;
  definitionName: string;
  definitionCategory?: string | null;
  subjectId?: WorkTypedValue | null;
  concurrencyKey?: WorkTypedValue | null;
  identifiers?: WorkTypedValue[];
  state: WorkerState;
  isFinal: boolean;
  createdAt: string;
  stateChangedAt?: string;
  nextRunAt?: string | null;
  retryAttempt?: number | null;
  updatedAt: string;
  version: WorkerVersion;
};

export type WorkerOverviewItem = {
  id: { value: string };
  definitionName: string;
  subjectId?: WorkTypedValue | null;
  concurrencyKey?: WorkTypedValue | null;
  identifiers?: WorkTypedValue[];
  revision: number;
  category?: string | null;
  state: WorkerState;
  isFinal: boolean;
  createdAt: string;
  stateChangedAt?: string;
  updatedAt: string;
  queueDuration?: string | null;
  totalExecutionDuration?: string;
  nextRunAt?: string | null;
};

export type WorkOverviewFailedWorkerStandard = {
  id: { value: string };
  definitionName: string;
  revision: number;
  updatedAt: string;
  totalExecutionDuration?: string;
};

export type WorkOverviewFailedWorkerDetailed = WorkOverviewFailedWorkerStandard & {
  subjectId?: WorkTypedValue | null;
  identifiers?: WorkTypedValue[];
  state: WorkerState;
};

export type WorkViewWorkerGridDetailed = WorkOverviewFailedWorkerStandard & {
  subjectId?: WorkTypedValue | null;
  identifiers?: WorkTypedValue[];
  state: WorkerState;
  isFinal: boolean;
  currentIterationSequence?: number | null;
};

export type WorkOverviewFailedWorker =
  | WorkOverviewFailedWorkerStandard
  | WorkOverviewFailedWorkerDetailed
  | WorkerOverviewItem;

export type WorkerSnapshot = WorkerSummary & {
  origin: WorkableRealtimeOrigin;
  input?: WorkData | null;
  output?: WorkData | null;
  options?: WorkerOptions | null;
  configuration?: WorkConfiguration | null;
  messages?: WorkMessage[];
  actionHistory?: WorkerActionHistoryEntry[];
  iterations?: WorkerIterationSnapshot[];
  currentIterationSequence?: number | null;
  lastIteration?: WorkerIterationSnapshot | null;
  lastIterationSequence?: number | null;
  profile?: WorkProfileSnapshot | null;
};

export type WorkerVersion = {
  workerId: { value: string };
  revision: number;
};

export type WorkerOptions = {
  profilingEnabled?: boolean;
  profilingCaptureMode?: "Bounded" | "Full";
  configuration?: WorkConfiguration | null;
};

export type WorkableHttpWorkerConfiguration = {
  profilingEnabled: boolean;
  profilingCaptureMode: "Bounded" | "Full";
  configuration: WorkConfiguration;
  input?: WorkData | null;
  subjectId?: WorkTypedValue | null;
  concurrencyKey?: WorkTypedValue | null;
  definitionInfo?: WorkInfo | null;
  queueRequestSchema: QueueRequestSchemaDescriptor;
};

export type WorkableProfilingCaptureRule = {
  id: string;
  definitionName?: string | null;
  actorId?: string | null;
  maximumMatches: number;
  remainingMatches: number;
  createdAt: string;
  expiresAt: string;
  createdBy: WorkableRealtimeOriginActor;
};

export type WorkableProfilingCaptureState = {
  maximumAutomaticInstrumentationNodes: number;
  rules: WorkableProfilingCaptureRule[];
};

export type WorkableExecutionDiagnosticCaptureRule = {
  id: string;
  definitionName?: string | null;
  minimumLogLevel: "Trace" | "Debug" | "Information" | "Warning" | "Error" | "Critical";
  profileCaptureMode?: "Bounded" | "Full" | null;
  artifactRetention: string;
  createdAt: string;
  activeUntil: string;
  createdBy: WorkableRealtimeOriginActor;
};

export type WorkableExecutionDiagnosticCaptureState = {
  persistenceAvailable: boolean;
  rules: WorkableExecutionDiagnosticCaptureRule[];
};

export type WorkConfiguration = {
  start: {
    policy:
      | "DoNotStart"
      | "StartAndReturnAfterAccepted"
      | "StartAndReturnAfterStarted"
      | "StartAndReturnAfterCompleted";
  };
  coordination: {
    isEnabled: boolean;
    storage: "Local" | "Persistent";
    idempotency: {
      isEnabled: boolean;
      conflictPolicy: "RejectDuplicates";
    };
    concurrency: {
      isEnabled: boolean;
      maximumCapacity: number;
      scope: "PerDefinition" | "PerSubject" | "PerConcurrencyKey";
      blockingMode:
        | "WhileExecutingPausedOrFailed"
        | "WhileExecutingOrPaused"
        | "WhileExecutingOrFailed"
        | "WhileExecuting";
      limitReachedBehavior: "Ignore" | "DeferStart";
      overrideBehavior: "Flexible" | "Strict";
    };
    durability: {
      isEnabled: boolean;
      completeDurably?: boolean;
    };
  };
  recurrence: {
    isEnabled: boolean;
    interval: string;
    continueAfterFailure: boolean;
    circuitBreakerFailureThreshold: number;
    retainedIterations: number;
    raiseCircuitBreakerOpenedEvent: boolean;
  };
  transientRetry: {
    count: number;
    initialDelay: string;
    jitter: string;
    maximumDelay: string;
    backoff: "None" | "Exponential";
  };
  failedWorker: {
    handling: "Manual" | "AutoCancel";
    autoCancelAfter: string;
  };
  logging: {
    isEnabled: boolean;
    level:
      | "Trace"
      | "Debug"
      | "Information"
      | "Warning"
      | "Error"
      | "Critical"
      | "None";
    maximumBufferedEntries: number;
  };
  retention: {
    purgeInterval: string;
    maximumFinalWorkers: number;
  };
  invocation?: Record<string, unknown> | null;
};

export type WorkTypedValue = {
  type: string;
  value: string;
};

export type WorkData = {
  json?: string | null;
  clrType?: string | null;
  contentType?: string | null;
  subjectId?: WorkTypedValue | null;
  concurrencyKey?: WorkTypedValue | null;
  identifiers?: WorkTypedValue[];
};

export type WorkerLogEntry = {
  id: string;
  occurredAt: string;
  sequence?: number | null;
  ordinal?: number | null;
  workerId?: { value: string } | null;
  definitionId?: { value: string } | null;
  category: string;
  level: string;
  eventId?: {
    id: number;
    name?: string | null;
  } | null;
  message: string;
  exceptionType?: string | null;
  exceptionMessage?: string | null;
};

export type WorkerActionHistoryEntry = {
  occurredAt: string;
  kind: string;
  action?: string | null;
  status: string;
  state: WorkerState;
  origin?: WorkableRealtimeOrigin | null;
  revision: number;
  stateSequence: number;
  messages?: WorkMessage[];
  reconfiguration?: Record<string, unknown> | null;
};

export type WorkProfileMetricType = "MethodScope" | "Scope" | "Timing" | "Metric";

export type WorkProfileSnapshotNode = {
  metricType: WorkProfileMetricType;
  instrumentation: string;
  treeMilliseconds: number;
  nodeMilliseconds: number;
  label: string;
  context?: unknown;
  children: WorkProfileSnapshotNode[];
};

export type WorkProfileSnapshot = {
  root: WorkProfileSnapshotNode;
  startedAt: string;
  capturedAt: string;
};

export type WorkerIterationSnapshot = {
  sequence: number;
  startedAt?: string;
  completedAt?: string;
  executionDuration?: string;
  occurredAt: string;
  status: WorkCompletionStatus;
  attemptCount: number;
  isFinal: boolean;
  output?: WorkData | null;
  messages?: WorkMessage[];
  logs?: WorkerLogEntry[];
  profile?: WorkProfileSnapshot | null;
};

export type WorkIterationLogSection = {
  summary: WorkWorkerOverviewLogSummary;
  page: WorkWorkerOverviewPage<WorkerLogEntry>;
};

export type WorkWorkerIterationOverviewActivity = "Auto" | "None" | "Messages" | "Logs";

export type WorkWorkerIterationOverviewWorker = {
  workerId: { value: string };
  definitionName: string;
  subjectId?: WorkTypedValue | null;
  concurrencyKey?: WorkTypedValue | null;
  identifiers: WorkTypedValue[];
  profilingEnabled: boolean;
};

export type WorkWorkerIterationOverviewIteration = {
  sequence: number;
  startedAt: string;
  completedAt: string;
  executionDuration: string;
  occurredAt: string;
  status: WorkCompletionStatus;
  attemptCount: number;
  isFinal: boolean;
  output?: WorkData | null;
  failure?: WorkerIterationFailure | null;
  profile?: WorkProfileSnapshot | null;
};

export type WorkWorkerIterationOverviewMessageSection = {
  summary: WorkIterationMessageSummary;
  page?: WorkIterationMessagePage | null;
};

export type WorkWorkerIterationOverviewLogSection = {
  summary: WorkWorkerOverviewLogSummary;
  page?: WorkWorkerOverviewPage<WorkerLogEntry> | null;
};

export type WorkWorkerIterationOverviewComponent = {
  activity: WorkWorkerIterationOverviewActivity;
  capabilities?: WorkableHttpSystemCapabilities | null;
  worker: WorkWorkerIterationOverviewWorker;
  input?: WorkData | null;
  iteration: WorkWorkerIterationOverviewIteration;
  messages: WorkWorkerIterationOverviewMessageSection;
  logs: WorkWorkerIterationOverviewLogSection;
};

export type WorkWorkerOverviewActivity = "Auto" | "Logs" | "Timeline";
export type WorkWorkerOverviewSortDirection = "Asc" | "Desc";

export type WorkWorkerOverviewOrigin = {
  channel: string;
  surface?: string | null;
  actorId?: string | null;
  actorName?: string | null;
  actorEmail?: string | null;
};

export type WorkWorkerOverviewFailureKind = "Failure" | "Exception";
export type WorkWorkerOverviewPendingStateMode = "Recurrence" | "Retry";

export type WorkWorkerOverviewPendingState = {
  mode: WorkWorkerOverviewPendingStateMode;
  nextRunAt?: string | null;
  stateChangedAt: string;
  updatedAt: string;
  retryAttempt?: number | null;
};

export type WorkWorkerOverviewFailure = {
  kind: WorkWorkerOverviewFailureKind;
  message: string;
  code?: string | null;
  target?: string | null;
  exceptionType?: string | null;
  stackTrace?: string | null;
  declaredByWork: boolean;
  pendingState?: WorkWorkerOverviewPendingState | null;
};

export type WorkWorkerOverviewLatestIteration = {
  workerId: { value: string };
  sequence: number;
  status: WorkCompletionStatus;
  startedAt: string;
  completedAt?: string | null;
  executionDuration?: string | null;
  attemptCount: number;
  output?: WorkData | null;
  failure?: WorkWorkerOverviewFailure | null;
};

export type WorkWorkerOverviewRecentIteration = {
  workerId: { value: string };
  sequence: number;
  status: WorkCompletionStatus;
  startedAt: string;
  completedAt?: string | null;
  attemptCount: number;
  executionDuration?: string | null;
};

export type WorkWorkerOverviewWorker = {
  workerId: { value: string };
  revision: number;
  stateSequence: number;
  state: WorkerState;
  isFinal: boolean;
  createdAt: string;
  stateChangedAt: string;
  updatedAt: string;
  nextRunAt?: string | null;
  retryAttempt?: number | null;
  createdOrigin: WorkWorkerOverviewOrigin;
  definitionName: string;
  definitionCategory: string;
  workflowRunId?: { value: string } | null;
  identifiers?: WorkTypedValue[];
  configDifferenceCount: number;
  profilingEnabled: boolean;
  profilingCaptureMode: "Bounded" | "Full";
  canToggleFullProfileCapture?: boolean;
};

export type WorkflowRunStatus =
  | "Running"
  | "Paused"
  | "Blocked"
  | "Completed"
  | "Failed"
  | "Canceled"
  | "Invalid"
  | "NotFound"
  | "Unauthorized";

export type WorkflowAvailableActions = {
  start: boolean;
  pause: boolean;
  cancel: boolean;
};

export type WorkflowStepKind = "DispatchWork" | "DispatchEach" | "Parallel" | "Branch" | "Join";

export type WorkflowOperatorNodeStatus =
  | "Pending"
  | "Running"
  | "WaitingOnChildren"
  | "Paused"
  | "Blocked"
  | "Completed"
  | "Failed"
  | "Canceled";

export type WorkflowChildWorkerSummary = {
  total: number;
  active: number;
  final: number;
};

export type WorkflowChildWorkerView = {
  workerId: string;
  definitionName: string;
  state: WorkerState;
};

export type WorkflowStepChildWorkerQueryResult = {
  workers: WorkflowChildWorkerView[];
  totalCount: number;
  skip: number;
  take: number;
};

export type WorkflowStepOperatorView = {
  name: string;
  kind: WorkflowStepKind;
  status: WorkflowOperatorNodeStatus;
  children: WorkflowChildWorkerSummary;
  childSample: WorkflowChildWorkerView[];
  steps: WorkflowStepOperatorView[];
};

export type WorkflowRunDetailView = {
  status: WorkflowRunStatus;
  availableActions: WorkflowAvailableActions;
  createdAt: string;
  startedAt?: string | null;
  completedAt?: string | null;
  currentStepName?: string | null;
  currentStepStatus?: WorkflowOperatorNodeStatus | null;
  outstandingChildren: WorkflowChildWorkerSummary;
  steps: WorkflowStepOperatorView[];
};

export type WorkWorkerOverviewLogSummary = {
  total: number;
  critical: number;
  error: number;
  errors: number;
  warning: number;
  warnings: number;
  information: number;
  debug: number;
  trace: number;
};

export type WorkWorkerOverviewLogEntry = {
  id: string;
  occurredAt: string;
  sequence?: number | null;
  ordinal?: number | null;
  level: string;
  category: string;
  message: string;
  eventId: number;
  eventName?: string | null;
  exceptionType?: string | null;
  exceptionMessage?: string | null;
};

export type WorkWorkerOverviewTimelineItemKind = "ActionRequest" | "StateChange" | "Iteration";
export type WorkWorkerOverviewTimelineCategory = "UserAction" | "SystemEvent" | "Failure";

export type WorkWorkerOverviewTimelineItem = {
  id: string;
  at: string;
  kind: WorkWorkerOverviewTimelineItemKind;
  category: WorkWorkerOverviewTimelineCategory;
  actionHistoryKind?: string | null;
  action?: WorkAction | null;
  actionStatus?: string | null;
  state?: WorkerState | null;
  sequence?: number | null;
  iterationStatus?: WorkCompletionStatus | null;
  attemptCount?: number | null;
  executionDuration?: string | null;
  origin?: WorkWorkerOverviewOrigin | null;
  failure?: WorkWorkerOverviewFailure | null;
  pendingState?: WorkWorkerOverviewPendingState | null;
};

export type WorkWorkerOverviewTimelineSummary = {
  total: number;
  userActionCount: number;
  systemEventCount: number;
  failureCount: number;
};

export type WorkWorkerOverviewPage<T> = {
  items: T[];
  hasMore: boolean;
  cursor?: string | null;
};

export type WorkWorkerOverviewLogSection = {
  summary: WorkWorkerOverviewLogSummary;
  page?: WorkWorkerOverviewPage<WorkWorkerOverviewLogEntry> | null;
};

export type WorkWorkerOverviewTimelineSection = {
  summary: WorkWorkerOverviewTimelineSummary;
  page?: WorkWorkerOverviewPage<WorkWorkerOverviewTimelineItem> | null;
};

export type WorkWorkerOverviewComponent = {
  activity: WorkWorkerOverviewActivity;
  worker: WorkWorkerOverviewWorker;
  input?: WorkData | null;
  latestIteration?: WorkWorkerOverviewLatestIteration | null;
  recentIterations: WorkWorkerOverviewRecentIteration[];
  logs: WorkWorkerOverviewLogSection;
  timeline: WorkWorkerOverviewTimelineSection;
};

export type WorkWorkerOverviewRealtimeCriteria = {
  workerControls?: WorkComponentShape;
  workerLogs?: WorkComponentShape;
  workerDuration?: WorkComponentShape;
  workerTimeline?: WorkComponentShape;
  logSortDirection?: WorkWorkerOverviewSortDirection;
  logLevels?: string[] | null;
  logIterationSequence?: number | null;
  timelineSortDirection?: WorkWorkerOverviewSortDirection;
  timelineCategories?: WorkWorkerOverviewTimelineCategory[] | null;
};

export type WorkWorkerOverviewRealtimeUpdate = {
  generatedAt: string;
  worker?: WorkWorkerOverviewWorker | null;
  latestIteration?: WorkWorkerOverviewLatestIteration | null;
  logSummary?: WorkWorkerOverviewLogSummary | null;
  logEntries?: WorkWorkerOverviewLogEntry[] | null;
  recentIterations?: WorkWorkerOverviewRecentIteration[] | null;
  timelineSummary?: WorkWorkerOverviewTimelineSummary | null;
  timelineItems?: WorkWorkerOverviewTimelineItem[] | null;
  requiresRefresh?: boolean;
  refreshReason?: string | null;
};

export type WorkerIterationOverviewItem = {
  workerId: { value: string };
  sequence: number;
  definitionName: string;
  category?: string | null;
  workerState: WorkerState;
  status: WorkCompletionStatus;
  isFinal: boolean;
  startedAt: string;
  completedAt: string;
  executionDuration: string;
  subjectId?: WorkTypedValue | null;
  concurrencyKey?: WorkTypedValue | null;
  identifiers?: WorkTypedValue[];
};

export type WorkOverviewIterationStandard = {
  workerId: { value: string };
  sequence: number;
  definitionName: string;
  completedAt: string;
  executionDuration: string;
};

export type WorkOverviewIterationDetailed = WorkOverviewIterationStandard & {
  workerState: WorkerState;
  subjectId?: WorkTypedValue | null;
  identifiers?: WorkTypedValue[];
};

export type WorkViewIterationGridDetailed = WorkOverviewIterationDetailed & {
  status: WorkCompletionStatus;
  isFinal: boolean;
};

export type WorkOverviewIteration =
  | WorkOverviewIterationStandard
  | WorkOverviewIterationDetailed
  | WorkerIterationOverviewItem;

export type WorkerQueryResult = {
  workers: WorkViewWorkerGridDetailed[];
  totalCount: number;
  skip: number;
  take: number;
};

export type WorkerIterationQueryResult = {
  iterations: WorkViewIterationGridDetailed[];
  totalCount: number;
  skip: number;
  take: number;
};

export type WorkerStatusSummary = {
  total: number;
  active: number;
  final: number;
  counts: Partial<Record<WorkerState, number>>;
};

export type WorkKeyKind = "Subject" | "ConcurrencyKey" | "Identifier";
export type WorkCompletionStatus =
  | "Executing"
  | "Completed"
  | "Failed"
  | "Paused"
  | "Interrupted"
  | "Canceled"
  | "Invalid"
  | "NotFound";

export type WorkKeyTypeFacet = {
  type: string;
  workerCount: number;
  workerCountByKind: Partial<Record<WorkKeyKind, number>>;
};

export type WorkIterationKeyTypeFacet = {
  type: string;
  iterationCount: number;
  iterationCountByKind: Partial<Record<WorkKeyKind, number>>;
};

export type WorkKeyTypeDescriptor = {
  type: string;
  workerCount: number;
  workerCountByKind: Partial<Record<WorkKeyKind, number>>;
  workers: WorkerOverviewItem[];
};

export type WorkKeyTypeQueryResult = {
  types: WorkKeyTypeDescriptor[];
  totalCount: number;
  skip: number;
  take: number;
};

export type WorkIterationKeyTypeDescriptor = {
  type: string;
  iterationCount: number;
  iterationCountByKind: Partial<Record<WorkKeyKind, number>>;
  iterations: WorkerIterationOverviewItem[];
};

export type WorkIterationKeyTypeQueryResult = {
  types: WorkIterationKeyTypeDescriptor[];
  totalCount: number;
  skip: number;
  take: number;
};

export type WorkSystemOverview = {
  systemName?: string | null;
  systemState: string;
  definitionCount: number;
  catalogCategories: WorkOverviewCatalogCategoryItem[];
  catalogDefinitions: WorkOverviewDefinitionItem[];
  activeWorkerCount: number;
  finalWorkerCount: number;
  failedWorkerCount: number;
  workerCountByState: Partial<Record<WorkerState, number>>;
  oldestQueuedAt?: string | null;
  currentIterationCount: number;
  completedIterationCount: number;
  failedIterationCount: number;
  canceledIterationCount: number;
  iterationCountByStatus: Partial<Record<WorkCompletionStatus, number>>;
  commonKeyTypes: WorkIterationKeyTypeFacet[];
  throughput?: WorkSystemThroughput | null;
  failedWorkers: WorkerOverviewItem[];
  failedIterations: WorkerIterationOverviewItem[];
  completedIterations: WorkerIterationOverviewItem[];
};

export type WorkComponentQueryResult = {
  generatedAt: string;
  components: Record<string, WorkComponentResult>;
};

export type WorkComponentShape = "compact" | "standard" | "detailed";

export type WorkComponentRequest = {
  id: string;
  type: string;
  options?: unknown;
  shape?: WorkComponentShape;
};

export type WorkComponentResult<TData = unknown> = {
  status: string;
  data?: TData;
  error?: string | null;
  shape?: WorkComponentShape;
};

export type WorkQueueDiagnosticsCompactComponent = {
  rejectedWorkCount: number;
  hasRejectedWork: boolean;
  lastRejectedAt?: string | null;
  lastRejectedCode?: string | null;
  lastRejectedMessage?: string | null;
  alertableRejectedWorkCount: number;
  hasAlertableRejectedWork: boolean;
  lastAlertableRejectedCode?: string | null;
  lastAlertableRejectedMessage?: string | null;
};

export type WorkQueueDiagnosticsDetailedComponent =
  WorkQueueDiagnosticsCompactComponent & {
    queue: WorkSystemQueueDiagnostics;
  };

export type WorkSystemDiagnosticsCompactComponent = {
  systemName?: string | null;
  systemState: string;
  isShuttingDown: boolean;
};

export type WorkReadModelDiagnosticsCompactComponent = {
  pendingUpdateCount: number;
  isReadModelBehind: boolean;
  readModelLagWarningThreshold: number;
  hasProjectorFailure: boolean;
  projectorFailureType?: string | null;
  projectorFailureMessage?: string | null;
};

export type WorkReadModelDiagnosticsDetailedComponent =
  WorkReadModelDiagnosticsCompactComponent & {
    readModel: WorkSystemReadModelDiagnostics;
  };

export type WorkRetentionDiagnosticsCompactComponent = {
  trackedFinalWorkerCount: number;
  scheduledPurgeCount: number;
  oldestDuePurgeAge: string;
  isRetentionBehind: boolean;
  retentionLagWarningSeconds: number;
  hasSchedulerFailure: boolean;
  schedulerFailureType?: string | null;
  schedulerFailureMessage?: string | null;
};

export type WorkRetentionDiagnosticsDetailedComponent =
  WorkRetentionDiagnosticsCompactComponent & {
    retention: WorkSystemRetentionDiagnostics;
  };

export type WorkConcurrencyDiagnosticsCompactComponent = {
  deferredStartCount: number;
  oldestDeferredStartAge: string;
  lastDrainReleasedCount: number;
  isConcurrencyBehind: boolean;
  concurrencyLagWarningSeconds: number;
};

export type WorkConcurrencyDiagnosticsDetailedComponent =
  WorkConcurrencyDiagnosticsCompactComponent & {
    concurrency: WorkSystemConcurrencyDiagnostics;
  };

export type WorkDurabilityDiagnosticsCompactComponent = {
  acceptedWaiterCount: number;
  oldestAcceptedWaiterAge: string;
  pendingCleanupCount: number;
  oldestPendingCleanupAge: string;
  isAcceptedWorkerMaterializationBehind: boolean;
  acceptedWorkerWarningSeconds: number;
  isCleanupBehind: boolean;
  cleanupWarningSeconds: number;
  hasReaderFailure: boolean;
  readerFailureType?: string | null;
  readerFailureMessage?: string | null;
  hasLeaseRenewalFailure: boolean;
  leaseRenewalFailureType?: string | null;
  leaseRenewalFailureMessage?: string | null;
  hasCleanupFailure: boolean;
  cleanupFailureType?: string | null;
  cleanupFailureMessage?: string | null;
};

export type WorkDurabilityDiagnosticsDetailedComponent =
  WorkDurabilityDiagnosticsCompactComponent & {
    durability: WorkSystemDurabilityDiagnostics;
  };

export type WorkIdempotencyDiagnosticsCompactComponent = {
  duplicateRejectionCount: number;
  lastDuplicateRejectedStorage?: string | null;
};

export type WorkIdempotencyDiagnosticsDetailedComponent =
  WorkIdempotencyDiagnosticsCompactComponent & {
    idempotency: WorkSystemIdempotencyDiagnostics;
  };

export type WorkOverviewThroughputComponent = {
  activeWorkerCount: number;
  throughput: WorkSystemThroughput;
};

export type WorkSystemThroughput = {
  from?: string;
  to?: string;
  windowSeconds: number;
  bucketSeconds?: number;
  settledCount: number;
  buckets?: WorkThroughputBucket[];
  executionSummary: WorkThroughputExecutionSummary;
  liveSummary: WorkThroughputLiveSummary;
};

export type WorkThroughputExecutionSummary = {
  executionCount: number;
  averageExecutionMilliseconds: number;
  slowestExecutionMilliseconds: number;
  p95ExecutionMilliseconds: number;
  p99ExecutionMilliseconds: number;
};

export type WorkThroughputBucket = {
  at: string;
  started: number;
  completed: number;
  failed: number;
  canceled: number;
  averageExecutionMilliseconds: number;
};

export type WorkThroughputLiveSummary = {
  rateWindowSeconds: number;
  startedPerSecond: number;
  completedPerSecond: number;
  failedPerSecond: number;
  canceledPerSecond: number;
  inFlightDeltaPerSecond: number;
};

export type WorkOverviewCatalogCategoryItem = {
  label: string;
  path: string;
  count: number;
};

export type WorkOverviewDefinitionItem = {
  name: string;
  category: string;
};

export type WorkSystemFailedWorkersOverview = {
  activeWorkerCount: number;
  finalWorkerCount: number;
  failedWorkerCount: number;
  workerCountByState: Partial<Record<WorkerState, number>>;
  oldestQueuedAt?: string | null;
  failedWorkers: WorkOverviewFailedWorker[];
};

export type WorkSystemLifecycleResult = {
  name?: string | null;
  state: string;
  forceCanceledWorkers?: WorkerSnapshot[];
};

export type WorkInfo = {
  definition: WorkDefinition;
  status: string;
  workers: {
    total: number;
    active: number;
    queued: number;
    running: number;
    waiting: number;
    paused: number;
    failed: number;
    canceled: number;
    completed: number;
    lastActivityAt?: string | null;
  };
  queueRequestSchema: QueueRequestSchemaDescriptor;
};

export type WorkDefinitionReconfigurationOutcome = {
  status: "Accepted" | "NotFound" | "Invalid" | "Conflict";
  definition?: WorkDefinition | null;
  messages: WorkMessage[];
};

export type WorkActionOutcome = {
  status: "Accepted" | "NotFound" | "Unauthorized" | "Invalid" | "Conflict";
  action: WorkAction;
  workerId?: { value: string } | null;
  worker?: WorkerSnapshot | null;
  messages: WorkMessage[];
};

export type WorkerState =
  | "Queued"
  | "Running"
  | "Waiting"
  | "Retrying"
  | "Pausing"
  | "Paused"
  | "Interrupting"
  | "Interrupted"
  | "Canceling"
  | "Failed"
  | "Canceled"
  | "Completed";

export type WorkAction = "Start" | "Pause" | "Cancel" | "Push" | "Purge";

export type WorkerQuery = {
  definitionName?: string;
  category?: string;
  includeSubcategories?: boolean;
  states?: WorkerState[];
  subjectId?: WorkTypedValue;
  take?: number;
  skip?: number;
};

export type QueueWorkRequest = {
  input?: unknown;
  completion?: "ReturnAfterAccepted" | "WaitForCompletion";
  subjectId?: WorkTypedValue;
  concurrencyKey?: WorkTypedValue;
  identifiers?: WorkTypedValue[];
  options?: WorkerOptions;
};

export type QueueRequestSchemaDescriptor = {
  schema: WorkSchema;
  tabs: QueueRequestSchemaTab[];
};

export type QueueRequestSchemaTab = {
  id: string;
  label: string;
  description: string;
  fields: QueueRequestSchemaField[];
};

export type QueueRequestSchemaField = {
  path: string;
  label: string;
  description: string;
};

export {
  WorkableApiError,
  WorkableRealtimeAuthenticationError,
  createWorkableRealtimeUrl,
  formatDateTime,
  getWorkableRealtimeAccessToken,
  invalidateWorkableRealtimeAccessToken,
  isWorkableRealtimeAuthenticationError,
  safeJsonParse,
  stateTone,
  workableFetch,
  workableQueryFetch,
} from "./workable-client";
