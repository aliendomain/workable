import assert from "node:assert/strict";
import test from "node:test";
import {
  DiagnosticsDetailCard,
  DiagnosticsEmptyState,
  DiagnosticsLoadingState,
  DiagnosticsSummarySection,
  getDiagnosticsSectionStatus,
} from "@/components/workable/console/diagnostics-summary";
import {
  assertMarkupExcludes,
  assertMarkupIncludes,
  renderMarkup,
} from "@/test/render";

test("diagnostics summary primitives render collapsed, expanded, loading, empty, and warning states", () => {
  assert.equal(getDiagnosticsSectionStatus(false), "Collapsed");
  assert.equal(getDiagnosticsSectionStatus(true), "Waiting");

  const collapsed = renderMarkup(
    <DiagnosticsSummarySection
      expanded={false}
      onExpandedChange={() => undefined}
      summary="Deferred 0"
      title="Concurrency diagnostics"
    >
      <span>Hidden detail</span>
    </DiagnosticsSummarySection>
  );
  assertMarkupIncludes(collapsed, "Concurrency diagnostics");
  assertMarkupIncludes(collapsed, "Deferred 0");
  assertMarkupIncludes(collapsed, "Collapsed");
  assertMarkupExcludes(collapsed, "Hidden detail");

  const expanded = renderMarkup(
    <DiagnosticsSummarySection
      expanded
      onExpandedChange={() => undefined}
      summary={<span>Pending 2</span>}
      title="Read model diagnostics"
    >
      <DiagnosticsLoadingState>Loading read model diagnostics.</DiagnosticsLoadingState>
      <DiagnosticsEmptyState>No diagnostics loaded.</DiagnosticsEmptyState>
      <DiagnosticsDetailCard className="text-xs">
        Healthy
      </DiagnosticsDetailCard>
      <DiagnosticsDetailCard className="text-xs" tone="muted">
        Muted
      </DiagnosticsDetailCard>
      <DiagnosticsDetailCard className="text-xs" tone="warning">
        Warning
      </DiagnosticsDetailCard>
    </DiagnosticsSummarySection>
  );
  assertMarkupIncludes(expanded, "rotate-90");
  assertMarkupIncludes(expanded, "Waiting");
  assertMarkupIncludes(expanded, "animate-spin");
  assertMarkupIncludes(expanded, "bg-muted/20");
  assertMarkupIncludes(expanded, "bg-muted/10");
  assertMarkupIncludes(expanded, "border-border");
  assertMarkupIncludes(
    expanded,
    "border-[var(--status-warning-border)] bg-[var(--status-warning-soft)]"
  );
});
