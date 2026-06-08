import assert from "node:assert/strict";
import test from "node:test";
import {
  ErrorBanner,
  ErrorPanel,
  FeedbackBanner,
  FeedbackPanel,
} from "@/components/workable/console/feedback-panel";
import {
  assertMarkupIncludes,
  countMarkupOccurrences,
  renderMarkup,
} from "@/test/render";

test("feedback panel filters empty messages and de-duplicates repeated messages", () => {
  const markup = renderMarkup(
    <FeedbackPanel
      messages={["Host unavailable", undefined, "Host unavailable", "Timed out"]}
      title="Connection issue"
      tone="warning"
    />
  );

  assert.equal(countMarkupOccurrences(markup, "Host unavailable"), 1);
  assert.equal(countMarkupOccurrences(markup, "Timed out"), 1);
  assert.equal(countMarkupOccurrences(markup, "Connection issue"), 2);
  assertMarkupIncludes(markup, "border-[var(--status-warning-border)]");
});

test("feedback banners cover tone, dismissal, error alias, and empty-message paths", () => {
  assert.equal(
    renderMarkup(<FeedbackBanner message="" title="Ignored" tone="info" />),
    ""
  );

  const info = renderMarkup(
    <FeedbackBanner message="Details loaded" title="Info" tone="info" />
  );
  assertMarkupIncludes(info, "border-[var(--status-info-border)]");
  assertMarkupIncludes(info, "Details loaded");
  assertMarkupIncludes(info, "aria-label=\"Dismiss message\"");

  const success = renderMarkup(
    <FeedbackBanner message="Saved" title="Success" tone="success" />
  );
  assertMarkupIncludes(success, "border-[var(--status-success-border)]");

  const error = renderMarkup(
    <ErrorBanner message="Denied" title="Access issue" />
  );
  assertMarkupIncludes(error, "Denied");
  assertMarkupIncludes(error, "Access issue");
  assertMarkupIncludes(error, "text-[var(--status-danger-text)]");
});

test("error panel uses the default title and hides when no errors are active", () => {
  assert.equal(renderMarkup(<ErrorPanel errors={[undefined]} />), "");

  const markup = renderMarkup(<ErrorPanel errors={["Cannot connect"]} />);
  assertMarkupIncludes(markup, "Connection issue");
  assertMarkupIncludes(markup, "Cannot connect");
});
