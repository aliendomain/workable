import { createHash, createHmac, randomBytes, timingSafeEqual } from "node:crypto";

export function randomBase64Url(byteLength = 32) {
  return randomBytes(byteLength).toString("base64url");
}

export function sha256Base64Url(value: string) {
  return createHash("sha256").update(value).digest("base64url");
}

export function sign(payload: string, secret: string) {
  return createHmac("sha256", secret).update(payload).digest("base64url");
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
