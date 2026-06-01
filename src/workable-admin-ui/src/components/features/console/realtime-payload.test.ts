import assert from "node:assert/strict";
import test from "node:test";
import {
  createRealtimePayloadConnectionId,
  createRealtimePayloadMessage,
  getRealtimePayloadComponentData,
} from "@/components/features/console/realtime-payload";

test("realtime payload messages capture component metadata, connection labels, byte size, and search text", () => {
  const payloadJson = JSON.stringify({ components: { workers: { status: "OK", shape: "detailed" } } });
  const message = createRealtimePayloadMessage(
    {
      components: {
        workers: {
          data: { count: 2 },
          shape: "detailed",
          status: "OK",
        },
      },
    },
    payloadJson,
    "message-1",
    "Overview",
    "Workers",
    {
      apiUrl: "https://workable.test",
      systemName: "Ops",
    }
  );

  assert.equal(message.bytes, new TextEncoder().encode(payloadJson).length);
  assert.equal(message.connectionId, "https://workable.test::Ops::Workers");
  assert.equal(message.connectionLabel, "Workers @ Ops");
  assert.deepEqual(message.components, [{ id: "workers", shape: "detailed", status: "OK" }]);
  assert.equal(message.id, "message-1");
  assert.equal(message.subscription, "Workers");
  assert.equal(message.viewName, "Overview");
  assert.match(message.searchText, /overview/);
  assert.match(message.searchText, /workers/);
});

test("realtime payload connection ids and component data handle missing optional values", () => {
  assert.equal(createRealtimePayloadConnectionId(null, "Overview"), "::::Overview");

  const message = createRealtimePayloadMessage(
    { ok: true },
    "{\"ok\":true}",
    "message-2",
    "Overview",
    "Overview",
    null
  );
  assert.equal(message.connectionLabel, "Overview");
  assert.deepEqual(message.components, []);

  assert.deepEqual(
    getRealtimePayloadComponentData({
      components: {
        summary: {
          data: { active: 3 },
          shape: "compact",
          status: "OK",
        },
      },
    }),
    [{
      data: { active: 3 },
      id: "summary",
      shape: "compact",
      status: "OK",
    }]
  );
  assert.deepEqual(getRealtimePayloadComponentData(null), []);
});
