import { useEffect, useId, useRef, useState } from "react";

export default function HelpPopover({ label, children }: {
  label: string;
  children: React.ReactNode;
}) {
  const id = useId();
  const trigger = useRef<HTMLButtonElement>(null);
  const panel = useRef<HTMLDivElement>(null);
  const pinned = useRef(false);
  const dismissTimer = useRef<ReturnType<typeof setTimeout> | undefined>(undefined);
  const [expanded, setExpanded] = useState(false);

  function cancelDismiss() { clearTimeout(dismissTimer.current); }
  function position() {
    if (!panel.current?.matches(":popover-open") || !trigger.current) return;
    const anchor = trigger.current.getBoundingClientRect();
    const box = panel.current.getBoundingClientRect();
    panel.current.style.left = `${Math.max(16, Math.min(anchor.left, innerWidth - box.width - 16))}px`;
    panel.current.style.top = `${Math.max(16, anchor.bottom + 8 + box.height <= innerHeight - 16 ? anchor.bottom + 8 : anchor.top - box.height - 8)}px`;
  }
  function show() {
    cancelDismiss();
    panel.current?.showPopover();
    position();
  }
  function dismissUnlessPinned() {
    cancelDismiss();
    dismissTimer.current = setTimeout(() => {
      if (!pinned.current) panel.current?.hidePopover();
    }, 120);
  }
  useEffect(() => {
    window.addEventListener("resize", position);
    window.addEventListener("scroll", position, true);
    return () => {
      clearTimeout(dismissTimer.current);
      window.removeEventListener("resize", position);
      window.removeEventListener("scroll", position, true);
    };
  }, []);

  return (
    <span className="help-popover">
      <button
        ref={trigger}
        type="button"
        className="help-popover-trigger"
        aria-label={label}
        aria-expanded={expanded}
        aria-controls={id}
        aria-describedby={expanded ? id : undefined}
        onPointerEnter={(event) => { if (event.pointerType === "mouse") show(); }}
        onPointerLeave={dismissUnlessPinned}
        onFocus={show}
        onBlur={dismissUnlessPinned}
        onClick={() => {
          cancelDismiss();
          if (pinned.current) {
            pinned.current = false;
            panel.current?.hidePopover();
          } else {
            pinned.current = true;
            show();
          }
        }}
      >
        <svg viewBox="0 0 24 24" width="20" height="20" fill="none" stroke="currentColor" strokeWidth="1.5" aria-hidden="true">
          <circle cx="12" cy="12" r="9" />
          <path d="M12 7v6" strokeLinecap="round" />
          <circle cx="12" cy="17" r=".8" fill="currentColor" stroke="none" />
        </svg>
      </button>
      <div
        ref={panel}
        id={id}
        popover="auto"
        role="tooltip"
        className="help-popover-panel"
        onPointerEnter={cancelDismiss}
        onPointerLeave={dismissUnlessPinned}
        onToggle={(event) => {
          const open = event.newState === "open";
          setExpanded(open);
          if (!open) pinned.current = false;
        }}
      >
        {children}
      </div>
    </span>
  );
}
