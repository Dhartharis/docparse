import { useCallback, useEffect, useId, useRef, useState } from "react";
import { ApiError } from "../../services/api";
import TrashAction from "../../components/TrashAction";
import { knowledge } from "../../services/knowledge";
import type {
  DocumentDetails,
  DocumentSummary,
  KnowledgeBase,
} from "../../services/knowledge";
import type { Language, MessageKey, Translate } from "../../i18n";

const errorKey = (failure: unknown): MessageKey =>
  failure instanceof ApiError
    ? (`errors.${failure.code}` as MessageKey)
    : "errors.server";
export default function DocumentsPanel({
  organizationId,
  base,
  t,
  language,
  onBusyChange,
  disabled,
}: {
  organizationId: string;
  base: KnowledgeBase;
  t: Translate;
  language: Language;
  onBusyChange: (value: boolean) => void;
  disabled: boolean;
}) {
  const [documents, setDocuments] = useState<DocumentSummary[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<MessageKey | null>(null);
  const [uploadError, setUploadError] = useState<MessageKey | null>(null);
  const [uploading, setUploading] = useState(false);
  const [retrying, setRetrying] = useState<string | null>(null);
  const [uploadedName, setUploadedName] = useState<string | null>(null);
  const [queue, setQueue] = useState<
    { file: File; status: "pending" | "uploading" | "done" | "error" }[]
  >([]);
  const uploadLock = useRef(false);
  const pollTimer = useRef<ReturnType<typeof setTimeout> | null>(null);
  const [dragging, setDragging] = useState(false);
  const fileId = useId();
  const helpId = useId();
  const [selected, setSelected] = useState("");
  const [detail, setDetail] = useState<DocumentDetails | null>(null);
  const [detailLoading, setDetailLoading] = useState(false);
  const [detailError, setDetailError] = useState<MessageKey | null>(null);
  const [downloading, setDownloading] = useState(false);
  const [downloadError, setDownloadError] = useState<MessageKey | null>(null);
  const lifecycle = useRef<AbortController | null>(null);
  const detailRequest = useRef<AbortController | null>(null);
  const input = useRef<HTMLInputElement>(null);
  const detailHeading = useRef<HTMLHeadingElement>(null);
  const detailTrigger = useRef<HTMLButtonElement | null>(null);
  const date = (value: string) =>
    new Intl.DateTimeFormat(language, {
      dateStyle: "medium",
      timeStyle: "short",
    }).format(new Date(value));
  const type = (value: string) => {
    const key = `knowledge.type_${value.toLowerCase()}` as MessageKey;
    return ["Invoice", "Receipt", "Contract", "Unknown", "Document"].includes(
      value,
    )
      ? t(key)
      : value;
  };
  const load = useCallback(
    async (signal: AbortSignal, silent = false) => {
      if (!silent) setLoading(true);
      try {
        const value = await knowledge.documents(
          organizationId,
          base.id,
          signal,
        );
        if (!signal.aborted) {
          setDocuments(value);
          setError(null);
        }
        return value;
      } catch (failure) {
        if (!signal.aborted) setError(errorKey(failure));
      } finally {
        if (!signal.aborted) setLoading(false);
      }
    },
    [organizationId, base.id],
  );
  useEffect(() => {
    const controller = new AbortController();
    lifecycle.current = controller;
    void Promise.resolve().then(() => {
      if (!controller.signal.aborted) return load(controller.signal);
    });
    return () => {
      controller.abort();
      detailRequest.current?.abort();
    };
  }, [load]);
  const hasPending = documents.some(
    (document) =>
      document.processingStatus === "queued" ||
      document.processingStatus === "processing",
  );
  useEffect(() => {
    if (!hasPending) return;
    const controller = new AbortController();
    async function poll() {
      if (document.visibilityState === "visible")
        await load(controller.signal, true);
      if (!controller.signal.aborted)
        pollTimer.current = setTimeout(() => void poll(), 3000);
    }
    pollTimer.current = setTimeout(() => void poll(), 1500);
    return () => {
      controller.abort();
      if (pollTimer.current) clearTimeout(pollTimer.current);
    };
  }, [hasPending, load]);
  async function retryDocument(document: DocumentSummary) {
    if (retrying) return;
    const signal = lifecycle.current!.signal;
    setRetrying(document.id);
    try {
      await knowledge.retry(organizationId, base.id, document.id, signal);
      if (!signal.aborted) await load(signal, true);
    } catch (failure) {
      if (!signal.aborted) setError(errorKey(failure));
    } finally {
      if (!signal.aborted) setRetrying(null);
    }
  }
  async function open(document: DocumentSummary, trigger?: HTMLButtonElement) {
    detailRequest.current?.abort();
    const controller = new AbortController();
    detailRequest.current = controller;
    if (trigger) detailTrigger.current = trigger;
    setSelected(document.id);
    setDetail(null);
    setDetailLoading(true);
    setDetailError(null);
    setDownloadError(null);
    try {
      const value = await knowledge.detail(
        organizationId,
        base.id,
        document.id,
        controller.signal,
      );
      if (!controller.signal.aborted) {
        setDetail(value);
        requestAnimationFrame(() => detailHeading.current?.focus());
      }
    } catch (failure) {
      if (!controller.signal.aborted) setDetailError(errorKey(failure));
    } finally {
      if (!controller.signal.aborted) setDetailLoading(false);
    }
  }
  function chooseFiles(values: File[]) {
    setUploadError(null);
    setQueue([]);
    setUploadedName(null);
    if (
      values.length > 10 ||
      values.some(
        (value) =>
          !/\.(pdf|png|jpe?g)$/i.test(value.name) ||
          value.size === 0 ||
          value.size > 10 * 1024 * 1024,
      )
    ) {
      setUploadError(
        values.length > 10 ? "knowledge.too_many" : "knowledge.file_invalid",
      );
      if (input.current) input.current.value = "";
      return;
    }
    if (values.length) void upload(values);
  }
  async function upload(values: File[]) {
    if (!values.length || uploadLock.current || disabled) return;
    uploadLock.current = true;
    setQueue(values.map((file) => ({ file, status: "pending" })));
    const signal = lifecycle.current!.signal;
    setUploading(true);
    setUploadError(null);
    setUploadedName(null);
    onBusyChange(true);
    try {
      for (const [index, file] of values.entries()) {
        setQueue((previous) =>
          previous.map((item, row) =>
            row === index ? { ...item, status: "uploading" } : item,
          ),
        );
        await knowledge.upload(organizationId, base.id, file, signal);
        if (signal.aborted) return;
        setUploadedName(file.name);
        setQueue((previous) =>
          previous.map((item, row) =>
            row === index ? { ...item, status: "done" } : item,
          ),
        );
      }
      if (input.current) input.current.value = "";
      setQueue([]);
      await load(signal, true);
    } catch (failure) {
      if (!signal.aborted) {
        setUploadError(errorKey(failure));
        setQueue((previous) =>
          previous.map((item) =>
            item.status === "uploading" ? { ...item, status: "error" } : item,
          ),
        );
        await load(signal);
      }
    } finally {
      if (!signal.aborted) {
        uploadLock.current = false;
        setUploading(false);
        onBusyChange(false);
      }
    }
  }
  async function download(format: "file" | "export/csv") {
    if (!detail || downloading) return;
    const signal = lifecycle.current!.signal;
    setDownloading(true);
    setDownloadError(null);
    try {
      await knowledge.download(
        organizationId,
        base.id,
        detail.document,
        format,
        signal,
      );
    } catch (failure) {
      if (!signal.aborted) setDownloadError(errorKey(failure));
    } finally {
      if (!signal.aborted) setDownloading(false);
    }
  }
  return (
    <div className="documents-panel">
      <div className="documents-heading">
        <div>
          <p className="eyebrow">{t("knowledge.documents")}</p>
          <h3>{base.name}</h3>
        </div>
        <button
          type="button"
          className="text-button"
          disabled={uploading || loading}
          onClick={() => void load(lifecycle.current!.signal)}
        >
          {t("knowledge.refresh")}
        </button>
      </div>
      <div
        className={`document-upload${dragging ? " is-dragging" : ""}`}
        onDragOver={(event) => {
          event.preventDefault();
          if (!uploading && !disabled) setDragging(true);
        }}
        onDragLeave={(event) => {
          if (!event.currentTarget.contains(event.relatedTarget as Node | null))
            setDragging(false);
        }}
        onDrop={(event) => {
          event.preventDefault();
          setDragging(false);
          if (!uploading && !disabled && event.dataTransfer.files.length)
            chooseFiles(Array.from(event.dataTransfer.files));
        }}
        aria-busy={uploading}
      >
        <label
          htmlFor={fileId}
          className={`subtle-file-picker${uploading ? " is-busy" : ""}`}
        >
          <span className="file-picker-symbol" aria-hidden="true">
            {uploading ? "↻" : "+"}
          </span>
          <span>
            {t("knowledge.file")}
            <small>{t("knowledge.drop_hint")}</small>
          </span>
          <input
            id={fileId}
            ref={input}
            aria-label={t("knowledge.file")}
            className="sr-only"
            type="file"
            multiple
            accept=".pdf,.png,.jpg,.jpeg"
            disabled={uploading || loading || disabled}
            aria-describedby={helpId}
            onChange={(event) =>
              chooseFiles(Array.from(event.currentTarget.files ?? []))
            }
          />
        </label>
        <p id={helpId} className="form-help">
          {t("knowledge.file_help")}
        </p>
        {queue.length > 0 && (
          <ul className="upload-queue">
            {queue.map((item, index) => (
              <li
                key={`${item.file.name}-${index}`}
                className={`file-${item.status}`}
              >
                <span>{item.file.name}</span>
                <span
                  className="file-result"
                  aria-label={t(`knowledge.file_${item.status}`)}
                  title={t(`knowledge.file_${item.status}`)}
                >
                  {item.status === "done"
                    ? "✓"
                    : item.status === "error"
                      ? "!"
                      : item.status === "uploading"
                        ? "↻"
                        : "·"}
                </span>
              </li>
            ))}
          </ul>
        )}
        {uploading && (
          <p className="form-help" role="status">
            {t("knowledge.processing_help")}
          </p>
        )}
        {uploadError && (
          <div className="workspace-alert" role="alert">
            <p>{t(uploadError)}</p>
            {uploadError !== "knowledge.file_invalid" &&
              uploadError !== "errors.processing" && (
                <p>{t("knowledge.upload_failed_help")}</p>
              )}
          </div>
        )}
        {uploadedName && !uploading && (
          <p className="document-success" role="status">
            {t("knowledge.received", { name: uploadedName })}
          </p>
        )}
      </div>
      {loading ? (
        <p role="status">{t("knowledge.documents_loading")}</p>
      ) : error ? (
        <div className="workspace-alert" role="alert">
          <p>{t(error)}</p>
          <button
            type="button"
            onClick={() => void load(lifecycle.current!.signal)}
          >
            {t("common.retry")}
          </button>
        </div>
      ) : documents.length === 0 ? (
        <div className="documents-empty">
          <h4>{t("knowledge.no_documents")}</h4>
          <p className="muted">{t("knowledge.no_documents_description")}</p>
        </div>
      ) : (
        <ul className="document-list" aria-label={t("knowledge.documents")}>
          {documents.map((document) => {
            const state = document.processingStatus ?? "ready";
            return (
              <li key={document.id} className={`document-row is-${state}`}>
                <button
                  type="button"
                  disabled={uploading || state !== "ready"}
                  aria-expanded={selected === document.id}
                  onClick={(event) => void open(document, event.currentTarget)}
                >
                  <span className="file-badge" aria-hidden="true">
                    {document.contentType === "application/pdf" ? "PDF" : "IMG"}
                  </span>
                  <span className="document-name">
                    <strong>{document.fileName}</strong>
                    <span
                      className="document-processing-state"
                      role="status"
                      aria-live="polite"
                      aria-atomic="true"
                    >
                      <span className="sr-only">{document.fileName}: </span>
                      {state === "ready"
                        ? t("knowledge.state_ready")
                        : t(`knowledge.state_${state}`)}
                      <span aria-hidden="true"> · </span>
                      <time dateTime={document.createdAt}>
                        {date(document.createdAt)}
                      </time>
                    </span>
                  </span>
                  <span aria-hidden="true">
                    {state === "ready" ? "✓" : state === "failed" ? "!" : "↻"}
                  </span>
                </button>
                <TrashAction
                  path={`/organizations/${organizationId}/knowledge-bases/${base.id}/documents/${document.id}`}
                  name={document.fileName}
                  label="trash.move_document"
                  t={t}
                  disabled={disabled || uploading || retrying !== null}
                  onBusyChange={onBusyChange}
                  onMoved={() => {
                    detailRequest.current?.abort();
                    setSelected("");
                    setDetail(null);
                    void load(lifecycle.current!.signal, true);
                  }}
                />
                {state === "failed" && (
                  <div className="document-job-error">
                    <p>
                      {t(
                        document.processingError === "usage_limit"
                          ? "errors.usage_limit"
                          : document.processingError === "usage_unavailable"
                            ? "errors.usage_unavailable"
                            : document.processingError === "invalid_document"
                              ? "knowledge.invalid_document"
                              : "knowledge.processing_failed",
                      )}
                    </p>
                    <button
                      type="button"
                      className="text-button"
                      disabled={retrying !== null}
                      onClick={() => void retryDocument(document)}
                    >
                      {t("knowledge.retry_processing")}
                    </button>
                  </div>
                )}
              </li>
            );
          })}
        </ul>
      )}
      {selected && (
        <section
          className="document-detail"
          aria-label={t("knowledge.details")}
        >
          <div className="documents-heading">
            <h4 ref={detailHeading} tabIndex={-1}>
              {detail?.document.fileName ?? t("knowledge.details")}
            </h4>
            <button
              type="button"
              className="text-button"
              onClick={() => {
                detailRequest.current?.abort();
                setSelected("");
                setDetail(null);
                detailTrigger.current?.focus();
              }}
            >
              {t("common.close")}
            </button>
          </div>
          {detailLoading ? (
            <p role="status">{t("knowledge.detail_loading")}</p>
          ) : detailError ? (
            <div className="workspace-alert" role="alert">
              <p>{t(detailError)}</p>
              <button
                type="button"
                onClick={() => {
                  const document = documents.find(
                    (entry) => entry.id === selected,
                  );
                  if (document) void open(document);
                }}
              >
                {t("common.retry")}
              </button>
            </div>
          ) : (
            detail && (
              <>
                <p className="muted">
                  {type(detail.document.documentType)}
                  <span aria-hidden="true"> · </span>
                  {date(detail.document.createdAt)}
                </p>
                <div className="document-downloads">
                  {detail.document.hasOriginal && (
                    <button
                      type="button"
                      className="secondary-button"
                      disabled={downloading}
                      onClick={() => void download("file")}
                    >
                      {t("knowledge.download")}
                    </button>
                  )}
                  <button
                    type="button"
                    className="text-button"
                    disabled={downloading}
                    onClick={() => void download("export/csv")}
                  >
                    {t("knowledge.export_csv")}
                  </button>
                  {downloading && (
                    <span role="status">{t("knowledge.downloading")}</span>
                  )}
                </div>
                {downloadError && (
                  <p role="alert" className="field-error">
                    {t(downloadError)}
                  </p>
                )}
                <h5>{t("knowledge.fields")}</h5>
                {Object.keys(detail.fields).length ? (
                  <dl className="extracted-fields">
                    {Object.entries(detail.fields).map(([key, value]) => (
                      <div key={key}>
                        <dt>{key}</dt>
                        <dd>{value}</dd>
                      </div>
                    ))}
                  </dl>
                ) : (
                  <p className="muted">{t("knowledge.no_fields")}</p>
                )}
              </>
            )
          )}
        </section>
      )}
    </div>
  );
}
