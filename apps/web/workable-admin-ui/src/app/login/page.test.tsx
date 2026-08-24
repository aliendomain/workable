import test from "node:test";
import LoginPage from "@/app/login/page";
import {
  assertMarkupExcludes,
  assertMarkupIncludes,
  renderMarkup,
} from "@/test/render";

test("login page rejects unsafe next paths before passing them to Entra login", async () => {
  const protocolRelativeMarkup = await renderLoginPage({
    error: " Please sign in again. ",
    next: "//evil.test/admin",
  });
  const backslashMarkup = await renderLoginPage({
    next: "/\\evil.test/admin",
  });

  assertMarkupIncludes(protocolRelativeMarkup, "Session expired");
  assertMarkupIncludes(protocolRelativeMarkup, "Please sign in again.");
  assertMarkupIncludes(protocolRelativeMarkup, "/api/auth/entra/login?next=%2F");
  assertMarkupExcludes(protocolRelativeMarkup, "evil.test");
  assertMarkupIncludes(backslashMarkup, "/api/auth/entra/login?next=%2F");
  assertMarkupExcludes(backslashMarkup, "evil.test");
});

test("login page uses the first search parameter value and preserves safe next paths", async () => {
  const markup = await renderLoginPage({
    error: ["Denied.", "Ignored error."],
    next: ["/workers?state=Failed", "/ignored"],
    reason: ["unauthorized", "ignored"],
  });

  assertMarkupIncludes(markup, "Unauthorized");
  assertMarkupIncludes(markup, "Denied.");
  assertMarkupIncludes(markup, "/api/auth/entra/login?next=%2Fworkers%3Fstate%3DFailed");
  assertMarkupExcludes(markup, "Ignored error.");
  assertMarkupExcludes(markup, "/ignored");
});

async function renderLoginPage(
  searchParams: Record<string, string | string[] | undefined>
) {
  const originalConfigDisabled = process.env.WORKABLE_ADMIN_CONFIG_DISABLED;
  const originalAuthProvider = process.env.WORKABLE_ADMIN_UI_AUTH_PROVIDER;

  process.env.WORKABLE_ADMIN_CONFIG_DISABLED = "true";
  process.env.WORKABLE_ADMIN_UI_AUTH_PROVIDER = "entra";

  try {
    return renderMarkup(
      await LoginPage({
        searchParams: Promise.resolve(searchParams),
      })
    );
  } finally {
    restoreEnv("WORKABLE_ADMIN_CONFIG_DISABLED", originalConfigDisabled);
    restoreEnv("WORKABLE_ADMIN_UI_AUTH_PROVIDER", originalAuthProvider);
  }
}

function restoreEnv(key: string, value: string | undefined) {
  if (value === undefined) {
    delete process.env[key];
  } else {
    process.env[key] = value;
  }
}
