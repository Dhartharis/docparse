import { api } from "./api";

export type KnowledgeBase = { id: string; name: string };
export type DocumentSummary = {
  id: string;
  fileName: string;
  contentType: string | null;
  documentType: string;
  createdAt: string;
  hasOriginal: boolean;
  processingStatus?: "queued" | "processing" | "ready" | "failed";
  processingError?: string | null;
};
export type DocumentDetails = {
  document: DocumentSummary;
  fields: Record<string, string>;
};
const root = (organizationId: string) =>
  `/organizations/${organizationId}/knowledge-bases`;
const documents = (organizationId: string, knowledgeBaseId: string) =>
  `${root(organizationId)}/${knowledgeBaseId}/documents`;

export const knowledge = {
  rename: (
    organizationId: string,
    baseId: string,
    name: string,
    signal: AbortSignal,
  ) => api(`${root(organizationId)}/${baseId}`, { name }, signal, "PATCH"),
  list: (organizationId: string, signal: AbortSignal) =>
    api<KnowledgeBase[]>(`${root(organizationId)}/`, undefined, signal),
  create: (organizationId: string, name: string, signal: AbortSignal) =>
    api<KnowledgeBase>(`${root(organizationId)}/`, { name }, signal),
  documents: (organizationId: string, baseId: string, signal: AbortSignal) =>
    api<DocumentSummary[]>(
      `${documents(organizationId, baseId)}/all`,
      undefined,
      signal,
    ),
  detail: (
    organizationId: string,
    baseId: string,
    id: string,
    signal: AbortSignal,
  ) =>
    api<DocumentDetails>(
      `${documents(organizationId, baseId)}/${id}`,
      undefined,
      signal,
    ),
  retry: (
    organizationId: string,
    baseId: string,
    id: string,
    signal: AbortSignal,
  ) => api(`${documents(organizationId, baseId)}/${id}/retry`, {}, signal),
  upload: (
    organizationId: string,
    baseId: string,
    file: File,
    signal: AbortSignal,
  ) => {
    const data = new FormData();
    data.append("file", file);
    return api<{ id: string; processingStatus: string }>(
      `${documents(organizationId, baseId)}/extract`,
      data,
      signal,
    );
  },
  download: async (
    organizationId: string,
    baseId: string,
    document: DocumentSummary,
    format: "file" | "export/csv",
    signal: AbortSignal,
  ) => {
    const blob = await api<Blob>(
      `${documents(organizationId, baseId)}/${document.id}/${format}`,
      undefined,
      signal,
      "GET",
      "blob",
    );
    if (signal.aborted) return;
    const url = URL.createObjectURL(blob);
    const anchor = window.document.createElement("a");
    anchor.href = url;
    anchor.download =
      format === "file"
        ? Array.from(document.fileName, (character) =>
            character.charCodeAt(0) < 32 ||
            character === "/" ||
            character === "\\"
              ? "_"
              : character,
          ).join("")
        : `document_${document.id}.csv`;
    window.document.body.append(anchor);
    anchor.click();
    anchor.remove();
    // Keep the object URL alive while the browser starts the download.
    setTimeout(() => URL.revokeObjectURL(url), 60_000);
  },
};
