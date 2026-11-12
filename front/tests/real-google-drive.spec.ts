import { expect, test } from "@playwright/test";
import AxeBuilder from "@axe-core/playwright";
import { fakeGooglePicker } from "./google-picker-fixture";

test("Admin selects a Google file and previews real provider-backed data", async ({ page }, testInfo) => {
  test.skip(!process.env.DOCPARSE_GOOGLE_ACCOUNT, "Run tests/google_drive_integration.py --browser.");
  await page.route("**/localization-api/**", route => route.abort()); await fakeGooglePicker(page);
  await page.goto("/"); await page.getByLabel("Username or email", { exact: true }).fill(process.env.DOCPARSE_TEST_ADMIN_EMAIL!); await page.getByLabel("Password", { exact: true }).fill(process.env.DOCPARSE_TEST_ADMIN_PASSWORD!);
  await page.getByRole("button", { name: "Sign in", exact: true }).click(); await page.locator(".account-menu > summary").click();
  await page.getByRole("button", { name: "Data connections", exact: true }).click(); await page.getByRole("button", { name: "Google Drive", exact: true }).click();
  await page.getByRole("combobox", { name: "Google connection", exact: true }).selectOption(process.env.DOCPARSE_GOOGLE_ACCOUNT!);
  await page.getByRole("button", { name: "Choose file", exact: true }).click();
  await expect(page.getByRole("combobox", { name: "Offer identifier column", exact: true })).toHaveValue("ID");
  await page.getByLabel("Presentation for these rows", { exact: true }).fill("One carton"); await page.getByLabel("Quantity unit code", { exact: true }).fill("L"); await page.getByLabel("Currency code", { exact: true }).fill("USD");
  await page.getByRole("button", { name: "Preview mapped rows", exact: true }).click();
  await expect(page.getByText("999,999,999,999,999,999.123456 USD", { exact: true })).toBeVisible();
  await expect(page.getByText("0001 · One carton", { exact: true })).toBeVisible();
  await expect(page.getByText("0 L", { exact: true })).toBeVisible();
  await expect(page.getByLabel("Allow cached Excel formula results (they may be outdated)", { exact: true })).toHaveCount(0);
  for (const width of [390,1440]) {
    await page.setViewportSize({ width, height: 900 }); expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
    expect((await new AxeBuilder({ page }).include("dialog").withTags(["wcag2a","wcag2aa","wcag21aa"]).analyze()).violations).toEqual([]);
    await page.getByText("0001 · One carton", { exact: true }).scrollIntoViewIfNeeded();await page.screenshot({ path: testInfo.outputPath(`google-result-${width}.png`) });
  }
});
