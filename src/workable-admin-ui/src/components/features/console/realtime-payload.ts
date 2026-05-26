"use client";

import type { WorkComponentResult, WorkComponentShape } from "@/lib/workable";

export type RealtimePayloadMessage = {
  bytes: number;
  components: Array<{ id: string; shape?: string; status?: string }>;
  id: string;
  receivedAt: number;
  subscription: string;
  value: unknown;
  viewName: string;
};

export function createRealtimePayloadMessage<T>(
  result: T,
  payloadJson: string,
  id: string,
  viewName: string,
  subscription: string
): RealtimePayloadMessage {
  const maybeComponents =
    typeof result === "object" && result !== null && "components" in result
      ? (result as { components?: Record<string, WorkComponentResult> }).components
      : undefined;

  return {
    bytes: new TextEncoder().encode(payloadJson).length,
    components: Object.entries(maybeComponents ?? {}).map(([componentId, component]) => ({
      id: componentId,
      shape: component.shape,
      status: component.status,
    })),
    id,
    receivedAt: Date.now(),
    subscription,
    value: result,
    viewName,
  };
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
