import type { Translate } from "../../i18n";

export type SyncState = "synced" | "pending" | "saving" | "error";

export default function BotSyncStatus({
  state,
  t,
}: {
  state: SyncState;
  t: Translate;
}) {
  const label = t(`sync.${state}`);
  return (
    <div
      className={`card-sync is-${state}`}
      role="status"
      aria-live="polite"
      aria-atomic="true"
      title={t("sync.assistant_status", { status: label })}
    >
      <span className="sync-symbol" aria-hidden="true">
        <svg
          viewBox="0 0 32 32"
          fill="none"
          stroke="currentColor"
          strokeWidth="1.6"
          strokeLinecap="round"
          strokeLinejoin="round"
        >
          <path d="M8 24a6 6 0 0 1-1-11.9A9 9 0 0 1 24 10a7 7 0 0 1 .5 14" />
          {state === "synced" ? (
            <path d="m11 22 4 4 7-8" />
          ) : state === "error" ? (
            <>
              <path d="m13 19 8 8m0-8-8 8" />
            </>
          ) : state === "pending" ? (
            <>
              <circle cx="17" cy="22" r="6" />
              <path d="M17 18v4l3 2" />
            </>
          ) : (
            <g className="sync-arrows">
              <path d="M11 21a6 6 0 0 1 10-3l2 2m0-4v4h-4M23 25a6 6 0 0 1-10 3l-2-2m0 4v-4h4" />
            </g>
          )}
        </svg>
      </span>
      <span className="sync-label">{label}</span>
    </div>
  );
}
