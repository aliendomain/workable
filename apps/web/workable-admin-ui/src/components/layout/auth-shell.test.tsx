import test from "node:test";
import { AuthShell } from "@/components/layout/auth-shell";
import {
  assertMarkupIncludes,
  renderMarkup,
} from "@/test/render";

test("auth shell renders the centered auth layout and supports section class override", () => {
  const markup = renderMarkup(
    <AuthShell className="custom-auth-section">
      <form>Sign in</form>
    </AuthShell>
  );

  assertMarkupIncludes(markup, "<main");
  assertMarkupIncludes(markup, "min-h-svh");
  assertMarkupIncludes(markup, "bg-background");
  assertMarkupIncludes(markup, "custom-auth-section");
  assertMarkupIncludes(markup, "Sign in");
});
