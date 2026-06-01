"use client";

import {
  ArrowDown,
  ArrowUp,
  Equal,
} from "lucide-react";
import { PanelShell } from "@/components/features/console/panel-shell";
import { Button } from "@/components/ui/button";
import { Skeleton } from "@/components/ui/skeleton";
import { Tabs, TabsContent, TabsList, TabsTrigger } from "@/components/ui/tabs";
import { Tooltip, TooltipContent, TooltipTrigger } from "@/components/ui/tooltip";
import type {
  WorkComponentShape,
  WorkSystemThroughput,
  WorkThroughputBucket,
  WorkThroughputLiveSummary,
} from "@/lib/workable";

export type ThroughputMode = "completion" | "execution";
const throughputSeriesIds = ["started", "completed", "failed", "canceled"] as const;
export type ThroughputSeriesId = typeof throughputSeriesIds[number];

export type ThroughputMetric = {
  description: string;
  icon?: typeof ArrowUp;
  iconClass?: string;
  id: string;
  label: string;
  pulseClass?: string;
  value: string;
  valueClass?: string;
  widthClass?: string;
};

export type ThroughputSeries = {
  color: string;
  gradientId: string;
  id: string;
  label: string;
  legendClass: string;
  strokeDasharray?: string;
  strokeWidth?: string;
  values: number[];
};

const chartViewBoxWidth = 1000;
const chartViewBoxHeight = 220;
const chartTopInset = 20;
const chartBottomInset = 30;
const chartValueRange = chartViewBoxHeight - chartTopInset - chartBottomInset;

export const throughputWindows = [
  { bucketSeconds: 1, label: "60s", seconds: 60 },
  { bucketSeconds: 5, label: "5m", seconds: 5 * 60 },
  { bucketSeconds: 15, label: "15m", seconds: 15 * 60 },
  { bucketSeconds: 60, label: "1h", seconds: 60 * 60 },
];
export const compactThroughputWindow = throughputWindows[0];

export function ThroughputChartPanel({
  hiddenSeries,
  loading,
  mode,
  onClose,
  onModeChange,
  onSeriesToggle,
  onShapeChange,
  onWindowChange,
  shape,
  supportedShapes,
  throughput,
  windowSeconds,
}: {
  hiddenSeries: ThroughputSeriesId[];
  loading: boolean;
  mode: ThroughputMode;
  onClose: () => void;
  onModeChange: (mode: ThroughputMode) => void;
  onSeriesToggle: (seriesId: ThroughputSeriesId) => void;
  onShapeChange: (shape: WorkComponentShape) => void;
  onWindowChange: (seconds: number) => void;
  shape: WorkComponentShape;
  supportedShapes: WorkComponentShape[];
  throughput?: WorkSystemThroughput;
  windowSeconds: number;
}) {
  const compact = shape === "compact";
  const chartLabel = mode === "execution" ? "Execution time" : "Throughput";
  const chartDescription = mode === "execution"
    ? "Execution timing for completed iterations, scoped to the current overview filter."
    : "Started, completed, failed, and canceled iteration rates, scoped to the current overview filter.";

  return (
    <PanelShell
      actions={compact ? (
        <CompactThroughputStrip
          loading={loading}
          throughput={throughput}
          windowSeconds={compactThroughputWindow.seconds}
        />
      ) : (
        <div className="flex flex-wrap items-center gap-2">
          <div className="flex rounded-lg bg-muted/40 p-0.5">
            {throughputWindows.map((window) => (
              <Button
                className="h-7 px-2 text-xs"
                key={window.seconds}
                onClick={() => onWindowChange(window.seconds)}
                size="sm"
                variant={windowSeconds === window.seconds ? "secondary" : "ghost"}
              >
                {window.label}
              </Button>
            ))}
          </div>
        </div>
      )}
      centerActions={compact}
      className={compact ? "w-full" : undefined}
      contentClassName={compact ? "hidden" : undefined}
      description={compact ? undefined : chartDescription}
      onClose={onClose}
      onViewStateChange={onShapeChange}
      supportedViewStates={supportedShapes}
      title={compact ? "Throughput & Execution" : chartLabel}
      viewState={shape}
    >
      {!compact && (
        <Tabs value={mode} onValueChange={(value) => onModeChange(value as ThroughputMode)}>
          <TabsList className="h-8">
            <TabsTrigger className="text-xs" value="completion">Throughput</TabsTrigger>
            <TabsTrigger className="text-xs" value="execution">Execution</TabsTrigger>
          </TabsList>
          <TabsContent className="mt-3" value={mode}>
            {loading ? (
              <Skeleton className="h-52 w-full" />
            ) : (
              <ThroughputAreaChart
                hiddenSeries={hiddenSeries}
                key={mode}
                mode={mode}
                onSeriesToggle={onSeriesToggle}
                throughput={throughput}
                windowSeconds={windowSeconds}
              />
            )}
          </TabsContent>
        </Tabs>
      )}
    </PanelShell>
  );
}

function CompactThroughputStrip({
  loading,
  throughput,
  windowSeconds,
}: {
  loading: boolean;
  throughput?: WorkSystemThroughput;
  windowSeconds: number;
}) {
  const throughputMetrics = createThroughputMetrics(
    "completion",
    throughput,
    windowSeconds
  ).filter((metric) =>
    metric.id !== "window-average"
  );
  const executionMetrics = createThroughputMetrics(
    "execution",
    throughput,
    windowSeconds
  ).filter((metric) =>
    [
      "execution-average",
      "execution-p95",
      "execution-p99",
      "execution-slowest",
    ].includes(metric.id)
  );
  const metrics = [...throughputMetrics, ...executionMetrics];

  if (loading) {
    return (
      <div className="flex h-8 max-w-full flex-wrap items-center justify-center gap-2 overflow-hidden">
        {Array.from({ length: 10 }).map((_, index) => (
          <Skeleton className="h-7 w-20 shrink-0 rounded-full" key={index} />
        ))}
      </div>
    );
  }

  return (
    <div className="flex min-h-8 max-w-full flex-wrap items-center justify-center gap-1.5 overflow-hidden">
      {metrics.map((metric) => (
        <ThroughputMetricPill key={metric.id} metric={metric} />
      ))}
    </div>
  );
}

function ThroughputAreaChart({
  hiddenSeries,
  mode,
  onSeriesToggle,
  showChart = true,
  showLegend = true,
  throughput,
  windowSeconds,
}: {
  hiddenSeries: ThroughputSeriesId[];
  mode: ThroughputMode;
  onSeriesToggle: (seriesId: ThroughputSeriesId) => void;
  showChart?: boolean;
  showLegend?: boolean;
  throughput?: WorkSystemThroughput;
  windowSeconds: number;
}) {
  const buckets = getThroughputBuckets(throughput);
  const bucketSeconds = throughput?.bucketSeconds ??
    throughputWindows.find((window) => window.seconds === windowSeconds)?.bucketSeconds ??
    1;
  const allSeries = createThroughputSeries(mode, buckets, bucketSeconds);
  const hiddenSeriesSet = new Set(hiddenSeries);
  const visibleSeries = mode === "completion"
    ? allSeries.filter((item) => !isThroughputSeriesId(item.id) || !hiddenSeriesSet.has(item.id))
    : allSeries;
  const series = visibleSeries.length > 0 ? visibleSeries : allSeries;
  const maxValue = getNiceChartMax(Math.max(0, ...series.flatMap((item) => item.values)), mode);
  const yTicks = createYAxisTicks(maxValue);
  const drawableSeries = series.filter((item) => !isZeroOnlySeries(item.values));
  const xTicks = createTimeAxisTicks(throughput, buckets);
  const metrics = createThroughputMetrics(
    mode,
    throughput,
    windowSeconds
  );
  const lineSeries = mode === "completion" && drawableSeries.length > 1
    ? [...drawableSeries.slice(1), drawableSeries[0]]
    : drawableSeries;

  return (
    <div className="space-y-3">
      <div className={`flex flex-wrap items-center gap-3 ${showLegend ? "justify-between" : "justify-end"}`}>
        {showLegend && (
          <div className="flex flex-wrap items-center gap-3">
            {allSeries.map((item) => {
              const seriesId = isThroughputSeriesId(item.id) ? item.id : null;
              return (
                <ThroughputLegendItem
                  hidden={seriesId ? hiddenSeriesSet.has(seriesId) : false}
                  item={item}
                  key={item.id}
                  onToggle={
                    mode === "completion" && seriesId
                      ? () => onSeriesToggle(seriesId)
                      : undefined
                  }
                />
              );
            })}
          </div>
        )}
        <div className="flex flex-wrap items-center justify-end gap-1.5">
          {metrics.map((metric) => {
            const seriesId = isThroughputSeriesId(metric.id) ? metric.id : null;
            return (
              <ThroughputMetricPill
                hidden={seriesId ? hiddenSeriesSet.has(seriesId) : false}
                key={metric.id}
                metric={metric}
                onClick={
                  mode === "completion" && seriesId
                    ? () => onSeriesToggle(seriesId)
                    : undefined
                }
              />
            );
          })}
        </div>
      </div>
      {showChart && (
        <div>
          <div className="relative grid h-56 grid-cols-[3.25rem_1fr] overflow-hidden rounded-lg border bg-background/40">
            <div className="relative border-r border-border/70 px-2 text-right font-mono text-[10px] text-muted-foreground">
              {yTicks.map((tick, index) => (
                <span
                  className={`absolute right-2 ${
                    index === 0
                      ? "translate-y-0"
                      : index === yTicks.length - 1
                        ? "-translate-y-full"
                        : "-translate-y-1/2"
                  }`}
                  key={tick}
                  style={{ top: `${(chartY(tick, maxValue) / chartViewBoxHeight) * 100}%` }}
                >
                  {formatThroughputAxisValue(mode, tick)}
                </span>
              ))}
            </div>
            <div className="relative min-w-0">
              <svg
                aria-label={mode === "execution" ? "Execution time chart" : "Throughput chart"}
                className="h-full w-full"
                preserveAspectRatio="none"
                role="img"
                viewBox={`0 0 ${chartViewBoxWidth} ${chartViewBoxHeight}`}
              >
                <defs>
                  {drawableSeries.map((item) => (
                    <linearGradient id={item.gradientId} key={item.gradientId} x1="0" x2="0" y1="0" y2="1">
                      <stop offset="5%" stopColor={item.color} stopOpacity="0.42" />
                      <stop offset="95%" stopColor={item.color} stopOpacity="0.04" />
                    </linearGradient>
                  ))}
                </defs>
                {yTicks.map((tick, index) => (
                  <line
                    className={index === yTicks.length - 1 ? "stroke-border/90" : "stroke-border"}
                    key={tick}
                    strokeDasharray={index === yTicks.length - 1 ? undefined : "4 8"}
                    strokeWidth="1"
                    x1="0"
                    x2={chartViewBoxWidth}
                    y1={chartY(tick, maxValue)}
                    y2={chartY(tick, maxValue)}
                  />
                ))}
                {drawableSeries.map((item) => (
                  <path d={createAreaPath(item.values, maxValue)} fill={`url(#${item.gradientId})`} key={`${item.label}-area`} />
                ))}
                {lineSeries.map((item) => (
                  <path
                    d={createLinePath(item.values, maxValue)}
                    fill="none"
                    key={`${item.label}-line`}
                    stroke={item.color}
                    strokeDasharray={item.strokeDasharray}
                    strokeLinecap="round"
                    strokeLinejoin="round"
                    strokeWidth={item.strokeWidth ?? "2.5"}
                    vectorEffect="non-scaling-stroke"
                  />
                ))}
              </svg>
            </div>
            {buckets.length === 0 && (
              <div className="absolute inset-0 grid place-items-center bg-background/70 text-muted-foreground text-sm">
                Waiting for throughput data.
              </div>
            )}
          </div>
          {xTicks.length > 0 && (
            <div className="ml-[3.25rem] mt-1 grid grid-cols-5 gap-2 px-1 font-mono text-[10px] text-foreground/75">
              {xTicks.map((tick, index) => (
                <span
                  className={
                    index === 0
                      ? "text-left"
                      : index === xTicks.length - 1
                        ? "text-right"
                        : "text-center"
                  }
                  key={`${tick.position}-${tick.label}`}
                >
                  {tick.label}
                </span>
              ))}
            </div>
          )}
        </div>
      )}
    </div>
  );
}

function ThroughputLegendItem({
  hidden,
  item,
  onToggle,
}: {
  hidden: boolean;
  item: ThroughputSeries;
  onToggle?: () => void;
}) {
  const content = (
    <>
      <span className={`size-2 rounded-full ${item.legendClass}`} />
      <span>{item.label}</span>
    </>
  );

  if (!onToggle) {
    return (
      <div className="flex items-center gap-1.5 text-muted-foreground text-xs">
        {content}
      </div>
    );
  }

  return (
    <button
      aria-pressed={!hidden}
      className={`flex cursor-pointer items-center gap-1.5 rounded-md border px-1.5 py-0.5 text-xs transition-colors focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring focus-visible:ring-offset-2 focus-visible:ring-offset-background ${
        hidden
          ? "border-transparent text-muted-foreground/45 hover:border-foreground/10 hover:bg-muted/20 hover:text-muted-foreground"
          : "border-foreground/10 bg-muted/20 text-foreground shadow-sm hover:border-primary/50 hover:bg-accent/50 hover:text-primary"
      }`}
      onClick={onToggle}
      type="button"
    >
      {content}
    </button>
  );
}

function ThroughputMetricPill({
  hidden = false,
  metric,
  onClick,
}: {
  hidden?: boolean;
  metric: ThroughputMetric;
  onClick?: () => void;
}) {
  const Icon = metric.icon;
  const content = (
    <>
      {metric.pulseClass && <span className={`size-2 rounded-full ${metric.pulseClass}`} />}
      {Icon && <Icon className={`size-3.5 ${metric.iconClass ?? "text-muted-foreground"}`} />}
      {metric.label && <span className="text-muted-foreground text-[11px]">{metric.label}</span>}
      <span className={`font-mono font-medium text-xs ${metric.valueClass ?? ""}`}>{metric.value}</span>
    </>
  );
  const className = `flex items-center justify-center gap-1.5 whitespace-nowrap rounded-full border px-2.5 py-1 shadow-sm transition-all ${
    onClick
      ? hidden
        ? "border-foreground/10 bg-background/40 opacity-50"
        : "border-primary/35 bg-accent/35 ring-1 ring-primary/20"
      : "border-foreground/10 bg-background/70"
  } ${metric.widthClass ?? "min-w-24"}`;

  return (
    <Tooltip delayDuration={500} disableHoverableContent>
      <TooltipTrigger asChild>
        {onClick ? (
          <button
            aria-pressed={!hidden}
            className={`${className} cursor-pointer hover:border-primary/70 hover:bg-accent/60 hover:ring-primary/35 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring focus-visible:ring-offset-2 focus-visible:ring-offset-background`}
            onClick={onClick}
            type="button"
          >
            {content}
          </button>
        ) : (
          <div className={className} tabIndex={0}>
            {content}
          </div>
        )}
      </TooltipTrigger>
      <TooltipContent className="max-w-64 whitespace-normal text-left" side="top" sideOffset={6}>
        {metric.description}
      </TooltipContent>
    </Tooltip>
  );
}

export function getThroughputBuckets(throughput?: WorkSystemThroughput) {
  if (!throughput) {
    return [];
  }

  const buckets = throughput.buckets ?? [];
  const bucketSeconds = throughput.bucketSeconds;
  const toTime = parseChartTimestamp(throughput.to);
  if (!bucketSeconds || toTime === null) {
    return buckets;
  }

  const normalizedBucketSeconds = Math.max(1, bucketSeconds);
  const windowSeconds = Math.max(normalizedBucketSeconds, throughput.windowSeconds);
  const bucketCount = Math.max(1, Math.ceil(windowSeconds / normalizedBucketSeconds));
  const toSecond = Math.floor(toTime / 1000);
  const latestBucketSecond = toSecond - normalizedBucketSeconds + 1;
  const firstBucketSecond = latestBucketSecond - (bucketCount - 1) * normalizedBucketSeconds;
  const bucketsBySecond = new Map<number, WorkThroughputBucket>();

  for (const bucket of buckets) {
    const bucketTime = parseChartTimestamp(bucket.at);
    if (bucketTime === null) {
      continue;
    }

    bucketsBySecond.set(Math.floor(bucketTime / 1000), bucket);
  }

  return Array.from({ length: bucketCount }, (_, index) => {
    const bucketSecond = firstBucketSecond + index * normalizedBucketSeconds;
    return bucketsBySecond.get(bucketSecond) ?? createEmptyThroughputBucket(bucketSecond);
  });
}

export function createEmptyThroughputBucket(second: number): WorkThroughputBucket {
  return {
    at: new Date(second * 1000).toISOString(),
    averageExecutionMilliseconds: 0,
    canceled: 0,
    completed: 0,
    failed: 0,
    started: 0,
  };
}

export function createThroughputSeries(
  mode: ThroughputMode,
  buckets: WorkThroughputBucket[],
  bucketSeconds: number
): ThroughputSeries[] {
  const normalizedBucketSeconds = Math.max(1, bucketSeconds);
  if (mode === "execution") {
    return [
      {
        color: "#a78bfa",
        gradientId: "execution-throughput",
        id: "execution-average",
        label: "Avg successful execution ms",
        legendClass: "bg-violet-400",
        values: buckets.map((bucket) => Math.round(bucket.averageExecutionMilliseconds)),
      },
    ];
  }

  return [
    {
      color: "#38bdf8",
      gradientId: "started-throughput",
      id: "started",
      label: "Started",
      legendClass: "bg-sky-400",
      strokeDasharray: "6 5",
      strokeWidth: "3",
      values: buckets.map((bucket) => bucket.started / normalizedBucketSeconds),
    },
    {
      color: "#34d399",
      gradientId: "completed-throughput",
      id: "completed",
      label: "Completed",
      legendClass: "bg-emerald-400",
      values: buckets.map((bucket) => bucket.completed / normalizedBucketSeconds),
    },
    {
      color: "#f87171",
      gradientId: "failed-throughput",
      id: "failed",
      label: "Failed",
      legendClass: "bg-red-400",
      values: buckets.map((bucket) => bucket.failed / normalizedBucketSeconds),
    },
    {
      color: "#fbbf24",
      gradientId: "canceled-throughput",
      id: "canceled",
      label: "Canceled",
      legendClass: "bg-amber-400",
      values: buckets.map((bucket) => bucket.canceled / normalizedBucketSeconds),
    },
  ];
}

export function createLinePath(values: number[], maxValue: number) {
  if (values.length === 0) {
    return "";
  }

  return values
    .map((value, index) => {
      const point = chartPoint(value, index, values.length, maxValue);
      return `${index === 0 ? "M" : "L"} ${point.x.toFixed(2)} ${point.y.toFixed(2)}`;
    })
    .join(" ");
}

export function createAreaPath(values: number[], maxValue: number) {
  const line = createLinePath(values, maxValue);
  if (!line) {
    return "";
  }

  const last = chartPoint(values.at(-1) ?? 0, values.length - 1, values.length, maxValue);
  const first = chartPoint(values[0] ?? 0, 0, values.length, maxValue);
  const baselineY = chartY(0, maxValue);
  return `${line} L ${last.x.toFixed(2)} ${baselineY.toFixed(2)} L ${first.x.toFixed(2)} ${baselineY.toFixed(2)} Z`;
}

export function chartPoint(value: number, index: number, count: number, maxValue: number) {
  const x = count <= 1 ? 0 : (index / (count - 1)) * chartViewBoxWidth;
  const y = chartY(value, maxValue);
  return { x, y };
}

export function chartY(value: number, maxValue: number) {
  return chartTopInset + (1 - value / maxValue) * chartValueRange;
}

export function isZeroOnlySeries(values: number[]) {
  return values.length > 0 && values.every((value) => value === 0);
}

export function createThroughputMetrics(
  mode: ThroughputMode,
  chartThroughput: WorkSystemThroughput | undefined,
  chartWindowSeconds: number
): ThroughputMetric[] {
  const totalDescription = `Total settled iterations in the selected ${formatThroughputWindowLabel(chartWindowSeconds)} chart window. This includes completed, failed, and canceled iterations.`;
  if (!chartThroughput) {
    if (mode === "execution") {
      return [
        {
          description: `Exact average execution time across completed iterations in the selected ${formatThroughputWindowLabel(chartWindowSeconds)} chart window. Failed and canceled iterations are excluded.`,
          id: "execution-average",
          label: "Avg",
          pulseClass: "bg-violet-400",
          value: "-",
          widthClass: "min-w-20",
        },
        {
          description: `Approximate p95 execution time across completed iterations in the selected ${formatThroughputWindowLabel(chartWindowSeconds)} chart window, interpolated from fast-work-optimized backend histogram buckets. Failed and canceled iterations are excluded.`,
          id: "execution-p95",
          label: "P95",
          value: "-",
          widthClass: "min-w-20",
        },
        {
          description: `Approximate p99 execution time across completed iterations in the selected ${formatThroughputWindowLabel(chartWindowSeconds)} chart window, interpolated from fast-work-optimized backend histogram buckets. Failed and canceled iterations are excluded.`,
          id: "execution-p99",
          label: "P99",
          value: "-",
          widthClass: "min-w-20",
        },
        {
          description: `Exact slowest execution time across completed iterations in the selected ${formatThroughputWindowLabel(chartWindowSeconds)} chart window. Failed and canceled iterations are excluded.`,
          id: "execution-slowest",
          label: "Slow",
          value: "-",
          widthClass: "min-w-20",
        },
        {
          description: `Exact count of completed iterations with execution timing in the selected ${formatThroughputWindowLabel(chartWindowSeconds)} chart window.`,
          id: "execution-count",
          label: "Count",
          value: "-",
          widthClass: "min-w-20",
        },
      ];
    }

    return [
      {
        description: "Started iterations per second over the last 60 seconds.",
        id: "started",
        label: "",
        pulseClass: "bg-sky-400",
        value: "-",
        valueClass: "text-sky-300",
        widthClass: "min-w-16",
      },
      {
        description: "Completed iterations per second over the last 60 seconds.",
        id: "completed",
        label: "",
        pulseClass: "bg-emerald-400",
        value: "-",
        valueClass: "text-emerald-300",
        widthClass: "min-w-16",
      },
      {
        description: "Failed iterations per second over the last 60 seconds.",
        id: "failed",
        label: "",
        pulseClass: "bg-red-400",
        value: "-",
        valueClass: "text-red-300",
        widthClass: "min-w-16",
      },
      {
        description: "Canceled iterations per second over the last 60 seconds.",
        id: "canceled",
        label: "",
        pulseClass: "bg-amber-400",
        value: "-",
        valueClass: "text-amber-300",
        widthClass: "min-w-16",
      },
      {
        description: "Live execution pressure over the last 60 seconds: started iterations per second minus completed, failed, and canceled iterations per second.",
        icon: Equal,
        iconClass: "text-muted-foreground",
        id: "execution-pressure",
        label: "",
        value: "-",
        valueClass: "text-muted-foreground",
        widthClass: "w-24 shrink-0",
      },
      {
        description: totalDescription,
        id: "total",
        label: "Total",
        value: "-",
        widthClass: "min-w-20",
      },
      {
        description: `Average execution time across completed iterations in the selected ${formatThroughputWindowLabel(chartWindowSeconds)} chart window. Failed and canceled iterations are excluded.`,
        id: "window-average",
        label: "Avg",
        value: "-",
        widthClass: "min-w-20",
      },
    ];
  }

  if (mode === "execution") {
    const executionSummary = chartThroughput.executionSummary;
    return [
      {
        description: `Exact average execution time across ${executionSummary.executionCount} completed ${pluralize("iteration", executionSummary.executionCount)} in the selected ${formatThroughputWindowLabel(chartWindowSeconds)} chart window. Failed and canceled iterations are excluded.`,
        id: "execution-average",
        label: "Avg",
        pulseClass: "bg-violet-400 shadow-[0_0_14px_rgba(167,139,250,0.75)]",
        value: formatMilliseconds(executionSummary.averageExecutionMilliseconds),
        widthClass: "min-w-20",
      },
      {
        description: `Approximate p95 execution time across completed iterations in the selected ${formatThroughputWindowLabel(chartWindowSeconds)} chart window, interpolated from fast-work-optimized backend histogram buckets. Failed and canceled iterations are excluded.`,
        id: "execution-p95",
        label: "P95",
        value: formatMilliseconds(executionSummary.p95ExecutionMilliseconds),
        widthClass: "min-w-20",
      },
      {
        description: `Approximate p99 execution time across completed iterations in the selected ${formatThroughputWindowLabel(chartWindowSeconds)} chart window, interpolated from fast-work-optimized backend histogram buckets. Failed and canceled iterations are excluded.`,
        id: "execution-p99",
        label: "P99",
        value: formatMilliseconds(executionSummary.p99ExecutionMilliseconds),
        widthClass: "min-w-20",
      },
      {
        description: `Exact slowest execution time across completed iterations in the selected ${formatThroughputWindowLabel(chartWindowSeconds)} chart window. Failed and canceled iterations are excluded.`,
        id: "execution-slowest",
        label: "Slow",
        value: formatMilliseconds(executionSummary.slowestExecutionMilliseconds),
        widthClass: "min-w-20",
      },
      {
        description: `Exact count of completed iterations with execution timing in the selected ${formatThroughputWindowLabel(chartWindowSeconds)} chart window.`,
        id: "execution-count",
        label: "Count",
        value: String(executionSummary.executionCount),
        widthClass: "min-w-20",
      },
    ];
  }

  const liveSummary = chartThroughput.liveSummary;
  const executionSummary = chartThroughput.executionSummary;
  const latestStartedRate = liveSummary.startedPerSecond;
  const latestCompletedRate = liveSummary.completedPerSecond;
  const latestFailedRate = liveSummary.failedPerSecond;
  const latestCanceledRate = liveSummary.canceledPerSecond;
  const settledTotal = chartThroughput.settledCount;
  const executionPressureMetric = createExecutionPressureMetric(liveSummary);
  return [
    {
      description: "Started iterations per second over the last 60 seconds.",
      id: "started",
      label: "",
      pulseClass: "bg-sky-400 shadow-[0_0_14px_rgba(56,189,248,0.75)]",
      value: `${formatRate(latestStartedRate)}/s`,
      valueClass: "text-sky-300",
      widthClass: "min-w-16",
    },
    {
      description: "Completed iterations per second over the last 60 seconds.",
      id: "completed",
      label: "",
      pulseClass: "bg-emerald-400 shadow-[0_0_14px_rgba(52,211,153,0.75)]",
      value: `${formatRate(latestCompletedRate)}/s`,
      valueClass: "text-emerald-300",
      widthClass: "min-w-16",
    },
    {
      description: "Failed iterations per second over the last 60 seconds.",
      id: "failed",
      label: "",
      pulseClass: "bg-red-400 shadow-[0_0_14px_rgba(248,113,113,0.7)]",
      value: `${formatRate(latestFailedRate)}/s`,
      valueClass: "text-red-300",
      widthClass: "min-w-16",
    },
    {
      description: "Canceled iterations per second over the last 60 seconds.",
      id: "canceled",
      label: "",
      pulseClass: "bg-amber-400 shadow-[0_0_14px_rgba(251,191,36,0.7)]",
      value: `${formatRate(latestCanceledRate)}/s`,
      valueClass: "text-amber-300",
      widthClass: "min-w-16",
    },
    executionPressureMetric,
    {
      description: totalDescription,
      id: "total",
      label: "Total",
      value: String(settledTotal),
      widthClass: "min-w-20",
    },
    {
      description: `Exact average execution time across ${executionSummary.executionCount} completed ${pluralize("iteration", executionSummary.executionCount)} in the selected ${formatThroughputWindowLabel(chartWindowSeconds)} chart window. Failed and canceled iterations are excluded.`,
      id: "window-average",
      label: "Avg",
      value: formatMilliseconds(executionSummary.averageExecutionMilliseconds),
      widthClass: "min-w-20",
    },
  ];
}

export function createExecutionPressureMetric(summary: WorkThroughputLiveSummary): ThroughputMetric {
  const deltaPerSecond = summary.inFlightDeltaPerSecond;
  if (deltaPerSecond > 0) {
    return {
      description: `Live execution pressure is increasing. Over the last ${summary.rateWindowSeconds} seconds, iterations started ${formatRate(deltaPerSecond)} per second faster than they settled.`,
      icon: ArrowUp,
      iconClass: "text-red-300",
      id: "execution-pressure",
      label: "",
      value: `+${formatRate(deltaPerSecond)}/s`,
      valueClass: "text-red-300",
      widthClass: "w-24 shrink-0",
    };
  }

  if (deltaPerSecond < 0) {
    const absoluteDeltaPerSecond = Math.abs(deltaPerSecond);
    return {
      description: `Live execution pressure is decreasing. Over the last ${summary.rateWindowSeconds} seconds, iterations settled ${formatRate(absoluteDeltaPerSecond)} per second faster than they started.`,
      icon: ArrowDown,
      iconClass: "text-emerald-300",
      id: "execution-pressure",
      label: "",
      value: `-${formatRate(absoluteDeltaPerSecond)}/s`,
      valueClass: "text-emerald-300",
      widthClass: "w-24 shrink-0",
    };
  }

  return {
    description: `Live execution pressure is balanced. Over the last ${summary.rateWindowSeconds} seconds, starts and settled outcomes matched.`,
    icon: Equal,
    iconClass: "text-muted-foreground",
    id: "execution-pressure",
    label: "",
    value: "0/s",
    valueClass: "text-muted-foreground",
    widthClass: "w-24 shrink-0",
  };
}

export function formatThroughputWindowLabel(seconds: number) {
  if (seconds === 60) {
    return "60-second";
  }
  if (seconds === 3600) {
    return "1-hour";
  }
  if (seconds % 3600 === 0) {
    return `${seconds / 3600}-hour`;
  }
  if (seconds % 60 === 0) {
    return `${seconds / 60}-minute`;
  }

  return `${seconds}-second`;
}

export function getNiceChartMax(value: number, mode: ThroughputMode) {
  if (value <= 0) {
    return mode === "execution" ? 100 : 1;
  }

  const exponent = Math.floor(Math.log10(value));
  const magnitude = 10 ** exponent;
  const normalized = value / magnitude;
  const nice = normalized <= 1
    ? 1
    : normalized <= 2
      ? 2
      : normalized <= 5
        ? 5
        : 10;
  return nice * magnitude;
}

export function createYAxisTicks(maxValue: number) {
  return [maxValue, maxValue * 2 / 3, maxValue / 3, 0];
}

export function formatThroughputAxisValue(mode: ThroughputMode, value: number) {
  if (mode === "execution") {
    return formatMilliseconds(value);
  }

  return `${formatRate(value)}/s`;
}

export function createTimeAxisTicks(
  throughput: WorkSystemThroughput | undefined,
  buckets: WorkThroughputBucket[]
) {
  if (!throughput || buckets.length === 0 || !throughput.bucketSeconds) {
    return [];
  }

  const firstBucketTime = parseChartTimestamp(buckets[0].at);
  const latestBucketTime = parseChartTimestamp(buckets.at(-1)?.at ?? throughput.to);
  const toTime = parseChartTimestamp(throughput.to);
  const latest = latestBucketTime ?? toTime;
  const from = firstBucketTime ?? (
    latest === null ? null : latest - Math.max(1, buckets.length - 1) * throughput.bucketSeconds * 1000
  );
  if (from === null || latest === null || !Number.isFinite(from) || !Number.isFinite(latest)) {
    return [];
  }

  const windowSeconds = Math.max(1, Math.round((latest - from) / 1000) + throughput.bucketSeconds);
  return [0, 0.25, 0.5, 0.75, 1].map((position) => {
    const timestamp = from + (latest - from) * position;
    return {
      label: formatChartTimeAxisLabel(timestamp, windowSeconds),
      position,
    };
  });
}

export function parseChartTimestamp(value: string | undefined) {
  if (!value) {
    return null;
  }

  const timestamp = Date.parse(value);
  return Number.isFinite(timestamp) ? timestamp : null;
}

export function formatChartTimeAxisLabel(timestamp: number, windowSeconds: number) {
  const options: Intl.DateTimeFormatOptions =
    windowSeconds >= 3600
      ? { hour: "numeric", minute: "2-digit" }
      : { hour: "numeric", minute: "2-digit", second: "2-digit" };
  return new Intl.DateTimeFormat(undefined, options).format(new Date(timestamp));
}

export function formatRate(value: number) {
  if (value >= 100) {
    return value.toFixed(0);
  }
  if (value >= 10) {
    return value.toFixed(1);
  }
  if (value >= 1) {
    return value.toFixed(2);
  }
  return value.toFixed(2);
}

export function formatMilliseconds(value: number) {
  if (value >= 1000) {
    return `${(value / 1000).toFixed(value >= 60_000 ? 0 : 1)}s`;
  }

  return `${Math.round(value)}ms`;
}

export function pluralize(word: string, count: number) {
  return count === 1 ? word : `${word}s`;
}

export function isThroughputSeriesId(value: unknown): value is ThroughputSeriesId {
  return typeof value === "string" && throughputSeriesIds.includes(value as ThroughputSeriesId);
}
