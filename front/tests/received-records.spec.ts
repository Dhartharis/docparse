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
  test(`received records preserve precision and show errors in ${locale}`, async ({ page }, testInfo) => {
    await session(page);
    await page.addInitScript(value => localStorage.setItem("docparse.language", value), locale);
    const t = strings(locale);
    await page.route("**/connections/", route => route.fulfill({ json: { connections: [source()], providerOriginsConfigured: true } }));
    const offer = { externalId: "milk", productId: "product", name: "Milk", presentation: "One carton", unitCode: "L", active: true,
      price: { known: true, amount: "999999999999999999.123456", currency: "USD", taxTreatment: "inclusive", observedAt: "2026-11-11T00:00:00Z" },
      availability: { available: { known: true, quantity: "8.5" }, reserved: { known: false, quantity: null }, observedAt: "2026-11-11T00:00:00Z" } };
    let fail = false;
    await page.route("**/connections/source/records", route => fail ? route.fulfill({ status: 503, json: {} }) : route.fulfill({ json: { total: 1, records: [{ offer, priceCurrent: true, availabilityCurrent: false }] } }));
    await signIn(page,locale);
    await page.getByRole("button", { name: t["connections.title"], exact: true }).click();
    await page.getByRole("button", { name: t["records.show"], exact: true }).click();
    const data = page.locator(".received-records");
    const amounts: Record<string,string> = { en: "999,999,999,999,999,999.123456 USD", es: "999.999.999.999.999.999,123456 USD", vi: "999.999.999.999.999.999,123456 USD" };
    await expect(data.getByText(amounts[locale], { exact: true })).toBeVisible();
    await expect(data.getByText(t["records.unknown"], { exact: true })).toBeVisible();
    await expect(data.getByText(t["records.not_current"], { exact: true })).toBeVisible();
    for (const width of [390,1440]) {
      await page.setViewportSize({ width, height: 900 });
      expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
      expect((await new AxeBuilder({ page }).include("dialog").withTags(["wcag2a","wcag2aa","wcag21aa"]).analyze()).violations).toEqual([]);
      await data.scrollIntoViewIfNeeded();
      await page.screenshot({ path: testInfo.outputPath(`received-${locale}-${width}.png`) });
    }
    fail = true;
    await data.getByRole("button", { name: t["connections.refresh"], exact: true }).click();
    await expect(data.getByRole("alert")).toBeVisible();
    await expect(data.getByText(amounts[locale], { exact: true })).toHaveCount(0);
    await page.getByRole("button", { name: t["records.hide"], exact: true }).click();
    await expect(data).toHaveCount(0);
  });
}
