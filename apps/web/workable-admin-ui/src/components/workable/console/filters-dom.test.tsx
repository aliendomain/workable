import assert from "node:assert/strict";
import test from "node:test";
import {
  OverviewCatalogFilter,
  QueryFilterPanelContent,
} from "@/components/workable/console/filters";
import { DefinitionCatalogBrowser } from "@/components/workable/console/catalog-browser";
import { clearDefinitionCatalogLevelCache } from "@/components/workable/console/catalog-browser-data";
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

test("query filter panel content applies facet and key kind draft changes", async () => {
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
      refreshToken={0}
    />
  );

  try {
    const apply = render.getByText("Apply") as HTMLButtonElement;
    assert.equal(apply.disabled, true);

    await render.click(render.getByText("Running"));
    await render.click(render.getByRole("combobox"));
    await render.click(render.getByRole("option", { name: "Concurrency" }));

    assert.equal(apply.disabled, false);
    await render.click(apply);

    assert.deepEqual(applied, {
      categoryFilter: "",
      definitionFilter: "",
      facetValue: ["Running"],
      keyKindFilter: "ConcurrencyKey",
      keyTypeFilter: "",
      keyValueFilter: "",
    });
  } finally {
    await render.restore();
  }
});

test("overview catalog filter applies definition selections and clears active scope", async () => {
  clearDefinitionCatalogLevelCache();
  const previousFetch = globalThis.fetch;
  const calls: string[] = [];
  globalThis.fetch = (async (input) => {
    const path = String(input);
    calls.push(path);

    if (path === "/api/workable/systems/Ops/definitions?level=true") {
      return Response.json({
        categories: [{ count: 1, label: "Billing", path: "Billing" }],
        definitions: [],
      });
    }

    if (path === "/api/workable/systems/Ops/definitions?level=true&category=Billing") {
      return Response.json({
        categories: [],
        definitions: [
          {
            category: "Billing",
            id: { value: "definition-bill-customers" },
            name: "BillCustomers",
          },
        ],
      });
    }

    return Response.json({ error: `Unhandled request: ${path}` }, { status: 500 });
  }) as typeof fetch;
  const selectedCategories: string[] = [];
  const selectedDefinitions: Array<{ category: string; definitionName: string }> = [];
  let clearCount = 0;
  const element = (scope: Parameters<typeof OverviewCatalogFilter>[0]["scope"]) => (
    <OverviewCatalogFilter
      connection={connection}
      onClear={() => {
        clearCount += 1;
      }}
      onSelectCategory={(category) => selectedCategories.push(category)}
      onSelectDefinition={(definitionName, category) =>
        selectedDefinitions.push({ category, definitionName })}
      refreshToken={0}
      scope={scope}
    />
  );
  const render = await renderDom(element(null));

  try {
    await render.click(render.getByRole("button", { name: "Filter overview" }));
    await render.waitFor(() => render.getByText("Billing"));
    await render.click(render.getByText("Billing"));
    await render.waitFor(() => render.getByText("BillCustomers"));
    await render.click(render.getByText("BillCustomers"));
    await render.click(render.getByText("Apply"));

    assert.deepEqual(selectedCategories, []);
    assert.deepEqual(selectedDefinitions, [
      {
        category: "Billing",
        definitionName: "BillCustomers",
      },
    ]);
    assert.equal(
      calls.includes("/api/workable/systems/Ops/definitions?level=true&category=Billing"),
      true
    );

    await render.rerender(element({
      category: "Billing",
      definitionName: "BillCustomers",
    }));
    await render.click(render.getByRole("button", { name: "Filter overview" }));
    await render.waitFor(() => render.getByText("BillCustomers"));
    await render.click(render.getByText("Clear"));

    assert.equal(clearCount, 1);
  } finally {
    globalThis.fetch = previousFetch;
    await render.restore();
    clearDefinitionCatalogLevelCache();
  }
});

test("catalog 404 does not republish an empty cache and request forever", async () => {
  clearDefinitionCatalogLevelCache();
  const previousFetch = globalThis.fetch;
  const calls: string[] = [];
  globalThis.fetch = (async (input) => {
    calls.push(String(input));
    return Response.json({ error: "Catalog missing" }, { status: 404 });
  }) as typeof fetch;
  const render = await renderDom(
    <DefinitionCatalogBrowser
      connection={connection}
      emptyState={<div>No entries</div>}
      loadingState={<div>Loading catalog</div>}
      onNavigate={() => undefined}
      path=""
      renderCategory={(category) => <div>{category.label}</div>}
      renderDefinition={(definition) => <div>{definition.name}</div>}
      renderError={(error) => <div>{error}</div>}
    />
  );

  try {
    await render.waitFor(() => render.getByText("Catalog missing"));
    await new Promise((resolve) => setTimeout(resolve, 25));
    assert.deepEqual(calls, ["/api/workable/systems/Ops/definitions?level=true"]);
  } finally {
    globalThis.fetch = previousFetch;
    await render.restore();
    clearDefinitionCatalogLevelCache();
  }
});

test("catalog failure invalidation stays isolated to the affected connection", async () => {
  clearDefinitionCatalogLevelCache();
  const previousFetch = globalThis.fetch;
  const calls: string[] = [];
  globalThis.fetch = (async (input) => {
    const path = String(input);
    calls.push(path);
    if (path === "/api/workable/systems/Alpha/definitions?level=true") {
      return Response.json({
        categories: [],
        definitions: [{ category: "Ops", id: { value: "alpha-1" }, name: "AlphaWork" }],
      });
    }

    if (path === "/api/workable/systems/Beta/definitions?level=true") {
      return Response.json({ error: "Beta catalog missing" }, { status: 404 });
    }

    return Response.json({ error: `Unhandled request: ${path}` }, { status: 500 });
  }) as typeof fetch;
  const browser = (systemName: string) => (
    <DefinitionCatalogBrowser
      connection={{ apiUrl: connection.apiUrl, systemName }}
      emptyState={<div>No entries</div>}
      loadingState={<div>Loading catalog</div>}
      onNavigate={() => undefined}
      path=""
      renderCategory={(category) => <div>{category.label}</div>}
      renderDefinition={(definition) => <div>{definition.name}</div>}
      renderError={(error) => <div>{error}</div>}
    />
  );
  const render = await renderDom(browser("Alpha"));

  try {
    await render.waitFor(() => render.getByText("AlphaWork"));
    await render.rerender(browser("Beta"));
    await render.waitFor(() => render.getByText("Beta catalog missing"));
    assert.equal(render.queryByText("AlphaWork"), null);

    await render.rerender(browser("Alpha"));
    await render.waitFor(() => render.getByText("AlphaWork"));
    await new Promise((resolve) => setTimeout(resolve, 25));

    assert.equal(
      calls.filter((path) =>
        path === "/api/workable/systems/Alpha/definitions?level=true"
      ).length,
      1
    );
  } finally {
    globalThis.fetch = previousFetch;
    await render.restore();
    clearDefinitionCatalogLevelCache();
  }
});
