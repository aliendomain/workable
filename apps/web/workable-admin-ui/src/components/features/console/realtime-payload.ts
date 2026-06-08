"use client";

import type { WorkComponentResult, WorkComponentShape } from "@/lib/workable";

export type RealtimePayloadMessage = {
  bytes: number;
  components: Array<{ id: string; shape?: string; status?: string }>;
  connectionId: string;
  connectionLabel: string;
  id: string;
  payloadJson: string;
  receivedAt: number;
  searchText: string;
  subscription: string;
  value: unknown;
  viewName: string;
};

type RealtimePayloadConnectionKey = {
  apiUrl?: string;
  systemName?: string | null;
};

export function createRealtimePayloadMessage<T>(
  result: T,
  payloadJson: string,
  id: string,
  viewName: string,
  subscription: string,
  connection?: RealtimePayloadConnectionKey | null
): RealtimePayloadMessage {
  const maybeComponents =
    typeof result === "object" && result !== null && "components" in result
      ? (result as { components?: Record<string, WorkComponentResult> }).components
      : undefined;
  const connectionId = createRealtimePayloadConnectionId(connection, subscription);

  return {
    bytes: new TextEncoder().encode(payloadJson).length,
    components: Object.entries(maybeComponents ?? {}).map(([componentId, component]) => ({
      id: componentId,
      shape: component.shape,
      status: component.status,
    })),
    connectionId,
    connectionLabel: createRealtimePayloadConnectionLabel(viewName, subscription, connection),
    id,
    payloadJson,
    receivedAt: Date.now(),
    searchText: createRealtimePayloadSearchText(
      payloadJson,
      viewName,
      subscription,
      Object.keys(maybeComponents ?? {})
    ),
    subscription,
    value: result,
    viewName,
  };
}

export function createRealtimePayloadConnectionId(
  connection: RealtimePayloadConnectionKey | null | undefined,
  subscription: string
) {
  return [
    connection?.apiUrl ?? "",
    connection?.systemName ?? "",
    subscription,
  ].join("::");
}

function createRealtimePayloadConnectionLabel(
  viewName: string,
  subscription: string,
  connection: RealtimePayloadConnectionKey | null | undefined
) {
  const name = subscription === viewName ? viewName : subscription;
  return connection?.systemName ? `${name} @ ${connection.systemName}` : name;
}

function createRealtimePayloadSearchText(
  payloadJson: string,
  viewName: string,
  subscription: string,
  componentIds: string[]
) {
  return [
    viewName,
    subscription,
    ...componentIds,
    payloadJson,
  ].join("\n").toLowerCase();
}

export function getRealtimePayloadComponentData(value: unknown) {
  const components =
    typeof value === "object" && value !== null && "components" in value
      ? (value as { components?: Record<string, WorkComponentResult> }).components
      : undefined;

  return Object.entries(components ?? {}).map(([id, component]) => ({
    data: component.data,
    id,
    shape: component.shape as WorkComponentShape | undefined,
    status: component.status,
  }));
}
