import { readFileSync } from "node:fs";
import { expect, test } from "@playwright/test";
import AxeBuilder from "@axe-core/playwright";
import { fakeGooglePicker } from "./google-picker-fixture";

const strings = (locale: string): Record<string, string> => JSON.parse(readFileSync(new URL(`../localization/docparse/locales/${locale}.json`, import.meta.url), "utf8")).strings;
for (const locale of ["en", "es", "vi"]) test(`Google authorization popup, file selection and mapping in ${locale}`, async ({ page }, testInfo) => {
  const t = strings(locale); let authorized = false; const state = "a".repeat(64); const id = "google-account";
  await page.addInitScript(value => localStorage.setItem("docparse.language", value), locale);
  await page.route("**/localization-api/**", route => route.abort());
  await page.route("**/api/**", route => {
    const path = new URL(route.request().url()).pathname;
    if (path.endsWith("/login")) return route.fulfill({ json: { accessToken: "access", refreshToken: "refresh", expiresIn: 900 } });
    if (path.endsWith("/me")) return route.fulfill({ json: { email: "admin@example.test" } });
    if (path === "/api/organizations/") return route.fulfill({ json: [{ id: "org", name: "Demo", role: "Admin" }] });
    if (path.endsWith("/bots/")) return route.fulfill({ json: [] });
    if (path.endsWith("/connections/")) return route.fulfill({ json: { connections: [], providerOriginsConfigured: true } });
    if (path.endsWith("/google-drive/")) return route.fulfill({ json: { enabled: true, accounts: authorized ? [{ id, createdAt: "2026-11-12T00:00:00Z", needsReconnect: false }] : [] } });
    if (path.endsWith("/authorize")) return route.fulfill({ json: { id, state, authorizationUrl: `http://127.0.0.1:45173/fake-google?state=${state}` } });
    if (path.endsWith("/complete")) { expect(route.request().postDataJSON()).toEqual({ state, code: "fixture-code" }); authorized = true; return route.fulfill({ json: { id } }); }
    if (path.endsWith("/picker")) return route.fulfill({ json: { accessToken: "fixture-access", apiKey: "key", projectNumber: "12345" } });
    if (path.endsWith("/inspect")) return route.fulfill({ json: { file: { id: "sheet", name: "Inventory" }, inspection: { sheet: "Inventory", sheets: ["Inventory"], headers: ["ID", "Name", "Price", "Stock", "Updated"], rowCount: 1, headersValid: true } } });
    if (path.endsWith("/preview")) { expect(route.request().postDataJSON().mapping.columns.externalId).toBe("ID"); return route.fulfill({ status: 400, json: { code: "google_changed" } }); }
    if (path.endsWith("/disconnect")) { authorized = false; return route.fulfill({ status: 204 }); }
    return route.fulfill({ status: 404, json: {} });
  });
  // Callback is the actual isolated app page, not a mocked postMessage implementation.
  await page.context().route("**/fake-google?**", route => route.fulfill({ status: 302, headers: { location: `/google-drive-callback.html?state=${state}&code=fixture-code` } }));
  await fakeGooglePicker(page);
  await page.goto("/");
  await page.locator('input[autocomplete="username"]').fill("admin"); await page.locator('input[type="password"]').fill("example-password");
  await page.getByRole("button", { name: t["login.submit"], exact: true }).click(); await page.locator(".account-menu > summary").click();
  await page.getByRole("button", { name: t["connections.title"], exact: true }).click();
  await page.getByRole("button", { name: t["google.title"], exact: true }).click();
  await page.getByRole("button", { name: t["google.connect"], exact: true }).click();
  await expect(page.getByRole("combobox", { name: t["catalog.external_id"], exact: true })).toHaveValue("ID");
  await expect(page.getByRole("dialog")).toBeVisible();
  await page.getByLabel(t["catalog.presentation"], { exact: true }).fill("One carton"); await page.getByLabel(t["catalog.unit"], { exact: true }).fill("L"); await page.getByLabel(t["catalog.currency"], { exact: true }).fill("USD");
  await page.getByRole("button", { name: t["catalog.preview"], exact: true }).click();
  await expect(page.getByRole("alert")).toHaveText(t["errors.google_changed"]);
  expect(await page.evaluate(() => JSON.stringify([localStorage, sessionStorage]))).not.toMatch(/fixture-access|fixture-code|fixture-refresh/);
  for (const width of [390, 1440]) {
    await page.setViewportSize({ width, height: 900 });
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
    expect((await new AxeBuilder({ page }).include("dialog").withTags(["wcag2a", "wcag2aa", "wcag21aa"]).analyze()).violations).toEqual([]);
    await page.getByRole("heading", { name: t["google.title"], exact: true }).scrollIntoViewIfNeeded();
    await page.screenshot({ path: testInfo.outputPath(`google-${locale}-${width}.png`) });
  }
  await page.getByRole("button", { name: t["google.disconnect"], exact: true }).click();
  const confirmation = page.getByRole("group", { name: t["google.disconnect"], exact: true });
  await expect(confirmation).toContainText(t["google.disconnect_help"]);
  await confirmation.getByRole("button", { name: t["google.disconnect"], exact: true }).click();
  await expect(page.getByRole("heading", { name: "Inventory", exact: true })).toHaveCount(0);
});
