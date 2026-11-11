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
  test(`scheduled status and confirmed restart fit both layouts in ${locale}`, async ({ page }, testInfo) => {
    await session(page);
    await page.addInitScript(value => localStorage.setItem("docparse.language", value), locale);
    const t = strings(locale);
    await page.route("**/connections/", route => route.fulfill({ json: { connections: [{...source(),pullScheduled:true}], providerOriginsConfigured: true } }));
    let restarted = 0;
    await page.route("**/connections/source/sync", route => route.fulfill({ json: { state: restarted ? "waiting" : "failed", attempts: restarted ? 0 : 5, dueAt: "2026-11-11T12:00:00Z", errorCode: restarted ? null : "sync_failed", lastSuccessAt: "2026-11-11T11:00:00Z", lastCompletedAt: null, snapshotCompleted: false } }));
    await page.route("**/connections/source/sync/restart", route => { restarted++; return route.fulfill({ status: 204 }); });
    await signIn(page,locale);
    await page.getByRole("button", { name: t["connections.title"], exact: true }).click();
    await page.getByRole("button", { name: t["source_sync.show"], exact: true }).click();
    await expect(page.getByRole("status")).toContainText(t["source_sync.failed"]);
    for (const width of [390,1440]) {
      await page.setViewportSize({ width, height: 900 });
      expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
      expect((await new AxeBuilder({ page }).include("dialog").withTags(["wcag2a","wcag2aa","wcag21aa"]).analyze()).violations).toEqual([]);
      await page.getByRole("heading", { name: t["source_sync.title"], exact: true }).evaluate(element => element.scrollIntoView({ block: "start" }));
      await page.screenshot({ path: testInfo.outputPath(`sync-${locale}-${width}.png`) });
    }
    await page.getByRole("button", { name: t["source_sync.restart"], exact: true }).click();
    expect(restarted).toBe(0);
    await page.getByRole("button", { name: t["common.cancel"], exact: true }).click();
    expect(restarted).toBe(0);
    await page.getByRole("button", { name: t["source_sync.restart"], exact: true }).click();
    await page.getByRole("button", { name: t["connections.confirm"], exact: true }).click();
    await expect(page.getByRole("status")).toContainText(t["source_sync.waiting"]);
    expect(restarted).toBe(1);
  });
}
