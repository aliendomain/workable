import { LoginForm } from "./login-form";
import { getAdminAuthProvider } from "@/lib/admin-security";
import { normalizeAdminReturnPath } from "@/lib/admin-security/return-path";

type LoginPageProps = {
  searchParams: Promise<Record<string, string | string[] | undefined>>;
};

export default async function LoginPage({ searchParams }: LoginPageProps) {
  const params = await searchParams;
  return (
    <LoginForm
      authProvider={getAdminAuthProvider()}
      initialError={normalizeError(params.error)}
      initialReason={normalizeReason(params.reason)}
      nextPath={normalizeAdminReturnPath(firstValue(params.next))}
    />
  );
}

function normalizeError(value: string | string[] | undefined) {
  const candidate = firstValue(value);
  return candidate?.trim() || null;
}

function normalizeReason(value: string | string[] | undefined) {
  const candidate = firstValue(value);
  return candidate === "unauthorized" ? candidate : null;
}

function firstValue(value: string | string[] | undefined) {
  return Array.isArray(value) ? value[0] : value;
}
