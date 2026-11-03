import { mkdir, writeFile } from "node:fs/promises";

// These are snapshots for offline use. The portal catalog owns this copy.
const endpoint =
  "https://fyr-localization-production.up.railway.app/v1/products/fyr-portal/locales";
const keys = ["doc.category", "doc.description", "footer.copyright"];
const root = new URL("../localization/fyr-portal/", import.meta.url);
const request = async (url) => {
  const response = await fetch(url, { signal: AbortSignal.timeout(10000) });
  if (!response.ok)
    throw new Error(`Portal localization returned ${response.status}`);
  return response.json();
};
const manifest = await request(endpoint);
if (manifest.schemaVersion !== 1 || !Array.isArray(manifest.locales))
  throw new Error("Invalid portal manifest");
const packs = [];
for (const locale of ["en", "es", "vi"]) {
  const entry = manifest.locales.find((entry) => entry.code === locale);
  const pack = await request(`${endpoint}/${locale}`);
  if (
    !entry ||
    pack.locale !== locale ||
    pack.version !== entry.version ||
    !Number.isInteger(pack.version) ||
    pack.version < 1 ||
    !keys.every(
      (key) =>
        typeof pack.strings?.[key] === "string" && pack.strings[key].trim(),
    )
  )
    throw new Error(`Invalid portal pack: ${locale}`);
  packs.push({
    ...pack,
    strings: Object.fromEntries(keys.map((key) => [key, pack.strings[key]])),
  });
}
await mkdir(root, { recursive: true });
for (const pack of packs)
  await writeFile(
    new URL(`${pack.locale}.json`, root),
    `${JSON.stringify(pack, null, 2)}\n`,
  );
console.info(
  "Updated offline branding snapshots from fyr-localization (fyr-portal).",
);
