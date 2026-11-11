import { expect, test } from "@playwright/test";
import AxeBuilder from "@axe-core/playwright";

test("Admin inspects real durable sync failure and confirms full-import controls", async ({ page }, testInfo) => {
  test.skip(!process.env.DOCPARSE_LIVE_PROVIDER_ORIGIN, "Run through tests/scheduled_sync_integration.py --browser.");
  await page.route("**/localization-api/**", route => route.abort());
  await page.goto("/");
  await page.getByLabel("Username or email", { exact: true }).fill(process.env.DOCPARSE_TEST_ADMIN_EMAIL!);
  await page.getByLabel("Password", { exact: true }).fill(process.env.DOCPARSE_TEST_ADMIN_PASSWORD!);
  await page.getByRole("button", { name: "Sign in", exact: true }).click();
  await page.locator(".account-menu > summary").click();
  await page.getByRole("button", { name: "Data connections", exact: true }).click();
  const row = page.locator(".connection-list > li").filter({ has: page.getByRole("heading", { name: "Scheduled inventory", exact: true }) });
  await row.getByRole("button", { name: "View synchronization", exact: true }).click();
  await expect(row.getByText("Synchronization stopped", { exact: true })).toBeVisible();
  await expect(row.getByText("The initial snapshot is complete.", { exact: true })).toBeVisible();
  await expect(row.getByText(/The provider did not authorize/)).toBeVisible();
  for (const width of [390,1440]) {
    await page.setViewportSize({ width, height: 900 });
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
    expect((await new AxeBuilder({ page }).include("dialog").withTags(["wcag2a","wcag2aa","wcag21aa"]).analyze()).violations).toEqual([]);
    await row.getByRole("heading", { name: "Scheduled synchronization", exact: true }).evaluate(element => element.scrollIntoView({ block: "start" }));
    await page.screenshot({ path: testInfo.outputPath(`scheduled-sync-${width}.png`) });
  }
  await row.getByRole("button", { name: "Restart full import", exact: true }).click();
  await expect(row.getByText(/Existing records stay available while it loads/)).toBeVisible();
  await row.getByRole("button", { name: "Cancel", exact: true }).click();
  await expect(row.getByRole("button", { name: "Restart full import", exact: true })).toBeVisible();
});
