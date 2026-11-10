import { useEffect, useId, useRef } from "react";
import type { ReactNode } from "react";

export default function Modal({
  title,
  closeLabel,
  onClose,
  busy = false,
  children,
}: {
  title: string;
  closeLabel: string;
  onClose: () => void;
  busy?: boolean;
  children: ReactNode;
}) {
  const dialog = useRef<HTMLDialogElement>(null);
  const titleId = useId();
  useEffect(() => {
    const trigger =
      document.activeElement instanceof HTMLElement
        ? document.activeElement
        : null;
    dialog.current?.showModal();
    dialog.current
      ?.querySelector<HTMLElement>(
        "input:not([type=checkbox]):not([disabled]), textarea:not([disabled]), select:not([disabled])",
      )
      ?.focus();
    const node = dialog.current;
    return () => {
      node?.close();
      if (trigger?.isConnected) trigger.focus();
    };
  }, []);
  return (
    <dialog
      className="app-modal"
      ref={dialog}
      aria-labelledby={titleId}
      onCancel={(event) => {
        event.preventDefault();
        if (!busy) onClose();
      }}
    >
      <header className="modal-heading">
        <h2 id={titleId}>{title}</h2>
        <button
          type="button"
          className="icon-button"
          aria-label={closeLabel}
          disabled={busy}
          onClick={onClose}
        >
          ×
        </button>
      </header>
      {children}
    </dialog>
  );
}
