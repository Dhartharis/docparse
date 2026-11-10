import { useEffect, useRef, useState } from "react";
import type { MessageKey, Translate } from "../i18n";
import { api, ApiError } from "../services/api";
import Modal from "./Modal";
export default function TrashAction({
  path,
  name,
  label,
  help = "trash.move_help",
  t,
  disabled = false,
  onMoved,
  onBusyChange,
}: {
  path: string;
  name: string;
  label: MessageKey;
  help?: MessageKey;
  t: Translate;
  disabled?: boolean;
  onMoved: () => void;
  onBusyChange: (v: boolean) => void;
}) {
  const [open, setOpen] = useState(false);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<MessageKey | null>(null);
  const life = useRef<AbortController | null>(null);
  useEffect(() => {
    const controller = new AbortController();
    life.current = controller;
    return () => controller.abort();
  }, []);
  async function move() {
    if (busy) return;
    setBusy(true);
    onBusyChange(true);
    setError(null);
    const signal = life.current!.signal;
    try {
      await api(path, undefined, signal, "DELETE");
      if (!signal.aborted) {
        setOpen(false);
        onMoved();
      }
    } catch (e) {
      if (!signal.aborted)
        setError(
          e instanceof ApiError
            ? (`errors.${e.code}` as MessageKey)
            : "errors.server",
        );
    } finally {
      if (!signal.aborted) setBusy(false);
      onBusyChange(false);
    }
  }
  return (
    <>
      <button
        type="button"
        className="trash-trigger"
        disabled={disabled || busy}
        onClick={() => {
          setError(null);
          setOpen(true);
        }}
      >
        {t(label)}
      </button>
      {open && (
        <Modal
          title={t("trash.confirm_title", { name })}
          closeLabel={t("common.close")}
          busy={busy}
          onClose={() => setOpen(false)}
        >
          <p className="modal-intro">{t(help)}</p>
          {error && (
            <p role="alert" className="workspace-alert">
              {t(error)}
            </p>
          )}
          <button
            type="button"
            className="secondary-button"
            disabled={busy}
            onClick={() => void move()}
          >
            {t("trash.confirm")}
          </button>
        </Modal>
      )}
    </>
  );
}
