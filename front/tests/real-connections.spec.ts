import { expect, test } from "@playwright/test";
import AxeBuilder from "@axe-core/playwright";

test("real Admin creates, updates, rotates and disconnects a source without exposing stored secrets", async ({ page }, testInfo) => {
  test.skip(!process.env.DOCPARSE_REAL_API, "Run through tests/connections_integration.py --browser.");
  await page.route("**/localization-api/**", route => route.abort());
  await page.goto("/");
  await page.getByLabel("Username or email", { exact: true }).fill(process.env.DOCPARSE_TEST_ADMIN_EMAIL!);
  await page.getByLabel("Password", { exact: true }).fill(process.env.DOCPARSE_TEST_ADMIN_PASSWORD!);
  await page.getByRole("button", { name: "Sign in", exact: true }).click();
  await page.locator(".account-menu > summary").click();
  await page.getByRole("button", { name: "Data connections", exact: true }).click();
  await page.getByRole("button", { name: "Add connection", exact: true }).click();
  await page.getByLabel("Connection name", { exact: true }).fill("Browser source");
  await page.getByLabel("Source business identifier", { exact: true }).fill("browser-business");
  await page.getByRole("button", { name: "Save connection", exact: true }).click();
  const secret = page.getByRole("textbox", { name: "Signing secret", exact: true });
  await expect(secret).toHaveValue(/^[a-f0-9]{64}$/);
  const original = await secret.inputValue();
  for (const width of [390, 1440]) {
    await page.setViewportSize({ width, height: 900 });
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
    expect((await new AxeBuilder({ page }).include("dialog").withTags(["wcag2a", "wcag2aa", "wcag21aa"]).analyze()).violations).toEqual([]);
    await page.screenshot({ path: testInfo.outputPath(`connections-secret-${width}.png`) });
  }
  await page.getByRole("button", { name: "Hide secret", exact: true }).click();
  let row = page.getByRole("listitem").filter({ has: page.getByRole("heading", { name: "Browser source", exact: true }) });
  await row.getByRole("button", { name: "Edit settings", exact: true }).click();
  await page.getByLabel("Connection name", { exact: true }).fill("Browser renamed");
  await page.getByLabel("Maximum stock age (seconds)", { exact: true }).fill("45");
  await page.getByRole("button", { name: "Save connection", exact: true }).click();
  row = page.getByRole("listitem").filter({ has: page.getByRole("heading", { name: "Browser renamed", exact: true }) });
  await row.getByRole("button", { name: "Replace receiving key", exact: true }).click();
  await page.getByRole("button", { name: "Confirm change", exact: true }).click();
  await expect(secret).toHaveValue(/^[a-f0-9]{64}$/);
  expect(await secret.inputValue()).not.toBe(original);
  await page.getByRole("button", { name: "Hide secret", exact: true }).click();
  await row.getByRole("button", { name: "Disconnect", exact: true }).click();
  await page.getByRole("button", { name: "Confirm change", exact: true }).click();
  await expect(row.getByText("Disconnected", { exact: true })).toBeVisible();
  await expect(row.getByRole("button")).toHaveCount(0);
  await expect(secret).toHaveCount(0);
});
