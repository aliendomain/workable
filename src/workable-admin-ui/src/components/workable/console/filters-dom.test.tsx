import assert from "node:assert/strict";
import test from "node:test";
import { QueryFilterPanelContent } from "@/components/workable/console/filters";
import { renderDom } from "@/test/dom";
import type { WorkableConnection, WorkerState } from "@/lib/workable";

const connection: WorkableConnection = {
  apiUrl: "https://workable.test",
  systemName: "Ops",
};

test("query filter panel content applies edited draft text filters and dismisses", async () => {
  let applied:
    | {
        categoryFilter: string;
        definitionFilter: string;
        facetValue: WorkerState[];
        keyKindFilter: "Any" | "Subject" | "ConcurrencyKey" | "Identifier";
        keyTypeFilter: string;
        keyValueFilter: string;
      }
    | null = null;
  let dismissCount = 0;

  const render = await renderDom(
    <QueryFilterPanelContent
      allFacetLabel="All states"
      catalogScope={null}
      connection={connection}
      facetLabel="Worker states"
      facetOptions={["Queued", "Running", "Failed"]}
      facetValue={[]}
      isOpen={false}
      keyKindFilter="Any"
      keyTypeFilter=""
      keyValueFilter=""
      onApply={(next) => {
        applied = next;
      }}
      onDismiss={() => {
        dismissCount += 1;
      }}
      refreshToken={0}
    />
  );

  try {
    const apply = render.getByText("Apply") as HTMLButtonElement;
    const keyTypeInput = render.container.querySelector("input[placeholder='Any key type']");
    const keyValueInput = render.container.querySelector("input[placeholder='Any key value']");

    assert.equal(apply.disabled, true);
    assert.ok(keyTypeInput instanceof render.dom.window.HTMLInputElement);
    assert.ok(keyValueInput instanceof render.dom.window.HTMLInputElement);

    await render.input(keyTypeInput, " Order ");
    await render.input(keyValueInput, " 100 ");
    assert.equal(apply.disabled, false);

    await render.click(apply);
    assert.deepEqual(applied, {
      categoryFilter: "",
      definitionFilter: "",
      facetValue: [],
      keyKindFilter: "Any",
      keyTypeFilter: " Order ",
      keyValueFilter: " 100 ",
    });
    assert.equal(dismissCount, 1);
  } finally {
    await render.restore();
  }
});

test("query filter panel clear resets every draft option and dismisses", async () => {
  let applied: unknown = null;
  let dismissCount = 0;
  const render = await renderDom(
    <QueryFilterPanelContent
      allFacetLabel="All states"
      catalogScope={{
        category: "Ops",
        definitionName: "ImportOrders",
        includeSubcategories: undefined,
      }}
      connection={connection}
      facetLabel="Worker states"
      facetOptions={["Queued", "Running", "Failed"]}
      facetValue={["Queued", "Failed"]}
      isOpen={false}
      keyKindFilter="Subject"
      keyTypeFilter="Order"
      keyValueFilter="100"
      onApply={(next) => {
        applied = next;
      }}
      onDismiss={() => {
        dismissCount += 1;
      }}
      refreshToken={0}
    />
  );

  try {
    await render.click(render.getByText("Clear"));
    assert.deepEqual(applied, {
      categoryFilter: "",
      definitionFilter: "",
      facetValue: [],
      keyKindFilter: "Any",
      keyTypeFilter: "",
      keyValueFilter: "",
    });
    assert.equal(dismissCount, 1);
  } finally {
    await render.restore();
  }
});
