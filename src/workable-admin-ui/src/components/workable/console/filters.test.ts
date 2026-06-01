import assert from "node:assert/strict";
import test from "node:test";
import {
  areStringArraysEqual,
  catalogFilterPanelFrameClassName,
  createQueryFilterDescriptions,
  filterPanelSectionClassName,
  formatFilterValues,
  formatQueryKeyKindLabel,
  getQueryFilterActiveCount,
} from "@/components/workable/console/filters";

test("query filter active count includes catalog, facets, key kind, type, and value options", () => {
  assert.equal(getQueryFilterActiveCount(null, [], "Any", "", ""), 0);
  assert.equal(
    getQueryFilterActiveCount(
      { category: "Ops", includeSubcategories: true },
      [],
      "Any",
      "",
      ""
    ),
    1
  );
  assert.equal(
    getQueryFilterActiveCount(
      { category: "Ops", definitionName: "Import", includeSubcategories: undefined },
      ["Failed", "Executing"],
      "Identifier",
      " order-id ",
      " 123 "
    ),
    5
  );
});

test("catalog filter panel can grow with the stretched filter card", () => {
  assert.match(filterPanelSectionClassName, /\bflex\b/);
  assert.match(filterPanelSectionClassName, /\bmin-h-0\b/);
  assert.match(catalogFilterPanelFrameClassName, /\bflex-1\b/);
  assert.ok(catalogFilterPanelFrameClassName.includes("min-h-[22rem]"));
});

test("query filter descriptions format catalog, facet, and key option branches", () => {
  assert.deepEqual(
    createQueryFilterDescriptions(
      { category: "Ops", definitionName: "ImportOrders", includeSubcategories: undefined },
      "Status",
      ["Queued", "Executing", "Failed", "Complete"],
      "ConcurrencyKey",
      " order ",
      " 42 "
    ),
    [
      "catalog: Ops / ImportOrders",
      "status: Queued, Executing, Failed, +1 more",
      "key kind: concurrency",
      "key type: order",
      "key value: 42",
    ]
  );
  assert.deepEqual(
    createQueryFilterDescriptions(null, "State", [], "Any", " ", ""),
    []
  );
});

test("query filter value helpers cover labels, truncation, and ordered equality", () => {
  assert.equal(formatQueryKeyKindLabel("Subject"), "subject");
  assert.equal(formatQueryKeyKindLabel("ConcurrencyKey"), "concurrency");
  assert.equal(formatQueryKeyKindLabel("Identifier"), "identity");
  assert.equal(formatQueryKeyKindLabel("Any"), "none");
  assert.equal(formatFilterValues(["A", "B", "C"]), "A, B, C");
  assert.equal(formatFilterValues(["A", "B", "C", "D", "E"]), "A, B, C, +2 more");
  assert.equal(areStringArraysEqual(["A", "B"], ["A", "B"]), true);
  assert.equal(areStringArraysEqual(["B", "A"], ["A", "B"]), false);
  assert.equal(areStringArraysEqual(["A"], ["A", "B"]), false);
});
