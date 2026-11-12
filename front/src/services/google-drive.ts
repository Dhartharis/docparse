import { api, ApiError } from "./api";
import type { CatalogInspection, CatalogMapping, CatalogPreview } from "./spreadsheet-catalog";

export type GoogleAccount = { id: string; createdAt: string; needsReconnect: boolean };
export type GoogleFile = { id: string; name: string; mimeType: string; version: string; modifiedAt: string };
const root = (org: string) => `/organizations/${org}/google-drive`;
export const googleAccounts = (org: string, signal: AbortSignal) => api<{ enabled: boolean; accounts: GoogleAccount[] }>(root(org) + "/", undefined, signal);
export const disconnectGoogle = (org: string, id: string, signal: AbortSignal) => api<void>(`${root(org)}/${id}/disconnect`, {}, signal);
export const inspectGoogle = (org: string, id: string, fileId: string, sheet: string, headerRow: number, signal: AbortSignal) =>
  api<{ file: GoogleFile; inspection: CatalogInspection }>(`${root(org)}/${id}/inspect`, { fileId, sheet: sheet || null, headerRow }, signal);
export const previewGoogle = (org: string, id: string, fileId: string, sheet: string, mapping: CatalogMapping, signal: AbortSignal) =>
  api<{ file: GoogleFile; preview: CatalogPreview }>(`${root(org)}/${id}/preview`, { fileId, sheet, mapping }, signal);

export async function authorizeGoogle(org: string, popup: Window, signal: AbortSignal) {
  try {
    const start = await api<{ id: string; state: string; authorizationUrl: string }>(root(org) + "/authorize", {}, signal);
    const destination = new URL(start.authorizationUrl);
    if (destination.origin !== "https://accounts.google.com" && !(import.meta.env.DEV && destination.origin === location.origin)) throw new ApiError("google_authorization");
    const code = await new Promise<string>((resolve, reject) => {
      function cleanup() { window.removeEventListener("message", received); signal.removeEventListener("abort", aborted); clearInterval(closed); clearTimeout(timeout); }
      function aborted() { cleanup(); reject(new DOMException("Aborted", "AbortError")); }
      function received(event: MessageEvent) {
        if (event.origin !== location.origin || event.source !== popup || event.data?.type !== "docparse-google-drive" || event.data?.state !== start.state) return;
        cleanup();
        if (event.data.error || typeof event.data.code !== "string" || event.data.code.length > 4096) reject(new ApiError("google_authorization"));
        else resolve(event.data.code);
      }
      window.addEventListener("message", received);
      signal.addEventListener("abort", aborted, { once: true });
      const closed = setInterval(() => { if (popup.closed) { cleanup(); reject(new ApiError("google_authorization")); } }, 500);
      const timeout = setTimeout(() => { cleanup(); reject(new ApiError("google_authorization")); }, 600_000);
      if (signal.aborted) { aborted(); return; }
      popup.location.assign(destination.href);
    });
    popup.close();
    const result = await api<{ id: string }>(root(org) + "/complete", { state: start.state, code }, signal);
    if (result.id !== start.id) throw new ApiError("google_authorization");
    return result.id;
  } finally { popup.close(); }
}

type Picker = { setVisible(value: boolean): void; dispose(): void };
type PickerBuilder = { setDeveloperKey(key: string): PickerBuilder; setAppId(id: string): PickerBuilder; setOAuthToken(token: string): PickerBuilder; setOrigin(origin: string): PickerBuilder; setLocale(locale: string): PickerBuilder; addView(view: unknown): PickerBuilder; setCallback(callback: (data: { action: string; docs?: { id: string }[] }) => void): PickerBuilder; build(): Picker };
type PickerApi = { PickerBuilder: new () => PickerBuilder; View: new (id: string) => { setMimeTypes(types: string): unknown }; ViewId: { DOCS: string }; Action: { PICKED: string; CANCEL: string } };
type GoogleWindow = Window & { gapi?: { load(name: string, options: { callback: () => void; onerror: () => void; timeout: number; ontimeout: () => void }): void }; google?: { picker: PickerApi } };
let loading: Promise<void> | null = null;
function loadPicker() {
  if (!loading) loading = new Promise<void>((resolve, reject) => {
    const target = window as GoogleWindow;
    const fail = () => { script.remove(); loading = null; reject(new ApiError("google_unavailable")); };
    const script = document.createElement("script"); script.src = "https://apis.google.com/js/api.js"; script.referrerPolicy = "no-referrer";
    const timer = setTimeout(fail, 30_000);
    const failed = () => { clearTimeout(timer); fail(); };
    script.onerror = failed;
    script.onload = () => {
      if (!target.gapi) { failed(); return; }
      target.gapi.load("picker", { callback: () => { clearTimeout(timer); resolve(); }, onerror: failed, timeout: 25_000, ontimeout: failed });
    };
    document.head.append(script);
  });
  return loading;
}
export async function chooseGoogleFile(org: string, account: string, locale: string, signal: AbortSignal, dialog: HTMLDialogElement | null) {
  await loadPicker();
  const credentials = await api<{ accessToken: string; apiKey: string; projectNumber: string }>(`${root(org)}/${account}/picker`, {}, signal);
  if (signal.aborted) throw new DOMException("Aborted", "AbortError");
  const pickerApi = (window as GoogleWindow).google?.picker;
  if (!pickerApi) throw new ApiError("google_unavailable");
  const wasOpen = dialog?.open;
  dialog?.close();
  try {
    return await new Promise<string | null>((resolve, reject) => {
      let picker: Picker | null = null;
      function cleanup() { clearTimeout(timer); signal.removeEventListener("abort", aborted); picker?.setVisible(false); picker?.dispose(); }
      function aborted() { cleanup(); reject(new DOMException("Aborted", "AbortError")); }
      const timer = setTimeout(() => { cleanup(); reject(new ApiError("google_unavailable")); }, 300_000);
      signal.addEventListener("abort", aborted, { once: true });
      try {
        const view = new pickerApi.View(pickerApi.ViewId.DOCS); view.setMimeTypes("application/vnd.google-apps.spreadsheet,application/vnd.openxmlformats-officedocument.spreadsheetml.sheet");
        picker = new pickerApi.PickerBuilder().setDeveloperKey(credentials.apiKey).setAppId(credentials.projectNumber).setOAuthToken(credentials.accessToken).setOrigin(location.origin).setLocale(locale).addView(view).setCallback(data => {
          if (data.action === pickerApi.Action.PICKED || data.action === pickerApi.Action.CANCEL) {
            cleanup(); const id = data.docs?.[0]?.id;
            if (data.action === pickerApi.Action.CANCEL) resolve(null);
            else if (typeof id === "string" && /^[A-Za-z0-9_-]{1,256}$/.test(id)) resolve(id);
            else reject(new ApiError("google_file"));
          }
        }).build();
        picker.setVisible(true);
      } catch { cleanup(); reject(new ApiError("google_unavailable")); }
    });
  } finally { credentials.accessToken = ""; if (wasOpen && dialog?.isConnected && !dialog.open) dialog.showModal(); }
}
