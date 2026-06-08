"use client";

import { Check, ListPlus, Minus, Plus, WandSparkles } from "lucide-react";
import type { ReactNode } from "react";
import {
  FormEmptyState,
  FormFieldHeader,
} from "@/components/features/console/form-controls";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from "@/components/ui/select";
import {
  Tooltip,
  TooltipContent,
  TooltipTrigger,
} from "@/components/ui/tooltip";
import {
  createDefaultValue,
  formatInputValue,
  formatStringValue,
  getSchemaAtPath,
  getSchemaType,
  getValueAtPath,
  humanize,
  inputTypeFor,
  isNullable,
  isPathRequired,
  isRecord,
  nextDictionaryKey,
  parseEnumValue,
  parseFieldPath,
  placeholderFor,
  setValueAtPath,
  type FieldPath,
} from "@/components/workable/schema-form-data";
import { cn } from "@/lib/utils";
import type { JsonSchemaNode } from "@/lib/workable";

export {
  compactJson,
  createDefaultValue,
  parseJsonSchema,
} from "@/components/workable/schema-form-data";

type SchemaFormProps = {
  schema: JsonSchemaNode | null;
  value: unknown;
  onChange: (value: unknown) => void;
};

type SchemaPathFieldProps = {
  description?: string;
  label?: string;
  onChange: (value: unknown) => void;
  path: string;
  schema: JsonSchemaNode | null;
  value: unknown;
};

export function SchemaForm({ schema, value, onChange }: SchemaFormProps) {
  if (!schema) {
    return (
      <FormEmptyState>
        This definition does not expose a typed input schema.
      </FormEmptyState>
    );
  }

  return (
    <div className="space-y-4">
      <SchemaField
        name="input"
        onChange={onChange}
        path={[]}
        required
        schema={schema}
        value={value}
      />
    </div>
  );
}

export function SchemaPathField({
  description,
  label,
  onChange,
  path,
  schema,
  value,
}: SchemaPathFieldProps) {
  const segments = parseFieldPath(path);
  const fieldSchema = schema ? getSchemaAtPath(schema, segments) : null;

  if (!fieldSchema) {
    return (
      <FormEmptyState padding="compact">
        {path}
      </FormEmptyState>
    );
  }

  return (
    <div className="w-full max-w-md">
      <SchemaField
        name={label ?? String(segments.at(-1) ?? path)}
        onChange={(next) => onChange(setValueAtPath(value, segments, next))}
        path={segments}
        required={isPathRequired(schema, segments)}
        schema={{
          ...fieldSchema,
          description: description ?? fieldSchema.description,
          title: label ?? fieldSchema.title,
        }}
        value={getValueAtPath(value, segments)}
      />
    </div>
  );
}

function SchemaField({
  name,
  onChange,
  path,
  required,
  schema,
  value,
}: {
  name: string;
  onChange: (value: unknown) => void;
  path: FieldPath;
  required: boolean;
  schema: JsonSchemaNode;
  value: unknown;
}) {
  const type = getSchemaType(schema);
  const label = schema.title ?? humanize(name);
  const nullable = isNullable(schema);

  if (schema.enum?.length) {
    return (
      <FieldShell name={label} nullable={nullable} path={path} required={required} schema={schema}>
        <Select
          onValueChange={(next) => onChange(parseEnumValue(schema, next))}
          value={value === undefined || value === null ? "" : String(value)}
        >
          <SelectTrigger className="w-full">
            <SelectValue placeholder={`Select ${label}`} />
          </SelectTrigger>
          <SelectContent>
            {schema.enum.map((option) => (
              <SelectItem key={String(option)} value={String(option)}>
                {String(option)}
              </SelectItem>
            ))}
          </SelectContent>
        </Select>
      </FieldShell>
    );
  }

  if (type === "object") {
    return (
      <ObjectField
        name={label}
        nullable={nullable}
        onChange={onChange}
        path={path}
        required={required}
        schema={schema}
        value={value}
      />
    );
  }

  if (type === "array") {
    return (
      <ArrayField
        name={label}
        nullable={nullable}
        onChange={onChange}
        path={path}
        required={required}
        schema={schema}
        value={value}
      />
    );
  }

  if (type === "boolean") {
    const checked = Boolean(value);

    return (
      <FieldShell name={label} nullable={nullable} path={path} required={required} schema={schema}>
        <div className="inline-flex rounded-lg border bg-background p-1">
          <Button
            onClick={() => onChange(true)}
            size="sm"
            type="button"
            variant={checked ? "secondary" : "ghost"}
          >
            {checked && <Check className="size-3.5" />}
            True
          </Button>
          <Button
            onClick={() => onChange(false)}
            size="sm"
            type="button"
            variant={!checked ? "secondary" : "ghost"}
          >
            {!checked && <Check className="size-3.5" />}
            False
          </Button>
        </div>
      </FieldShell>
    );
  }

  if (type === "integer" || type === "number") {
    return (
      <FieldShell name={label} nullable={nullable} path={path} required={required} schema={schema}>
        <Input
          inputMode="decimal"
          onChange={(event) => {
            const next = event.target.value;
            onChange(next === "" ? undefined : Number(next));
          }}
          step={type === "integer" ? 1 : "any"}
          type="number"
          value={typeof value === "number" ? String(value) : ""}
        />
      </FieldShell>
    );
  }

  return (
    <FieldShell name={label} nullable={nullable} path={path} required={required} schema={schema}>
      <Input
        onChange={(event) => onChange(formatStringValue(schema, event.target.value))}
        placeholder={placeholderFor(schema, label)}
        type={inputTypeFor(schema)}
        value={formatInputValue(schema, value)}
      />
    </FieldShell>
  );
}

function ObjectField({
  name,
  nullable,
  onChange,
  path,
  required,
  schema,
  value,
}: {
  name: string;
  nullable: boolean;
  onChange: (value: unknown) => void;
  path: FieldPath;
  required: boolean;
  schema: JsonSchemaNode;
  value: unknown;
}) {
  const objectValue = isRecord(value) ? value : {};
  const properties = schema.properties ?? {};
  const propertyEntries = Object.entries(properties);

  if (propertyEntries.length === 0 && schema.additionalProperties) {
    return (
      <DictionaryField
        name={name}
        nullable={nullable}
        onChange={onChange}
        path={path}
        required={required}
        schema={schema}
        value={objectValue}
      />
    );
  }

  return (
    <section className={cn("min-w-0 space-y-4", path.length > 0 && "rounded-lg border p-4")}>
      {path.length > 0 && (
        <FieldHeader name={name} nullable={nullable} path={path} required={required} schema={schema} />
      )}
      <div
        className={cn(
          "grid min-w-0 gap-4",
          path.length === 0 ? "lg:grid-cols-2" : "grid-cols-1"
        )}
      >
        {propertyEntries.map(([key, child]) => {
          const nextRequired = schema.required?.includes(key) ?? false;

          return (
            <SchemaField
              key={key}
              name={key}
              onChange={(next) => {
                onChange({
                  ...objectValue,
                  [key]: next,
                });
              }}
              path={[...path, key]}
              required={nextRequired}
              schema={child}
              value={objectValue[key]}
            />
          );
        })}
      </div>
    </section>
  );
}

function ArrayField({
  name,
  nullable,
  onChange,
  path,
  required,
  schema,
  value,
}: {
  name: string;
  nullable: boolean;
  onChange: (value: unknown) => void;
  path: FieldPath;
  required: boolean;
  schema: JsonSchemaNode;
  value: unknown;
}) {
  const items = Array.isArray(value) ? value : [];
  const itemSchema = schema.items ?? { type: "string" };

  return (
    <section className="min-w-0 space-y-3 rounded-lg border p-4">
      <div className="flex items-center justify-between gap-3">
        <FieldHeader name={name} nullable={nullable} path={path} required={required} schema={schema} />
        <Button
          onClick={() => onChange([...items, createDefaultValue(itemSchema)])}
          size="sm"
          type="button"
          variant="outline"
        >
          <ListPlus className="size-4" />
          Add
        </Button>
      </div>
      {items.length === 0 ? (
        <FormEmptyState className="rounded-md" padding="compact">
          No items.
        </FormEmptyState>
      ) : (
        <div className="space-y-3">
          {items.map((item, index) => (
            <div className="min-w-0 rounded-lg border bg-muted/20 p-3" key={index}>
              <div className="mb-3 flex items-center justify-between gap-3">
                <span className="text-muted-foreground text-sm">Item {index + 1}</span>
                <Button
                  onClick={() => onChange(items.filter((_, itemIndex) => itemIndex !== index))}
                  size="icon-sm"
                  type="button"
                  variant="ghost"
                >
                  <Minus className="size-4" />
                  <span className="sr-only">{`Remove item ${index + 1}`}</span>
                </Button>
              </div>
              <SchemaField
                name={`${name} ${index + 1}`}
                onChange={(next) => {
                  const copy = [...items];
                  copy[index] = next;
                  onChange(copy);
                }}
                path={[...path, index]}
                required
                schema={itemSchema}
                value={item}
              />
            </div>
          ))}
        </div>
      )}
    </section>
  );
}

function DictionaryField({
  name,
  nullable,
  onChange,
  path,
  required,
  schema,
  value,
}: {
  name: string;
  nullable: boolean;
  onChange: (value: unknown) => void;
  path: FieldPath;
  required: boolean;
  schema: JsonSchemaNode;
  value: Record<string, unknown>;
}) {
  const entries = Object.entries(value);
  const valueSchema =
    typeof schema.additionalProperties === "object"
      ? schema.additionalProperties
      : ({ type: "string" } satisfies JsonSchemaNode);

  return (
    <section className="space-y-3 rounded-lg border p-4">
      <div className="flex items-center justify-between gap-3">
        <FieldHeader name={name} nullable={nullable} path={path} required={required} schema={schema} />
        <Button
          onClick={() => {
            const key = nextDictionaryKey(value);
            onChange({ ...value, [key]: createDefaultValue(valueSchema) });
          }}
          size="sm"
          type="button"
          variant="outline"
        >
          <Plus className="size-4" />
          Add
        </Button>
      </div>
      {entries.length === 0 ? (
        <FormEmptyState className="rounded-md" padding="compact">
          No key-value pairs.
        </FormEmptyState>
      ) : (
        <div className="space-y-3">
          {entries.map(([key, entryValue]) => (
            <div className="grid gap-3 md:grid-cols-[minmax(0,12rem)_1fr_auto]" key={key}>
              <Input
                aria-label="Dictionary key"
                onChange={(event) => {
                  const nextKey = event.target.value;
                  const copy = { ...value };
                  delete copy[key];
                  copy[nextKey] = entryValue;
                  onChange(copy);
                }}
                value={key}
              />
              <SchemaField
                name={key}
                onChange={(next) => onChange({ ...value, [key]: next })}
                path={[...path, key]}
                required
                schema={valueSchema}
                value={entryValue}
              />
              <Button
                onClick={() => {
                  const copy = { ...value };
                  delete copy[key];
                  onChange(copy);
                }}
                size="icon-sm"
                type="button"
                variant="ghost"
              >
                <Minus className="size-4" />
                <span className="sr-only">{`Remove ${key}`}</span>
              </Button>
            </div>
          ))}
        </div>
      )}
    </section>
  );
}

function FieldShell({
  children,
  name,
  nullable,
  path,
  required,
  schema,
}: {
  children: ReactNode;
  name: string;
  nullable: boolean;
  path: FieldPath;
  required: boolean;
  schema: JsonSchemaNode;
}) {
  return (
    <div className="grid min-w-0 gap-2">
      {path.length > 0 && (
        <FieldHeader name={name} nullable={nullable} path={path} required={required} schema={schema} />
      )}
      {children}
    </div>
  );
}

function FieldHeader({
  name,
  nullable,
  path,
  required,
  schema,
}: {
  name: string;
  nullable: boolean;
  path: FieldPath;
  required: boolean;
  schema: JsonSchemaNode;
}) {
  const type = getSchemaType(schema);
  const description = schema.description;
  const details = [
    `Type: ${type}`,
    required ? "Required" : "Optional",
    nullable ? "Nullable" : undefined,
    path.length > 0 ? `Path: ${path.join(".")}` : undefined,
  ].filter(Boolean);

  return (
    <FormFieldHeader
      description={description}
      details={(
        <div className="space-y-0.5 leading-snug">
          {details.map((detail) => (
            <div className="break-all font-mono" key={detail}>
              {detail}
            </div>
          ))}
        </div>
      )}
      label={name}
    />
  );
}

export function SchemaPresetButton({
  schema,
  onApply,
}: {
  schema: JsonSchemaNode | null;
  onApply: (value: unknown) => void;
}) {
  return (
    <Tooltip delayDuration={250}>
      <TooltipTrigger asChild>
        <Button
          disabled={!schema}
          onClick={() => onApply(createDefaultValue(schema))}
          size="sm"
          type="button"
          variant="outline"
        >
          <WandSparkles className="size-4" />
          Use input defaults
        </Button>
      </TooltipTrigger>
      <TooltipContent side="top" sideOffset={6}>
        Fill the input form with the default values declared by this work&apos;s input schema.
      </TooltipContent>
    </Tooltip>
  );
}
