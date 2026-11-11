import { expect, test } from "@playwright/test";
import AxeBuilder from "@axe-core/playwright";

test("real Admin sees the reconciled test subscription and opens its sandbox portal", async ({ page }, testInfo) => {
  test.skip(!process.env.DOCPARSE_REAL_API, "Run through tests/billing_integration.py --browser.");
  await page.route("**/localization-api/**", route => route.abort());
  await page.route("https://billing.stripe.com/**", route => route.fulfill({ contentType: "text/html", body: "<h1>Local test customer portal</h1>" }));
  await page.goto("/");
  await page.getByLabel("Username or email", { exact: true }).fill(process.env.DOCPARSE_TEST_ADMIN_EMAIL!);
  await page.getByLabel("Password", { exact: true }).fill(process.env.DOCPARSE_TEST_ADMIN_PASSWORD!);
  await page.getByRole("button", { name: "Sign in", exact: true }).click();
  await page.locator(".account-menu > summary").click();
  await page.getByRole("button", { name: "Test payments", exact: true }).click();
  const dialog = page.getByRole("dialog");
  await expect(dialog.getByText("Active", { exact: true })).toBeVisible();
  await expect(dialog.getByRole("button", { name: "Open test checkout", exact: true })).toBeDisabled();
  for (const width of [390, 1440]) {
    await page.setViewportSize({ width, height: 900 });
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
    expect((await new AxeBuilder({ page }).include("dialog").withTags(["wcag2a", "wcag2aa", "wcag21aa"]).analyze()).violations).toEqual([]);
    await page.screenshot({ path: testInfo.outputPath(`real-billing-${width}.png`) });
  }
  await dialog.getByRole("button", { name: "Manage test subscription", exact: true }).click();
  await expect(page.getByRole("heading", { name: "Local test customer portal" })).toBeVisible();
});
