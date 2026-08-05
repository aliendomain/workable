import assert from "node:assert/strict";
import test from "node:test";
import {
  findWorkProfileHotspots,
  WorkProfilePanel,
  collectWorkProfileExpandableNodeIds,
  createDefaultExpandedWorkProfileNodeIds,
  createWorkProfileSqlBatch,
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
              instrumentation: "application",
              metricType: "Timing",
              nodeMilliseconds: 12,
              treeMilliseconds: 12,
            },
          ],
          context: { cacheKey: "home-page" },
          label: "Load source data",
          instrumentation: "application",
          metricType: "Scope",
          nodeMilliseconds: 8,
          treeMilliseconds: 20,
        },
        {
          children: [
            {
              children: [],
              label: "Render section",
              instrumentation: "application",
              metricType: "Timing",
              nodeMilliseconds: 5,
              treeMilliseconds: 5,
            },
          ],
          label: "Executing DemoProfilingSectionWorker.RunAsync",
          instrumentation: "application",
          metricType: "MethodScope",
          nodeMilliseconds: 5,
          treeMilliseconds: 5,
        },
        {
          children: [],
          label: "Message count",
          instrumentation: "application",
          metricType: "Metric",
          nodeMilliseconds: 0,
          treeMilliseconds: 0,
        },
      ],
      label: "Executing ImportOrders.Execute",
      instrumentation: "application",
      metricType: "MethodScope",
      nodeMilliseconds: 12,
      treeMilliseconds: 37,
    },
    startedAt: "2026-06-10T18:04:58.000Z",
  };
}

function duplicateMethodScopeProfileSnapshot(): WorkProfileSnapshot {
  return {
    capturedAt: "2026-06-10T18:05:00.000Z",
    root: {
      children: [
        {
          children: [
            {
              children: [],
              label: "Render header",
              instrumentation: "application",
              metricType: "Timing",
              nodeMilliseconds: 7,
              treeMilliseconds: 7,
            },
          ],
          label: "Executing DemoProfilingSectionWorker.RunAsync",
          instrumentation: "application",
          metricType: "MethodScope",
          nodeMilliseconds: 7,
          treeMilliseconds: 7,
        },
        {
          children: [
            {
              children: [],
              label: "Render footer",
              instrumentation: "application",
              metricType: "Timing",
              nodeMilliseconds: 9,
              treeMilliseconds: 9,
            },
          ],
          label: "Executing DemoProfilingSectionWorker.RunAsync",
          instrumentation: "application",
          metricType: "MethodScope",
          nodeMilliseconds: 9,
          treeMilliseconds: 9,
        },
        {
          children: [],
          label: "Capture summary",
          instrumentation: "application",
          metricType: "Metric",
          nodeMilliseconds: 0,
          treeMilliseconds: 0,
        },
      ],
      label: "Executing ImportOrders.Execute",
      instrumentation: "application",
      metricType: "MethodScope",
      nodeMilliseconds: 16,
      treeMilliseconds: 16,
    },
    startedAt: "2026-06-10T18:04:58.000Z",
  };
}

function sqlProfileSnapshot(): WorkProfileSnapshot {
  return {
    capturedAt: "2026-06-10T18:05:00.000Z",
    root: {
      children: [
        {
          children: [
            {
              children: [],
              context: {
                CommandType: "Text",
                Database: "workable",
                HasTransaction: false,
                Operation: "ExecuteReader",
                ParameterCount: 3,
                Parameters: [
                  { Direction: "Input", IsRedacted: false, Name: "@SectionOrdinal", Type: "Int", Value: 1 },
                  { Direction: "Input", IsRedacted: false, Name: "@SectionLabel", Type: "NVarChar", Value: "Gather worker context" },
                  { Direction: "Input", IsRedacted: false, Name: "@Phase", Type: "NVarChar", Value: "Preparation" },
                ],
                Provider: "Microsoft.Data.SqlClient",
                Statement: "SELECT CAST(DB_NAME() AS nvarchar(128)) AS DatabaseName, CAST(@@SPID AS int) AS SessionId, @SectionOrdinal AS SectionOrdinal, @SectionLabel AS SectionLabel, @Phase AS Phase;",
                StatementKind: "SELECT",
              },
              label: "SQL ExecuteReader",
              instrumentation: "sql.client",
              metricType: "Timing",
              nodeMilliseconds: 14,
              treeMilliseconds: 14,
            },
            {
              children: [],
              context: {
                CommandType: "Text",
                Database: "workable",
                HasTransaction: false,
                Operation: "ExecuteScalar",
                ParameterCount: 2,
                Parameters: [
                  { Direction: "Input", IsRedacted: false, Name: "@WorkSystemName", Type: "NVarChar", Value: "default" },
                  { Direction: "Input", IsRedacted: false, Name: "@DefinitionPattern", Type: "NVarChar", Value: "sample.demo.%" },
                ],
                Provider: "Microsoft.Data.SqlClient",
                Statement: "SELECT COUNT(*) FROM workable.WorkEntries WHERE WorkSystemName = @WorkSystemName AND DefinitionName LIKE @DefinitionPattern;",
                StatementKind: "SELECT",
              },
              label: "SQL ExecuteScalar",
              instrumentation: "sql.client",
              metricType: "Timing",
              nodeMilliseconds: 9,
              treeMilliseconds: 9,
            },
          ],
          label: "Capture database sample",
          instrumentation: "application",
          metricType: "Scope",
          nodeMilliseconds: 3,
          treeMilliseconds: 26,
        },
        {
          children: [],
          label: "Render summary",
          instrumentation: "application",
          metricType: "Timing",
          nodeMilliseconds: 6,
          treeMilliseconds: 6,
        },
      ],
      label: "Executing DemoProfilingLabWork.RunAsync",
      instrumentation: "application",
      metricType: "MethodScope",
      nodeMilliseconds: 9,
      treeMilliseconds: 32,
    },
    startedAt: "2026-06-10T18:04:58.000Z",
  };
}

function httpProfileSnapshot(): WorkProfileSnapshot {
  return {
    capturedAt: "2026-06-10T18:05:00.000Z",
    root: {
      children: [
        {
          children: [
            {
              children: [],
              context: {
                telemetry: {
                  method: "GET",
                  outcome: "Success",
                  provider: "System.Net.Http",
                  statusCode: 200,
                  uri: "https://api.example.test/orders",
                },
              },
              label: "HTTP Request",
              instrumentation: "http.client",
              metricType: "Timing",
              nodeMilliseconds: 14,
              treeMilliseconds: 14,
            },
            {
              children: [],
              context: {
                Outcome: "Completed",
                Provider: "System.Net.Http",
                retryCount: 1,
              },
              label: "HTTP retry budget",
              instrumentation: "custom.http-retry",
              metricType: "Metric",
              nodeMilliseconds: 0,
              treeMilliseconds: 0,
            },
          ],
          label: "Capture HTTP sample",
          instrumentation: "application",
          metricType: "Scope",
          nodeMilliseconds: 3,
          treeMilliseconds: 17,
        },
        {
          children: [],
          context: {
            CommandType: "Text",
            Operation: "ExecuteScalar",
            Provider: "Microsoft.Data.SqlClient",
            Statement: "SELECT COUNT(*) FROM workable.WorkEntries;",
          },
          label: "SQL ExecuteScalar",
          instrumentation: "sql.client",
          metricType: "Timing",
          nodeMilliseconds: 8,
          treeMilliseconds: 8,
        },
        {
          children: [],
          label: "Render summary",
          instrumentation: "application",
          metricType: "Timing",
          nodeMilliseconds: 4,
          treeMilliseconds: 4,
        },
      ],
      label: "Executing DemoProfilingLabWork.RunAsync",
      instrumentation: "application",
      metricType: "MethodScope",
      nodeMilliseconds: 15,
      treeMilliseconds: 29,
    },
    startedAt: "2026-06-10T18:04:58.000Z",
  };
}

function nestedSqlProfileSnapshot(): WorkProfileSnapshot {
  return {
    capturedAt: "2026-06-10T18:05:00.000Z",
    root: {
      children: [
        {
          children: [
            {
              children: [],
              context: {
                metadata: {
                  sql: {
                    CommandType: "Text",
                    Database: "workable",
                    HasTransaction: false,
                    Operation: "ExecuteReader",
                    ParameterCount: 1,
                    Parameters: [
                      { Direction: "Input", IsRedacted: false, Name: "@SectionOrdinal", Type: "Int", Value: 1 },
                    ],
                    Provider: "Microsoft.Data.SqlClient",
                    Statement: "SELECT @SectionOrdinal AS SectionOrdinal;",
                    StatementKind: "SELECT",
                  },
                },
              },
              label: "SQL ExecuteReader",
              instrumentation: "sql.client",
              metricType: "Timing",
              nodeMilliseconds: 14,
              treeMilliseconds: 14,
            },
          ],
          label: "Capture database sample",
          instrumentation: "application",
          metricType: "Scope",
          nodeMilliseconds: 3,
          treeMilliseconds: 17,
        },
      ],
      label: "Executing DemoProfilingLabWork.RunAsync",
      instrumentation: "application",
      metricType: "MethodScope",
      nodeMilliseconds: 9,
      treeMilliseconds: 20,
    },
    startedAt: "2026-06-10T18:04:58.000Z",
  };
}

function camelCaseSqlProfileSnapshot(): WorkProfileSnapshot {
  return {
    capturedAt: "2026-06-10T18:05:00.000Z",
    root: {
      children: [
        {
          children: [
            {
              children: [],
              context: {
                commandType: "Text",
                database: "workable",
                hasTransaction: false,
                operation: "ExecuteReader",
                parameterCount: 2,
                parameters: [
                  { direction: "Input", isRedacted: false, name: "@WorkSystemName", type: "NVarChar", value: "default" },
                  { direction: "Input", isRedacted: false, name: "@DefinitionPattern", type: "NVarChar", value: "sample.demo.%" },
                ],
                provider: "Microsoft.Data.SqlClient",
                statement: "SELECT COUNT(*) FROM workable.WorkEntries WHERE WorkSystemName = @WorkSystemName AND DefinitionName LIKE @DefinitionPattern;",
                statementKind: "SELECT",
              },
              label: "SQL ExecuteReader",
              instrumentation: "sql.client",
              metricType: "Timing",
              nodeMilliseconds: 12,
              treeMilliseconds: 12,
            },
          ],
          label: "Capture database sample",
          instrumentation: "application",
          metricType: "Scope",
          nodeMilliseconds: 3,
          treeMilliseconds: 15,
        },
      ],
      label: "Executing DemoProfilingLabWork.RunAsync",
      instrumentation: "application",
      metricType: "MethodScope",
      nodeMilliseconds: 6,
      treeMilliseconds: 18,
    },
    startedAt: "2026-06-10T18:04:58.000Z",
  };
}

function largeSqlProfileSnapshot(): WorkProfileSnapshot {
  const repeatedPredicate = " OR WorkSystemName = @WorkSystemName";
  const statement = `SELECT COUNT(*) FROM workable.WorkEntries WHERE DefinitionName LIKE @DefinitionPattern${repeatedPredicate.repeat(900)};`;
  return {
    capturedAt: "2026-06-10T18:05:00.000Z",
    root: {
      children: [
        {
          children: [
            {
              children: [],
              context: {
                CommandType: "Text",
                Database: "workable",
                HasTransaction: false,
                Operation: "ExecuteScalar",
                ParameterCount: 2,
                Parameters: [
                  { Direction: "Input", IsRedacted: false, Name: "@WorkSystemName", Type: "NVarChar", Value: "default" },
                  { Direction: "Input", IsRedacted: false, Name: "@DefinitionPattern", Type: "NVarChar", Value: "sample.demo.%" },
                ],
                Provider: "Microsoft.Data.SqlClient",
                Statement: statement,
                StatementKind: "SELECT",
              },
              label: "SQL ExecuteScalar",
              instrumentation: "sql.client",
              metricType: "Timing",
              nodeMilliseconds: 12,
              treeMilliseconds: 12,
            },
          ],
          label: "Capture database sample",
          instrumentation: "application",
          metricType: "Scope",
          nodeMilliseconds: 3,
          treeMilliseconds: 15,
        },
      ],
      label: "Executing DemoProfilingLabWork.RunAsync",
      instrumentation: "application",
      metricType: "MethodScope",
      nodeMilliseconds: 6,
      treeMilliseconds: 18,
    },
    startedAt: "2026-06-10T18:04:58.000Z",
  };
}

function sqlLabelFalsePositiveProfileSnapshot(): WorkProfileSnapshot {
  return {
    capturedAt: "2026-06-10T18:05:00.000Z",
    root: {
      children: [
        {
          children: [
            {
              children: [],
              context: {
                CommandType: "Text",
                Database: "workable",
                HasTransaction: false,
                Operation: "ExecuteReader",
                ParameterCount: 1,
                Parameters: [
                  { Direction: "Input", IsRedacted: false, Name: "@SectionOrdinal", Type: "Int", Value: 1 },
                ],
                Provider: "Microsoft.Data.SqlClient",
                Statement: "SELECT @SectionOrdinal AS SectionOrdinal;",
                StatementKind: "SELECT",
              },
              label: "SQL InternalExecuteReaderAsync",
              instrumentation: "custom.sql-probe",
              metricType: "Timing",
              nodeMilliseconds: 1,
              treeMilliseconds: 1,
            },
          ],
          context: {
            connectionTarget: "sample persistence SQL Server",
            label: "Gather worker context",
            ordinal: 1,
            phase: "Preparation",
          },
          label: "Executing SampleHost.Demo.DemoProfilingSqlProbe.CaptureAsync",
          instrumentation: "application",
          metricType: "MethodScope",
          nodeMilliseconds: 18,
          treeMilliseconds: 18,
        },
      ],
      label: "Executing DemoProfilingLabWork.RunAsync",
      instrumentation: "application",
      metricType: "MethodScope",
      nodeMilliseconds: 2,
      treeMilliseconds: 20,
    },
    startedAt: "2026-06-10T18:04:58.000Z",
  };
}

function storedProcedureSqlProfileSnapshot(): WorkProfileSnapshot {
  return {
    capturedAt: "2026-06-10T18:05:00.000Z",
    root: {
      children: [
        {
          children: [
            {
              children: [],
              context: {
                commandType: "StoredProcedure",
                database: "workable",
                hasTransaction: false,
                operation: "ExecuteNonQuery",
                parameterCount: 2,
                parameters: [
                  { direction: "Input", isRedacted: false, name: "@Name", type: "NVarChar", value: "alpha" },
                  { direction: "Output", isRedacted: false, name: "@CreatedId", type: "Int", value: null },
                ],
                provider: "Microsoft.Data.SqlClient",
                statement: "workable.CreateThing",
                statementKind: "EXEC",
              },
              label: "SQL ExecuteNonQuery",
              instrumentation: "sql.client",
              metricType: "Timing",
              nodeMilliseconds: 8,
              treeMilliseconds: 8,
            },
          ],
          label: "Invoke stored procedure",
          instrumentation: "application",
          metricType: "Scope",
          nodeMilliseconds: 2,
          treeMilliseconds: 10,
        },
      ],
      label: "Executing DemoProfilingLabWork.RunAsync",
      instrumentation: "application",
      metricType: "MethodScope",
      nodeMilliseconds: 5,
      treeMilliseconds: 13,
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

test("work profile panel distinguishes disabled, pending, and missing profile snapshots", () => {
  const disabled = renderMarkup(
    <WorkProfilePanel
      iterationIsFinal
      iterationStatus="Completed"
      onClose={() => undefined}
      onViewStateChange={() => undefined}
      profile={null}
      profilingEnabled={false}
      viewState="standard"
    />
  );
  assertMarkupIncludes(disabled, "Profiling was disabled for this iteration");

  const pending = renderMarkup(
    <WorkProfilePanel
      iterationIsFinal={false}
      iterationStatus="Executing"
      onClose={() => undefined}
      onViewStateChange={() => undefined}
      profile={null}
      profilingEnabled
      viewState="standard"
    />
  );
  assertMarkupIncludes(pending, "This iteration is still executing");
  assertMarkupIncludes(pending, "profile tree will appear after the iteration finishes");

  const missing = renderMarkup(
    <WorkProfilePanel
      iterationIsFinal
      iterationStatus="Completed"
      onClose={() => undefined}
      onViewStateChange={() => undefined}
      profile={null}
      profilingEnabled
      viewState="standard"
    />
  );
  assertMarkupIncludes(missing, "Profiling was enabled for this iteration");
  assertMarkupIncludes(missing, "no profile snapshot is available");
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

test("work profile panel exposes its description through an info tooltip instead of inline text", async () => {
  const result = await renderDom(
    <WorkProfilePanel
      onClose={() => undefined}
      onViewStateChange={() => undefined}
      profile={profileSnapshot()}
      viewState="standard"
    />
  );

  try {
    assert.equal(
      result.queryByText("Per-iteration profile tree with timings, scopes, and captured context."),
      null
    );

    const infoButton = result.getByRole("button", {
      name: "Profile: Per-iteration profile tree with timings, scopes, and captured context.",
    });
    await result.focus(infoButton);
    await result.waitFor(() => result.getByText(
      "Per-iteration profile tree with timings, scopes, and captured context."
    ));
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

test("work profile panel method scope filter keeps one option per method identity and shows every matching subtree", async () => {
  const result = await renderDom(
    <WorkProfilePanel
      onClose={() => undefined}
      onViewStateChange={() => undefined}
      profile={duplicateMethodScopeProfileSnapshot()}
      viewState="standard"
    />
  );

  try {
    await result.click(result.getByRole("combobox", { name: "Profile method scope" }));
    await result.waitFor(() => result.getByRole("option", { name: "DemoProfilingSectionWorker.RunAsync" }));

    const duplicateOptions = [...result.dom.window.document.querySelectorAll("[role='option']")]
      .filter((element) => element.textContent?.trim() === "DemoProfilingSectionWorker.RunAsync");
    assert.equal(duplicateOptions.length, 1);

    const methodScopeOption = duplicateOptions[0];
    assert.ok(methodScopeOption instanceof result.dom.window.HTMLElement);
    await result.click(methodScopeOption);

    await result.waitFor(() => result.getByText("Render header"));
    await result.waitFor(() => result.getByText("Render footer"));
    await result.waitFor(() => {
      assert.equal(result.queryByText("Capture summary"), null);
    });
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

    await result.click(result.getByRole("button", { name: "Ancestor context" }));
    await result.waitFor(() => result.getByText("Load source data"));
    await result.waitFor(() => result.getByText("Executing ImportOrders.Execute"));
    await result.waitFor(() => {
      assert.equal(result.queryByText("Message count"), null);
    });

    await result.click(result.getByRole("button", { name: "Ancestor context" }));
    await result.waitFor(() => {
      assert.equal(result.queryByText("Load source data"), null);
      assert.equal(result.queryByText("Executing ImportOrders.Execute"), null);
    });
  } finally {
    await result.restore();
  }
});

test("work profile sql batch helper combines the captured SQL commands into a replayable script", () => {
  const batch = createWorkProfileSqlBatch(sqlProfileSnapshot());

  assert.equal(batch?.statementCount, 2);
  assert.equal(batch?.parameterCount, 5);
  assert.equal(batch?.redactedParameterCount, 0);
  assert.match(batch?.replayableBatch ?? "", /DECLARE @SectionOrdinal int = 1;/);
  assert.match(batch?.replayableBatch ?? "", /DECLARE @WorkSystemName nvarchar\(max\) = N'default';/);
  assert.match(batch?.replayableBatch ?? "", /SELECT COUNT\(\*\) FROM workable\.WorkEntries WHERE WorkSystemName = @WorkSystemName AND DefinitionName LIKE @DefinitionPattern;/);
  assert.match(batch?.replayableBatch ?? "", /\nGO(?:\n|$)/);
  assert.match(batch?.originalExecutionBatch ?? "", /DECLARE @cmd1_SectionOrdinal int = 1;/);
  assert.match(batch?.originalExecutionBatch ?? "", /EXEC sp_executesql/);
  assert.match(batch?.originalExecutionBatch ?? "", /@DefinitionPattern = @cmd2_DefinitionPattern/);
  assert.doesNotMatch(batch?.originalExecutionBatch ?? "", /\nGO(?:\n|$)/);
});

test("work profile sql batch helper finds nested SQL command context", () => {
  const batch = createWorkProfileSqlBatch(nestedSqlProfileSnapshot());

  assert.equal(batch?.statementCount, 1);
  assert.equal(batch?.parameterCount, 1);
  assert.match(batch?.replayableBatch ?? "", /DECLARE @SectionOrdinal int = 1;/);
  assert.match(batch?.replayableBatch ?? "", /SELECT @SectionOrdinal AS SectionOrdinal;/);
});

test("work profile sql batch helper understands camelCase SQL command context", () => {
  const batch = createWorkProfileSqlBatch(camelCaseSqlProfileSnapshot());

  assert.equal(batch?.statementCount, 1);
  assert.equal(batch?.parameterCount, 2);
  assert.match(batch?.replayableBatch ?? "", /DECLARE @WorkSystemName nvarchar\(max\) = N'default';/);
  assert.match(batch?.replayableBatch ?? "", /DefinitionName LIKE @DefinitionPattern;/);
});

test("work profile sql batch helper keeps omitted binary Variant values non-replayable", () => {
  const batch = createWorkProfileSqlBatch({
    capturedAt: "2026-06-10T18:05:00.000Z",
    root: {
      children: [],
      context: {
        CommandType: "Text",
        Operation: "ExecuteNonQuery",
        Parameters: [
          {
            Direction: "Input",
            IsBinaryOmitted: true,
            IsRedacted: false,
            Name: "@Payload",
            Type: "Variant",
            Value: "<binary omitted>",
          },
        ],
        Provider: "Microsoft.Data.SqlClient",
        Statement: "SELECT @Payload;",
        StatementKind: "SELECT",
      },
      label: "SQL ExecuteNonQuery",
      instrumentation: "sql.client",
      metricType: "Timing",
      nodeMilliseconds: 4,
      treeMilliseconds: 4,
    },
    startedAt: "2026-06-10T18:04:58.000Z",
  });

  assert.match(
    batch?.replayableBatch ?? "",
    /DECLARE @Payload nvarchar\(max\) = NULL \/\* binary parameter value was omitted from profile \*\//
  );
  assert.doesNotMatch(batch?.replayableBatch ?? "", /N'<binary omitted>'/);
});

test("work profile panel can filter directly to SQL nodes and optionally keep ancestor context", async () => {
  const result = await renderDom(
    <WorkProfilePanel
      onClose={() => undefined}
      onViewStateChange={() => undefined}
      profile={sqlProfileSnapshot()}
      viewState="standard"
    />
  );

  try {
    await result.waitFor(() => result.getByText("Capture database sample"));
    await result.click(result.getByRole("button", { name: "SQL nodes only" }));

    await result.waitFor(() => result.getByText("SQL ExecuteReader"));
    await result.waitFor(() => result.getByText("SQL ExecuteScalar"));
    await result.waitFor(() => {
      assert.equal(result.queryByText("Capture database sample"), null);
      assert.equal(result.queryByText("Executing DemoProfilingLabWork.RunAsync"), null);
      assert.equal(result.queryByText("Render summary"), null);
    });

    await result.click(result.getByRole("button", { name: "Ancestor context" }));
    await result.waitFor(() => result.getByText("Capture database sample"));
    await result.waitFor(() => result.getByText("Executing DemoProfilingLabWork.RunAsync"));
    await result.waitFor(() => {
      assert.equal(result.queryByText("Render summary"), null);
    });
  } finally {
    await result.restore();
  }
});

test("work profile panel filters HTTP nodes by instrumentation identity", async () => {
  const result = await renderDom(
    <WorkProfilePanel
      onClose={() => undefined}
      onViewStateChange={() => undefined}
      profile={httpProfileSnapshot()}
      viewState="standard"
    />
  );

  try {
    const httpOnlyButton = result.getByRole("button", { name: "HTTP request nodes only" });
    assert.equal(
      httpOnlyButton.getAttribute("title"),
      "HTTP-only filter is off. Show only captured HTTP request nodes."
    );

    await result.click(httpOnlyButton);
    await result.waitFor(() => result.getByText("HTTP Request"));
    await result.waitFor(() => {
      assert.equal(httpOnlyButton.getAttribute("aria-pressed"), "true");
      assert.equal(
        httpOnlyButton.getAttribute("title"),
        "HTTP-only filter is on. The profile tree is limited to captured HTTP request nodes."
      );
      assert.equal(result.queryByText("Capture HTTP sample"), null);
      assert.equal(result.queryByText("HTTP retry budget"), null);
      assert.equal(result.queryByText("SQL ExecuteScalar"), null);
      assert.equal(result.queryByText("Render summary"), null);
    });

    await result.click(result.getByRole("button", { name: "Ancestor context" }));
    await result.waitFor(() => result.getByText("Capture HTTP sample"));
    assert.equal(result.queryByText("HTTP retry budget"), null);

    const sqlOnlyButton = result.getByRole("button", { name: "SQL nodes only" });
    await result.click(sqlOnlyButton);
    await result.waitFor(() => result.getByText("SQL ExecuteScalar"));
    await result.waitFor(() => {
      assert.equal(sqlOnlyButton.getAttribute("aria-pressed"), "true");
      assert.equal(httpOnlyButton.getAttribute("aria-pressed"), "false");
      assert.equal(result.queryByText("HTTP Request"), null);
    });
  } finally {
    await result.restore();
  }
});

test("work profile panel exposes the SQL filter state and enables SQL batch from nested SQL context", async () => {
  const result = await renderDom(
    <WorkProfilePanel
      onClose={() => undefined}
      onViewStateChange={() => undefined}
      profile={nestedSqlProfileSnapshot()}
      viewState="standard"
    />
  );

  try {
    const sqlOnlyButton = result.getByRole("button", { name: "SQL nodes only" });
    assert.equal(
      sqlOnlyButton.getAttribute("title"),
      "SQL-only filter is off. Show only captured SQL command nodes."
    );

    await result.click(sqlOnlyButton);
    await result.waitFor(() => {
      assert.equal(
        sqlOnlyButton.getAttribute("title"),
        "SQL-only filter is on. The profile tree is limited to captured SQL command nodes."
      );
    });

    const sqlBatchButton = result.getByRole("button", { name: "Open SQL batch" });
    assert.equal(sqlBatchButton.hasAttribute("disabled"), false);

    await result.click(sqlBatchButton);
    await result.waitFor(() => result.getByText("SQL batch"));
    const batchViewer = result.getByRole("region", { name: "Replayable SQL batch" });
    assert.match(batchViewer.textContent ?? "", /SELECT @SectionOrdinal AS SectionOrdinal;/);
  } finally {
    await result.restore();
  }
});

test("work profile panel disables SQL actions and explains how to enable them when SQL profiling is unavailable", async () => {
  const result = await renderDom(
    <WorkProfilePanel
      onClose={() => undefined}
      onViewStateChange={() => undefined}
      profile={profileSnapshot()}
      sqlProfilingAvailable={false}
      viewState="standard"
    />
  );

  try {
    const sqlOnlyButton = result.getByRole("button", { name: "SQL nodes only" });
    const sqlBatchButton = result.getByRole("button", { name: "Open SQL batch" });
    assert.equal(sqlOnlyButton.hasAttribute("disabled"), true);
    assert.equal(sqlBatchButton.hasAttribute("disabled"), true);
    assert.equal(
      sqlOnlyButton.getAttribute("title"),
      "SQL profiling is not available for this system. Enable it by calling AddWorkableSqlServerProfiling() in the host's Workable SQL Server configuration."
    );
    assert.equal(
      sqlBatchButton.getAttribute("title"),
      "SQL profiling is not available for this system. Enable it by calling AddWorkableSqlServerProfiling() in the host's Workable SQL Server configuration."
    );

    const sqlOnlyTrigger = sqlOnlyButton.parentElement;
    assert.ok(sqlOnlyTrigger instanceof result.dom.window.HTMLElement);
    await result.focus(sqlOnlyTrigger);
    await result.waitFor(() => result.getByText(
      "SQL profiling is not available for this system. Enable it by calling AddWorkableSqlServerProfiling() in the host's Workable SQL Server configuration."
    ));
  } finally {
    await result.restore();
  }
});

test("work profile panel disables the HTTP filter and explains how to enable profiling when unavailable", async () => {
  const result = await renderDom(
    <WorkProfilePanel
      httpClientProfilingAvailable={false}
      onClose={() => undefined}
      onViewStateChange={() => undefined}
      profile={profileSnapshot()}
      viewState="standard"
    />
  );

  try {
    const httpOnlyButton = result.getByRole("button", { name: "HTTP request nodes only" });
    assert.equal(httpOnlyButton.hasAttribute("disabled"), true);
    assert.equal(
      httpOnlyButton.getAttribute("title"),
      "HTTP client profiling is not available for this system. Enable it by calling AddWorkableHttpClientProfiling() in the host's Workable configuration."
    );

    const httpOnlyTrigger = httpOnlyButton.parentElement;
    assert.ok(httpOnlyTrigger instanceof result.dom.window.HTMLElement);
    await result.focus(httpOnlyTrigger);
    await result.waitFor(() => result.getByText(
      "HTTP client profiling is not available for this system. Enable it by calling AddWorkableHttpClientProfiling() in the host's Workable configuration."
    ));
  } finally {
    await result.restore();
  }
});

test("work profile panel opens an informational SQL batch dialog when the profile has no captured SQL commands", async () => {
  const result = await renderDom(
    <WorkProfilePanel
      onClose={() => undefined}
      onViewStateChange={() => undefined}
      profile={profileSnapshot()}
      viewState="standard"
    />
  );

  try {
    const sqlBatchButton = result.getByRole("button", { name: "Open SQL batch" });
    assert.equal(sqlBatchButton.hasAttribute("disabled"), false);
    assert.equal(
      sqlBatchButton.getAttribute("title"),
      "Open the SQL batch viewer for any captured SQL commands in this profile."
    );

    await result.click(sqlBatchButton);
    await result.waitFor(() => result.getByText("SQL batch"));
    await result.waitFor(() => result.getByText(
      "This profile does not contain captured SQL commands, so there is no SQL batch to display."
    ));

    assert.equal(result.dom.window.document.querySelector("[role='tab']"), null);
    assert.equal(result.queryByText("Copy SQL"), null);
  } finally {
    await result.restore();
  }
});

test("work profile panel does not infer SQL instrumentation from labels or context", async () => {
  assert.equal(createWorkProfileSqlBatch(sqlLabelFalsePositiveProfileSnapshot()), null);
  const result = await renderDom(
    <WorkProfilePanel
      onClose={() => undefined}
      onViewStateChange={() => undefined}
      profile={sqlLabelFalsePositiveProfileSnapshot()}
      viewState="standard"
    />
  );

  try {
    await result.waitFor(() => result.getByText("Executing SampleHost.Demo.DemoProfilingSqlProbe.CaptureAsync"));
    await result.click(result.getByRole("button", { name: "SQL nodes only" }));

    await result.waitFor(() => result.getByText("No SQL profile nodes matched the active filters."));
    assert.equal(result.queryByText("SQL InternalExecuteReaderAsync"), null);
    assert.equal(result.queryByText("Executing SampleHost.Demo.DemoProfilingSqlProbe.CaptureAsync"), null);
  } finally {
    await result.restore();
  }
});

test("work profile panel enables SQL batch for camelCase SQL context", async () => {
  const result = await renderDom(
    <WorkProfilePanel
      onClose={() => undefined}
      onViewStateChange={() => undefined}
      profile={camelCaseSqlProfileSnapshot()}
      viewState="standard"
    />
  );

  try {
    const sqlBatchButton = result.getByRole("button", { name: "Open SQL batch" });
    assert.equal(sqlBatchButton.hasAttribute("disabled"), false);

    await result.click(sqlBatchButton);
    await result.waitFor(() => result.getByText("SQL batch"));
    const batchViewer = result.getByRole("region", { name: "Replayable SQL batch" });
    assert.match(batchViewer.textContent ?? "", /SELECT COUNT\(\*\) FROM workable\.WorkEntries/);
  } finally {
    await result.restore();
  }
});

test("work profile panel inlines SQL parameter values in node context previews", async () => {
  const result = await renderDom(
    <WorkProfilePanel
      onClose={() => undefined}
      onViewStateChange={() => undefined}
      profile={sqlProfileSnapshot()}
      viewState="detailed"
    />
  );

  try {
    await result.waitFor(() => result.getByText("SQL ExecuteReader"));
    await result.waitFor(() => result.getByText("SQL ExecuteScalar"));

    const contextBlocks = [...result.dom.window.document.querySelectorAll("pre")];
    assert.equal(contextBlocks.length, 2);

    const combinedText = contextBlocks.map((element) => element.textContent ?? "").join("\n");
    assert.match(combinedText, /1 AS SectionOrdinal/);
    assert.match(combinedText, /N'Gather worker context' AS SectionLabel/);
    assert.match(combinedText, /N'Preparation' AS Phase/);
    assert.match(combinedText, /WorkSystemName = N'default'/);
    assert.match(combinedText, /DefinitionName LIKE N'sample\.demo\.%'/);
    assert.equal(combinedText.includes('"Parameters"'), false);
    assert.equal(combinedText.includes('"parameters"'), false);
  } finally {
    await result.restore();
  }
});

test("work profile panel formats JSON strings nested in context", async () => {
  const result = await renderDom(
    <WorkProfilePanel
      onClose={() => undefined}
      onViewStateChange={() => undefined}
      profile={{
        capturedAt: "2026-06-10T18:05:00.000Z",
        root: {
          children: [],
          context: {
            json: JSON.stringify({
              configuration: {
                enabled: true,
                name: "nested value",
              },
            }),
          },
          label: "Captured request",
          instrumentation: "application",
          metricType: "Metric",
          nodeMilliseconds: 0,
          treeMilliseconds: 0,
        },
        startedAt: "2026-06-10T18:04:58.000Z",
      }}
      viewState="detailed"
    />
  );

  try {
    const contextBlock = result.dom.window.document.querySelector("pre");
    assert.ok(contextBlock);
    assert.match(contextBlock.textContent ?? "", /"json": \{\n    "configuration": \{/);
    assert.match(contextBlock.textContent ?? "", /"enabled": true/);
    assert.equal((contextBlock.textContent ?? "").includes("\\\"configuration\\\""), false);
  } finally {
    await result.restore();
  }
});

test("work profile panel comments output parameters inside SQL statement previews", async () => {
  const result = await renderDom(
    <WorkProfilePanel
      onClose={() => undefined}
      onViewStateChange={() => undefined}
      profile={storedProcedureSqlProfileSnapshot()}
      viewState="detailed"
    />
  );

  try {
    await result.waitFor(() => result.getByText("SQL ExecuteNonQuery"));

    const contextBlocks = [...result.dom.window.document.querySelectorAll("pre")];
    assert.equal(contextBlocks.length, 1);

    const contextText = contextBlocks[0]?.textContent ?? "";
    assert.match(contextText, /EXEC workable\.CreateThing/);
    assert.match(contextText, /@Name = N'alpha'/);
    assert.match(contextText, /@CreatedId = NULL \/\* Output parameter \*\//);
    assert.equal(contextText.includes('"parameters"'), false);
  } finally {
    await result.restore();
  }
});

test("work profile panel opens the generated SQL batch in a scrollable dialog", async () => {
  const result = await renderDom(
    <WorkProfilePanel
      onClose={() => undefined}
      onViewStateChange={() => undefined}
      profile={sqlProfileSnapshot()}
      viewState="standard"
    />
  );

  try {
    await result.waitFor(() => result.getByRole("button", { name: "Open SQL batch" }));
    await result.click(result.getByRole("button", { name: "Open SQL batch" }));
    await result.waitFor(() => result.getByText("SQL batch"));

    const dialog = result.getByRole("dialog");
    assert.match(dialog.className, /\bflex\b/);
    assert.match(dialog.className, /\bflex-col\b/);
    assert.match(dialog.className, /max-h-\[calc\(100vh-2rem\)\]/);

    const batchViewer = result.getByRole("region", { name: "Replayable SQL batch" });
    assert.match(batchViewer.className, /\boverflow-auto\b/);
    assert.match(batchViewer.className, /\bflex-1\b/);
    assert.match(batchViewer.className, /min-h-\[14rem\]/);
    assert.match(batchViewer.textContent ?? "", /DECLARE @SectionOrdinal int = 1;/);
    assert.match(batchViewer.textContent ?? "", /DefinitionName LIKE @DefinitionPattern;/);
    const pre = batchViewer.querySelector("pre");
    assert.ok(pre instanceof result.dom.window.HTMLElement);
    assert.match(pre.className, /\bfont-mono\b/);
    assert.ok(batchViewer.querySelector(".text-violet-300"));
    assert.ok(batchViewer.querySelector(".text-sky-300"));
    assert.ok(batchViewer.querySelector(".text-emerald-300"));
    const highlightedKeywords = [...batchViewer.querySelectorAll(".text-violet-300")]
      .map((element) => element.textContent?.trim())
      .filter((text): text is string => Boolean(text));
    assert.ok(highlightedKeywords.includes("SELECT"));
    assert.ok(batchViewer.querySelector(".text-cyan-300"));
    result.getByRole("button", { name: "Copy SQL" });
  } finally {
    await result.restore();
  }
});

test("work profile panel can switch to the parameterized SQL view", async () => {
  const result = await renderDom(
    <WorkProfilePanel
      onClose={() => undefined}
      onViewStateChange={() => undefined}
      profile={sqlProfileSnapshot()}
      viewState="standard"
    />
  );

  try {
    await result.waitFor(() => result.getByRole("button", { name: "Open SQL batch" }));
    await result.click(result.getByRole("button", { name: "Open SQL batch" }));
    await result.waitFor(() => result.getByText("SQL batch"));
    await result.click(result.getByRole("tab", { name: "Parameterized view" }));

    await result.waitFor(() => result.getByRole("region", { name: "Parameterized SQL batch" }));
    const batchViewer = result.getByRole("region", { name: "Parameterized SQL batch" });
    assert.match(batchViewer.textContent ?? "", /EXEC sp_executesql/);
    assert.match(batchViewer.textContent ?? "", /@DefinitionPattern = @cmd2_DefinitionPattern/);
    assert.doesNotMatch(batchViewer.textContent ?? "", /\nGO(?:\n|$)/);
  } finally {
    await result.restore();
  }
});

test("work profile panel disables SQL syntax highlighting for very large batches", async () => {
  const result = await renderDom(
    <WorkProfilePanel
      onClose={() => undefined}
      onViewStateChange={() => undefined}
      profile={largeSqlProfileSnapshot()}
      viewState="standard"
    />
  );

  try {
    await result.waitFor(() => result.getByRole("button", { name: "Open SQL batch" }));
    await result.click(result.getByRole("button", { name: "Open SQL batch" }));
    await result.waitFor(() => result.getByText("SQL batch"));
    await result.waitFor(() => result.getByText(
      "Syntax highlighting is disabled for large SQL batches so the viewer stays responsive."
    ));

    const batchViewer = result.getByRole("region", { name: "Replayable SQL batch" });
    assert.equal(batchViewer.querySelector(".text-violet-300"), null);
    assert.match(batchViewer.textContent ?? "", /SELECT COUNT\(\*\) FROM workable\.WorkEntries/);
  } finally {
    await result.restore();
  }
});

test("work profile panel can copy the generated SQL batch", async () => {
  const copiedValues: string[] = [];
  const result = await renderDom(
    <WorkProfilePanel
      onClose={() => undefined}
      onViewStateChange={() => undefined}
      profile={sqlProfileSnapshot()}
      viewState="standard"
    />,
    {
      setupWindow: (window) => {
        Object.defineProperty(window.navigator, "clipboard", {
          configurable: true,
          value: {
            writeText: async (value: string) => {
              copiedValues.push(value);
            },
          },
        });
      },
    }
  );

  try {
    await result.waitFor(() => result.getByRole("button", { name: "Open SQL batch" }));
    await result.click(result.getByRole("button", { name: "Open SQL batch" }));
    await result.waitFor(() => result.getByText("SQL batch"));

    await result.click(result.getByRole("button", { name: "Copy SQL" }));
    await result.waitFor(() => result.getByRole("button", { name: "Copied" }));

    assert.equal(copiedValues.length, 1);
    assert.match(copiedValues[0] ?? "", /DECLARE @SectionOrdinal int = 1;/);
    assert.match(copiedValues[0] ?? "", /\nGO(?:\n|$)/);
  } finally {
    await result.restore();
  }
});
