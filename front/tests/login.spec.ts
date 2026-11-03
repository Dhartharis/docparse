import { expect, test } from "@playwright/test";
import AxeBuilder from "@axe-core/playwright";

test.beforeEach(async ({ context }) => {
  // Automated tests never depend on the live localization service.
  await context.route("**/localization-api/**", (route) => route.abort());
});

test("validates credentials, supports keyboard, and never sends a preview login", async ({
  page,
}) => {
  const requests: string[] = [];
  page.on("request", (request) => {
    if (
      request.method() === "POST" ||
      (request.isNavigationRequest() && request.method() !== "GET")
    )
      requests.push(request.url());
  });
  await page.goto("/");
  await page.getByRole("button", { name: "Sign in", exact: true }).click();
  await expect(page.getByLabel("Username", { exact: true })).toBeFocused();
  await expect(
    page.getByText("Enter your username.", { exact: true }),
  ).toBeVisible();
  await page.getByLabel("Username", { exact: true }).fill("preview-user");
  await page.getByRole("button", { name: "Sign in", exact: true }).click();
  await expect(page.getByLabel("Password", { exact: true })).toBeFocused();
  await expect(
    page.getByText("Enter your password.", { exact: true }),
  ).toBeVisible();
  await page.getByLabel("Username", { exact: true }).focus();
  await page.keyboard.press("Tab");
  await expect(page.getByLabel("Password", { exact: true })).toBeFocused();
  await page.keyboard.type("preview-only-password");
  await page.keyboard.press("Tab");
  await expect(
    page.getByRole("button", { name: "Show password" }),
  ).toBeFocused();
  await page.keyboard.press("Space");
  await expect(page.getByLabel("Password", { exact: true })).toHaveAttribute(
    "type",
    "text",
  );
  await page.keyboard.press("Space");
  await expect(page.getByLabel("Password", { exact: true })).toHaveAttribute(
    "type",
    "password",
  );
  await page.keyboard.press("Tab");
  await page.keyboard.press("Enter");
  await expect(page.getByRole("status")).toContainText(
    "Your credentials have not been sent or saved.",
  );
  expect(requests).toEqual([]);
  expect(await page.evaluate(() => ({ ...localStorage }))).toEqual({});
});

test("uses bundled language, persists explicit choice, and falls back to English", async ({
  browser,
}) => {
  const context = await browser.newContext({ locale: "es-CL" });
  await context.route("**/localization-api/**", (route) => route.abort());
  const page = await context.newPage();
  await page.goto("/");
  await expect(
    page.getByRole("button", { name: "Iniciar sesión" }),
  ).toBeVisible();
  await page.getByRole("combobox", { name: "Idioma" }).selectOption("en");
  await page.reload();
  await expect(
    page.getByRole("button", { name: "Sign in", exact: true }),
  ).toBeVisible();
  await page.evaluate(() =>
    localStorage.setItem("docparse.language", "unsupported"),
  );
  await page.reload();
  await expect(page.locator("html")).toHaveAttribute("lang", "es");
  await context.close();
  const fallback = await browser.newContext({ locale: "fr-FR" });
  await fallback.route("**/localization-api/**", (route) => route.abort());
  const fallbackPage = await fallback.newPage();
  await fallbackPage.goto("/");
  await expect(fallbackPage.locator("html")).toHaveAttribute("lang", "en");
  await fallback.close();
});

test("reflows and passes automated accessibility checks in all supported languages", async ({
  page,
}) => {
  await page.goto("/");
  for (const language of ["en", "es", "vi"]) {
    await page.locator("select").selectOption(language);
    for (const width of [320, 390, 768, 1440]) {
      await page.setViewportSize({ width, height: 900 });
      expect(
        await page.evaluate(
          () => document.documentElement.scrollWidth <= innerWidth,
        ),
      ).toBe(true);
      await expect(page.locator('button[type="submit"]')).toBeVisible();
      const report = await new AxeBuilder({ page })
        .withTags(["wcag2a", "wcag2aa", "wcag21aa", "wcag22aa"])
        .analyze();
      expect(report.violations).toEqual([]);
    }
  }
});
