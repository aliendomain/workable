import type {
  WorkComponentQueryResult,
  WorkComponentRequest,
  WorkComponentResult,
  WorkComponentShape,
} from "@/lib/workable";

export function createWorkComponentRequest(
  id: string,
  type: string = id,
  shape?: WorkComponentShape,
  options?: unknown
): WorkComponentRequest {
  return {
    id,
    type,
    ...(shape ? { shape } : {}),
    ...(options !== undefined ? { options } : {}),
  };
}

export function getWorkComponentData<T>(
  result: WorkComponentQueryResult | undefined,
  id: string
): T | undefined {
  const component = result?.components?.[id] as WorkComponentResult<T> | undefined;
  return component?.status?.toLowerCase() === "ok" ? component.data : undefined;
}

export function getWorkComponentErrors(result: WorkComponentQueryResult | undefined) {
  return Object.entries(result?.components ?? {})
    .filter(([, component]) => component.status?.toLowerCase() !== "ok")
    .map(([id, component]) => component.error ?? `${id} failed to load.`);
}
