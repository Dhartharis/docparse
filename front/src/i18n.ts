import { useEffect, useState } from "react";
import manifest from "../localization/docparse/locales.json";
import en from "../localization/docparse/locales/en.json";
import es from "../localization/docparse/locales/es.json";
import vi from "../localization/docparse/locales/vi.json";
import portalEn from "../localization/fyr-portal/en.json";
import portalEs from "../localization/fyr-portal/es.json";
import portalVi from "../localization/fyr-portal/vi.json";

export type MessageKey =
  keyof typeof en.strings | keyof typeof portalEn.strings;
export type Language = "en" | "es" | "vi";
export type Translate = (key: MessageKey, params?: Record<string, string | number>) => string;
type Product = "docparse" | "fyr-portal";
type LocalePack = {
  locale: string;
  version: number;
  strings: Record<string, string>;
};
type Catalogs = Record<Product, LocalePack>;
export const languages = manifest.locales;
const bundled: Record<Product, Record<Language, LocalePack>> = {
  docparse: { en, es, vi },
  "fyr-portal": { en: portalEn, es: portalEs, vi: portalVi },
};
const baseUrl = import.meta.env.DEV
  ? "/localization-api"
  : "https://fyr-localization-production.up.railway.app";
const refreshInterval = 300_000;
const isLanguage = (value: string | null): value is Language =>
  languages.some((entry) => entry.code === value);
const parameters = (text: string) =>
  [...text.matchAll(/\{([A-Za-z][A-Za-z0-9_]*)\}/g)]
    .map((match) => match[1])
    .sort()
    .join("|");

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}

export function validPack(
  value: unknown,
  product: Product,
  locale: Language,
  version?: number,
): value is LocalePack {
  if (
    !isRecord(value) ||
    value.locale !== locale ||
    !Number.isInteger(value.version) ||
    (value.version as number) < bundled[product][locale].version ||
    (version !== undefined && value.version !== version) ||
    !isRecord(value.strings)
  )
    return false;
  const strings = value.strings;
  return Object.entries(bundled[product].en.strings).every(
    ([key, english]) =>
      typeof strings[key] === "string" &&
      strings[key].trim().length > 0 &&
      strings[key].length <= 10000 &&
      parameters(strings[key]) === parameters(english),
  );
}

export function resolveLanguage(
  saved: string | null,
  preferred: readonly string[],
  explicit: string | null = null,
): Language {
  if (isLanguage(explicit)) return explicit;
  if (isLanguage(saved)) return saved;
  for (const language of preferred) {
    const base = language.toLowerCase().split("-")[0];
    if (isLanguage(base)) return base;
  }
  return "en";
}

export function readLanguage(): Language {
  let saved: string | null = null;
  try {
    saved = localStorage.getItem("docparse.language");
  } catch {
    /* Use URL/device preference when storage is unavailable. */
  }
  // The portal already passes ?lang= on product links. Keep that choice on entry.
  return resolveLanguage(
    saved,
    navigator.languages,
    new URLSearchParams(location.search).get("lang"),
  );
}

function cacheKey(product: Product, language: Language) {
  return `fyr.docparse.pack.${product}.${language}`;
}
function readCatalog(product: Product, language: Language): LocalePack {
  try {
    const pack: unknown = JSON.parse(
      localStorage.getItem(cacheKey(product, language)) ?? "null",
    );
    if (validPack(pack, product, language)) return pack;
  } catch {
    /* Invalid or unavailable cache falls back to the bundled catalog. */
  }
  return bundled[product][language];
}
function readCatalogs(language: Language): Catalogs {
  return {
    docparse: readCatalog("docparse", language),
    "fyr-portal": readCatalog("fyr-portal", language),
  };
}

async function request(url: string, signal: AbortSignal): Promise<unknown> {
  const response = await fetch(url, {
    credentials: "omit",
    cache: "no-cache",
    signal: AbortSignal.any([signal, AbortSignal.timeout(5000)]),
  });
  if (!response.ok) throw new Error(`Localization response ${response.status}`);
  return response.json();
}

async function fetchCatalog(
  product: Product,
  language: Language,
  current: LocalePack,
  signal: AbortSignal,
): Promise<LocalePack> {
  const endpoint = `${baseUrl}/v1/products/${product}/locales`;
  const value = await request(endpoint, signal);
  if (
    !isRecord(value) ||
    value.schemaVersion !== 1 ||
    !Array.isArray(value.locales)
  )
    throw new Error("Invalid localization manifest");
  const entry: unknown = value.locales.find(
    (item) => isRecord(item) && item.code === language,
  );
  if (
    !isRecord(entry) ||
    !Number.isInteger(entry.version) ||
    (entry.version as number) < current.version
  )
    throw new Error("Unsupported or stale locale");
  const pack = await request(`${endpoint}/${language}`, signal);
  if (!validPack(pack, product, language, entry.version as number))
    throw new Error("Invalid localization pack");
  // Only cache the consumed keys; the portal owns a larger, independent catalog.
  return {
    ...pack,
    strings: Object.fromEntries(
      Object.keys(bundled[product].en.strings).map((key) => [
        key,
        pack.strings[key],
      ]),
    ),
  };
}

export function useLocalization() {
  const [language, setCurrentLanguage] = useState<Language>(readLanguage);
  const [catalogs, setCatalogs] = useState<Catalogs>(() =>
    readCatalogs(language),
  );

  function setLanguage(next: Language) {
    try {
      localStorage.setItem("docparse.language", next);
    } catch {
      /* Apply for this visit even if persistence is unavailable. */
    }
    const url = new URL(location.href);
    if (url.searchParams.has("lang")) {
      url.searchParams.set("lang", next);
      history.replaceState(null, "", url);
    }
    setCatalogs(readCatalogs(next));
    setCurrentLanguage(next);
  }

  useEffect(() => {
    if(import.meta.env.VITE_LOCALIZATION_REMOTE === "false") return;
    const controller = new AbortController();
    const current = new Map<Product, LocalePack>(
      Object.entries(readCatalogs(language)) as [Product, LocalePack][],
    );
    const state = { refreshing: false };
    const refresh = async () => {
      if (state.refreshing || document.hidden) return;
      state.refreshing = true;
      try {
        await Promise.all(
          (["docparse", "fyr-portal"] as const).map(async (product) => {
            try {
              const pack = await fetchCatalog(
                product,
                language,
                current.get(product)!,
                controller.signal,
              );
              if (controller.signal.aborted) return;
              current.set(product, pack);
              try {
                localStorage.setItem(
                  cacheKey(product, language),
                  JSON.stringify(pack),
                );
              } catch {
                /* Remote text remains usable for this visit. */
              }
              setCatalogs((previous) => ({ ...previous, [product]: pack }));
              document.documentElement.dataset[
                product === "docparse"
                  ? "loginLocalization"
                  : "brandLocalization"
              ] = "remote";
            } catch {
              // Public translations are optional: retain validated cached/bundled copy.
              if (!controller.signal.aborted)
                document.documentElement.dataset[
                  product === "docparse"
                    ? "loginLocalization"
                    : "brandLocalization"
                ] = "bundled-or-cached";
            }
          }),
        );
      } finally {
        state.refreshing = false;
      }
    };
    void refresh();
    const interval = window.setInterval(() => void refresh(), refreshInterval);
    const onVisible = () => {
      if (!document.hidden) void refresh();
    };
    document.addEventListener("visibilitychange", onVisible);
    return () => {
      controller.abort();
      window.clearInterval(interval);
      document.removeEventListener("visibilitychange", onVisible);
    };
  }, [language]);

  const t = (key: MessageKey, params: Record<string, string | number> = {}) => {
    const product: Product = Object.hasOwn(en.strings, key)
      ? "docparse"
      : "fyr-portal";
    const text =
      catalogs[product].strings[key] ?? bundled[product].en.strings[key];
    return text.replace(/\{([A-Za-z][A-Za-z0-9_]*)\}/g, (match, name: string) =>
      String(params[name] ?? match),
    );
  };
  return { language, setLanguage, t };
}

export function bundledTranslator(language: Language): Translate {
  return (key, params = {}) => {
    const text = bundled.docparse[language].strings[key] ?? bundled.docparse.en.strings[key] ?? key;
    return text.replace(/\{([A-Za-z][A-Za-z0-9_]*)\}/g, (match, name: string) => String(params[name] ?? match));
  };
}
