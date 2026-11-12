import { readFileSync } from "node:fs";
import { expect, test } from "@playwright/test";
import type { Page } from "@playwright/test";
import AxeBuilder from "@axe-core/playwright";

const strings = (locale: string): Record<string, string> => JSON.parse(readFileSync(new URL(`../localization/docparse/locales/${locale}.json`, import.meta.url), "utf8")).strings;
async function session(page: Page, role = "Admin") {
  await page.route("**/localization-api/**", route => route.abort());
  await page.route("**/api/**", route => {
    const path = new URL(route.request().url()).pathname;
    if (path.endsWith("/login")) return route.fulfill({ json: { accessToken: "access", refreshToken: "refresh", expiresIn: 900 } });
    if (path.endsWith("/me")) return route.fulfill({ json: { email: "admin@example.test" } });
    if (path === "/api/organizations/") return route.fulfill({ json: [{ id: "org", name: "Demo", role }] });
    if (path.endsWith("/bots/")) return route.fulfill({ json: [] });
    return route.fulfill({ status: 404, json: {} });
  });
}
async function signIn(page: Page, locale = "en") {
  const t = strings(locale);
  await page.goto("/");
  await page.locator('input[autocomplete="username"]').fill("admin");
  await page.locator('input[type="password"]').fill("example-password");
  await page.getByRole("button", { name: t["login.submit"], exact: true }).click();
  await page.locator(".account-menu > summary").click();
}

for (const locale of ["en", "es", "vi"]) {
  test(`spreadsheet mapping preview is localized and accessible in ${locale}`, async ({ page }, testInfo) => {
    await session(page);
    await page.addInitScript(value => localStorage.setItem("docparse.language", value), locale);
    const t = strings(locale);
    await page.route("**/connections/", route => route.fulfill({ json: { connections: [], providerOriginsConfigured: true } }));
    await page.route("**/spreadsheet-catalog/inspect", route => route.fulfill({ json: { sheet: "Inventory", sheets: ["Inventory"], headers: ["ID","Name","Price","Stock","Updated"], rowCount: 1 } }));
    await page.route("**/spreadsheet-catalog/preview", route => {
      const body = route.request().postDataBuffer()!.toString();
      expect(body).toContain('"externalId":"ID"');expect(body).toContain('"freshnessBasis":"column"');
      return route.fulfill({ json: { sheet: "Inventory", sheets: ["Inventory"], headers: [], rowCount: 1, offers: [], issues: [{ row: 2, field: "externalId", code: "text_identifier" }], canImport: false } });
    });
    await signIn(page,locale);
    await page.getByRole("button", { name: t["connections.title"], exact: true }).click();
    await page.getByRole("button", { name: t["catalog.show"], exact: true }).click();
    await page.getByLabel(t["catalog.file"], { exact: true }).setInputFiles({ name: "catalog.xlsx", mimeType: "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", buffer: Buffer.from("fixture") });
    await page.getByRole("button", { name: t["catalog.inspect"], exact: true }).click();
    await expect(page.getByRole("combobox", { name: t["catalog.external_id"], exact: true })).toHaveValue("ID");
    await page.getByLabel(t["catalog.presentation"], { exact: true }).fill("One carton");
    await page.getByLabel(t["catalog.unit"], { exact: true }).fill("L");
    await page.getByLabel(t["catalog.currency"], { exact: true }).fill("USD");
    await page.getByRole("button", { name: t["catalog.preview"], exact: true }).click();
    await expect(page.getByText(t["catalog.issue_text_identifier"], { exact: false })).toBeVisible();
    for (const width of [390,1440]) {
      await page.setViewportSize({ width, height: 900 });
      expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
      expect((await new AxeBuilder({ page }).include("dialog").withTags(["wcag2a","wcag2aa","wcag21aa"]).analyze()).violations).toEqual([]);
      await page.getByRole("heading", { name: t["catalog.title"], exact: true }).evaluate(element => element.scrollIntoView({ block: "start" }));
      await page.screenshot({ path: testInfo.outputPath(`catalog-${locale}-${width}.png`) });
    }
  });
}

test("a cover sheet without headers keeps worksheet selection available", async ({ page }) => {
  await session(page);
  const t = strings("en");
  await page.route("**/connections/", route => route.fulfill({ json: { connections: [], providerOriginsConfigured: true } }));
  await page.route("**/spreadsheet-catalog/inspect", route => {
    const inventory = route.request().postDataBuffer()!.toString().includes("Inventory");
    return route.fulfill({ json: { sheet: inventory ? "Inventory" : "Cover", sheets: ["Cover", "Inventory"], headers: inventory ? ["ID", "Name"] : [], rowCount: inventory ? 1 : 0, headersValid: inventory } });
  });
  await signIn(page);
  await page.getByRole("button", { name: t["connections.title"], exact: true }).click();
  await page.getByRole("button", { name: t["catalog.show"], exact: true }).click();
  await page.getByLabel(t["catalog.file"], { exact: true }).setInputFiles({ name: "cover.xlsx", mimeType: "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", buffer: Buffer.from("fixture") });
  await page.getByRole("button", { name: t["catalog.inspect"], exact: true }).click();
  await expect(page.getByRole("alert")).toHaveText(t["errors.catalog_headers"]);
  await expect(page.getByRole("heading", { name: t["catalog.mapping"], exact: true })).toHaveCount(0);
  await page.getByRole("combobox", { name: t["catalog.sheet"], exact: true }).selectOption("Inventory");
  await page.getByRole("button", { name: t["catalog.inspect"], exact: true }).click();
  await expect(page.getByRole("combobox", { name: t["catalog.external_id"], exact: true })).toHaveValue("ID");
  await expect(page.getByRole("alert")).toHaveCount(0);
});
