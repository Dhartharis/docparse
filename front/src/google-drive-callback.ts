import en from "../localization/docparse/locales/en.json";
import es from "../localization/docparse/locales/es.json";
import vi from "../localization/docparse/locales/vi.json";

const params = new URLSearchParams(location.search);
history.replaceState(null, "", location.pathname);
let language = "en";
try { language = localStorage.getItem("docparse.language") ?? navigator.language.split("-")[0]; } catch { /* Bundled English remains available. */ }
const pack = language === "es" ? es : language === "vi" ? vi : en;
document.documentElement.lang = pack.locale;
const state = params.get("state"); const code = params.get("code");
const valid = state && /^[a-f0-9]{64}$/.test(state) && params.getAll("state").length === 1 && params.getAll("code").length <= 1 && params.getAll("error").length <= 1;
if (valid && window.opener) window.opener.postMessage({ type: "docparse-google-drive", state, code: code && code.length <= 4096 ? code : null, error: params.has("error") }, location.origin);
document.getElementById("message")!.textContent = pack.strings["google.callback_return"];
const close = document.getElementById("close") as HTMLButtonElement;
close.textContent = pack.strings["common.close"]; close.hidden = false; close.onclick = () => window.close();
