import type { WorkComponentShape } from "@/lib/workable";
import type { PanelVisibilityOption } from "@/components/features/console/panel-visibility-settings";

export const overviewPanelIds = [
  "workers",
  "failedWorkers",
  "throughput",
  "iterations",
  "failedIterations",
  "completedIterations",
] as const;

export type OverviewPanelId = (typeof overviewPanelIds)[number];
export type OverviewPanelShapeMap = Record<OverviewPanelId, WorkComponentShape>;

export const overviewPanelShapeCapabilities: Record<OverviewPanelId, {
  defaultShape: WorkComponentShape;
  supportedShapes: WorkComponentShape[];
}> = {
  workers: {
    defaultShape: "standard",
    supportedShapes: ["compact", "standard"],
  },
  failedWorkers: {
    defaultShape: "detailed",
    supportedShapes: ["standard", "detailed"],
  },
  throughput: {
    defaultShape: "standard",
    supportedShapes: ["compact", "standard"],
  },
  iterations: {
    defaultShape: "standard",
    supportedShapes: ["compact", "standard"],
  },
  failedIterations: {
    defaultShape: "standard",
    supportedShapes: ["standard", "detailed"],
  },
  completedIterations: {
    defaultShape: "standard",
    supportedShapes: ["standard", "detailed"],
  },
};

export const overviewPanelOptions: PanelVisibilityOption<OverviewPanelId>[] = [
  {
    id: "workers",
    label: "Workers",
    description: "Worker counts by state, definition coverage, and oldest queued age.",
  },
  {
    id: "failedWorkers",
    label: "Failed workers",
    description: "Recent failed workers with quick access into the worker console.",
  },
  {
    id: "throughput",
    label: "Throughput",
    description: "Live execution and completion throughput over recent buckets.",
  },
  {
    id: "iterations",
    label: "Iterations",
    description: "Iteration rollups and the most common iteration key types.",
  },
  {
    id: "failedIterations",
    label: "Failed iterations",
    description: "Latest failed iterations with status, key, and timing context.",
  },
  {
    id: "completedIterations",
    label: "Completed iterations",
    description: "Latest completed iterations with timing and key details.",
  },
];
