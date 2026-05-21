import { LoginForm } from "./login-form";
import { getAdminAuthProvider } from "@/lib/admin-security";

type LoginPageProps = {
  searchParams: Promise<Record<string, string | string[] | undefined>>;
};

export default async function LoginPage({ searchParams }: LoginPageProps) {
  const params = await searchParams;
  return (
    <LoginForm
      authProvider={getAdminAuthProvider()}
      initialError={normalizeError(params.error)}
      nextPath={normalizeNextPath(params.next)}
    />
  );
}

function normalizeNextPath(value: string | string[] | undefined) {
  const candidate = Array.isArray(value) ? value[0] : value;
  if (!candidate?.startsWith("/") || candidate.startsWith("//")) {
    return "/";
  }

  return candidate;
}

function normalizeError(value: string | string[] | undefined) {
  const candidate = Array.isArray(value) ? value[0] : value;
  return candidate?.trim() || null;
}
