import { expect, test } from "@playwright/test";
import AxeBuilder from "@axe-core/playwright";

test("Admin previews an actual XLSX catalog and preserves exact IDs and amounts", async ({ page }, testInfo) => {
  test.skip(!process.env.DOCPARSE_CATALOG_FIXTURE, "Run through tests/catalog_integration.py --browser.");
  await page.route("**/localization-api/**", route => route.abort());
  await page.goto("/");
  await page.getByLabel("Username or email", { exact: true }).fill(process.env.DOCPARSE_TEST_ADMIN_EMAIL!);
  await page.getByLabel("Password", { exact: true }).fill(process.env.DOCPARSE_TEST_ADMIN_PASSWORD!);
  await page.getByRole("button", { name: "Sign in", exact: true }).click();
  await page.locator(".account-menu > summary").click();
  await page.getByRole("button", { name: "Data connections", exact: true }).click();
  await page.getByRole("button", { name: "Preview spreadsheet", exact: true }).click();
  await page.getByLabel("Excel file (.xlsx, up to 10 MiB)", { exact: true }).setInputFiles(process.env.DOCPARSE_CATALOG_FIXTURE!);
  await page.getByRole("button", { name: "Read columns", exact: true }).click();
  await page.getByRole("combobox", { name: "Offer identifier column", exact: true }).selectOption("ID");
  await page.getByRole("combobox", { name: "Product name column", exact: true }).selectOption("Name");
  await page.getByLabel("Presentation for these rows", { exact: true }).fill("One carton");
  await page.getByLabel("Quantity unit code", { exact: true }).fill("L");
  await page.getByRole("combobox", { name: "Selling price column", exact: true }).selectOption("Price");
  await page.getByLabel("Currency code", { exact: true }).fill("USD");
  await page.getByRole("combobox", { name: "Available stock column", exact: true }).selectOption("Stock");
  await page.getByRole("combobox", { name: "Freshness timestamp policy", exact: true }).selectOption("column");
  await page.getByRole("combobox", { name: "Source observation timestamp column", exact: true }).selectOption("Updated");
  await page.getByRole("button", { name: "Preview mapped rows", exact: true }).click();
  await expect(page.getByText("999,999,999,999,999,999.123456 USD", { exact: true })).toBeVisible();
  await expect(page.getByText("0001 · One carton", { exact: true })).toBeVisible();
  await expect(page.getByText("0 L", { exact: true })).toBeVisible();
  for (const width of [390,1440]) {
    await page.setViewportSize({ width, height: 900 });
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
    expect((await new AxeBuilder({ page }).include("dialog").withTags(["wcag2a","wcag2aa","wcag21aa"]).analyze()).violations).toEqual([]);
    await page.getByRole("heading", { name: "Spreadsheet catalog preview", exact: true }).evaluate(element => element.scrollIntoView({ block: "start" }));
    await page.screenshot({ path: testInfo.outputPath(`catalog-${width}.png`) });
    await page.getByText("0001 · One carton", { exact: true }).scrollIntoViewIfNeeded();
    await page.screenshot({ path: testInfo.outputPath(`catalog-result-${width}.png`) });
  }
});
