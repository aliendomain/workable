import type { WorkSystemAccessSummary } from "@/lib/workable";

export type View =
  | "overview"
  | "definitions"
  | "definition"
  | "workers"
  | "iterations"
  | "worker";

export type ServerView = Exclude<View, "worker">;

export type OverviewScope = {
  category?: string;
  definitionName?: string;
  includeSubcategories?: boolean;
};

export type WorkableHostConnection = {
  id: string;
  name: string;
  apiUrl: string;
  realtimeEnabled: boolean;
  realtimeHubPath?: string | null;
  realtimeTransport?: string | null;
  systems: WorkableSystemConnection[];
};

export type WorkableSystemConnection = {
  id: string;
  hostId: string;
  name: string;
  systemName?: string;
  access?: WorkSystemAccessSummary;
  persistentCoordinationAvailable: boolean;
  state?: string | null;
};

export type Loadable<T> = {
  data?: T;
  error?: string;
  loading: boolean;
  refreshing?: boolean;
};

export type PendingDelete =
  | { kind: "host"; host: WorkableHostConnection }
  | { kind: "system"; host: WorkableHostConnection; system: WorkableSystemConnection };

export type PendingStopSystem = {
  system: WorkableSystemConnection;
};
