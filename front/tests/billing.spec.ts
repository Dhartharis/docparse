import { expect, test } from "@playwright/test";
import type { Page } from "@playwright/test";
import AxeBuilder from "@axe-core/playwright";
import { readFileSync } from "node:fs";

const strings = (locale: string): Record<string, string> => JSON.parse(readFileSync(new URL(`../localization/docparse/locales/${locale}.json`, import.meta.url), "utf8")).strings;

async function fixture(page: Page, enabled: boolean, role = "Admin", failures = 0) {
  await page.route("**/localization-api/**", route => route.abort());
  await page.route("**/api/**", route => {
    const path = new URL(route.request().url()).pathname;
    if (path.endsWith("/login")) return route.fulfill({ json: { accessToken: "access", refreshToken: "refresh", expiresIn: 900 } });
    if (path.endsWith("/me")) return route.fulfill({ json: { email: "admin@example.test" } });
    if (path === "/api/organizations/") return route.fulfill({ json: [{ id: "org", name: "Demo", role }] });
    if (path.endsWith("/bots/")) return route.fulfill({ json: [] });
    if (path.endsWith("/billing/")) return route.fulfill({ json: { enabled, mode: "test", availablePlans: ["starter", "pro"], state: { status: "none", planKey: null, hasCustomer: false, pendingEvents: 0, failedEvents: failures } } });
    return route.fulfill({ status: 404, json: {} });
  });
}

for (const locale of ["en", "es", "vi"]) {
  test(`billing setup and test state remain accessible in ${locale}`, async ({ page }, testInfo) => {
    await fixture(page, false);
    await page.addInitScript(value => localStorage.setItem("docparse.language", value), locale);
    const t = strings(locale);
    await page.goto("/");
    await page.locator('input[name="email"], input[autocomplete="username"]').first().fill("admin");
    await page.locator('input[type="password"]').fill("example-password");
    await page.getByRole("button", { name: t["login.submit"], exact: true }).click();
    await page.locator(".account-menu > summary").click();
    await page.getByRole("button", { name: t["billing.title"], exact: true }).click();
    const dialog = page.getByRole("dialog");
    await expect(dialog.getByText(t["billing.not_configured"], { exact: true })).toBeVisible();
    await expect(dialog.getByText(t["billing.test_notice"], { exact: true })).toBeVisible();
    await expect(dialog.getByRole("button", { name: t["billing.checkout"] })).toHaveCount(0);
    for (const width of [390, 1440]) {
      await page.setViewportSize({ width, height: 900 });
      await expect(dialog).toBeVisible();
      expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
      expect((await new AxeBuilder({ page }).include("dialog").withTags(["wcag2a", "wcag2aa", "wcag21aa"]).analyze()).violations).toEqual([]);
      await page.screenshot({ path: testInfo.outputPath(`billing-${locale}-${width}.png`) });
    }
  });
}

test("checkout prevents repeated clicks, recovers errors and rejects an unsafe redirect", async ({ page }) => {
  await fixture(page, true);
  let attempts = 0;
  let releaseFirst: (() => void) | undefined;
  let stableRequest = "";
  await page.route("**/billing/checkout", async route => {
    const body = route.request().postDataJSON();
    expect(body.planKey).toBe("starter");
    if (!stableRequest) stableRequest = body.requestId;
    expect(body.requestId).toBe(stableRequest);
    attempts++;
    if (attempts === 1) {
      await new Promise<void>(resolve => { releaseFirst = resolve; });
      return route.fulfill({ status: 503, json: { code: "billing_unavailable" } });
    }
    return route.fulfill({ json: { url: attempts === 2 ? "https://attacker.invalid/pay" : "https://checkout.stripe.com/c/pay/test_fixture" } });
  });
  await page.route("https://checkout.stripe.com/**", route => route.fulfill({ contentType: "text/html", body: "<h1>Local test checkout destination</h1>" }));
  await page.goto("/");
  await page.getByLabel("Username or email", { exact: true }).fill("admin");
  await page.getByLabel("Password", { exact: true }).fill("example-password");
  await page.getByRole("button", { name: "Sign in", exact: true }).click();
  await page.locator(".account-menu > summary").click();
  await page.getByRole("button", { name: "Test payments", exact: true }).click();
  const checkout = page.getByRole("button", { name: "Open test checkout", exact: true });
  await checkout.click();
  await expect(checkout).toBeDisabled();
  await expect.poll(() => Boolean(releaseFirst)).toBe(true);
  releaseFirst!();
  await expect(page.getByRole("alert")).toContainText("payment connection is unavailable");
  await checkout.click();
  await expect(page.getByRole("alert")).toBeVisible();
  expect(new URL(page.url()).hostname).toBe("127.0.0.1");
  expect(attempts).toBe(2);
  await checkout.click();
  await expect(page.getByRole("heading", { name: "Local test checkout destination" })).toBeVisible();
});

test("Viewers cannot open payment administration", async ({ page }) => {
  await fixture(page, true, "Viewer");
  await page.goto("/");
  await page.getByLabel("Username or email", { exact: true }).fill("viewer");
  await page.getByLabel("Password", { exact: true }).fill("example-password");
  await page.getByRole("button", { name: "Sign in", exact: true }).click();
  await page.locator(".account-menu > summary").click();
  await expect(page.getByRole("button", { name: "Test payments", exact: true })).toHaveCount(0);
});
