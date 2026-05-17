export const DEFAULT_WORKABLE_API_URL = "http://localhost:61932/workable";

export type WorkableConnection = {
  apiUrl: string;
  systemName?: string;
};

export type WorkRealtimeCapability = {
  enabled: boolean;
  transport?: string | null;
  hubPath?: string | null;
  features?: string[] | null;
};

export type WorkableHttpCapabilities = {
  realtime: WorkRealtimeCapability;
};

export type WorkableHttpSystems = {
  systems: WorkableHttpSystemInfo[];
};

export type WorkableHttpSystemInfo = {
  id: { value: string };
  name?: string | null;
  state: string;
  isDefault: boolean;
  capabilities: WorkableHttpCapabilities;
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
  severity: string;
  text: string;
  target?: string | null;
};

export type WorkerSummary = {
  id: { value: string };
  revision: number;
  stateSequence: number;
  definitionId: { value: string };
  definitionName: string;
  definitionCategory?: string | null;
  subjectId?: WorkTypedValue | null;
  concurrencyKey?: WorkTypedValue | null;
  identifiers?: WorkTypedValue[];
  state: WorkerState;
  createdAt: string;
  stateChangedAt?: string;
  updatedAt: string;
  version: WorkerVersion;
};

export type WorkerOverviewItem = {
  id: { value: string };
  definitionId: { value: string };
  definitionName: string;
  subjectId?: WorkTypedValue | null;
  concurrencyKey?: WorkTypedValue | null;
  identifiers?: WorkTypedValue[];
  revision: number;
  category?: string | null;
  state: WorkerState;
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
};

export type WorkOverviewFailedWorker =
  | WorkOverviewFailedWorkerStandard
  | WorkOverviewFailedWorkerDetailed
  | WorkerOverviewItem;

export type WorkerSnapshot = WorkerSummary & {
  input?: WorkData | null;
  output?: WorkData | null;
  messages?: WorkMessage[];
  logs?: WorkerLogEntry[];
  actionHistory?: WorkerActionHistoryEntry[];
  iterations?: WorkerIterationSnapshot[];
  profile?: unknown;
};

export type WorkerVersion = {
  workerId: { value: string };
  revision: number;
};

export type WorkerOptions = {
  profilingEnabled?: boolean;
  configuration?: WorkConfiguration | null;
};

export type WorkConfiguration = {
  start: {
    policy:
      | "DoNotStart"
      | "StartAndReturnAfterAccepted"
      | "StartAndReturnAfterStarted"
      | "StartAndReturnAfterCompleted";
  };
  idempotency: {
    isEnabled: boolean;
    conflictPolicy: "RejectDuplicates";
  };
  recurrence: {
    isEnabled: boolean;
    interval: string;
    continueAfterFailure: boolean;
    circuitBreakerFailureThreshold: number;
    maximumSuccessfulIterations: number;
    maximumFailedIterations: number;
    raiseCircuitBreakerOpenedEvent: boolean;
  };
  transientRetry: {
    count: number;
    initialDelay: string;
    jitter: string;
    maximumDelay: string;
    backoff: "None" | "Exponential";
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
  occurredAt: string;
  category: string;
  level: string;
  message: string;
};

export type WorkerActionHistoryEntry = {
  occurredAt: string;
  kind: string;
  action?: string | null;
  status: string;
  revision: number;
  stateSequence: number;
  messages?: WorkMessage[];
};

export type WorkerIterationSnapshot = {
  sequence: number;
  startedAt?: string;
  completedAt?: string;
  executionDuration?: string;
  occurredAt: string;
  status: WorkCompletionStatus;
  output?: WorkData | null;
  messages?: WorkMessage[];
};

export type WorkerIterationOverviewItem = {
  workerId: { value: string };
  sequence: number;
  definitionId: { value: string };
  definitionName: string;
  category?: string | null;
  workerState: WorkerState;
  status: WorkCompletionStatus;
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
  id: { value: string };
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
  id: { value: string };
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
};

export type WorkDefinitionReconfigurationOutcome = {
  status: "Accepted" | "NotFound" | "Invalid" | "Conflict";
  definitionId: { value: string };
  definition?: WorkDefinition | null;
  messages: WorkMessage[];
};

export type WorkerState =
  | "Queued"
  | "Running"
  | "Waiting"
  | "Retrying"
  | "Pausing"
  | "Paused"
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

export class WorkableApiError extends Error {
  constructor(
    message: string,
    public readonly status: number,
    public readonly body: unknown
  ) {
    super(message);
  }
}

const inFlightGetRequests = new Map<string, Promise<unknown>>();

export function formatDateTime(value?: string | null) {
  if (!value) {
    return "Never";
  }

  return new Intl.DateTimeFormat(undefined, {
    dateStyle: "medium",
    timeStyle: "medium",
  }).format(new Date(value));
}

export function stateTone(state: string) {
  switch (state) {
    case "Running":
    case "Waiting":
      return "bg-emerald-500/15 text-emerald-300 border-emerald-500/30";
    case "Queued":
    case "Retrying":
    case "Paused":
      return "bg-sky-500/15 text-sky-300 border-sky-500/30";
    case "Failed":
    case "Canceled":
      return "bg-red-500/15 text-red-300 border-red-500/30";
    case "Completed":
      return "bg-teal-500/15 text-teal-300 border-teal-500/30";
    default:
      return "bg-muted text-muted-foreground";
  }
}

export function safeJsonParse(value: string) {
  if (!value.trim()) {
    return undefined;
  }

  try {
    return JSON.parse(value);
  } catch {
    throw new Error("Input must be valid JSON.");
  }
}

export async function workableFetch<T>(
  connection: WorkableConnection,
  path: string,
  init?: RequestInit
): Promise<T> {
  const scopedPath = createScopedWorkablePath(connection, path);
  const method = init?.method?.toUpperCase() ?? "GET";
  const requestKey =
    method === "GET"
      ? `${method}:${connection.apiUrl}:${scopedPath}`
      : undefined;

  if (requestKey) {
    const existing = inFlightGetRequests.get(requestKey);
    if (existing) {
      return existing as Promise<T>;
    }
  }

  const request = fetchWorkable<T>(connection, scopedPath, init);
  if (requestKey) {
    inFlightGetRequests.set(requestKey, request);
    request.then(
      () => inFlightGetRequests.delete(requestKey),
      () => inFlightGetRequests.delete(requestKey)
    );
  }

  return request;
}

async function fetchWorkable<T>(
  connection: WorkableConnection,
  scopedPath: string,
  init?: RequestInit
): Promise<T> {
  const response = await fetch(`/api/workable/${scopedPath}`, {
    ...init,
    headers: {
      "content-type": "application/json",
      "x-workable-api-url": connection.apiUrl,
      ...init?.headers,
    },
  });

  const contentType = response.headers.get("content-type") ?? "";
  const responseText = await response.text();
  const body = contentType.includes("application/json") && responseText.trim()
    ? JSON.parse(responseText)
    : responseText;

  if (!response.ok) {
    const message = getWorkableErrorMessage(response.status, body);
    throw new WorkableApiError(message, response.status, body);
  }

  return body as T;
}

function getWorkableErrorMessage(status: number, body: unknown) {
  if (typeof body === "object" && body) {
    if ("error" in body && typeof body.error === "string" && body.error.trim()) {
      return body.error;
    }

    if ("messages" in body && Array.isArray(body.messages)) {
      const details = body.messages
        .map((message) => {
          if (typeof message === "object" && message && "text" in message) {
            return String(message.text ?? "").trim();
          }

          return "";
        })
        .filter(Boolean)
        .join(" ");
      if (details) {
        return details;
      }
    }

    if ("detail" in body && typeof body.detail === "string" && body.detail.trim()) {
      return body.detail;
    }

    if ("errors" in body && typeof body.errors === "object" && body.errors) {
      const details = Object.values(body.errors)
        .flatMap((value) => Array.isArray(value) ? value : [value])
        .map((value) => String(value ?? "").trim())
        .filter(Boolean)
        .join(" ");
      if (details) {
        return details;
      }
    }

    if ("title" in body && typeof body.title === "string" && body.title.trim()) {
      return body.title;
    }
  }

  if (typeof body === "string" && body.trim()) {
    return body.trim();
  }

  return `Workable request failed with ${status}.`;
}

function createScopedWorkablePath(connection: WorkableConnection, path: string) {
  const normalizedPath = path.replace(/^\/+/, "");
  const systemName = connection.systemName?.trim();

  if (!systemName) {
    return normalizedPath;
  }

  return `systems/${encodeURIComponent(systemName)}/${normalizedPath}`;
}
