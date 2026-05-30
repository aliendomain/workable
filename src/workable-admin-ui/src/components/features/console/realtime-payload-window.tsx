"use client";

import { ChevronRight, Pause, Pin, PinOff, Play, Rows2, Rows3, Rows4, X } from "lucide-react";
import type { PointerEvent, ReactNode } from "react";
import { useDeferredValue, useEffect, useMemo, useRef, useState } from "react";
import { createPortal } from "react-dom";
import { Button } from "@/components/ui/button";
import {
  Popover,
  PopoverContent,
  PopoverTrigger,
} from "@/components/ui/popover";
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from "@/components/ui/select";
import type { ConsoleRealtimeStatsSnapshot } from "@/components/features/console/realtime";
import {
  getRealtimePayloadComponentData,
  type RealtimePayloadMessage,
} from "@/components/features/console/realtime-payload";

export type RealtimePayloadWindowProps = {
  activeTab: RealtimePayloadWindowTab;
  eventTabContent?: ReactNode;
  maxMessages: number;
  messages: RealtimePayloadMessage[];
  onClearMessages: () => void;
  onActiveTabChange: (tab: RealtimePayloadWindowTab) => void;
  onMaxMessagesChange: (maxMessages: number) => void;
  onOpenChange: (open: boolean) => void;
  open: boolean;
  realtimeStats: ConsoleRealtimeStatsSnapshot;
};

type PayloadInspectorView = "payload" | "componentData";
export type RealtimePayloadWindowTab = "events" | "payloads";
type RealtimePayloadWindowMode = "compact" | "standard" | "detailed";
type RealtimePayloadDockSide = "left" | "right";
const allSubscriptionsValue = "__all_subscriptions__";

export function RealtimePayloadWindow({
  activeTab,
  eventTabContent,
  maxMessages,
  messages,
  onClearMessages,
  onActiveTabChange,
  onMaxMessagesChange,
  onOpenChange,
  open,
  realtimeStats,
}: RealtimePayloadWindowProps) {
  const [position, setPosition] = useState({ x: 0, y: 0 });
  const [windowMode, setWindowMode] = useState<RealtimePayloadWindowMode>("detailed");
  const [streamCollapsed, setStreamCollapsed] = useState(false);
  const [inspectorCollapsed, setInspectorCollapsed] = useState(false);
  const [inspectorView, setInspectorView] = useState<PayloadInspectorView>("payload");
  const [searchText, setSearchText] = useState("");
  const [selectedSubscription, setSelectedSubscription] = useState(allSubscriptionsValue);
  const [selectedComponentId, setSelectedComponentId] = useState<string | null>(null);
  const [selectedMessageId, setSelectedMessageId] = useState<string | null>(null);
  const [tablePaused, setTablePaused] = useState(false);
  const [pausedMessages, setPausedMessages] = useState<RealtimePayloadMessage[] | null>(null);
  const [pinnedMessages, setPinnedMessages] = useState<Record<string, RealtimePayloadMessage>>({});
  const dragRef = useRef<{
    originX: number;
    originY: number;
    startX: number;
    startY: number;
  } | null>(null);
  const lastExpandedPositionRef = useRef<{ x: number; y: number } | null>(null);
  const compactDockSideRef = useRef<RealtimePayloadDockSide>("right");
  const panelRef = useRef<HTMLDivElement | null>(null);
  const wasOpenRef = useRef(false);
  const hasEventsTab = Boolean(eventTabContent);
  const activeWindowTab =
    activeTab === "events" && !hasEventsTab ? "payloads" : activeTab;
  const allMessages = useMemo(
    () => messages.slice(0, maxMessages),
    [maxMessages, messages]
  );
  const tableBaseMessages = tablePaused && pausedMessages ? pausedMessages : allMessages;
  const displayMessages = useMemo(
    () => mergePinnedPayloadMessages(tableBaseMessages, pinnedMessages),
    [pinnedMessages, tableBaseMessages]
  );
  const pinnedMessageList = useMemo(
    () => Object.values(pinnedMessages).sort((left, right) => right.receivedAt - left.receivedAt),
    [pinnedMessages]
  );
  const pinnedMessageIds = useMemo(
    () => new Set(Object.keys(pinnedMessages)),
    [pinnedMessages]
  );
  const newMessageCount = useMemo(() => {
    if (!tablePaused || !pausedMessages) {
      return 0;
    }

    const pausedIds = new Set(pausedMessages.map((message) => message.id));
    return allMessages.filter((message) => !pausedIds.has(message.id)).length;
  }, [allMessages, pausedMessages, tablePaused]);
  const deferredSearchText = useDeferredValue(searchText);
  const normalizedSearchText = deferredSearchText.trim().toLowerCase();
  const subscriptionOptions = useMemo(
    () =>
      Array.from(
        displayMessages.reduce((counts, message) => {
          counts.set(message.subscription, (counts.get(message.subscription) ?? 0) + 1);
          return counts;
        }, new Map<string, number>())
      )
        .sort(([left], [right]) => left.localeCompare(right))
        .map(([subscription, count]) => ({
          count,
          subscription,
        })),
    [displayMessages]
  );
  const effectiveSelectedSubscription =
    selectedSubscription !== allSubscriptionsValue &&
    subscriptionOptions.some((option) => option.subscription === selectedSubscription)
      ? selectedSubscription
      : allSubscriptionsValue;
  const filteredMessages = displayMessages.filter((message) => {
    if (
      effectiveSelectedSubscription !== allSubscriptionsValue &&
      message.subscription !== effectiveSelectedSubscription
    ) {
      return false;
    }

    return (
      !normalizedSearchText ||
      message.connectionLabel.toLowerCase().includes(normalizedSearchText) ||
      message.subscription.toLowerCase().includes(normalizedSearchText) ||
      message.viewName.toLowerCase().includes(normalizedSearchText) ||
      message.components.some((component) => component.id.toLowerCase().includes(normalizedSearchText)) ||
      getRealtimePayloadSearchText(message).includes(normalizedSearchText)
    );
  });
  const selectedMessage =
    filteredMessages.find((message) => message.id === selectedMessageId) ??
    (selectedMessageId ? pinnedMessages[selectedMessageId] : undefined) ??
    filteredMessages[0];
  const returnedComponents = getRealtimePayloadComponentData(selectedMessage?.value);
  const selectedComponent =
    returnedComponents.find((component) => component.id === selectedComponentId) ??
    returnedComponents[0];
  const isCompactWindow = windowMode === "compact";
  const isStandardWindow = windowMode === "standard";
  const windowMetrics = getRealtimePayloadWindowMetrics(windowMode);
  useEffect(() => {
    if (open && !wasOpenRef.current) {
      setPosition(
        isCompactWindow
          ? getDockedRealtimePayloadPosition(compactDockSideRef.current, windowMode)
          : getCenteredRealtimePayloadPosition(windowMode)
      );
    }
    wasOpenRef.current = open;
  }, [isCompactWindow, open, windowMode]);

  const changeWindowMode = (nextMode: RealtimePayloadWindowMode) => {
    if (nextMode === windowMode) {
      return;
    }

    if (nextMode === "compact") {
      const viewportWidth = window.visualViewport?.width ?? window.innerWidth;
      compactDockSideRef.current = position.x <= viewportWidth / 2 ? "left" : "right";
      lastExpandedPositionRef.current = position;
      setWindowMode(nextMode);
      setPosition(getDockedRealtimePayloadPosition(compactDockSideRef.current, nextMode));
      return;
    }

    const nextPosition = isCompactWindow
      ? lastExpandedPositionRef.current ?? getCenteredRealtimePayloadPosition(nextMode)
      : clampRealtimePayloadPosition(position, nextMode);
    setWindowMode(nextMode);
    setPosition(nextPosition);
  };

  const toggleTablePaused = () => {
    if (tablePaused) {
      setPausedMessages(null);
      setTablePaused(false);
      return;
    }

    setPausedMessages(allMessages);
    setTablePaused(true);
  };

  const showNewMessages = () => {
    setPausedMessages(allMessages);
  };

  const clearPayloadMessages = () => {
    setSelectedMessageId(null);
    setSelectedComponentId(null);
    setTablePaused(false);
    setPausedMessages(null);
    setPinnedMessages({});
    onClearMessages();
  };

  const selectMessage = (messageId: string) => {
    setSelectedMessageId(messageId);
    setSelectedComponentId(null);
  };

  const togglePinnedMessage = (message: RealtimePayloadMessage) => {
    setPinnedMessages((current) => {
      if (current[message.id]) {
        const next = { ...current };
        delete next[message.id];
        return next;
      }

      return {
        ...current,
        [message.id]: message,
      };
    });
  };

  const startDrag = (event: PointerEvent<HTMLDivElement>) => {
    if (isCompactWindow) {
      return;
    }

    event.currentTarget.setPointerCapture(event.pointerId);
    dragRef.current = {
      originX: position.x,
      originY: position.y,
      startX: event.clientX,
      startY: event.clientY,
    };
  };

  const drag = (event: PointerEvent<HTMLDivElement>) => {
    if (!dragRef.current) {
      return;
    }

    const nextX = dragRef.current.originX + event.clientX - dragRef.current.startX;
    const nextY = dragRef.current.originY + event.clientY - dragRef.current.startY;
    const viewportWidth = window.visualViewport?.width ?? window.innerWidth;
    const viewportHeight = window.visualViewport?.height ?? window.innerHeight;
    const panelWidth = panelRef.current?.offsetWidth ?? 0;
    const panelHeight = panelRef.current?.offsetHeight ?? 0;

    setPosition({
      x: clampFloatingWindowPosition(nextX, viewportWidth, panelWidth),
      y: clampFloatingWindowPosition(nextY, viewportHeight, panelHeight),
    });
  };

  const stopDrag = (event: PointerEvent<HTMLDivElement>) => {
    dragRef.current = null;
    event.currentTarget.releasePointerCapture(event.pointerId);
  };

  if (!open || typeof document === "undefined") {
    return null;
  }

  if (isCompactWindow) {
    return createPortal(
      <div
        className="fixed z-50 flex h-12 items-center gap-3 overflow-hidden rounded-lg border bg-popover px-3 text-sm text-popover-foreground shadow-2xl ring-1 ring-foreground/10"
        ref={panelRef}
        style={{
          left: position.x,
          top: position.y,
          width: windowMetrics.width,
        }}
      >
        <div className="min-w-0 flex-1">
          <div className="truncate font-medium text-sm">Realtime payloads</div>
          <div className="truncate text-muted-foreground text-xs">
            {allMessages.length.toLocaleString()} captured{tablePaused ? " - paused" : ""}
          </div>
        </div>
        <WindowModeButton currentMode={windowMode} onModeChange={changeWindowMode} />
        <Button
          aria-label="Close realtime payloads"
          className="cursor-pointer"
          onClick={() => onOpenChange(false)}
          size="icon-sm"
          variant="ghost"
        >
          <X className="size-4" />
        </Button>
      </div>,
      document.body
    );
  }

  return createPortal(
    <div
      className={`fixed z-50 grid grid-rows-[auto_auto_minmax(0,1fr)] overflow-hidden rounded-lg border bg-popover text-sm text-popover-foreground shadow-2xl ring-1 ring-foreground/10 ${
        isStandardWindow
          ? "min-h-[28rem] min-w-[42rem] resize"
          : "min-h-[34rem] min-w-[56rem] resize"
      }`}
      ref={panelRef}
      style={{
        height: windowMetrics.height,
        left: position.x,
        top: position.y,
        width: windowMetrics.width,
      }}
    >
      <div
        className="flex cursor-move items-center justify-between gap-3 border-b px-4 py-3 select-none"
        onPointerDown={startDrag}
        onPointerMove={drag}
        onPointerUp={stopDrag}
      >
        <div className="flex min-w-0 items-center gap-2">
          <div className="font-medium text-base">Realtime</div>
          <div className="flex items-center gap-0.5 rounded-md border bg-muted/30 p-0.5">
            <Button
              className={activeWindowTab === "payloads" ? "bg-accent text-accent-foreground" : ""}
              onClick={() => onActiveTabChange("payloads")}
              onPointerDown={(event) => event.stopPropagation()}
              size="sm"
              variant="ghost"
            >
              Payloads
            </Button>
            {hasEventsTab && (
              <Button
                className={activeWindowTab === "events" ? "bg-accent text-accent-foreground" : ""}
                onClick={() => onActiveTabChange("events")}
                onPointerDown={(event) => event.stopPropagation()}
                size="sm"
                variant="ghost"
              >
                Events
              </Button>
            )}
          </div>
        </div>
        <div className="flex shrink-0 items-center gap-1">
          <WindowModeButton currentMode={windowMode} onModeChange={changeWindowMode} />
          <Button
            aria-label="Close realtime payloads"
            className="cursor-pointer"
            onClick={() => onOpenChange(false)}
            onPointerDown={(event) => event.stopPropagation()}
            size="icon-sm"
            variant="ghost"
          >
            <X className="size-4" />
          </Button>
        </div>
      </div>
      {activeWindowTab === "events" && eventTabContent ? (
        <div className="min-h-0 overflow-hidden p-3">
          {eventTabContent}
        </div>
      ) : (
        <div
          className={`grid min-h-0 gap-3 overflow-hidden p-3 ${
            streamCollapsed
              ? "grid-cols-[2.75rem_minmax(0,1fr)]"
              : inspectorCollapsed
                ? "grid-cols-[minmax(0,1fr)_2.75rem]"
                : isStandardWindow
                  ? "xl:grid-cols-[minmax(0,1fr)_minmax(24rem,0.68fr)]"
                  : "xl:grid-cols-[minmax(0,1fr)_minmax(28rem,0.72fr)]"
          }`}
        >
          <PayloadMessageTable
            collapsed={streamCollapsed}
            canClear={allMessages.length > 0}
            maxMessages={maxMessages}
            messages={filteredMessages}
            newMessageCount={newMessageCount}
            onClearMessages={clearPayloadMessages}
            onCollapsedChange={setStreamCollapsed}
            onMaxMessagesChange={onMaxMessagesChange}
            onShowNewMessages={showNewMessages}
            onSearchTextChange={setSearchText}
            onSelectedSubscriptionChange={setSelectedSubscription}
            onTogglePin={togglePinnedMessage}
            onSelectMessage={selectMessage}
            onTablePausedChange={toggleTablePaused}
            pinnedMessages={pinnedMessageList}
            pinnedMessageIds={pinnedMessageIds}
            realtimeStats={realtimeStats}
            searchText={searchText}
            selectedSubscription={effectiveSelectedSubscription}
            selectedMessageId={selectedMessage?.id ?? null}
            subscriptionOptions={subscriptionOptions}
            tablePaused={tablePaused}
          />
          <div className="grid min-h-0 grid-rows-[auto_minmax(0,1fr)] overflow-hidden rounded-md border">
            <div className="flex items-center justify-between gap-2 border-b px-2 py-1.5">
              {!inspectorCollapsed && (
                <div className="font-medium text-muted-foreground text-xs">Inspector</div>
              )}
              <Button
                aria-label={inspectorCollapsed ? "Show inspector" : "Collapse inspector"}
                className="ml-auto"
                onClick={() => setInspectorCollapsed((current) => !current)}
                size="icon-sm"
                variant="ghost"
              >
                <ChevronRight
                  className={`size-4 transition-transform ${
                    inspectorCollapsed ? "rotate-180" : ""
                  }`}
                />
              </Button>
            </div>
            {inspectorCollapsed ? (
              <div className="flex min-h-0 items-start justify-center overflow-hidden py-2">
                <div className="font-mono text-muted-foreground text-xs [writing-mode:vertical-rl]">
                  {selectedMessage ? selectedMessage.connectionLabel : "No selection"}
                </div>
              </div>
            ) : (
              <PayloadInspector
                inspectorView={inspectorView}
                onComponentSelect={setSelectedComponentId}
                onInspectorViewChange={setInspectorView}
                onTogglePin={togglePinnedMessage}
                pinned={selectedMessage ? pinnedMessageIds.has(selectedMessage.id) : false}
                returnedComponents={returnedComponents}
                selectedComponent={selectedComponent}
                selectedMessage={selectedMessage}
              />
            )}
          </div>
        </div>
      )}
    </div>,
    document.body
  );
}

export function RealtimeStatsMenu({ realtimeStats }: { realtimeStats: ConsoleRealtimeStatsSnapshot }) {
  const hasConnections = realtimeStats.connections.length > 0;

  return (
    <Popover>
      <PopoverTrigger asChild>
        <Button className="h-7 px-2 text-xs" size="sm" variant={hasConnections ? "secondary" : "ghost"}>
          {realtimeStats.physicalConnectionCount.toLocaleString()} conn / {realtimeStats.onHandlerCount.toLocaleString()} on
        </Button>
      </PopoverTrigger>
      <PopoverContent align="end" className="z-[60] w-[28rem] gap-2 p-2">
        <div className="grid grid-cols-2 gap-2 text-xs">
          <RealtimeStatsMetric label="Connections" value={realtimeStats.physicalConnectionCount.toLocaleString()} />
          <RealtimeStatsMetric label=".on handlers" value={realtimeStats.onHandlerCount.toLocaleString()} />
          <RealtimeStatsMetric label="Lifecycle" value={realtimeStats.lifecycleHandlerCount.toLocaleString()} />
          <RealtimeStatsMetric label="Subscriptions" value={realtimeStats.activeSubscriptionCount.toLocaleString()} />
        </div>
        <div className="max-h-80 overflow-auto rounded-md border">
          {hasConnections ? (
            realtimeStats.connections.map((connection) => (
              <div className="grid gap-1 border-b px-2 py-2 text-xs last:border-b-0" key={connection.id}>
                <div className="flex items-center justify-between gap-2">
                  <span className="min-w-0 truncate font-medium">{connection.label}</span>
                  <span className="font-mono text-muted-foreground">{connection.connectionState}</span>
                </div>
                <div className="flex flex-wrap items-center gap-x-3 gap-y-1 text-muted-foreground">
                  <span>{connection.kind}</span>
                  <span>{connection.onHandlerCount} on</span>
                  <span>{connection.lifecycleHandlerCount} lifecycle</span>
                  <span>{connection.subscriptionCount} sub</span>
                </div>
                <div className="truncate font-mono text-[11px] text-muted-foreground/80">
                  {connection.connectionId ? `connId:${connection.connectionId}` : "connId:pending"}
                </div>
                <div className="truncate text-[11px] text-muted-foreground/80">
                  {connection.lastMessageAt
                    ? `last:${formatRealtimeStatsTimestamp(connection.lastMessageAt)}${connection.lastMessageLabel ? ` (${connection.lastMessageLabel})` : ""}`
                    : "last:none"}
                </div>
              </div>
            ))
          ) : (
            <div className="p-3 text-muted-foreground text-xs">
              No active SignalR connections.
            </div>
          )}
        </div>
      </PopoverContent>
    </Popover>
  );
}

function formatRealtimeStatsTimestamp(value: number) {
  return new Date(value).toLocaleTimeString();
}

function WindowModeButton({
  currentMode,
  onModeChange,
}: {
  currentMode: RealtimePayloadWindowMode;
  onModeChange: (mode: RealtimePayloadWindowMode) => void;
}) {
  const options: Array<{
    icon: typeof Rows2;
    label: string;
    mode: RealtimePayloadWindowMode;
  }> = [
    { icon: Rows2, label: "Compact realtime payloads", mode: "compact" },
    { icon: Rows3, label: "Standard realtime payloads", mode: "standard" },
    { icon: Rows4, label: "Detailed realtime payloads", mode: "detailed" },
  ];
  const currentIndex = options.findIndex((option) => option.mode === currentMode);
  const currentOption = options[currentIndex] ?? options[0];
  const nextOption = options[(currentIndex + 1) % options.length] ?? options[0];
  const Icon = currentOption.icon;

  return (
    <Button
      aria-label={`Switch realtime payloads to ${nextOption.label.toLowerCase()}`}
      className="cursor-pointer"
      onClick={() => onModeChange(nextOption.mode)}
      onPointerDown={(event) => event.stopPropagation()}
      size="icon-sm"
      variant="ghost"
    >
      <Icon className="size-4" />
    </Button>
  );
}

function PinnedPayloadMenu({
  messages,
  onSelectMessage,
  onTogglePin,
  selectedMessageId,
}: {
  messages: RealtimePayloadMessage[];
  onSelectMessage: (messageId: string) => void;
  onTogglePin: (message: RealtimePayloadMessage) => void;
  selectedMessageId: string | null;
}) {
  return (
    <Popover>
      <PopoverTrigger asChild>
        <Button
          aria-label="Open pinned payloads"
          className="h-8 px-2 text-xs"
          disabled={messages.length === 0}
          size="sm"
          variant={messages.length > 0 ? "secondary" : "ghost"}
        >
          <Pin className="size-3.5" />
          Pinned {messages.length.toLocaleString()}
        </Button>
      </PopoverTrigger>
      <PopoverContent align="end" className="z-[60] w-[26rem] gap-2 p-2">
        <div className="flex items-center justify-between gap-2 px-1">
          <div className="font-medium text-xs">Pinned payloads</div>
          <div className="font-mono text-muted-foreground text-xs">
            {messages.length.toLocaleString()}
          </div>
        </div>
        <div className="max-h-80 overflow-auto rounded-md border">
          {messages.map((message) => (
            <div
              className={`flex items-start gap-2 border-b p-2 text-xs last:border-b-0 ${
                message.id === selectedMessageId ? "bg-accent text-accent-foreground" : ""
              }`}
              key={message.id}
            >
              <button
                className="grid min-w-0 flex-1 gap-1 text-left"
                onClick={() => onSelectMessage(message.id)}
                type="button"
              >
                <span className="flex min-w-0 items-center gap-2">
                  <span className="font-mono">{formatPayloadTime(message.receivedAt)}</span>
                  <span className="min-w-0 truncate font-mono text-muted-foreground">
                    {message.connectionLabel}
                  </span>
                </span>
                <span className="min-w-0 truncate">
                  {message.viewName} - {message.subscription}
                </span>
                <span className="min-w-0 truncate text-muted-foreground">
                  {message.components.map((component) => component.id).join(", ") || "-"}
                </span>
              </button>
              <button
                aria-label="Unpin payload"
                className="flex size-6 shrink-0 items-center justify-center rounded text-muted-foreground hover:bg-background/80 hover:text-foreground"
                onClick={() => onTogglePin(message)}
                type="button"
              >
                <PinOff className="size-3.5" />
              </button>
            </div>
          ))}
        </div>
      </PopoverContent>
    </Popover>
  );
}

function PayloadMessageTable({
  collapsed,
  canClear,
  maxMessages,
  messages,
  newMessageCount,
  onClearMessages,
  onCollapsedChange,
  onMaxMessagesChange,
  onShowNewMessages,
  onSearchTextChange,
  onSelectedSubscriptionChange,
  onTogglePin,
  onSelectMessage,
  onTablePausedChange,
  pinnedMessages,
  pinnedMessageIds,
  realtimeStats,
  searchText,
  selectedSubscription,
  selectedMessageId,
  subscriptionOptions,
  tablePaused,
}: {
  collapsed: boolean;
  canClear: boolean;
  maxMessages: number;
  messages: RealtimePayloadMessage[];
  newMessageCount: number;
  onClearMessages: () => void;
  onCollapsedChange: (collapsed: boolean) => void;
  onMaxMessagesChange: (maxMessages: number) => void;
  onShowNewMessages: () => void;
  onSearchTextChange: (searchText: string) => void;
  onSelectedSubscriptionChange: (subscription: string) => void;
  onTogglePin: (message: RealtimePayloadMessage) => void;
  onSelectMessage: (messageId: string) => void;
  onTablePausedChange: () => void;
  pinnedMessages: RealtimePayloadMessage[];
  pinnedMessageIds: Set<string>;
  realtimeStats: ConsoleRealtimeStatsSnapshot;
  searchText: string;
  selectedSubscription: string;
  selectedMessageId: string | null;
  subscriptionOptions: Array<{ count: number; subscription: string }>;
  tablePaused: boolean;
}) {
  if (collapsed) {
    return (
      <div className="grid min-h-0 grid-rows-[auto_minmax(0,1fr)] overflow-hidden rounded-md border">
        <div className="border-b p-1">
          <Button
            aria-label="Show event stream"
            className="size-7"
            onClick={() => onCollapsedChange(false)}
            size="icon-sm"
            variant="ghost"
          >
            <ChevronRight className="size-4" />
          </Button>
        </div>
        <div className="flex min-h-0 items-start justify-center overflow-hidden py-2">
          <div className="font-mono text-muted-foreground text-xs [writing-mode:vertical-rl]">
            {messages.length.toLocaleString()} events
          </div>
        </div>
      </div>
    );
  }

  return (
    <div className="grid min-h-0 grid-rows-[auto_minmax(0,1fr)] overflow-hidden rounded-md border">
      <div className="grid gap-2 border-b bg-muted/30 px-2 py-2">
        <div className="flex min-w-0 flex-wrap items-center gap-1">
          <input
            className="h-7 min-w-48 flex-1 rounded-md border bg-background px-2 text-foreground text-xs"
            onChange={(event) => onSearchTextChange(event.currentTarget.value)}
            placeholder="Filter payloads"
            value={searchText}
          />
          <Select
            onValueChange={onSelectedSubscriptionChange}
            value={selectedSubscription}
          >
            <SelectTrigger className="h-7 min-w-40 px-2 text-xs" size="sm">
              <SelectValue placeholder="All subscriptions" />
            </SelectTrigger>
            <SelectContent align="end">
              <SelectItem value={allSubscriptionsValue}>All subscriptions</SelectItem>
              {subscriptionOptions.map((option) => (
                <SelectItem key={option.subscription} value={option.subscription}>
                  {option.subscription} ({option.count.toLocaleString()})
                </SelectItem>
              ))}
            </SelectContent>
          </Select>
          <PinnedPayloadMenu
            messages={pinnedMessages}
            onSelectMessage={onSelectMessage}
            onTogglePin={onTogglePin}
            selectedMessageId={selectedMessageId}
          />
          <RealtimeStatsMenu realtimeStats={realtimeStats} />
          <Button
            className="h-7 px-2 text-xs"
            onClick={onTablePausedChange}
            size="sm"
            variant={tablePaused ? "secondary" : "ghost"}
          >
            {tablePaused ? <Play className="size-3.5" /> : <Pause className="size-3.5" />}
            {tablePaused ? "Resume" : "Pause"}
          </Button>
          {tablePaused && (
            <Button
              className="h-7 px-2 text-xs"
              disabled={newMessageCount === 0}
              onClick={onShowNewMessages}
              size="sm"
              variant={newMessageCount > 0 ? "secondary" : "ghost"}
            >
              Show {newMessageCount.toLocaleString()} new
            </Button>
          )}
          <label className="flex h-7 items-center gap-1.5 rounded-md border bg-background px-2 text-xs">
            <span className="text-muted-foreground">Max</span>
            <input
              className="w-14 bg-transparent font-mono text-foreground outline-none"
              max={1000}
              min={1}
              onChange={(event) =>
                onMaxMessagesChange(normalizeRealtimeMaxMessages(event.currentTarget.value))
              }
              type="number"
              value={maxMessages}
            />
          </label>
          <Button
            className="h-7 px-2 text-xs"
            disabled={!canClear}
            onClick={onClearMessages}
            size="sm"
            variant="ghost"
          >
            Clear
          </Button>
          <Button
            aria-label="Collapse event stream"
            className="size-7"
            onClick={() => onCollapsedChange(true)}
            size="icon-sm"
            variant="ghost"
          >
            <ChevronRight className="size-4 rotate-180" />
          </Button>
        </div>
        <div className="grid grid-cols-[2rem_6.5rem_minmax(9rem,1fr)_8rem_minmax(10rem,1.2fr)_5rem] gap-3 font-medium text-muted-foreground text-xs">
          <span />
          <span>Time</span>
          <span>Connection</span>
          <span>View</span>
          <span>Components</span>
          <span className="text-right">Size</span>
        </div>
      </div>
      <div className="min-h-0 overflow-auto">
        {messages.length === 0 ? (
          <div className="p-4 text-muted-foreground text-sm">
            Waiting for realtime payloads.
          </div>
        ) : (
          messages.map((message) => (
            <div
              className={`grid w-full grid-cols-[2rem_6.5rem_minmax(9rem,1fr)_8rem_minmax(10rem,1.2fr)_5rem] gap-3 border-b px-3 py-2 text-left text-xs transition-colors last:border-b-0 ${
                message.id === selectedMessageId
                  ? "bg-accent text-accent-foreground"
                  : "hover:bg-accent/50"
              }`}
              key={message.id}
            >
              <button
                aria-label={pinnedMessageIds.has(message.id) ? "Unpin payload" : "Pin payload"}
                className="flex size-5 items-center justify-center rounded text-muted-foreground hover:bg-background/80 hover:text-foreground"
                onClick={(event) => {
                  event.stopPropagation();
                  onTogglePin(message);
                }}
                type="button"
              >
                {pinnedMessageIds.has(message.id)
                  ? <PinOff className="size-3.5" />
                  : <Pin className="size-3.5" />}
              </button>
              <button
                className="contents"
                onClick={() => onSelectMessage(message.id)}
                type="button"
              >
                <span className="font-mono">{formatPayloadTime(message.receivedAt)}</span>
                <span className="min-w-0 truncate font-mono">{message.connectionLabel}</span>
                <span className="min-w-0 truncate">{message.viewName}</span>
                <span className="min-w-0 truncate text-muted-foreground">
                  {message.components.map((component) => component.id).join(", ") || "-"}
                </span>
                <span className="font-mono text-right text-muted-foreground">
                  {formatByteCount(message.bytes)}
                </span>
              </button>
            </div>
          ))
        )}
      </div>
    </div>
  );
}

function PayloadInspector({
  inspectorView,
  onComponentSelect,
  onInspectorViewChange,
  onTogglePin,
  pinned,
  returnedComponents,
  selectedComponent,
  selectedMessage,
}: {
  inspectorView: PayloadInspectorView;
  onComponentSelect: (componentId: string) => void;
  onInspectorViewChange: (view: PayloadInspectorView) => void;
  onTogglePin: (message: RealtimePayloadMessage) => void;
  pinned: boolean;
  returnedComponents: ReturnType<typeof getRealtimePayloadComponentData>;
  selectedComponent: ReturnType<typeof getRealtimePayloadComponentData>[number] | undefined;
  selectedMessage: RealtimePayloadMessage | undefined;
}) {
  const [formatJson, setFormatJson] = useState(false);
  const parsedPayloadJson = useMemo(
    () => formatJson ? parseCapturedPayloadJson(selectedMessage?.payloadJson) : null,
    [formatJson, selectedMessage?.payloadJson]
  );
  const payloadText = selectedMessage
    ? selectedMessage.payloadJson || "Payload JSON was not captured for this message."
    : "Waiting for the first realtime payload.";
  const canFormatJson = inspectorView === "payload"
    ? !!selectedMessage?.payloadJson
    : !!selectedComponent;

  return (
    <div
      className={`grid min-h-0 overflow-hidden ${
        inspectorView === "componentData"
          ? "grid-rows-[auto_auto_minmax(0,1fr)]"
          : "grid-rows-[auto_minmax(0,1fr)]"
      }`}
    >
      <div className="grid gap-2 border-b px-3 py-2">
        <div className="grid gap-1 text-xs">
          <PayloadInlineMetric label="Id" value={selectedMessage?.id ?? "-"} />
          <PayloadInlineMetric label="Subscription" value={selectedMessage?.subscription ?? "-"} />
          <PayloadInlineMetric label="Components" value={selectedMessage?.components.map((component) => component.id).join(", ") || "-"} />
        </div>
        <div className="flex items-center justify-between gap-2">
          <div className="flex rounded-md border bg-muted/30 p-0.5">
            <Button
              className={`h-6 px-2 text-xs ${
                inspectorView === "payload" ? "bg-accent text-accent-foreground" : ""
              }`}
              onClick={() => onInspectorViewChange("payload")}
              size="sm"
              variant="ghost"
            >
              Payload
            </Button>
            <Button
              className={`h-6 px-2 text-xs ${
                inspectorView === "componentData" ? "bg-accent text-accent-foreground" : ""
              }`}
              disabled={returnedComponents.length === 0}
              onClick={() => onInspectorViewChange("componentData")}
              size="sm"
              variant="ghost"
            >
              Data
            </Button>
          </div>
          <div className="flex min-w-0 items-center gap-1">
            {inspectorView === "componentData" && (
              <div className="min-w-0 truncate font-mono text-muted-foreground text-xs">
                {selectedComponent
                  ? `${selectedComponent.id}:${selectedComponent.shape ?? "?"}:${selectedComponent.status ?? "?"}`
                  : "No component data"}
              </div>
            )}
            <Button
              className="h-7 px-2 text-xs"
              disabled={!canFormatJson}
              onClick={() => setFormatJson((current) => !current)}
              size="sm"
              variant={formatJson ? "secondary" : "ghost"}
            >
              {formatJson ? "Raw JSON" : "Format JSON"}
            </Button>
            {inspectorView === "payload" && (
              <Button
                className="h-7 px-2 text-xs"
                disabled={!selectedMessage}
                onClick={() => selectedMessage && onTogglePin(selectedMessage)}
                size="sm"
                variant={pinned ? "secondary" : "ghost"}
              >
                {pinned ? <PinOff className="size-3.5" /> : <Pin className="size-3.5" />}
                {pinned ? "Unpin" : "Pin"}
              </Button>
            )}
          </div>
        </div>
      </div>
      {inspectorView === "componentData" && (
        <div className="flex min-w-0 gap-1 overflow-x-auto border-b px-3 py-2">
          {returnedComponents.length === 0 ? (
            <span className="text-muted-foreground text-xs">
              No returned components.
            </span>
          ) : (
            returnedComponents.map((component) => (
              <button
                className={`shrink-0 rounded-md border px-2 py-1 font-mono text-xs transition-colors ${
                  component.id === selectedComponent?.id
                    ? "bg-accent text-accent-foreground"
                    : "text-muted-foreground hover:bg-accent/50"
                }`}
                key={component.id}
                onClick={() => onComponentSelect(component.id)}
                type="button"
              >
                {component.id}
              </button>
            ))
          )}
        </div>
      )}
      <pre className="min-h-0 overflow-auto whitespace-pre-wrap break-words bg-muted/30 p-3 font-mono text-xs leading-relaxed">
        {inspectorView === "componentData" ? (
          selectedComponent ? (
            formatJson ? (
              <JsonValue maxExpandedArrayItems={100} value={selectedComponent.data} />
            ) : (
              stringifyJsonRawForDisplay(selectedComponent.data)
            )
          ) : (
            "Select a returned component."
          )
        ) : selectedMessage ? (
          formatJson && parsedPayloadJson?.parsed ? (
            <JsonValue maxExpandedArrayItems={100} value={parsedPayloadJson.value} />
          ) : formatJson && parsedPayloadJson && !parsedPayloadJson.parsed ? (
            parsedPayloadJson.text
          ) : (
            payloadText
          )
        ) : (
          payloadText
        )}
      </pre>
    </div>
  );
}

export function JsonValue({
  collapseToComponentLevel = false,
  indent = 0,
  maxExpandedArrayItems,
  value,
}: {
  collapseToComponentLevel?: boolean;
  indent?: number;
  maxExpandedArrayItems?: number;
  value: unknown;
}) {
  if (value === null) {
    return <span className="text-muted-foreground">null</span>;
  }

  if (Array.isArray(value)) {
    return (
      <JsonArrayValue
        collapseToComponentLevel={collapseToComponentLevel}
        indent={indent}
        maxExpandedArrayItems={maxExpandedArrayItems}
        value={value}
      />
    );
  }

  if (typeof value === "object") {
    return (
      <JsonObjectValue
        collapseToComponentLevel={collapseToComponentLevel}
        indent={indent}
        maxExpandedArrayItems={maxExpandedArrayItems}
        value={value as Record<string, unknown>}
      />
    );
  }

  if (typeof value === "string") {
    return <span className="text-emerald-300">{JSON.stringify(value)}</span>;
  }

  if (typeof value === "number") {
    return <span className="text-amber-300">{value}</span>;
  }

  if (typeof value === "boolean") {
    return <span className="text-purple-300">{String(value)}</span>;
  }

  if (typeof value === "undefined") {
    return <span className="text-muted-foreground">undefined</span>;
  }

  return <span>{JSON.stringify(value)}</span>;
}

function JsonArrayValue({
  collapseToComponentLevel,
  indent,
  maxExpandedArrayItems,
  value,
}: {
  collapseToComponentLevel: boolean;
  indent: number;
  maxExpandedArrayItems?: number;
  value: unknown[];
}) {
  const [manualExpanded, setManualExpanded] = useState<boolean | null>(null);
  const isCollapsedToComponent = collapseToComponentLevel && indent >= 2;
  const isExpanded = manualExpanded ?? !isCollapsedToComponent;
  const expandedItemLimit = maxExpandedArrayItems && maxExpandedArrayItems > 0
    ? maxExpandedArrayItems
    : value.length;
  const visibleItems = value.length > expandedItemLimit
    ? value.slice(0, expandedItemLimit)
    : value;
  const hiddenItemCount = value.length - visibleItems.length;

  if (value.length === 0) {
    return <span>[]</span>;
  }

  if (!isExpanded) {
    return (
      <JsonCollapseButton
        closer="]"
        count={`${value.length} items`}
        expanded={false}
        opener="["
        onToggle={() => setManualExpanded(true)}
      />
    );
  }

  return (
    <>
      <JsonCollapseButton
        expanded={isExpanded}
        onToggle={() => setManualExpanded(false)}
        opener="["
      />
      {visibleItems.map((item, index) => (
        <span key={index}>
          {"\n"}
          {jsonIndent(indent + 1)}
          <JsonValue
            collapseToComponentLevel={collapseToComponentLevel}
            indent={indent + 1}
            maxExpandedArrayItems={maxExpandedArrayItems}
            value={item}
          />
          {index < value.length - 1 ? <span>,</span> : null}
        </span>
      ))}
      {hiddenItemCount > 0 && (
        <span>
          {"\n"}
          {jsonIndent(indent + 1)}
          <span className="text-muted-foreground">
            ... {hiddenItemCount.toLocaleString()} more item{hiddenItemCount === 1 ? "" : "s"}
          </span>
        </span>
      )}
      {"\n"}
      {jsonIndent(indent)}
      <span>]</span>
    </>
  );
}

function JsonObjectValue({
  collapseToComponentLevel,
  indent,
  maxExpandedArrayItems,
  value,
}: {
  collapseToComponentLevel: boolean;
  indent: number;
  maxExpandedArrayItems?: number;
  value: Record<string, unknown>;
}) {
  const [manualExpanded, setManualExpanded] = useState<boolean | null>(null);
  const entries = Object.entries(value);
  const isCollapsedToComponent = collapseToComponentLevel && indent >= 2;
  const isExpanded = manualExpanded ?? !isCollapsedToComponent;

  if (entries.length === 0) {
    return <span>{"{}"}</span>;
  }

  if (!isExpanded) {
    return (
      <JsonCollapseButton
        closer="}"
        count={`${entries.length} keys`}
        expanded={false}
        opener="{"
        onToggle={() => setManualExpanded(true)}
      />
    );
  }

  return (
    <>
      <JsonCollapseButton
        expanded={isExpanded}
        onToggle={() => setManualExpanded(false)}
        opener="{"
      />
      {entries.map(([key, item], index) => (
        <span key={key}>
          {"\n"}
          {jsonIndent(indent + 1)}
          <span className="text-sky-300">{JSON.stringify(key)}</span>
          <span>: </span>
          <JsonValue
            collapseToComponentLevel={collapseToComponentLevel}
            indent={indent + 1}
            maxExpandedArrayItems={maxExpandedArrayItems}
            value={item}
          />
          {index < entries.length - 1 ? <span>,</span> : null}
        </span>
      ))}
      {"\n"}
      {jsonIndent(indent)}
      <span>{"}"}</span>
    </>
  );
}

function JsonCollapseButton({
  closer,
  count,
  expanded,
  onToggle,
  opener,
}: {
  closer?: string;
  count?: string;
  expanded: boolean;
  onToggle: () => void;
  opener: string;
}) {
  return (
    <button
      className="inline-flex items-center gap-1 rounded px-0.5 text-left hover:bg-accent"
      onClick={onToggle}
      type="button"
    >
      <ChevronRight className={`size-3 transition-transform ${expanded ? "rotate-90" : ""}`} />
      <span>{opener}</span>
      {count ? <span className="text-muted-foreground">{count}</span> : null}
      {closer ? <span>{closer}</span> : null}
    </button>
  );
}

function PayloadInlineMetric({
  label,
  value,
  wide = false,
}: {
  label: string;
  value: string;
  wide?: boolean;
}) {
  return (
    <div className={`flex min-w-0 items-center gap-1 ${wide ? "max-w-[34rem]" : ""}`}>
      <span className="text-muted-foreground">{label}</span>
      <span className="min-w-0 truncate font-mono text-foreground">{value}</span>
    </div>
  );
}

function RealtimeStatsMetric({
  label,
  value,
}: {
  label: string;
  value: string;
}) {
  return (
    <div className="rounded-md border bg-muted/30 px-2 py-1.5">
      <div className="text-muted-foreground text-[11px]">{label}</div>
      <div className="font-mono text-xs">{value}</div>
    </div>
  );
}

function jsonIndent(level: number) {
  return <span>{Array.from({ length: level }).map(() => "  ").join("")}</span>;
}

function formatPayloadTime(value: number) {
  return new Intl.DateTimeFormat(undefined, {
    hour: "numeric",
    minute: "numeric",
    second: "numeric",
  }).format(new Date(value));
}

function formatByteCount(value: number) {
  if (value < 1024) {
    return `${value}b`;
  }

  return `${(value / 1024).toFixed(1)}kb`;
}

function getRealtimePayloadSearchText(message: RealtimePayloadMessage) {
  return message.searchText ?? stringifySearchValue(message.value).toLowerCase();
}

function stringifyJsonRawForDisplay(value: unknown) {
  try {
    return JSON.stringify(value) ?? "";
  } catch {
    return String(value);
  }
}

function parseCapturedPayloadJson(payloadJson: string | undefined):
  | { parsed: true; value: unknown }
  | { parsed: false; text: string } {
  if (!payloadJson) {
    return {
      parsed: false,
      text: "Payload JSON was not captured for this message.",
    };
  }

  try {
    return {
      parsed: true,
      value: JSON.parse(payloadJson),
    };
  } catch {
    return {
      parsed: false,
      text: payloadJson,
    };
  }
}

function stringifySearchValue(value: unknown) {
  try {
    return JSON.stringify(value) ?? "";
  } catch {
    return String(value);
  }
}

function mergePinnedPayloadMessages(
  messages: RealtimePayloadMessage[],
  pinnedMessages: Record<string, RealtimePayloadMessage>
) {
  const byId = new Map<string, RealtimePayloadMessage>();

  for (const message of messages) {
    byId.set(message.id, message);
  }

  for (const message of Object.values(pinnedMessages)) {
    byId.set(message.id, message);
  }

  return Array.from(byId.values()).sort((left, right) => right.receivedAt - left.receivedAt);
}

function getRealtimePayloadWindowMetrics(mode: RealtimePayloadWindowMode) {
  const viewportWidth = window.visualViewport?.width ?? window.innerWidth;
  const viewportHeight = window.visualViewport?.height ?? window.innerHeight;

  if (mode === "compact") {
    return {
      height: 48,
      width: Math.min(viewportWidth - 24, 360),
    };
  }

  if (mode === "standard") {
    return {
      height: Math.min(viewportHeight * 0.82, 544),
      width: Math.min(viewportWidth * 0.96, 928),
    };
  }

  return {
    height: Math.min(viewportHeight * 0.9, 928),
    width: Math.min(viewportWidth * 0.96, 1664),
  };
}

function getCenteredRealtimePayloadPosition(mode: RealtimePayloadWindowMode) {
  const viewportWidth = window.visualViewport?.width ?? window.innerWidth;
  const viewportHeight = window.visualViewport?.height ?? window.innerHeight;
  const panelMetrics = getRealtimePayloadWindowMetrics(mode);

  return {
    x: Math.max(8, Math.round((viewportWidth - panelMetrics.width) / 2)),
    y: Math.max(8, Math.round((viewportHeight - panelMetrics.height) / 2)),
  };
}

function getDockedRealtimePayloadPosition(
  side: RealtimePayloadDockSide,
  mode: RealtimePayloadWindowMode
) {
  const viewportWidth = window.visualViewport?.width ?? window.innerWidth;
  const viewportHeight = window.visualViewport?.height ?? window.innerHeight;
  const panelMetrics = getRealtimePayloadWindowMetrics(mode);
  const inset = 12;

  return {
    x: side === "left"
      ? inset
      : Math.max(inset, viewportWidth - panelMetrics.width - inset),
    y: Math.max(inset, viewportHeight - panelMetrics.height - inset),
  };
}

function clampRealtimePayloadPosition(
  position: { x: number; y: number },
  mode: RealtimePayloadWindowMode
) {
  const viewportWidth = window.visualViewport?.width ?? window.innerWidth;
  const viewportHeight = window.visualViewport?.height ?? window.innerHeight;
  const panelMetrics = getRealtimePayloadWindowMetrics(mode);

  return {
    x: clampFloatingWindowPosition(position.x, viewportWidth, panelMetrics.width),
    y: clampFloatingWindowPosition(position.y, viewportHeight, panelMetrics.height),
  };
}

function normalizeRealtimeMaxMessages(value: string) {
  const parsed = Number.parseInt(value, 10);
  if (!Number.isFinite(parsed)) {
    return 100;
  }

  return Math.min(1000, Math.max(1, parsed));
}

function clampFloatingWindowPosition(value: number, viewport: number, size: number) {
  const visibleGrip = 40;
  const min = size > 0 ? Math.min(8, visibleGrip - size) : 8;
  const max = Math.max(8, viewport - visibleGrip);

  return Math.min(Math.max(min, value), max);
}
