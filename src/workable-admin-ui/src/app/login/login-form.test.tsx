import test from "node:test";
import { LoginForm } from "@/app/login/login-form";
import {
  assertMarkupExcludes,
  assertMarkupIncludes,
  renderMarkup,
} from "@/test/render";

test("basic login form renders credential fields, submit action, and unauthorized error state", () => {
  const markup = renderMarkup(
    <LoginForm
      authProvider="basic"
      initialError="Unauthorized user."
      initialReason="unauthorized"
      nextPath="/workable?tab=overview"
    />
  );

  assertMarkupIncludes(markup, "Username");
  assertMarkupIncludes(markup, "Password");
  assertMarkupIncludes(markup, "autoComplete=\"username\"");
  assertMarkupIncludes(markup, "autoComplete=\"current-password\"");
  assertMarkupIncludes(markup, "Unauthorized");
  assertMarkupIncludes(markup, "Unauthorized user.");
  assertMarkupIncludes(markup, "Sign in");
  assertMarkupExcludes(markup, "Sign in with Microsoft");
});

test("entra login form renders Microsoft sign-in link and session-expired error title", () => {
  const markup = renderMarkup(
    <LoginForm
      authProvider="entra"
      initialError="Please sign in again."
      initialReason={null}
      nextPath="/workable/systems?name=Ops"
    />
  );

  assertMarkupIncludes(markup, "Session expired");
  assertMarkupIncludes(markup, "Please sign in again.");
  assertMarkupIncludes(markup, "Sign in with Microsoft");
  assertMarkupIncludes(markup, "/api/auth/entra/login?next=%2Fworkable%2Fsystems%3Fname%3DOps");
  assertMarkupExcludes(markup, "Username");
  assertMarkupExcludes(markup, "Password");
});
