import { expect, test } from "@playwright/test";
import AxeBuilder from "@axe-core/playwright";

test("Admin queries the real signed provider without storing a live result", async ({ page }, testInfo) => {
  test.skip(!process.env.DOCPARSE_LIVE_PROVIDER_ORIGIN, "Run through tests/live_queries_integration.py --browser.");
  await page.route("**/localization-api/**", route => route.abort());
  await page.goto("/");
  await page.getByLabel("Username or email", { exact: true }).fill(process.env.DOCPARSE_TEST_ADMIN_EMAIL!);
  await page.getByLabel("Password", { exact: true }).fill(process.env.DOCPARSE_TEST_ADMIN_PASSWORD!);
  await page.getByRole("button", { name: "Sign in", exact: true }).click();
  await page.locator(".account-menu > summary").click();
  await page.getByRole("button", { name: "Data connections", exact: true }).click();
  const row = page.locator(".connection-list > li").filter({ has: page.getByRole("heading", { name: "Live inventory", exact: true }) });
  await row.getByRole("button", { name: "Query source now", exact: true }).click();
  await row.getByLabel("Search value", { exact: true }).fill("Milk");
  await row.getByRole("button", { name: "Query source", exact: true }).click();
  await expect(row.getByText("One matching offer.", { exact: true })).toBeVisible();
  await expect(row.getByText("999,999,999,999,999,999.123456 USD", { exact: true })).toBeVisible();
  for (const width of [390,1440]) {
    await page.setViewportSize({ width, height: 900 });
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
    expect((await new AxeBuilder({ page }).include("dialog").withTags(["wcag2a","wcag2aa","wcag21aa"]).analyze()).violations).toEqual([]);
    await row.getByRole("heading", { name: "Live source query", exact: true }).scrollIntoViewIfNeeded();
    await page.screenshot({ path: testInfo.outputPath(`live-query-${width}.png`) });
  }
  await row.getByLabel("Search value", { exact: true }).fill("credentials");
  await row.getByRole("button", { name: "Query source", exact: true }).click();
  await expect(row.getByRole("alert")).toContainText("The provider rejected its saved key");
  await expect(page.getByRole("dialog")).toBeVisible();
});
