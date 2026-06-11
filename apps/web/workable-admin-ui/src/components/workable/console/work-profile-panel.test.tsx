import assert from "node:assert/strict";
import test from "node:test";
import {
  findWorkProfileHotspots,
  WorkProfilePanel,
  collectWorkProfileExpandableNodeIds,
  createDefaultExpandedWorkProfileNodeIds,
  searchWorkProfile,
  summarizeWorkProfile,
} from "@/components/workable/console/work-profile-panel";
import type { WorkProfileSnapshot } from "@/lib/workable";
import {
  assertMarkupIncludes,
  renderMarkup,
} from "@/test/render";
import { renderDom } from "@/test/dom";

function profileSnapshot(): WorkProfileSnapshot {
  return {
    capturedAt: "2026-06-10T18:05:00.000Z",
    root: {
      children: [
        {
          children: [
            {
              children: [],
              label: "Query database",
              metricType: "Timing",
              nodeMilliseconds: 12,
              treeMilliseconds: 12,
            },
          ],
          context: { cacheKey: "home-page" },
          label: "Load source data",
          metricType: "Scope",
          nodeMilliseconds: 8,
          treeMilliseconds: 20,
        },
        {
          children: [
            {
              children: [],
              label: "Render section",
              metricType: "Timing",
              nodeMilliseconds: 5,
              treeMilliseconds: 5,
            },
          ],
          label: "Executing DemoProfilingSectionWorker.RunAsync",
          metricType: "MethodScope",
          nodeMilliseconds: 5,
          treeMilliseconds: 5,
        },
        {
          children: [],
          label: "Message count",
          metricType: "Metric",
          nodeMilliseconds: 0,
          treeMilliseconds: 0,
        },
      ],
      label: "Executing ImportOrders.Execute",
      metricType: "MethodScope",
      nodeMilliseconds: 12,
      treeMilliseconds: 37,
    },
    startedAt: "2026-06-10T18:04:58.000Z",
  };
}

test("work profile helpers summarize the tree and identify expandable nodes", () => {
  const profile = profileSnapshot();
  const querySearch = searchWorkProfile(profile, "query");
  const querySearchWithAncestors = searchWorkProfile(profile, "query", { keepAncestors: true });
  const treeHotspots = findWorkProfileHotspots(profile, "tree", "pct25");
  const nodeHotspots = findWorkProfileHotspots(profile, "node", "pct25");

  assert.deepEqual(summarizeWorkProfile(profile), {
    maxDepth: 3,
    metricCounts: {
      MethodScope: 2,
      Metric: 1,
      Scope: 1,
      Timing: 2,
    },
    nodeCount: 6,
    totalNodeMilliseconds: 12,
    totalTreeMilliseconds: 37,
  });
  assert.deepEqual(collectWorkProfileExpandableNodeIds(profile.root), ["root", "root.0", "root.1"]);
  assert.deepEqual(createDefaultExpandedWorkProfileNodeIds(profile), ["root"]);
  assert.deepEqual(querySearch?.matchedNodeCount, 1);
  assert.deepEqual([...(querySearch?.expandableNodeIds ?? [])].sort(), []);
  assert.deepEqual([...(querySearch?.visibleNodeIds ?? [])].sort(), ["root.0.0"]);
  assert.deepEqual([...(querySearchWithAncestors?.expandableNodeIds ?? [])].sort(), ["root", "root.0"]);
  assert.deepEqual([...(querySearchWithAncestors?.visibleNodeIds ?? [])].sort(), ["root", "root.0", "root.0.0"]);
  assert.deepEqual([...(treeHotspots?.matchedNodeIds ?? [])].sort(), ["root.0", "root.0.0"]);
  assert.deepEqual([...(nodeHotspots?.matchedNodeIds ?? [])].sort(), ["root.0.0"]);
  assert.equal(summarizeWorkProfile(null), null);
});

test("work profile panel renders compact summary pills and unavailable state", () => {
  const markup = renderMarkup(
    <WorkProfilePanel
      onClose={() => undefined}
      onViewStateChange={() => undefined}
      profile={profileSnapshot()}
      viewState="compact"
    />
  );

  assertMarkupIncludes(markup, "Profile");
  assertMarkupIncludes(markup, "37ms");
  assertMarkupIncludes(markup, "Nodes");
  assertMarkupIncludes(markup, "Depth");

  const unavailable = renderMarkup(
    <WorkProfilePanel
      onClose={() => undefined}
      onViewStateChange={() => undefined}
      profile={null}
      viewState="compact"
    />
  );

  assertMarkupIncludes(unavailable, "Unavailable");
});

test("work profile panel keeps the method scope picker fixed-width and truncated", async () => {
  const result = await renderDom(
    <WorkProfilePanel
      onClose={() => undefined}
      onViewStateChange={() => undefined}
      profile={profileSnapshot()}
      viewState="standard"
    />
  );

  try {
    const methodScopePicker = result.getByRole("combobox", { name: "Profile method scope" });
    assert.match(methodScopePicker.className, /\bsm:w-72\b/);

    const truncatedLabel = methodScopePicker.querySelector("span");
    assert.ok(truncatedLabel instanceof result.dom.window.HTMLElement);
    assert.match(truncatedLabel.className, /\btruncate\b/);
    assert.match(truncatedLabel.className, /\bflex-1\b/);
  } finally {
    await result.restore();
  }
});

test("work profile panel auto-expands the tree in detailed view", async () => {
  const result = await renderDom(
    <WorkProfilePanel
      onClose={() => undefined}
      onViewStateChange={() => undefined}
      profile={profileSnapshot()}
      viewState="standard"
    />
  );

  try {
    await result.waitFor(() => result.getByText("Load source data"));
    assert.equal(result.queryByText("Query database"), null);
    assert.equal(result.queryByText('"cacheKey"'), null);

    await result.rerender(
      <WorkProfilePanel
        onClose={() => undefined}
        onViewStateChange={() => undefined}
        profile={profileSnapshot()}
        viewState="detailed"
      />
    );

    await result.waitFor(() => result.getByText("Query database"));
    await result.waitFor(() => result.getByText('"cacheKey"'));
  } finally {
    await result.restore();
  }
});

test("work profile panel applies ancestor mode to hotspot filtering and exposes hotspot descriptions", async () => {
  const result = await renderDom(
    <WorkProfilePanel
      onClose={() => undefined}
      onViewStateChange={() => undefined}
      profile={profileSnapshot()}
      viewState="standard"
    />
  );

  try {
    const treeTimeButton = result.getByRole("button", { name: "Tree time" });
    await result.focus(treeTimeButton);
    await result.waitFor(() => result.getByText(
      "Tree time uses a node's total time including all descendant nodes. Use it to find slow regions of work."
    ));

    const nodeTimeButton = result.getByRole("button", { name: "Node time" });
    await result.focus(nodeTimeButton);
    await result.waitFor(() => result.getByText(
      "Node time uses only the time spent in the node itself, excluding descendants. Use it to find slow individual steps."
    ));

    await result.click(nodeTimeButton);
    await result.click(result.getByRole("combobox", { name: "Hotspot threshold" }));
    await result.click(result.getByRole("option", { name: ">= 25% total" }));
    await result.waitFor(() => result.getByText("Query database"));
    await result.waitFor(() => {
      assert.equal(result.queryByText("Load source data"), null);
      assert.equal(result.queryByText("Executing ImportOrders.Execute"), null);
      assert.equal(result.queryByText("Message count"), null);
    });

    await result.click(result.getByRole("button", { name: "Ancestors hidden" }));
    await result.waitFor(() => result.getByText("Load source data"));
    await result.waitFor(() => result.getByText("Executing ImportOrders.Execute"));
    await result.waitFor(() => {
      assert.equal(result.queryByText("Message count"), null);
    });

    await result.click(result.getByRole("button", { name: "Ancestors shown" }));
    await result.waitFor(() => {
      assert.equal(result.queryByText("Load source data"), null);
      assert.equal(result.queryByText("Executing ImportOrders.Execute"), null);
    });
  } finally {
    await result.restore();
  }
});
