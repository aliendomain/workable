import Image from "next/image";

export function WorkableLogo({
  className = "h-14 w-auto object-contain",
  priority = false,
}: {
  className?: string;
  priority?: boolean;
}) {
  return (
    <Image
      alt="Workable"
      className={className}
      height={70}
      priority={priority}
      src="/workable-logo-transparent.png"
      width={280}
    />
  );
}
