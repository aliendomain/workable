"use client";

import { Skeleton } from "@/components/ui/skeleton";
import { cn } from "@/lib/utils";

export function StackedSkeleton({
  className,
  count,
  itemClassName,
}: {
  className?: string;
  count: number;
  itemClassName?: string;
}) {
  return (
    <div className={cn("space-y-3", className)}>
      {Array.from({ length: count }).map((_, index) => (
        <Skeleton className={cn("h-10 w-full", itemClassName)} key={index} />
      ))}
    </div>
  );
}
