import { readFileSync } from "node:fs";
import { expect, test } from "@playwright/test";
import type { Page } from "@playwright/test";
import AxeBuilder from "@axe-core/playwright";

const strings = (locale: string): Record<string, string> => JSON.parse(readFileSync(new URL(`../localization/docparse/locales/${locale}.json`, import.meta.url), "utf8")).strings;
const source = () => ({ id: "source", sourceBusinessId: "business-demo", baseUrl: null, push: true, pullLive: false, pullScheduled: false,
  revision: "rev1", disconnectedAt: null, settings: { name: "Inventory", priceMaxAgeSeconds: 600, stockMaxAgeSeconds: 60, clockSkewSeconds: 0, syncIntervalSeconds: null },
  pushKey: { keyId: "receiving-key", expiresAt: "2099-11-11T00:00:00Z" }, pullKeyId: null, state: "configured" });

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
  test(`live query supports localized errors and ambiguity in ${locale}`, async ({ page }, testInfo) => {
    await session(page);
    await page.addInitScript(value => localStorage.setItem("docparse.language", value), locale);
    const t = strings(locale);
    await page.route("**/connections/", route => route.fulfill({ json: { connections: [{...source(),pullLive:true}], providerOriginsConfigured: true } }));
    let fail = false;
    await page.route("**/connections/source/query", route => {
      expect(route.request().postDataJSON().values).toEqual(["Milk"]);
      return fail ? route.fulfill({ status: 409, json: { code: "provider_credentials" } }) : route.fulfill({ json: { queriedAt: "2026-11-11T00:00:00Z", results: [{ selectorValue: "Milk", status: "not_found", records: [] }] } });
    });
    await signIn(page,locale);
    await page.getByRole("button", { name: t["connections.title"], exact: true }).click();
    await page.getByRole("button", { name: t["live.show"], exact: true }).click();
    await page.getByLabel(t["live.value"], { exact: true }).fill("Milk");
    await page.getByRole("button", { name: t["live.submit"], exact: true }).click();
    await expect(page.getByRole("status")).toContainText(t["live.not_found"]);
    for (const width of [390,1440]) {
      await page.setViewportSize({ width, height: 900 });
      expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
      expect((await new AxeBuilder({ page }).include("dialog").withTags(["wcag2a","wcag2aa","wcag21aa"]).analyze()).violations).toEqual([]);
      await page.screenshot({ path: testInfo.outputPath(`live-${locale}-${width}.png`) });
    }
    fail = true;
    await page.getByRole("button", { name: t["live.submit"], exact: true }).click();
    await expect(page.getByRole("alert")).toHaveText(t["errors.provider_credentials"]);
    await expect(page.getByRole("dialog")).toBeVisible();
    await expect(page.getByText(t["live.not_found"], { exact: true })).toHaveCount(0);
  });
}
