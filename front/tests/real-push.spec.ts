import { expect, test } from "@playwright/test";
import AxeBuilder from "@axe-core/playwright";

test("Admin reviews real received records with exact amounts, unknown values and freshness", async ({ page }, testInfo) => {
  test.skip(!process.env.DOCPARSE_REAL_API || process.env.DOCPARSE_TEST_ADMIN_EMAIL !== "push-a", "Run through tests/push_integration.py --browser.");
  await page.route("**/localization-api/**", route => route.abort());
  await page.goto("/");
  await page.getByLabel("Username or email", { exact: true }).fill(process.env.DOCPARSE_TEST_ADMIN_EMAIL!);
  await page.getByLabel("Password", { exact: true }).fill(process.env.DOCPARSE_TEST_ADMIN_PASSWORD!);
  await page.getByRole("button", { name: "Sign in", exact: true }).click();
  await page.locator(".account-menu > summary").click();
  await page.getByRole("button", { name: "Data connections", exact: true }).click();
  const row = page.locator(".connection-list > li").filter({ has: page.getByRole("heading", { name: "Push inventory", exact: true }) });
  await row.getByRole("button", { name: "View received data", exact: true }).click();
  const data = row.locator(".received-records");
  await expect(data.getByText("999,999,999,999,999,999.123456 USD", { exact: true }).first()).toBeVisible();
  await expect(data.getByText("8.5 L", { exact: true }).first()).toBeVisible();
  await expect(data.getByText("Unknown", { exact: true }).first()).toBeVisible();
  await expect(data.getByText("Expired, inactive or unknown", { exact: true }).first()).toBeVisible();
  for (const width of [390,1440]) {
    await page.setViewportSize({ width, height: 900 });
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
    expect((await new AxeBuilder({ page }).include("dialog").withTags(["wcag2a","wcag2aa","wcag21aa"]).analyze()).violations).toEqual([]);
    await data.scrollIntoViewIfNeeded();
    await page.screenshot({ path: testInfo.outputPath(`received-data-${width}.png`), fullPage: true });
  }
  await row.getByRole("button", { name: "Hide received data", exact: true }).click();
  await expect(data).toHaveCount(0);
});
