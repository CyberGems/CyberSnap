import { iconMap, type IconComponent, type IconName } from "@/lib/icon-map";

export type { IconComponent, IconName } from "@/lib/icon-map";

export function useIcon(name: IconName): IconComponent {
  return iconMap[name];
}
