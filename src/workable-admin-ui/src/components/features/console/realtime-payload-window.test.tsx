import assert from "node:assert/strict";
import test from "node:test";
import {
  JsonValue,
  RealtimePayloadWindow,
  RealtimeStatsMenu,
  clampFloatingWindowPosition,
  clampRealtimePayloadPosition,
  formatByteCount,
  getCenteredRealtimePayloadPosition,
  getDockedRealtimePayloadPosition,
  getRealtimePayloadSearchText,
  getRealtimePayloadViewportSize,
  getRealtimePayloadWindowMetrics,
  mergePinnedPayloadMessages,
  normalizeRealtimeMaxMessages,
  parseCapturedPayloadJson,
  stringifyJsonRawForDisplay,
  stringifySearchValue,
} from "@/components/features/console/realtime-payload-window";
import {
  assertMarkupIncludes,
  renderMarkup,
} from "@/test/render";
import type { RealtimePayloadMessage } from "@/components/features/console/realtime-payload";

const stats = {
  activeConsumerCount: 0,
  activeSubscriptionCount: 2,
  connections: [],
  lifecycleHandlerCount: 3,
  onHandlerCount: 4,
  physicalConnectionCount: 1,
  signalrHandlerCount: 7,
};

function payloadMessage(overrides: Partial<RealtimePayloadMessage>): RealtimePayloadMessage {
  return {
    bytes: 2,
    components: [],
    connectionId: "overview",
    connectionLabel: "Overview",
    id: "payload-1",
    payloadJson: "{}",
    receivedAt: 100,
    searchText: "overview",
    subscription: "Overview",
    value: {},
    viewName: "Overview",
    ...overrides,
  };
}

test("closed realtime payload window renders nothing without a browser window", () => {
  const markup = renderMarkup(
    <RealtimePayloadWindow
      activeTab="payloads"
      maxMessages={100}
      messages={[]}
      onActiveTabChange={() => undefined}
      onClearMessages={() => undefined}
      onMaxMessagesChange={() => undefined}
      onOpenChange={() => undefined}
      open={false}
      realtimeStats={stats}
    />
  );

  assert.equal(markup, "");
});

test("realtime stats menu renders compact connection and handler counts", () => {
  const markup = renderMarkup(
    <RealtimeStatsMenu
      realtimeStats={{
        ...stats,
        connections: [{
          connectionId: "abc",
          connectionKey: "view::overview",
          connectionState: "connected",
          consumerCount: 1,
          enabled: true,
          hubUrl: "https://workable.test/hub",
          id: "conn-1",
          kind: "view",
          label: "Overview",
          lastMessageAt: undefined,
          lastMessageLabel: undefined,
          lifecycleHandlerCount: 1,
          onHandlerCount: 2,
          subscriptionCount: 1,
        }],
      }}
    />
  );

  assertMarkupIncludes(markup, "1 conn / 4 on");
});

test("json value renderer covers primitive, empty, expanded, truncated, and collapsed paths", () => {
  assertMarkupIncludes(renderMarkup(<JsonValue value={null} />), "null");
  assertMarkupIncludes(renderMarkup(<JsonValue value="hello" />), "&quot;hello&quot;");
  assertMarkupIncludes(renderMarkup(<JsonValue value={42} />), "42");
  assertMarkupIncludes(renderMarkup(<JsonValue value />), "true");
  assertMarkupIncludes(renderMarkup(<JsonValue value={undefined} />), "undefined");
  assertMarkupIncludes(renderMarkup(<JsonValue value={[]} />), "[]");
  assertMarkupIncludes(renderMarkup(<JsonValue value={{}} />), "{}");

  const expanded = renderMarkup(
    <JsonValue
      maxExpandedArrayItems={2}
      value={{
        components: {
          workers: [{ id: 1 }, { id: 2 }, { id: 3 }],
        },
      }}
    />
  );
  assertMarkupIncludes(expanded, "&quot;components&quot;");
  assertMarkupIncludes(expanded, "&quot;workers&quot;");
  assertMarkupIncludes(expanded, "... 1 more item");

  const collapsed = renderMarkup(
    <JsonValue
      collapseToComponentLevel
      indent={2}
      value={{
        data: { count: 3 },
        status: "OK",
      }}
    />
  );
  assertMarkupIncludes(collapsed, "{");
  assertMarkupIncludes(collapsed, "2 keys");
});

test("realtime payload text helpers cover bytes, parsing, stringification, and search fallback", () => {
  assert.equal(formatByteCount(512), "512b");
  assert.equal(formatByteCount(1536), "1.5kb");
  assert.deepEqual(parseCapturedPayloadJson(undefined), {
    parsed: false,
    text: "Payload JSON was not captured for this message.",
  });
  assert.deepEqual(parseCapturedPayloadJson("{\"ok\":true}"), {
    parsed: true,
    value: { ok: true },
  });
  assert.deepEqual(parseCapturedPayloadJson("{oops"), {
    parsed: false,
    text: "{oops",
  });
  assert.equal(stringifyJsonRawForDisplay({ ok: true }), "{\"ok\":true}");
  assert.equal(stringifySearchValue(["A", "B"]), "[\"A\",\"B\"]");

  const circular: { self?: unknown } = {};
  circular.self = circular;
  assert.equal(stringifyJsonRawForDisplay(circular), "[object Object]");
  assert.equal(stringifySearchValue(circular), "[object Object]");
  assert.equal(
    getRealtimePayloadSearchText(payloadMessage({ searchText: undefined as unknown as string, value: { Name: "Ops" } })),
    "{\"name\":\"ops\"}"
  );
  assert.equal(
    getRealtimePayloadSearchText(payloadMessage({ searchText: "cached search", value: { Name: "Ops" } })),
    "cached search"
  );
});

test("realtime payload collection and window helpers cover pin merging, metrics, docking, and clamps", () => {
  const merged = mergePinnedPayloadMessages(
    [
      payloadMessage({ id: "base-a", receivedAt: 100 }),
      payloadMessage({ id: "base-b", receivedAt: 90 }),
    ],
    {
      "base-b": payloadMessage({ id: "base-b", receivedAt: 140 }),
      "pin-c": payloadMessage({ id: "pin-c", receivedAt: 110 }),
    }
  );
  assert.deepEqual(
    merged.map((message) => `${message.id}:${message.receivedAt}`),
    ["base-b:140", "pin-c:110", "base-a:100"]
  );

  const viewport = { height: 800, width: 1200 };
  assert.deepEqual(getRealtimePayloadWindowMetrics("compact", viewport), {
    height: 48,
    width: 360,
  });
  assert.deepEqual(getRealtimePayloadWindowMetrics("standard", viewport), {
    height: 544,
    width: 928,
  });
  assert.deepEqual(getRealtimePayloadWindowMetrics("detailed", viewport), {
    height: 720,
    width: 1152,
  });
  assert.deepEqual(getCenteredRealtimePayloadPosition("standard", viewport), {
    x: 136,
    y: 128,
  });
  assert.deepEqual(getDockedRealtimePayloadPosition("left", "compact", viewport), {
    x: 12,
    y: 740,
  });
  assert.deepEqual(getDockedRealtimePayloadPosition("right", "compact", viewport), {
    x: 828,
    y: 740,
  });
  assert.deepEqual(clampRealtimePayloadPosition({ x: -900, y: 900 }, "standard", viewport), {
    x: -888,
    y: 760,
  });
  assert.equal(clampFloatingWindowPosition(2000, 1000, 400), 960);
  assert.equal(clampFloatingWindowPosition(-500, 1000, 400), -360);
  assert.equal(clampFloatingWindowPosition(0, 1000, 0), 8);
  assert.equal(normalizeRealtimeMaxMessages("abc"), 100);
  assert.equal(normalizeRealtimeMaxMessages("0"), 1);
  assert.equal(normalizeRealtimeMaxMessages("2500"), 1000);
  assert.equal(normalizeRealtimeMaxMessages("50"), 50);
  assert.deepEqual(getRealtimePayloadViewportSize(), {
    height: 768,
    width: 1024,
  });
});
