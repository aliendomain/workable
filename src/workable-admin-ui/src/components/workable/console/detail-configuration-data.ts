import type {
  QueueRequestSchemaDescriptor,
  QueueWorkRequest,
  WorkConfiguration,
  WorkDefinition,
  WorkableHttpWorkerConfiguration,
  WorkerOptions,
} from "@/lib/workable";

export type QueueConfigurationField = QueueRequestSchemaDescriptor["tabs"][number]["fields"][number];
export type QueueConfigurationTab = QueueRequestSchemaDescriptor["tabs"][number];
export type QueueConfigurationFieldSection = {
  id: string;
  label: string;
  description?: string;
  fields: QueueConfigurationField[];
};

export type WorkerConfigurationDifference = {
  currentValue: unknown;
  defaultValue: unknown;
  label: string;
  path: string;
  tabLabel: string;
};

export type WorkerReconfigurationRequest = {
  profilingEnabled?: boolean;
  start?: WorkConfiguration["start"];
  coordination?: WorkConfiguration["coordination"];
  recurrence?: WorkConfiguration["recurrence"];
  transientRetry?: WorkConfiguration["transientRetry"];
  logging?: WorkConfiguration["logging"];
  retention?: WorkConfiguration["retention"];
};

const fieldSectionLabels: Record<string, string> = {
  concurrencyKey: "Concurrency key",
  idempotency: "Idempotency",
  durability: "Durability",
  subjectId: "Queue subject",
  transientRetry: "Transient retry",
};

const fieldSectionDescriptions: Record<string, string> = {
  concurrency: "Capacity rules that decide how many workers may occupy the same group and whether extra work waits or is rejected.",
  concurrencyKey: "Optional queue identity used when concurrency scope is configured per key.",
  durability: "Persistence-backed queueing and completion settings.",
  idempotency: "Duplicate-subject protection for this work definition.",
  subjectId: "Optional queue-time subject you supply when starting the worker. Idempotency, querying, and subject-scoped concurrency can use it.",
};

const rootFieldSectionDescriptions: Record<string, string> = {
  coordination: "Turn coordination on and choose where Workable keeps the state used by duplicate protection, capacity limits, and durable queueing.",
  queue: "How the queue request returns and which worker-level options are applied.",
};

export function createConfigurationFieldSections(tab: QueueConfigurationTab): QueueConfigurationFieldSection[] {
  const tabBasePath = findTabBasePath(tab);
  const sections = new Map<string, QueueConfigurationFieldSection>();

  for (const field of tab.fields) {
    const sectionId = getFieldSectionId(field.path, tabBasePath);
    const existing = sections.get(sectionId);
    if (existing) {
      existing.fields.push(field);
      continue;
    }

    sections.set(sectionId, {
      id: sectionId,
      label: labelForFieldSection(sectionId, tab),
      description: descriptionForFieldSection(sectionId, tab),
      fields: [field],
    });
  }

  return Array.from(sections.values());
}

export function findTabBasePath(tab: QueueConfigurationTab) {
  const configurationPrefix = `options.configuration.${tab.id}`;
  if (tab.fields.some((field) => field.path === configurationPrefix || field.path.startsWith(`${configurationPrefix}.`))) {
    return configurationPrefix;
  }

  return "";
}

export function getFieldSectionId(path: string, tabBasePath: string) {
  if (tabBasePath && (path === tabBasePath || path.startsWith(`${tabBasePath}.`))) {
    const remaining = path === tabBasePath
      ? []
      : path.slice(tabBasePath.length + 1).split(".");
    return remaining.length > 1 ? `${tabBasePath}.${remaining[0]}` : "root";
  }

  const segments = path.split(".").filter(Boolean);
  return segments.length > 1 ? segments[0] ?? "root" : "root";
}

export function labelForFieldSection(sectionId: string, tab: QueueConfigurationTab) {
  if (sectionId === "root") {
    return `${tab.label} settings`;
  }

  const segment = sectionId.split(".").at(-1) ?? sectionId;
  return fieldSectionLabels[segment] ?? humanizePathSegment(segment);
}

export function descriptionForFieldSection(sectionId: string, tab: QueueConfigurationTab) {
  if (sectionId === "root") {
    return rootFieldSectionDescriptions[tab.id];
  }

  const segment = sectionId.split(".").at(-1) ?? sectionId;
  return fieldSectionDescriptions[segment];
}

export function humanizePathSegment(value: string) {
  return value
    .replace(/([a-z0-9])([A-Z])/g, "$1 $2")
    .replace(/[_-]+/g, " ")
    .replace(/\b\w/g, (character) => character.toUpperCase());
}

export function createDefinitionConfigurationDescriptor(
  descriptor: QueueRequestSchemaDescriptor | null
): QueueRequestSchemaDescriptor | null {
  if (!descriptor) {
    return null;
  }

  const tabs = descriptor.tabs
    .map((tab) => ({
      ...tab,
      fields: tab.fields.filter((field) => field.path.startsWith("options.")),
    }))
    .filter((tab) => tab.fields.length > 0);

  return {
    ...descriptor,
    tabs,
  };
}

export function createWorkerConfigurationDescriptor(
  descriptor: QueueRequestSchemaDescriptor | null
): QueueRequestSchemaDescriptor | null {
  if (!descriptor) {
    return null;
  }

  const tabs = descriptor.tabs
    .map((tab) => ({
      ...tab,
      fields: tab.fields.filter((field) =>
        field.path === "options.profilingEnabled" ||
        field.path.startsWith("options.configuration.")
      ),
    }))
    .filter((tab) => tab.fields.length > 0);

  return tabs.length === 0
    ? null
    : {
        ...descriptor,
        tabs,
      };
}

export function parseSchemaJsonValue(json?: string | null) {
  if (!json?.trim()) {
    return null;
  }

  try {
    return JSON.parse(json) as unknown;
  } catch {
    return json;
  }
}

export function parseJsonText(value: string):
  | { ok: true; value: unknown }
  | { error: string; ok: false } {
  if (!value.trim()) {
    return { ok: true, value: null };
  }

  try {
    return { ok: true, value: JSON.parse(value) as unknown };
  } catch (caught) {
    return {
      error: caught instanceof Error ? caught.message : "Invalid JSON.",
      ok: false,
    };
  }
}

export function parseQueueJson(value: string) {
  if (!value.trim()) {
    return undefined;
  }

  try {
    return JSON.parse(value);
  } catch {
    throw new Error("Input must be valid JSON.");
  }
}

export function parseOptionalObjectJson<T>(value: string, label: string): T | undefined {
  const parsed = parseQueueJson(value);

  if (parsed === undefined) {
    return undefined;
  }

  if (!isPlainObject(parsed)) {
    throw new Error(`${label} must be a JSON object.`);
  }

  if (Object.keys(parsed).length === 0) {
    return undefined;
  }

  return parsed as T;
}

export function createEffectiveConfigurationOptions(
  definition: WorkDefinition | null
): WorkerOptions {
  return {
    profilingEnabled: definition?.defaultOptions?.profilingEnabled ?? false,
    configuration: stripInvocationConfiguration(cloneConfiguration(
      definition?.configuration ?? defaultWorkConfiguration
    )),
  };
}

export function createWorkerConfigurationRequest(worker: WorkableHttpWorkerConfiguration): QueueWorkRequest {
  return {
    options: {
      profilingEnabled: worker.profilingEnabled,
      configuration: stripInvocationConfiguration(cloneConfiguration(
        worker.configuration ?? defaultWorkConfiguration
      )),
    },
  };
}

export function createWorkerReconfiguration(request: QueueWorkRequest): WorkerReconfigurationRequest {
  const configuration = request.options?.configuration
    ? stripInvocationConfiguration(cloneConfiguration(request.options.configuration))
    : stripInvocationConfiguration(cloneConfiguration(defaultWorkConfiguration));

  return {
    profilingEnabled: request.options?.profilingEnabled ?? false,
    start: configuration.start,
    coordination: configuration.coordination,
    recurrence: configuration.recurrence,
    transientRetry: configuration.transientRetry,
    logging: configuration.logging,
    retention: configuration.retention,
  };
}

export function createWorkerConfigurationDifferences(
  currentRequest: QueueWorkRequest,
  defaultRequest: QueueWorkRequest,
  descriptor: QueueRequestSchemaDescriptor | null
): WorkerConfigurationDifference[] {
  if (!descriptor) {
    return [];
  }

  return descriptor.tabs.flatMap((tab) =>
    tab.fields.flatMap((field) => {
      const currentValue = getValueAtPath(currentRequest, field.path);
      const defaultValue = getValueAtPath(defaultRequest, field.path);
      if (configurationValuesEqual(currentValue, defaultValue)) {
        return [];
      }

      return [{
        currentValue,
        defaultValue,
        label: field.label,
        path: field.path,
        tabLabel: tab.label,
      } satisfies WorkerConfigurationDifference];
    })
  );
}

export function getValueAtPath(value: unknown, path: string): unknown {
  return path
    .split(".")
    .filter(Boolean)
    .reduce<unknown>((current, segment) => {
      if (!current || typeof current !== "object" || !(segment in current)) {
        return undefined;
      }

      return (current as Record<string, unknown>)[segment];
    }, value);
}

export function configurationValuesEqual(left: unknown, right: unknown) {
  return JSON.stringify(left ?? null) === JSON.stringify(right ?? null);
}

export function formatConfigurationValue(value: unknown) {
  if (value === undefined || value === null) {
    return "null";
  }

  if (typeof value === "string") {
    return value;
  }

  if (typeof value === "number" || typeof value === "boolean") {
    return String(value);
  }

  return JSON.stringify(value);
}

export function createQueueDialogRequest(
  definition: WorkDefinition | null,
  initialRequest?: QueueWorkRequest | null
): QueueWorkRequest {
  if (!initialRequest) {
    return createDefaultQueueRequest(definition);
  }

  const defaultRequest = createDefaultQueueRequest(definition);
  return applyQueueConfigurationRules(sanitizeQueueWorkRequest({
    ...defaultRequest,
    ...cloneQueueWorkRequest(initialRequest),
    options: {
      ...defaultRequest.options,
      ...cloneWorkerOptions(initialRequest.options),
    },
  }));
}

export function createDefaultQueueRequest(definition: WorkDefinition | null): QueueWorkRequest {
  return {
    completion: "ReturnAfterAccepted",
    options: createEffectiveConfigurationOptions(definition),
  };
}

export function createCopiedWorkerQueueRequest(
  worker: Pick<WorkableHttpWorkerConfiguration, "configuration" | "profilingEnabled" | "subjectId" | "concurrencyKey">,
  configurationOverride?: QueueWorkRequest | null
): QueueWorkRequest {
  const effectiveConfiguration = configurationOverride?.options?.configuration
    ? stripInvocationConfiguration(cloneConfiguration(configurationOverride.options.configuration))
    : stripInvocationConfiguration(cloneConfiguration(
      worker.configuration ?? defaultWorkConfiguration
    ));

  return sanitizeQueueWorkRequest({
    completion: "ReturnAfterAccepted",
    subjectId: cloneTypedValue(worker.subjectId) ?? undefined,
    concurrencyKey: cloneTypedValue(worker.concurrencyKey) ?? undefined,
    options: {
      profilingEnabled: configurationOverride?.options?.profilingEnabled ?? worker.profilingEnabled,
      configuration: effectiveConfiguration,
    },
  });
}

export function cloneQueueWorkRequest(request: QueueWorkRequest): QueueWorkRequest {
  return {
    ...request,
    concurrencyKey: cloneTypedValue(request.concurrencyKey),
    identifiers: cloneTypedValues(request.identifiers),
    input: cloneJsonValue(request.input),
    options: cloneWorkerOptions(request.options),
    subjectId: cloneTypedValue(request.subjectId),
  };
}

export function cloneWorkerOptions(options?: WorkerOptions | null): WorkerOptions | undefined {
  if (!options) {
    return undefined;
  }

  return {
    ...options,
    configuration: options.configuration
      ? stripInvocationConfiguration(cloneConfiguration(options.configuration))
      : options.configuration,
  };
}

export function cloneTypedValue<T extends { type: string; value: string } | null | undefined>(value: T): T {
  if (!value) {
    return value;
  }

  return { ...value } as T;
}

export function cloneTypedValues<T extends { type: string; value: string }>(values?: T[] | null): T[] | undefined {
  return values?.map((value) => ({ ...value }));
}

export function cloneJsonValue<T>(value: T): T {
  if (value === undefined || value === null) {
    return value;
  }

  return JSON.parse(JSON.stringify(value)) as T;
}

export function sanitizeQueueWorkRequest(request: QueueWorkRequest): QueueWorkRequest {
  const sanitized: QueueWorkRequest = { ...request };

  if (sanitized.subjectId && (!sanitized.subjectId.type.trim() || !sanitized.subjectId.value.trim())) {
    delete sanitized.subjectId;
  }

  if (sanitized.concurrencyKey && (!sanitized.concurrencyKey.type.trim() || !sanitized.concurrencyKey.value.trim())) {
    delete sanitized.concurrencyKey;
  }

  if (!sanitized.options?.configuration) {
    return sanitized;
  }

  return {
    ...sanitized,
    options: {
      ...sanitized.options,
      configuration: stripInvocationConfiguration(sanitized.options.configuration),
    },
  };
}

export function applyQueueConfigurationRules(request: QueueWorkRequest): QueueWorkRequest {
  const configuration = request.options?.configuration;
  const coordination = configuration?.coordination;
  if (
    !configuration ||
    !coordination ||
    coordination.storage !== "Persistent" ||
    !coordination.concurrency.isEnabled
  ) {
    return request;
  }

  if (
    coordination.durability.isEnabled &&
    coordination.concurrency.blockingMode === "WhileExecuting" &&
    coordination.concurrency.limitReachedBehavior === "DeferStart"
  ) {
    return request;
  }

  return {
    ...request,
    options: {
      ...request.options,
      configuration: {
        ...configuration,
        coordination: {
          ...coordination,
          concurrency: {
            ...coordination.concurrency,
            blockingMode: "WhileExecuting",
            limitReachedBehavior: "DeferStart",
          },
          durability: {
            ...coordination.durability,
            isEnabled: true,
          },
        },
      },
    },
  };
}

export function getQueueConfigurationFieldConstraint(
  request: QueueWorkRequest,
  path: string
): { reason: string; value: string | boolean } | null {
  if (!isPersistentConcurrencyActive(request)) {
    return null;
  }

  const reason = "Required while coordination storage is Persistent and concurrency is enabled.";
  switch (path) {
    case "options.configuration.coordination.concurrency.blockingMode":
      return { reason, value: "WhileExecuting" };
    case "options.configuration.coordination.concurrency.limitReachedBehavior":
      return { reason, value: "DeferStart" };
    case "options.configuration.coordination.durability.isEnabled":
      return {
        reason: "Persistent concurrency requires durable queueing so accepted workers can wait safely for capacity.",
        value: true,
      };
    default:
      return null;
  }
}

export function isPersistentConcurrencyActive(request: QueueWorkRequest) {
  const coordination = request.options?.configuration?.coordination;
  return coordination?.storage === "Persistent" &&
    coordination.concurrency.isEnabled;
}

export function cloneConfiguration(configuration: WorkConfiguration): WorkConfiguration {
  return JSON.parse(JSON.stringify(configuration)) as WorkConfiguration;
}

export function stripInvocationConfiguration(configuration: WorkConfiguration): WorkConfiguration {
  const queueConfiguration = { ...configuration } as WorkConfiguration & {
    invocation?: unknown;
  };
  delete queueConfiguration.invocation;

  return queueConfiguration;
}

export function isPlainObject(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}

export const defaultWorkConfiguration: WorkConfiguration = {
  start: {
    policy: "StartAndReturnAfterAccepted",
  },
  coordination: {
    isEnabled: false,
    storage: "Local",
    idempotency: {
      isEnabled: false,
      conflictPolicy: "RejectDuplicates",
    },
    concurrency: {
      isEnabled: false,
      maximumCapacity: 0,
      scope: "PerDefinition",
      blockingMode: "WhileExecutingPausedOrFailed",
      limitReachedBehavior: "Ignore",
      overrideBehavior: "Flexible",
    },
    durability: {
      isEnabled: false,
      completeDurably: false,
    },
  },
  recurrence: {
    isEnabled: false,
    interval: "00:00:00",
    continueAfterFailure: true,
    circuitBreakerFailureThreshold: 3,
    retainedIterations: 25,
    raiseCircuitBreakerOpenedEvent: true,
  },
  transientRetry: {
    count: 0,
    initialDelay: "00:00:00.8000000",
    jitter: "00:00:00.5000000",
    maximumDelay: "00:00:30",
    backoff: "Exponential",
  },
  logging: {
    isEnabled: true,
    level: "Information",
    maximumBufferedEntries: 100,
  },
  retention: {
    purgeInterval: "00:10:00",
    maximumFinalWorkers: 1000,
  },
};
