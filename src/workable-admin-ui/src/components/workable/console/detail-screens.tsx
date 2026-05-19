"use client";

import {
  Ban,
  Braces,
  CheckCircle2,
  Clock3,
  Info,
  Loader2,
  Pause,
  Play,
  RefreshCw,
  Search,
  Send,
  Trash2,
} from "lucide-react";
import type { Dispatch, ReactNode, SetStateAction } from "react";
import { Fragment, useEffect, useMemo, useRef, useState } from "react";
import { Badge } from "@/components/ui/badge";
import {
  Breadcrumb,
  BreadcrumbItem,
  BreadcrumbLink,
  BreadcrumbList,
  BreadcrumbPage,
  BreadcrumbSeparator,
} from "@/components/ui/breadcrumb";
import { Button } from "@/components/ui/button";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card";
import { Dialog, DialogContent, DialogHeader, DialogTitle } from "@/components/ui/dialog";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { Skeleton } from "@/components/ui/skeleton";
import { Tabs, TabsContent, TabsList, TabsTrigger } from "@/components/ui/tabs";
import { Textarea } from "@/components/ui/textarea";
import {
  SchemaForm,
  SchemaPathField,
  SchemaPresetButton,
  compactJson,
  createDefaultValue,
  parseJsonSchema,
} from "@/components/workable/schema-form";
import {
  ErrorBanner,
  ErrorPanel,
  FeedbackBanner,
  type FeedbackTone,
} from "@/components/workable/console/feedback-panel";
import {
  formatDateTime,
  stateTone,
  workableFetch,
  type QueueRequestSchemaDescriptor,
  type QueueWorkRequest,
  type WorkAction,
  type WorkConfiguration,
  type WorkDefinition,
  type WorkDefinitionReconfigurationOutcome,
  type WorkInfo,
  type WorkTypedValue,
  type WorkableConnection,
  type WorkerOptions,
  type WorkerSnapshot,
} from "@/lib/workable";

type OverviewScope = {
  category?: string;
  definitionName?: string;
  includeSubcategories?: boolean;
};
type Loadable<T> = {
  data?: T;
  error?: string;
  loading: boolean;
  refreshing?: boolean;
};
export function DefinitionsView({
  catalogScope,
  connection,
  onCatalogScopeChange,
  onOpenDefinition,
  onOpenWorker,
  onReady,
  refreshToken,
}: {
  catalogScope: OverviewScope | null;
  connection: WorkableConnection;
  onCatalogScopeChange: (scope: OverviewScope | null) => void;
  onOpenDefinition: (definitionId: string) => void;
  onOpenWorker: (workerId: string) => void;
  onReady: () => void;
  refreshToken: number;
}) {
  const definitions = useWorkableResource<WorkDefinition[]>(
    connection,
    "definitions",
    refreshToken
  );
  const [search, setSearch] = useState("");
  const [queueDefinition, setQueueDefinition] = useState<WorkDefinition | null>(null);
  const autoOpenedDefinitionScope = useRef("");
  const isReady = !definitions.loading;

  useEffect(() => {
    if (isReady) {
      onReady();
    }
  }, [isReady, onReady]);

  const filtered = useMemo(() => {
    const query = search.trim().toLowerCase();
    return (definitions.data ?? [])
      .filter((definition) => definitionMatchesCatalogScope(definition, catalogScope))
      .filter((definition) =>
        !query ||
        [definition.name, definition.category, definition.description]
        .filter(Boolean)
        .some((value) => String(value).toLowerCase().includes(query))
      );
  }, [catalogScope, definitions.data, search]);

  useEffect(() => {
    if (!catalogScope?.definitionName) {
      autoOpenedDefinitionScope.current = "";
    }
    const autoOpenKey =
      catalogScope?.definitionName && filtered.length === 1
        ? `${catalogScope.definitionName}:${filtered[0].id.value}`
        : "";
    if (
      !definitions.loading &&
      catalogScope?.definitionName &&
      filtered.length === 1 &&
      autoOpenedDefinitionScope.current !== autoOpenKey
    ) {
      autoOpenedDefinitionScope.current = autoOpenKey;
      onOpenDefinition(filtered[0].id.value);
    }
  }, [catalogScope?.definitionName, definitions.loading, filtered, onOpenDefinition]);

  return (
    <div className="space-y-6">
      <ErrorPanel errors={[definitions.error]} />
      <ViewActionLane />
      <Card>
        <CardHeader className="gap-4 md:flex-row md:items-center md:justify-between">
          <div className="min-w-0 flex-1">
            <ScopeTrail
              onClear={() => onCatalogScopeChange(null)}
              onSelectCategory={(category) => onCatalogScopeChange({
                category,
                includeSubcategories: true,
              })}
              scope={catalogScope}
            />
          </div>
          <div className="relative w-full md:w-80">
            <Search className="absolute left-3 top-2.5 size-4 text-muted-foreground" />
            <Input
              className="pl-9"
              onChange={(event) => setSearch(event.target.value)}
              placeholder="Search catalog"
              value={search}
            />
          </div>
        </CardHeader>
        <CardContent>
          {definitions.loading ? (
            <StackedSkeleton count={8} />
          ) : (
            <div className="grid gap-3 md:grid-cols-2">
              {filtered.map((definition) => (
                <div
                  className="rounded-lg border bg-card p-4"
                  key={definition.id.value}
                >
                  <div className="flex items-start justify-between gap-4">
                    <div className="min-w-0">
                      <div className="truncate font-mono text-sm">{definition.name}</div>
                      <div className="mt-1 text-muted-foreground text-sm">
                        {definition.description ?? "No description"}
                      </div>
                    </div>
                    <Badge variant="secondary">{definition.category ?? "Uncategorized"}</Badge>
                  </div>
                  <div className="mt-4 flex justify-end gap-2">
                    <Button
                      onClick={() => onOpenDefinition(definition.id.value)}
                      size="sm"
                      variant="outline"
                    >
                      <Info className="size-4" />
                      Definition
                    </Button>
                    <Button onClick={() => setQueueDefinition(definition)} size="sm">
                      <Send className="size-4" />
                      Queue
                    </Button>
                  </div>
                </div>
              ))}
            </div>
          )}
        </CardContent>
      </Card>
      <QueueDialog
        connection={connection}
        definition={queueDefinition}
        onQueuedWorker={onOpenWorker}
        onOpenChange={(open) => !open && setQueueDefinition(null)}
      />
    </div>
  );
}

export function DefinitionView({
  connection,
  definitionId,
  onOpenWorker,
  onReady,
  refreshToken,
}: {
  connection: WorkableConnection;
  definitionId: string;
  onOpenWorker: (workerId: string) => void;
  onReady: () => void;
  refreshToken: number;
}) {
  const info = useWorkableResource<WorkInfo>(
    connection,
    `definitions/${definitionId}/info`,
    refreshToken
  );
  const [queueDefinition, setQueueDefinition] = useState<WorkDefinition | null>(null);
  const [definitionRequest, setDefinitionRequest] = useState<QueueWorkRequest>(() =>
    createDefaultQueueRequest(null)
  );
  const [queueSchemaDescriptor, setQueueSchemaDescriptor] =
    useState<QueueRequestSchemaDescriptor | null>(null);
  const [saveError, setSaveError] = useState<string>();
  const [saveStatus, setSaveStatus] = useState<string>();
  const [isSaving, setIsSaving] = useState(false);
  const [updatedDefinition, setUpdatedDefinition] = useState<WorkDefinition | null>(null);
  const definition = updatedDefinition ?? info.data?.definition;
  const isReady = !info.loading;
  const queueRequestSchema = useMemo(
    () => parseJsonSchema(queueSchemaDescriptor?.schema?.jsonSchema),
    [queueSchemaDescriptor?.schema?.jsonSchema]
  );
  const definitionConfigurationDescriptor = useMemo(
    () => createDefinitionConfigurationDescriptor(queueSchemaDescriptor),
    [queueSchemaDescriptor]
  );

  useEffect(() => {
    if (isReady) {
      onReady();
    }
  }, [isReady, onReady]);

  useEffect(() => {
    if (!definition) {
      return;
    }

    const nextRequest = createDefaultQueueRequest(definition);
    queueMicrotask(() => {
      setDefinitionRequest(nextRequest);
      setSaveError(undefined);
      setSaveStatus(undefined);
    });
  }, [definition]);

  useEffect(() => {
    queueMicrotask(() => setUpdatedDefinition(null));
  }, [definitionId, info.data?.definition]);

  useEffect(() => {
    let canceled = false;

    workableFetch<QueueRequestSchemaDescriptor>(connection, "queue-request/schema")
      .then((descriptor) => {
        if (!canceled) {
          setQueueSchemaDescriptor(descriptor);
        }
      })
      .catch(() => {
        if (!canceled) {
          setQueueSchemaDescriptor(null);
        }
      });

    return () => {
      canceled = true;
    };
  }, [connection]);

  const saveConfiguration = async () => {
    if (!definition) {
      return;
    }

    setIsSaving(true);
    setSaveError(undefined);
    setSaveStatus(undefined);

    try {
      const configuration = definitionRequest.options?.configuration
        ? stripInvocationConfiguration(definitionRequest.options.configuration)
        : stripInvocationConfiguration(cloneConfiguration(defaultWorkConfiguration));
      const defaultOptions: WorkerOptions = {
        ...definition.defaultOptions,
        profilingEnabled: definitionRequest.options?.profilingEnabled ?? false,
        configuration: definition.defaultOptions?.configuration ?? null,
      };
      const result = await workableFetch<WorkDefinitionReconfigurationOutcome>(
        connection,
        `definitions/${definition.id.value}/reconfigure`,
        {
          method: "POST",
          body: JSON.stringify({
            revision: definition.revision,
            changes: {
              configuration,
              defaultOptions,
            },
          }),
        }
      );

      if (result.definition) {
        setUpdatedDefinition(result.definition);
      }
      setSaveStatus(`Definition configuration ${result.status.toLowerCase()}.`);
    } catch (caught) {
      setSaveError(caught instanceof Error ? caught.message : "Configuration update failed.");
    } finally {
      setIsSaving(false);
    }
  };

  return (
    <div className="space-y-6">
      <ErrorPanel errors={[info.error, saveError]} />
      <ViewActionLane />
      {info.loading ? (
        <StackedSkeleton count={6} />
      ) : !definition ? (
        <div className="rounded-lg border border-dashed p-8 text-center text-muted-foreground text-sm">
          Definition not found.
        </div>
      ) : (
        <>
          {saveStatus && (
            <FeedbackBanner
              key={saveStatus}
              message={saveStatus}
              title="Configuration saved"
              tone="success"
            />
          )}
          <Card>
            <CardHeader className="gap-4 md:flex-row md:items-start md:justify-between">
              <div className="min-w-0">
                <CardTitle className="truncate font-mono text-base">
                  {definition.name}
                </CardTitle>
                <CardDescription className="mt-1">
                  {definition.description ?? "No description"}
                </CardDescription>
                <div className="mt-3 flex flex-wrap gap-2">
                  <Badge variant="secondary">{definition.category ?? "Uncategorized"}</Badge>
                  <Badge variant="outline">Revision {definition.revision}</Badge>
                  <Badge variant="outline">{info.data?.status ?? "Unknown"}</Badge>
                </div>
              </div>
              <Button onClick={() => setQueueDefinition(definition)}>
                <Send className="size-4" />
                Queue
              </Button>
            </CardHeader>
            <CardContent>
              <div className="grid gap-3 md:grid-cols-4">
                <MetadataItem label="Total workers" value={String(info.data?.workers.total ?? 0)} />
                <MetadataItem label="Active" value={String(info.data?.workers.active ?? 0)} />
                <MetadataItem label="Failed" value={String(info.data?.workers.failed ?? 0)} />
                <MetadataItem
                  label="Last activity"
                  value={formatDateTime(info.data?.workers.lastActivityAt)}
                />
              </div>
            </CardContent>
          </Card>
          <Card>
            <CardHeader className="gap-3 md:flex-row md:items-center md:justify-between">
              <div>
                <CardTitle className="text-base">Configuration</CardTitle>
                <CardDescription>
                  Changes apply to future workers queued from this definition.
                </CardDescription>
              </div>
              <Button disabled={isSaving} onClick={() => void saveConfiguration()} size="sm">
                {isSaving ? (
                  <Loader2 className="size-4 animate-spin" />
                ) : (
                  <CheckCircle2 className="size-4" />
                )}
                Save
              </Button>
            </CardHeader>
            <CardContent>
              <div className="space-y-6">
                <QueueConfigurationTabs
                  descriptor={definitionConfigurationDescriptor}
                  onRequestChange={setDefinitionRequest}
                  request={definitionRequest}
                  schema={queueRequestSchema}
                />
                <div className="grid gap-4 lg:grid-cols-2">
                  <SnapshotBlock
                    label="Input schema"
                    value={parseSchemaJsonValue(definition.inputSchema?.jsonSchema)}
                  />
                  <SnapshotBlock
                    label="Output schema"
                    value={parseSchemaJsonValue(definition.outputSchema?.jsonSchema)}
                  />
                  <SnapshotBlock label="Metadata" value={definition.metadata} />
                </div>
              </div>
            </CardContent>
          </Card>
          <QueueDialog
            connection={connection}
            definition={queueDefinition}
            onQueuedWorker={onOpenWorker}
            onOpenChange={(open) => !open && setQueueDefinition(null)}
          />
        </>
      )}
    </div>
  );
}

export function WorkerConsoleView({
  backLabel,
  connection,
  onBack,
  refreshToken,
  workerId,
}: {
  backLabel: string;
  connection: WorkableConnection;
  onBack: () => void;
  refreshToken: number;
  workerId: string;
}) {
  const [actionFeedback, setActionFeedback] = useState<{
    message: string;
    tone: FeedbackTone;
  }>();
  const [actionRefreshToken, setActionRefreshToken] = useState(0);
  const snapshot = useWorkableResource<WorkerSnapshot>(
    connection,
    `workers/${workerId}`,
    refreshToken + actionRefreshToken
  );

  const executeAction = async (action: WorkAction) => {
    const current = snapshot.data;
    if (!current) {
      return;
    }

    try {
      const result = await workableFetch<{ status: string; messages?: { text: string }[] }>(
        connection,
        `workers/${current.id.value}/actions/${action.toLowerCase()}`,
        {
          method: "POST",
          body: JSON.stringify({ revision: current.revision }),
        }
      );
      const message = result.messages?.map((message) => message.text).filter(Boolean).join(" ") ||
        `${action} returned ${result.status}.`;
      setActionFeedback({
        message,
        tone: result.status === "Accepted" ? "info" : "warning",
      });
      setActionRefreshToken((value) => value + 1);
    } catch (error) {
      setActionFeedback({
        message: error instanceof Error ? error.message : `Unable to ${action.toLowerCase()} worker.`,
        tone: "warning",
      });
    }
  };

  return (
    <div className="space-y-6">
      <ViewActionLane />
      <div className="flex flex-wrap items-start justify-between gap-4">
        <PageHeading
          description={workerId}
          title={snapshot.data?.definitionName ?? "Worker"}
        />
        <Button onClick={onBack} size="sm" variant="outline">
          {backLabel}
        </Button>
      </div>
      {snapshot.loading && <StackedSkeleton count={8} />}
      {snapshot.error && (
        <ErrorBanner key={snapshot.error} message={snapshot.error} title="Unable to load worker" />
      )}
      {snapshot.data && (
        <>
          <div className="grid gap-4 md:grid-cols-2 xl:grid-cols-4">
            <Card>
              <CardHeader className="pb-2">
                <CardDescription>State</CardDescription>
              </CardHeader>
              <CardContent>
                <Badge className={stateTone(snapshot.data.state)} variant="outline">
                  {snapshot.data.state}
                </Badge>
              </CardContent>
            </Card>
            <Card>
              <CardHeader className="pb-2">
                <CardDescription>Revision</CardDescription>
              </CardHeader>
              <CardContent className="font-mono text-2xl">
                {snapshot.data.revision}
              </CardContent>
            </Card>
            <Card>
              <CardHeader className="pb-2">
                <CardDescription>State Sequence</CardDescription>
              </CardHeader>
              <CardContent className="font-mono text-2xl">
                {snapshot.data.stateSequence}
              </CardContent>
            </Card>
            <Card>
              <CardHeader className="pb-2">
                <CardDescription>Updated</CardDescription>
              </CardHeader>
              <CardContent className="text-sm">
                {formatDateTime(snapshot.data.updatedAt)}
              </CardContent>
            </Card>
          </div>

          <Card>
            <CardHeader className="gap-4 lg:flex-row lg:items-center lg:justify-between">
              <div>
                <CardTitle>Worker Console</CardTitle>
                <CardDescription>
                  Versioned actions are sent with the current snapshot revision.
                </CardDescription>
              </div>
              <div className="flex flex-wrap gap-2">
                <WorkerActionButton action="Start" icon={Play} onAction={executeAction} />
                <WorkerActionButton action="Pause" icon={Pause} onAction={executeAction} />
                <WorkerActionButton action="Cancel" icon={Ban} onAction={executeAction} />
                <WorkerActionButton action="Push" icon={Clock3} onAction={executeAction} />
                <WorkerActionButton action="Purge" icon={Trash2} onAction={executeAction} />
              </div>
            </CardHeader>
            <CardContent className="grid gap-4 md:grid-cols-2 xl:grid-cols-3">
              <MetadataItem label="Worker ID" value={snapshot.data.id.value} />
              <MetadataItem label="Definition" value={snapshot.data.definitionName} />
              <MetadataItem label="Definition ID" value={snapshot.data.definitionId.value} />
              <MetadataItem label="Category" value={snapshot.data.definitionCategory ?? "-"} />
              <MetadataItem label="Created" value={formatDateTime(snapshot.data.createdAt)} />
              <MetadataItem label="Updated" value={formatDateTime(snapshot.data.updatedAt)} />
              <MetadataItem
                label="Subject"
                value={formatTypedValue(snapshot.data.subjectId)}
              />
              <MetadataItem
                label="Concurrency Key"
                value={formatTypedValue(snapshot.data.concurrencyKey)}
              />
              <MetadataItem
                label="Identifiers"
                value={snapshot.data.identifiers?.map(formatTypedValue).join(", ") || "-"}
              />
            </CardContent>
          </Card>

          {actionFeedback && (
            <FeedbackBanner
              key={actionFeedback.message}
              message={actionFeedback.message}
              title="Action result"
              tone={actionFeedback.tone}
            />
          )}

          <div className="grid gap-4 xl:grid-cols-2">
            <SnapshotBlock label="Input" value={snapshot.data.input} />
            <SnapshotBlock label="Output" value={snapshot.data.output} />
            <SnapshotBlock label="Messages" value={snapshot.data.messages} />
            <SnapshotBlock label="Iterations" value={snapshot.data.iterations} />
            <SnapshotBlock label="Logs" value={snapshot.data.logs} />
            <SnapshotBlock label="Action History" value={snapshot.data.actionHistory} />
            <SnapshotBlock label="Profile" value={snapshot.data.profile} />
            <SnapshotBlock label="Version" value={snapshot.data.version} />
          </div>
        </>
      )}
    </div>
  );
}

export function QueueDialog({
  connection,
  definition,
  onQueuedWorker,
  onOpenChange,
}: {
  connection: WorkableConnection;
  definition: WorkDefinition | null;
  onQueuedWorker: (workerId: string) => void;
  onOpenChange: (open: boolean) => void;
}) {
  const inputSchema = useMemo(
    () => parseJsonSchema(definition?.inputSchema?.jsonSchema),
    [definition?.inputSchema?.jsonSchema]
  );
  const [activeTab, setActiveTab] = useState<"input" | "config" | "manual">("input");
  const [formValue, setFormValue] = useState<unknown>(undefined);
  const [manualRequestJson, setManualRequestJson] = useState("{}");
  const [queueRequest, setQueueRequest] = useState<QueueWorkRequest>(() =>
    createDefaultQueueRequest(null)
  );
  const [queueSchemaDescriptor, setQueueSchemaDescriptor] =
    useState<QueueRequestSchemaDescriptor | null>(null);
  const [isQueueing, setIsQueueing] = useState(false);
  const [status, setStatus] = useState<string | undefined>();
  const [error, setError] = useState<string | undefined>();
  const queueRequestSchema = useMemo(
    () => parseJsonSchema(queueSchemaDescriptor?.schema?.jsonSchema),
    [queueSchemaDescriptor?.schema?.jsonSchema]
  );

  useEffect(() => {
    if (!definition) {
      queueMicrotask(() => setQueueSchemaDescriptor(null));
      return;
    }

    let canceled = false;

    workableFetch<QueueRequestSchemaDescriptor>(connection, "queue-request/schema")
      .then((descriptor) => {
        if (!canceled) {
          setQueueSchemaDescriptor(descriptor);
        }
      })
      .catch(() => {
        if (!canceled) {
          setQueueSchemaDescriptor(null);
        }
      });

    return () => {
      canceled = true;
    };
  }, [connection, definition]);

  useEffect(() => {
    const nextValue = createDefaultValue(inputSchema);
    const nextRequest = createDefaultQueueRequest(definition);
    queueMicrotask(() => {
      setActiveTab(inputSchema ? "input" : "manual");
      setFormValue(nextValue);
      setManualRequestJson(compactJson({
        completion: "ReturnAfterAccepted",
        input: nextValue,
      }));
      setQueueRequest(nextRequest);
      setIsQueueing(false);
      setStatus(undefined);
      setError(undefined);
    });
  }, [definition, inputSchema]);

  const updateFormValue = (nextValue: unknown) => {
    setFormValue(nextValue);
  };

  const resetQueueConfiguration = () => {
    setQueueRequest((current) => ({
      ...current,
      options: createEffectiveConfigurationOptions(definition),
    }));
  };

  const createComposedRequest = () => {
    const request: QueueWorkRequest = {
      ...queueRequest,
      input: formValue,
    };

    return sanitizeQueueWorkRequest(request);
  };

  const queue = async () => {
    if (!definition) {
      return;
    }

    setError(undefined);
    setStatus(undefined);
    setIsQueueing(true);

    try {
      const request =
        activeTab === "manual"
          ? sanitizeQueueWorkRequest(
              parseOptionalObjectJson<QueueWorkRequest>(manualRequestJson, "Manual request") ?? {}
            )
          : createComposedRequest();
      const completionMode = request.completion ?? "ReturnAfterAccepted";

      const result = await workableFetch<{ queueOutcome?: { status: string }; workerId?: { value: string } }>(
        connection,
        `work/${definition.name}`,
        {
          method: "POST",
          body: JSON.stringify(request),
        }
      );

      if (completionMode !== "WaitForCompletion" && result.workerId?.value) {
        onOpenChange(false);
        onQueuedWorker(result.workerId.value);
        return;
      }

      setStatus(
        result.workerId
          ? completionMode === "WaitForCompletion"
            ? `Worker ${result.workerId.value} completed.`
            : `Queued worker ${result.workerId.value}`
          : `Queue result: ${result.queueOutcome?.status ?? "received"}`
      );
    } catch (caught) {
      setError(caught instanceof Error ? caught.message : "Queue request failed.");
    } finally {
      setIsQueueing(false);
    }
  };
  const isWaitingForCompletion = isQueueing && (
    activeTab === "manual"
      ? manualRequestJson.includes("WaitForCompletion")
      : queueRequest.completion === "WaitForCompletion"
  );

  return (
    <Dialog onOpenChange={onOpenChange} open={!!definition}>
      <DialogContent
        className="max-h-[88vh] overflow-hidden p-0 sm:max-w-5xl"
        onInteractOutside={(event) => event.preventDefault()}
      >
        <DialogHeader>
          <DialogTitle className="flex min-w-0 flex-wrap items-baseline gap-x-2 gap-y-1 px-4 pt-4">
            <span>Configure input, behavior, and runtime options for</span>
            <span
              className="min-w-0 truncate font-mono text-sm font-semibold text-sky-300"
              title={definition?.name}
            >
              {definition?.name ?? "worker"}
            </span>
          </DialogTitle>
        </DialogHeader>
        <div className="min-h-0 space-y-4 px-4">
          {error && (
            <ErrorBanner key={error} message={error} title="Queue failed" />
          )}
          {status && (
            <FeedbackBanner
              key={status}
              message={status}
              title="Queue accepted"
              tone="success"
            />
          )}
          {isWaitingForCompletion && (
            <FeedbackBanner
              message="The worker is executing. This dialog will update when the HTTP request returns."
              title="Waiting for completion"
              tone="info"
            />
          )}
          <Tabs
            onValueChange={(value) => {
              if (value === "manual") {
                try {
                  setManualRequestJson(compactJson(createComposedRequest()));
                } catch {
                  setManualRequestJson(compactJson({
                    completion: queueRequest.completion,
                    input: formValue,
                  }));
                }
              }
              setActiveTab(value as "input" | "config" | "manual");
            }}
            className="min-h-[62vh]"
            value={activeTab}
          >
            <div className="flex flex-wrap items-center justify-between gap-3 border-b pb-3">
              <TabsList className="grid w-full grid-cols-3 sm:w-[30rem]">
                <TabsTrigger disabled={!inputSchema} value="input">
                  Input
                </TabsTrigger>
                <TabsTrigger value="config">Config</TabsTrigger>
                <TabsTrigger value="manual">Manual JSON</TabsTrigger>
              </TabsList>
              {activeTab === "input" && (
                <SchemaPresetButton schema={inputSchema} onApply={updateFormValue} />
              )}
              {activeTab === "config" && (
                <Button onClick={resetQueueConfiguration} size="sm" type="button" variant="outline">
                  <RefreshCw className="size-4" />
                  Use definition defaults
                </Button>
              )}
            </div>
            <TabsContent className="mt-4 h-[54vh] overflow-y-auto pr-2" value="input">
              <SchemaForm
                onChange={updateFormValue}
                schema={inputSchema}
                value={formValue}
              />
            </TabsContent>
            <TabsContent className="mt-4 h-[54vh] overflow-y-auto pr-2" value="config">
              <QueueConfigurationTabs
                descriptor={queueSchemaDescriptor}
                onRequestChange={setQueueRequest}
                request={queueRequest}
                schema={queueRequestSchema}
              />
            </TabsContent>
            <TabsContent className="mt-4 h-[54vh] overflow-y-auto pr-2" value="manual">
              <JsonTextEditor
                label="Request JSON"
                onChange={setManualRequestJson}
                value={manualRequestJson}
              />
            </TabsContent>
          </Tabs>
          <div className="-mx-4 flex items-center justify-between gap-3 border-t bg-muted/30 px-4 py-3">
            <div className="min-w-0 text-sm">
              <span className="text-muted-foreground">Queue a worker for </span>
              <span className="font-mono font-semibold text-sky-300">
                {definition?.name ?? "definition"}
              </span>
            </div>
            <Button disabled={isQueueing} onClick={() => void queue()}>
              {isQueueing ? (
                <Loader2 className="size-4 animate-spin" />
              ) : (
                <Send className="size-4" />
              )}
              {isWaitingForCompletion ? "Waiting" : "Queue"}
            </Button>
          </div>
        </div>
      </DialogContent>
    </Dialog>
  );
}

function QueueConfigurationTabs({
  descriptor,
  onRequestChange,
  request,
  schema,
}: {
  descriptor: QueueRequestSchemaDescriptor | null;
  onRequestChange: Dispatch<SetStateAction<QueueWorkRequest>>;
  request: QueueWorkRequest;
  schema: ReturnType<typeof parseJsonSchema>;
}) {
  if (!descriptor || !schema) {
    return (
      <div className="rounded-lg border border-dashed p-6 text-muted-foreground text-sm">
        Queue configuration schema is not available from this Workable host.
      </div>
    );
  }

  const firstTab = descriptor.tabs[0]?.id ?? "queue";

  return (
    <Tabs className="min-h-full" defaultValue={firstTab}>
      <TabsList className="flex h-auto w-full flex-wrap justify-start">
        {descriptor.tabs.map((tab) => (
          <TabsTrigger key={tab.id} value={tab.id}>
            {tab.label}
          </TabsTrigger>
        ))}
      </TabsList>

      {descriptor.tabs.map((tab) => (
        <TabsContent className="mt-4 space-y-4" key={tab.id} value={tab.id}>
          <ConfigTabHeader description={tab.description} title={tab.label} />
          <div className="grid max-w-5xl gap-4 md:grid-cols-2">
            {tab.fields.map((field) => (
              <SchemaPathField
                description={field.description}
                key={`${tab.id}:${field.path}`}
                label={field.label}
                onChange={(next) => onRequestChange(next as QueueWorkRequest)}
                path={field.path}
                schema={schema}
                value={request}
              />
            ))}
          </div>
        </TabsContent>
      ))}
    </Tabs>
  );
}

function createDefinitionConfigurationDescriptor(
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

function ConfigTabHeader({
  description,
  title,
}: {
  description: string;
  title: string;
}) {
  return (
    <div className="max-w-3xl space-y-1">
      <h3 className="font-medium text-sm">{title}</h3>
      <p className="text-muted-foreground text-sm">{description}</p>
    </div>
  );
}

function ScopeTrail({
  onClear,
  onSelectCategory,
  onSelectDefinition,
  scope,
}: {
  onClear: () => void;
  onSelectCategory: (category: string) => void;
  onSelectDefinition?: (definitionName: string, category: string) => void;
  scope: OverviewScope | null;
}) {
  const categorySegments = splitCatalogPath(scope?.category ?? "");
  const categoryCrumbs = categorySegments.map((segment, index) => ({
    label: segment,
    path: categorySegments.slice(0, index + 1).join(":"),
  }));
  const hasDefinition = !!scope?.definitionName;
  const activeCategoryPath = scope?.category ?? "";

  return (
    <div className="min-w-0 overflow-x-auto text-xs">
      <Breadcrumb>
        <BreadcrumbList className="flex-nowrap whitespace-nowrap text-xs">
          <BreadcrumbItem className="shrink-0">
            {scope ? (
              <BreadcrumbLink asChild>
                <button onClick={onClear} type="button">
                  All categories
                </button>
              </BreadcrumbLink>
            ) : (
              <BreadcrumbPage>All categories</BreadcrumbPage>
            )}
          </BreadcrumbItem>
          {categoryCrumbs.map((crumb, index) => {
            const isCurrentCategory = !hasDefinition && index === categoryCrumbs.length - 1;

            return (
              <Fragment key={crumb.path}>
                <BreadcrumbSeparator className="shrink-0" />
                <BreadcrumbItem className="min-w-0 shrink-0">
                  {isCurrentCategory ? (
                    <BreadcrumbPage className="max-w-56 truncate">
                      {crumb.label}
                    </BreadcrumbPage>
                  ) : (
                    <BreadcrumbLink asChild className="max-w-56 truncate">
                      <button
                        onClick={() => onSelectCategory(crumb.path)}
                        type="button"
                      >
                        {crumb.label}
                      </button>
                    </BreadcrumbLink>
                  )}
                </BreadcrumbItem>
              </Fragment>
            );
          })}
          {hasDefinition && (
            <>
              <BreadcrumbSeparator className="shrink-0" />
              <BreadcrumbItem className="min-w-0 shrink-0">
                {onSelectDefinition && scope?.definitionName ? (
                  <BreadcrumbLink asChild className="max-w-80 truncate font-mono">
                    <button
                      onClick={() => onSelectDefinition(
                        scope.definitionName ?? "",
                        activeCategoryPath
                      )}
                      type="button"
                    >
                      {scope.definitionName}
                    </button>
                  </BreadcrumbLink>
                ) : (
                  <BreadcrumbPage className="max-w-80 truncate font-mono text-foreground">
                    {scope?.definitionName}
                  </BreadcrumbPage>
                )}
              </BreadcrumbItem>
            </>
          )}
        </BreadcrumbList>
      </Breadcrumb>
    </div>
  );
}

function ViewActionLane({ children }: { children?: ReactNode }) {
  return (
    <div
      aria-hidden={children ? undefined : true}
      className="-mb-2 flex min-h-9 min-w-0 -translate-y-2 items-center justify-end gap-1"
    >
      {children}
    </div>
  );
}

function PageHeading({
  description,
  title,
}: {
  description: string;
  title: string;
}) {
  return (
    <div>
      <h1 className="font-semibold text-2xl tracking-normal">{title}</h1>
      <p className="mt-1 max-w-3xl text-muted-foreground text-sm">
        {description}
      </p>
    </div>
  );
}

function WorkerActionButton({
  action,
  icon: Icon,
  onAction,
}: {
  action: WorkAction;
  icon: typeof Play;
  onAction: (action: WorkAction) => Promise<void>;
}) {
  return (
    <Button onClick={() => void onAction(action)} size="sm" variant="outline">
      <Icon className="size-4" />
      {action}
    </Button>
  );
}

function SnapshotBlock({ label, value }: { label: string; value: unknown }) {
  return (
    <div className="space-y-2">
      <div className="flex items-center gap-2 text-sm">
        <Braces className="size-4 text-muted-foreground" />
        <span className="font-medium">{label}</span>
      </div>
      <pre className="max-h-64 overflow-auto rounded-lg border bg-muted/30 p-3 font-mono text-xs leading-relaxed">
        <JsonValue value={value ?? null} />
      </pre>
    </div>
  );
}

function JsonTextEditor({
  label,
  onChange,
  value,
}: {
  label: string;
  onChange: (value: string) => void;
  value: string;
}) {
  const parsed = parseJsonText(value);

  const format = () => {
    if (!parsed.ok) {
      return;
    }

    onChange(JSON.stringify(parsed.value, null, 2));
  };

  return (
    <div className="grid h-full min-h-0 gap-3 lg:grid-cols-2">
      <div className="grid min-h-0 gap-2">
        <div className="flex items-center justify-between gap-2">
          <Label>{label}</Label>
          <Button disabled={!parsed.ok} onClick={format} size="xs" variant="outline">
            <Braces className="size-3" />
            Format
          </Button>
        </div>
        <Textarea
          className="h-[calc(54vh-2.25rem)] min-h-0 resize-none overflow-y-auto font-mono text-xs"
          onChange={(event) => onChange(event.target.value)}
          spellCheck={false}
          value={value}
        />
      </div>
      <div className="grid min-h-0 gap-2">
        <Label>Preview</Label>
        <pre className="h-[calc(54vh-2.25rem)] overflow-auto rounded-lg border bg-muted/30 p-3 font-mono text-xs leading-relaxed">
          {parsed.ok ? (
            <JsonValue value={parsed.value} />
          ) : (
            <span className="text-red-300">{parsed.error}</span>
          )}
        </pre>
      </div>
    </div>
  );
}

function JsonValue({ indent = 0, value }: { indent?: number; value: unknown }) {
  if (value === null || value === undefined) {
    return <span className="text-muted-foreground">null</span>;
  }

  if (typeof value === "string") {
    return <span className="text-emerald-300">{JSON.stringify(value)}</span>;
  }

  if (typeof value === "number") {
    return <span className="text-amber-300">{String(value)}</span>;
  }

  if (typeof value === "boolean") {
    return <span className="text-sky-300">{String(value)}</span>;
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
            {jsonIndent(indent + 1)}
            <JsonValue indent={indent + 1} value={item} />
            {index < value.length - 1 && <span>,</span>}
            {"\n"}
          </Fragment>
        ))}
        {jsonIndent(indent)}
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
            {jsonIndent(indent + 1)}
            <span className="text-violet-300">{JSON.stringify(key)}</span>
            <span>: </span>
            <JsonValue indent={indent + 1} value={item} />
            {index < entries.length - 1 && <span>,</span>}
            {"\n"}
          </Fragment>
        ))}
        {jsonIndent(indent)}
        <span>{"}"}</span>
      </>
    );
  }

  return <span>{JSON.stringify(value)}</span>;
}

function jsonIndent(level: number) {
  return "  ".repeat(level);
}

function MetadataItem({ label, value }: { label: string; value: string }) {
  return (
    <div className="min-w-0 rounded-md border bg-muted/20 p-3">
      <div className="text-muted-foreground text-xs">{label}</div>
      <div className="mt-1 break-words font-mono text-sm">{value}</div>
    </div>
  );
}

function formatTypedValue(value?: WorkTypedValue | null) {
  return value ? `${value.type}:${value.value}` : "-";
}

function StackedSkeleton({ count }: { count: number }) {
  return (
    <div className="space-y-3">
      {Array.from({ length: count }).map((_, index) => (
        <Skeleton className="h-10 w-full" key={index} />
      ))}
    </div>
  );
}

function normalizeScopeText(value: unknown) {
  return typeof value === "string" ? value.trim() : "";
}

function splitCategoryPath(category?: string | null) {
  return (category?.trim() || "General")
    .split(":")
    .map((segment) => segment.trim())
    .filter(Boolean);
}

function splitCatalogPath(path: unknown) {
  const value = normalizeScopeText(path);
  return value
    ? value
        .split(":")
        .map((segment) => segment.trim())
        .filter(Boolean)
    : [];
}

function startsWithCategoryPath(categorySegments: string[], pathSegments: string[]) {
  return pathSegments.every((segment, index) =>
    segment.localeCompare(categorySegments[index] ?? "", undefined, {
      sensitivity: "accent",
    }) === 0
  );
}

function definitionMatchesCatalogScope(
  definition: WorkDefinition,
  scope: OverviewScope | null
) {
  if (!scope) {
    return true;
  }

  if (scope.definitionName) {
    return definition.name === scope.definitionName;
  }

  const scopeSegments = splitCatalogPath(scope.category ?? "");
  if (scopeSegments.length === 0) {
    return true;
  }

  const categorySegments = splitCategoryPath(definition.category);
  return scope.includeSubcategories === false
    ? categorySegments.length === scopeSegments.length &&
        startsWithCategoryPath(categorySegments, scopeSegments)
    : startsWithCategoryPath(categorySegments, scopeSegments);
}

function parseSchemaJsonValue(json?: string | null) {
  if (!json?.trim()) {
    return null;
  }

  try {
    return JSON.parse(json) as unknown;
  } catch {
    return json;
  }
}

function parseJsonText(value: string):
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

function parseQueueJson(value: string) {
  if (!value.trim()) {
    return undefined;
  }

  try {
    return JSON.parse(value);
  } catch {
    throw new Error("Input must be valid JSON.");
  }
}

function parseOptionalObjectJson<T>(value: string, label: string): T | undefined {
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

function createEffectiveConfigurationOptions(
  definition: WorkDefinition | null
): WorkerOptions {
  return {
    profilingEnabled: definition?.defaultOptions?.profilingEnabled ?? false,
    configuration: stripInvocationConfiguration(cloneConfiguration(
      definition?.configuration ?? defaultWorkConfiguration
    )),
  };
}

function createDefaultQueueRequest(definition: WorkDefinition | null): QueueWorkRequest {
  return {
    completion: "ReturnAfterAccepted",
    options: createEffectiveConfigurationOptions(definition),
  };
}

function sanitizeQueueWorkRequest(request: QueueWorkRequest): QueueWorkRequest {
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

function cloneConfiguration(configuration: WorkConfiguration): WorkConfiguration {
  return JSON.parse(JSON.stringify(configuration)) as WorkConfiguration;
}

function stripInvocationConfiguration(configuration: WorkConfiguration): WorkConfiguration {
  const queueConfiguration = { ...configuration } as WorkConfiguration & {
    invocation?: unknown;
  };
  delete queueConfiguration.invocation;

  return queueConfiguration;
}

function isPlainObject(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}

const defaultWorkConfiguration: WorkConfiguration = {
  start: {
    policy: "StartAndReturnAfterAccepted",
  },
  idempotency: {
    isEnabled: false,
    storage: "Local",
    conflictPolicy: "RejectDuplicates",
  },
  recurrence: {
    isEnabled: false,
    interval: "00:00:00",
    continueAfterFailure: true,
    circuitBreakerFailureThreshold: 3,
    maximumSuccessfulIterations: 25,
    maximumFailedIterations: 5,
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
  concurrency: {
    isEnabled: false,
    maximumCapacity: 0,
    scope: "PerDefinition",
    blockingMode: "WhileExecutingPausedOrFailed",
    limitReachedBehavior: "Ignore",
    overrideBehavior: "Flexible",
    storage: "Local",
  },
  queueDurability: {
    isEnabled: false,
    completeDurably: false,
  },
};

function useWorkableResource<T>(
  connection: WorkableConnection,
  path: string | null,
  refreshToken: number
): Loadable<T> {
  const [state, setState] = useState<Loadable<T>>({ loading: !!path });
  const apiUrl = connection.apiUrl;
  const systemName = connection.systemName;

  useEffect(() => {
    if (!path) {
      queueMicrotask(() => setState({ loading: false }));
      return;
    }

    let canceled = false;
    queueMicrotask(() => {
      if (!canceled) {
        setState((current) => ({
          ...current,
          error: undefined,
          loading: current.data === undefined,
          refreshing: current.data !== undefined,
        }));
      }
    });

    const requestConnection = { apiUrl, systemName };
    workableFetch<T>(requestConnection, path)
      .then((data) => {
        if (!canceled) {
          setState({ data, loading: false, refreshing: false });
        }
      })
      .catch((error) => {
        if (!canceled) {
          const detail = error instanceof Error ? error.message : "Request failed.";
          setState((current) =>
            current.error === detail && !current.loading && !current.refreshing
              ? current
              : {
                  data: current.data,
                  error: detail,
                  loading: false,
                  refreshing: false,
                }
          );
        }
      });

    return () => {
      canceled = true;
    };
  }, [apiUrl, systemName, path, refreshToken]);

  return state;
}
