import {
  createCipheriv,
  createDecipheriv,
  createHash,
  createHmac,
  randomBytes,
  scryptSync,
  timingSafeEqual,
} from "node:crypto";

const SECRET_BOUND_VALUE_BYTES = 32;
const BASIC_CREDENTIAL_BINDING_PURPOSE =
  "workable.admin.basic-credential-binding.v1";

let cachedSecretBoundValue: {
  secret: string;
  value: string;
  derived: string;
} | undefined;

export function randomBase64Url(byteLength = 32) {
  return randomBytes(byteLength).toString("base64url");
}

export function sha256Base64Url(value: string) {
  return createHash("sha256").update(value).digest("base64url");
}

export function sign(payload: string, secret: string) {
  return createHmac("sha256", secret).update(payload).digest("base64url");
}

export function deriveBasicCredentialBinding(
  value: string,
  secret: string
) {
  if (cachedSecretBoundValue?.secret === secret &&
    cachedSecretBoundValue.value === value) {
    return cachedSecretBoundValue.derived;
  }

  const derived = scryptSync(
    value,
    `${BASIC_CREDENTIAL_BINDING_PURPOSE}\0${secret}`,
    SECRET_BOUND_VALUE_BYTES
  ).toString("base64url");
  cachedSecretBoundValue = { secret, value, derived };
  return derived;
}

export function base64UrlEncode(value: string) {
  return Buffer.from(value, "utf8").toString("base64url");
}

export function base64UrlDecode(value: string) {
  return Buffer.from(value, "base64url").toString("utf8");
}

export function constantTimeEquals(actual: string, expected: string) {
  const actualBuffer = Buffer.from(actual);
  const expectedBuffer = Buffer.from(expected);
  return actualBuffer.length === expectedBuffer.length &&
    timingSafeEqual(actualBuffer, expectedBuffer);
}

export function encrypt(value: string, secret: string, purpose: string) {
  const iv = randomBytes(12);
  const cipher = createCipheriv("aes-256-gcm", deriveEncryptionKey(secret, purpose), iv);
  const encrypted = Buffer.concat([
    cipher.update(value, "utf8"),
    cipher.final(),
  ]);
  const tag = cipher.getAuthTag();
  return `${iv.toString("base64url")}.${encrypted.toString("base64url")}.${tag.toString("base64url")}`;
}

export function decrypt(value: string, secret: string, purpose: string) {
  const [iv, encrypted, tag] = value.split(".");
  if (!iv || !encrypted || !tag) {
    throw new Error("Encrypted value is not valid.");
  }

  const decipher = createDecipheriv(
    "aes-256-gcm",
    deriveEncryptionKey(secret, purpose),
    Buffer.from(iv, "base64url")
  );
  decipher.setAuthTag(Buffer.from(tag, "base64url"));
  return Buffer.concat([
    decipher.update(Buffer.from(encrypted, "base64url")),
    decipher.final(),
  ]).toString("utf8");
}

function deriveEncryptionKey(secret: string, purpose: string) {
  return createHash("sha256")
    .update("workable-admin-ui")
    .update("\0")
    .update(purpose)
    .update("\0")
    .update(secret)
    .digest();
}
