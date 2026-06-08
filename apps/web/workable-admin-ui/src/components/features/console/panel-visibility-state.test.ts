import assert from "node:assert/strict";
import test from "node:test";
import { updateHiddenPanelIds } from "./panel-visibility-state.ts";

test("panel visibility helper toggles hidden panel ids without mutating the input set", () => {
  const current = new Set(["logs"]);
  const visible = updateHiddenPanelIds(current, "logs", true);
  const hidden = updateHiddenPanelIds(current, "timeline", false);

  assert.deepEqual([...current], ["logs"]);
  assert.deepEqual([...visible], []);
  assert.deepEqual([...hidden], ["logs", "timeline"]);
});
