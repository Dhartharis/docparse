import { useEffect } from "react";
import { playPortalEntry } from "./portal-entry";

export default function PortalEntry({ active }: { active: boolean }) {
  useEffect(() => {
    if (active) return playPortalEntry();
  }, [active]);
  return null;
}
