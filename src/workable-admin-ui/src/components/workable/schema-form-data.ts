import type { JsonSchemaNode } from "@/lib/workable";

export type FieldPath = Array<string | number>;

export function parseJsonSchema(json?: string | null): JsonSchemaNode | null {
  if (!json?.trim()) {
    return null;
  }

  try {
    const parsed = JSON.parse(json) as JsonSchemaNode;
    return typeof parsed === "object" && parsed ? parsed : null;
  } catch {
    return null;
  }
}

export function createDefaultValue(schema: JsonSchemaNode | null): unknown {
  if (!schema) {
    return undefined;
  }

  if ("default" in schema) {
    return schema.default;
  }

  if (schema.enum?.length) {
    return schema.enum[0];
  }

  const type = getSchemaType(schema);

  if (type === "object") {
    const properties = schema.properties ?? {};
    const entries = Object.entries(properties).map(([key, child]) => [
      key,
      createDefaultValue(child),
    ]);
    return Object.fromEntries(entries);
  }

  if (type === "array") {
    return [];
  }

  if (schema.format === "date") {
    return formatDateOnlyInputValue(new Date());
  }

  if (schema.format === "date-time") {
    return new Date().toISOString();
  }

  if (type === "boolean") {
    return false;
  }

  if (type === "integer" || type === "number") {
    return 0;
  }

  return "";
}

export function parseFieldPath(path: string): FieldPath {
  return path.split(".").filter(Boolean);
}

export function getSchemaAtPath(schema: JsonSchemaNode, path: FieldPath): JsonSchemaNode | null {
  let current: JsonSchemaNode | null = schema;

  for (const segment of path) {
    current = resolveNullableSchema(current);
    if (!current?.properties || typeof segment !== "string") {
      return null;
    }

    current = current.properties[segment] ?? null;
  }

  return resolveNullableSchema(current);
}

export function resolveNullableSchema(schema: JsonSchemaNode | null): JsonSchemaNode | null {
  if (!schema) {
    return null;
  }

  const nestedSchemas = [
    ...("anyOf" in schema && Array.isArray(schema.anyOf) ? schema.anyOf : []),
    ...("oneOf" in schema && Array.isArray(schema.oneOf) ? schema.oneOf : []),
  ] as JsonSchemaNode[];

  return nestedSchemas.find((option) => getSchemaType(option) !== "null") ?? schema;
}

export function isPathRequired(schema: JsonSchemaNode | null, path: FieldPath): boolean {
  if (!schema || path.length === 0) {
    return true;
  }

  let current: JsonSchemaNode | null = schema;

  for (const [index, segment] of path.entries()) {
    current = resolveNullableSchema(current);
    if (!current || typeof segment !== "string") {
      return false;
    }

    if (index === path.length - 1) {
      return current.required?.includes(segment) ?? false;
    }

    current = current.properties?.[segment] ?? null;
  }

  return false;
}

export function getValueAtPath(value: unknown, path: FieldPath): unknown {
  return path.reduce<unknown>((current, segment) => {
    if (!isRecord(current)) {
      return undefined;
    }

    return current[String(segment)];
  }, value);
}

export function setValueAtPath(value: unknown, path: FieldPath, nextValue: unknown): unknown {
  if (path.length === 0) {
    return nextValue;
  }

  const [head, ...tail] = path;
  const current = isRecord(value) ? value : {};

  return {
    ...current,
    [String(head)]: setValueAtPath(current[String(head)], tail, nextValue),
  };
}

export function compactJson(value: unknown) {
  return JSON.stringify(stripUndefined(value), null, 2);
}

export function getSchemaType(schema: JsonSchemaNode) {
  const types = Array.isArray(schema.type) ? schema.type : schema.type ? [schema.type] : [];

  if (schema.properties || schema.additionalProperties) {
    return "object";
  }

  if (schema.items) {
    return "array";
  }

  if (types.includes("boolean")) {
    return "boolean";
  }

  if (types.includes("integer")) {
    return "integer";
  }

  if (types.includes("number")) {
    return "number";
  }

  if (types.includes("object")) {
    return "object";
  }

  if (types.includes("array")) {
    return "array";
  }

  if (types.includes("null")) {
    return "null";
  }

  return "string";
}

export function isNullable(schema: JsonSchemaNode) {
  return Array.isArray(schema.type) && schema.type.includes("null");
}

export function parseEnumValue(schema: JsonSchemaNode, value: string) {
  return schema.enum?.find((option) => String(option) === value) ?? value;
}

export function inputTypeFor(schema: JsonSchemaNode) {
  if (schema.format === "date") {
    return "date";
  }

  if (schema.format === "date-time") {
    return "datetime-local";
  }

  if (schema.format === "uri") {
    return "url";
  }

  return "text";
}

export function formatInputValue(schema: JsonSchemaNode, value: unknown) {
  if (value === undefined || value === null) {
    return "";
  }

  if (schema.format === "date") {
    return String(value).slice(0, 10);
  }

  if (schema.format === "date-time") {
    const parsed = new Date(String(value));
    if (!Number.isNaN(parsed.getTime())) {
      return parsed.toISOString().slice(0, 16);
    }
  }

  return String(value);
}

export function formatStringValue(schema: JsonSchemaNode, value: string) {
  if (schema.format === "date-time" && value) {
    return new Date(value).toISOString();
  }

  return value;
}

export function formatDateOnlyInputValue(value: Date) {
  const year = value.getFullYear();
  const month = String(value.getMonth() + 1).padStart(2, "0");
  const day = String(value.getDate()).padStart(2, "0");

  return `${year}-${month}-${day}`;
}

export function placeholderFor(schema: JsonSchemaNode, label: string) {
  if (schema.format === "uri") {
    return "https://example.com/data.csv";
  }

  if (schema.format === "date") {
    return "yyyy-mm-dd";
  }

  if (schema.format === "date-time") {
    return "Select a date and time";
  }

  return label;
}

export function humanize(value: string) {
  return value
    .replace(/([a-z0-9])([A-Z])/g, "$1 $2")
    .replace(/[-_.]+/g, " ")
    .replace(/\b\w/g, (letter) => letter.toUpperCase());
}

export function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}

export function nextDictionaryKey(value: Record<string, unknown>) {
  let index = 1;
  let key = `key${index}`;

  while (key in value) {
    index += 1;
    key = `key${index}`;
  }

  return key;
}

export function stripUndefined(value: unknown): unknown {
  if (Array.isArray(value)) {
    return value.map(stripUndefined);
  }

  if (isRecord(value)) {
    return Object.fromEntries(
      Object.entries(value)
        .filter(([, entry]) => entry !== undefined)
        .map(([key, entry]) => [key, stripUndefined(entry)])
    );
  }

  return value;
}
