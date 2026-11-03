import { readFile } from "node:fs/promises";

const root = new URL("../localization/", import.meta.url);
const read = async (path) =>
  JSON.parse(await readFile(new URL(path, root), "utf8"));
const manifest = await read("docparse/locales.json");
if (manifest.schemaVersion !== 1)
  throw new Error("Unsupported localization schema");
const placeholders = (text) =>
  [...text.matchAll(/\{([A-Za-z][A-Za-z0-9_]*)\}/g)]
    .map((match) => match[1])
    .sort()
    .join("|");
for (const product of ["docparse", "fyr-portal"]) {
  const path = (locale) =>
    product === "docparse"
      ? `${product}/locales/${locale}.json`
      : `${product}/${locale}.json`;
  const english = await read(path("en"));
  const contract = Object.keys(english.strings).sort();
  for (const entry of manifest.locales) {
    const pack = await read(path(entry.code));
    if (
      pack.locale !== entry.code ||
      !Number.isInteger(pack.version) ||
      pack.version < 1 ||
      (product === "docparse" && pack.version !== entry.version)
    )
      throw new Error(`Invalid metadata: ${product}/${entry.code}`);
    if (
      JSON.stringify(Object.keys(pack.strings).sort()) !==
      JSON.stringify(contract)
    )
      throw new Error(`Incomplete key contract: ${product}/${entry.code}`);
    for (const key of contract) {
      const value = pack.strings[key];
      if (
        !/^[a-z][a-z0-9_]*(?:\.[a-z][a-z0-9_]*)+$/.test(key) ||
        typeof value !== "string" ||
        !value.trim() ||
        value.length > 10000 ||
        placeholders(value) !== placeholders(english.strings[key])
      )
        throw new Error(`Invalid string: ${product}/${entry.code}/${key}`);
    }
  }
}
console.info(
  "Validated English, Spanish and Vietnamese catalogs and portal branding snapshots.",
);
