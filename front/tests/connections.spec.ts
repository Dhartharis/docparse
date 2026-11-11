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
  test(`connection configuration is accessible and fits desktop/mobile in ${locale}`, async ({ page }, testInfo) => {
    await session(page);
    await page.addInitScript(value => localStorage.setItem("docparse.language", value), locale);
    await page.route("**/connections/", route => route.fulfill({ json: { connections: [], providerOriginsConfigured: false } }));
    await signIn(page, locale);
    const t = strings(locale);
    await page.getByRole("button", { name: t["connections.title"], exact: true }).click();
    await page.getByRole("button", { name: t["connections.add"], exact: true }).click();
    const dialog = page.getByRole("dialog");
    await expect(dialog.getByLabel(t["connections.name"], { exact: true })).toBeFocused();
    for (const width of [390, 1440]) {
      await page.setViewportSize({ width, height: 900 });
      expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
      expect((await new AxeBuilder({ page }).include("dialog").withTags(["wcag2a", "wcag2aa", "wcag21aa"]).analyze()).violations).toEqual([]);
      await page.screenshot({ path: testInfo.outputPath(`connections-${locale}-${width}.png`) });
    }
  });
}

test("create displays a secret only once and never persists it in browser storage", async ({ page }, testInfo) => {
  await session(page);
  let saved = false;
  let creations = 0;
  await page.addInitScript(() => Object.defineProperty(navigator, "clipboard", { value: { writeText: async () => { throw new Error("Clipboard unavailable"); } } }));
  await page.route("**/connections/", route => {
    if (route.request().method() === "GET") return route.fulfill({ json: { connections: saved ? [source()] : [], providerOriginsConfigured: true } });
    creations++;
    const body = route.request().postDataJSON();
    expect(body.definition.sourceBusinessId).toBe("business-demo");
    expect(body.definition.push).toBe(true);
    expect(body.definition.pullLive).toBe(false);
    expect(body.definition.settings.syncIntervalSeconds).toBeNull();
    saved = true;
    return route.fulfill({ json: { connection: source(), pushSecretHex: "a".repeat(64) } });
  });
  await signIn(page);
  await page.getByRole("button", { name: "Data connections", exact: true }).click();
  await page.getByRole("button", { name: "Add connection", exact: true }).click();
  await page.getByLabel("Connection name", { exact: true }).fill("Inventory");
  await page.getByLabel("Source business identifier", { exact: true }).fill("business-demo");
  await page.getByRole("button", { name: "Save connection", exact: true }).click();
  await expect(page.getByRole("textbox", { name: "Signing secret", exact: true })).toHaveValue("a".repeat(64));
  for (const width of [390, 1440]) {
    await page.setViewportSize({ width, height: 900 });
    expect((await new AxeBuilder({ page }).include("dialog").withTags(["wcag2a", "wcag2aa", "wcag21aa"]).analyze()).violations).toEqual([]);
    await page.screenshot({ path: testInfo.outputPath(`connections-secret-${width}.png`) });
  }
  await page.getByRole("button", { name: "Copy", exact: true }).click();
  await expect(page.getByRole("alert")).toContainText("Select and copy the secret manually");
  expect(await page.evaluate(() => JSON.stringify(localStorage) + JSON.stringify(sessionStorage))).not.toContain("a".repeat(64));
  await page.getByRole("button", { name: "Hide secret", exact: true }).click();
  await expect(page.getByRole("textbox", { name: "Signing secret", exact: true })).toHaveCount(0);
  await page.getByRole("button", { name: "Close", exact: true }).click();
  await page.locator(".account-menu > summary").click();
  await page.getByRole("button", { name: "Data connections", exact: true }).click();
  await expect(page.getByRole("heading", { name: "Inventory", exact: true })).toBeVisible();
  await expect(page.getByRole("textbox", { name: "Signing secret", exact: true })).toHaveCount(0);
  expect(creations).toBe(1);
});

test("key replacement requires confirmation and a stale revision recovers through refresh", async ({ page }) => {
  await session(page);
  let current = source();
  let rotations = 0;
  await page.route("**/connections/", route => route.fulfill({ json: { connections: [current], providerOriginsConfigured: true } }));
  await page.route("**/connections/source/push-key", route => {
    rotations++;
    if (rotations === 1) {
      expect(route.request().postDataJSON().revision).toBe("rev1");
      current = { ...current, revision: "other-admin-revision" };
      return route.fulfill({ status: 409, json: { code: "connection_conflict" } });
    }
    expect(route.request().postDataJSON().revision).toBe("other-admin-revision");
    current = { ...current, revision: "rev2", pushKey: { ...current.pushKey, keyId: "replacement-key" } };
    return route.fulfill({ json: { connection: current, secretHex: "b".repeat(64) } });
  });
  await signIn(page);
  await page.getByRole("button", { name: "Data connections", exact: true }).click();
  await page.getByRole("button", { name: "Replace receiving key", exact: true }).click();
  expect(rotations).toBe(0);
  await page.getByRole("button", { name: "Cancel", exact: true }).click();
  expect(rotations).toBe(0);
  await page.getByRole("button", { name: "Replace receiving key", exact: true }).click();
  await page.getByRole("button", { name: "Confirm change", exact: true }).click();
  await expect(page.getByRole("alert")).toContainText("Refresh the list");
  await page.getByRole("button", { name: "Refresh connections", exact: true }).click();
  await page.getByRole("button", { name: "Replace receiving key", exact: true }).click();
  await page.getByRole("button", { name: "Confirm change", exact: true }).click();
  await expect(page.getByRole("textbox", { name: "Signing secret", exact: true })).toHaveValue("b".repeat(64));
  expect(rotations).toBe(2);
});

test("Viewers do not receive connection administration", async ({ page }) => {
  await session(page, "Viewer");
  await signIn(page);
  await expect(page.getByRole("button", { name: "Data connections", exact: true })).toHaveCount(0);
});
