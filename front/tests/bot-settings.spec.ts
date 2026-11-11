import { test, expect } from "@playwright/test";
import type { Route } from "@playwright/test";
import AxeBuilder from "@axe-core/playwright";

test("parameters autosync serially, retain failed edits and logo, and expose a fourth API tab", async ({
  page,
}) => {
  await page.emulateMedia({ reducedMotion: "reduce" });
  await page.clock.install();
  const original = {
    id: "bot",
    name: "Assistant",
    welcomeMessage: "Welcome",
    accentColor: "#7441ec",
    logoVersion: null as string | null,
  };
  let saved = { ...original };
  let held: Route | undefined;
  let patches = 0;
  let logos = 0;
  let active = 0;
  let maximumActive = 0;
  const png = Buffer.from(
    "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+/mwoAAAAASUVORK5CYII=",
    "base64",
  );
  await page.route("**/localization-api/**", (route) => route.abort());
  await page.route("**/api/**", async (route) => {
    const path = new URL(route.request().url()).pathname;
    if (path.endsWith("/login"))
      return route.fulfill({
        json: {
          accessToken: "access",
          refreshToken: "refresh",
          expiresIn: 900,
        },
      });
    if (path.endsWith("/me"))
      return route.fulfill({
        json: { email: "admin@example.test", userName: "Admin" },
      });
    if (path === "/api/organizations/")
      return route.fulfill({
        json: [{ id: "org", name: "Company", role: "Admin" }],
      });
    if (path.endsWith("/channels/keys")) return route.fulfill({ json: [] });
    if (path.endsWith("/channels/embed"))
      return route.fulfill({ json: { enabled: false, origins: [] } });
    if (path.endsWith("/bots/")) return route.fulfill({ json: [saved] });
    if (path.endsWith("/bots/bot") && route.request().method() === "PATCH") {
      patches++;
      active++;
      maximumActive = Math.max(maximumActive, active);
      if (patches === 1) {
        held = route;
        return;
      }
      active--;
      if (patches === 2) return route.fulfill({ status: 503, json: {} });
      saved = { ...saved, ...route.request().postDataJSON() };
      return route.fulfill({ status: 204 });
    }
    if (path.endsWith("/logo")) {
      if (route.request().method() === "GET")
        return route.fulfill({ contentType: "image/png", body: png });
      logos++;
      if (logos === 1) return route.fulfill({ status: 503, json: {} });
      saved = { ...saved, logoVersion: "version-2" };
      return route.fulfill({ json: { logoVersion: "version-2" } });
    }
    return route.fulfill({ status: 404, json: {} });
  });
  await page.goto("/");
  await page.getByLabel("Username or email", { exact: true }).fill("admin");
  await page.getByLabel("Password", { exact: true }).fill("password");
  await page.getByRole("button", { name: "Sign in", exact: true }).click();
  const card = page.getByRole("article");
  await expect(card.getByRole("status")).toHaveText("Synced");
  await expect(card.locator(".card-knowledge-link")).toHaveCount(0);
  await expect(card.getByRole("tab")).toHaveCount(4);
  await expect(card.locator(".assistant-card-header [role=status]")).toHaveText(
    "Synced",
  );
  await expect(page.locator(".workspace-toolbar")).toHaveCount(0);
  await expect(card.locator(".assistant-card-body [role=status]")).toHaveCount(
    0,
  );
  await expect(
    card.getByRole("button", { name: /Save details|Save logo/ }),
  ).toHaveCount(0);
  await card.getByLabel("Bot name", { exact: true }).fill("First edit");
  await expect(card.getByRole("status")).toContainText("Changes pending");
  await page.clock.runFor(650);
  await expect.poll(() => patches).toBe(1);
  await card.getByLabel("Bot name", { exact: true }).fill("Final edit");
  await card
    .getByLabel("Welcome message", { exact: true })
    .fill("Latest welcome");
  await card
    .getByRole("combobox", { name: "Accent color", exact: true })
    .selectOption("#2457c5");
  await page.clock.runFor(650);
  expect(patches).toBe(1);
  saved = { ...saved, ...held!.request().postDataJSON() };
  active--;
  await held!.fulfill({ status: 204 });
  await expect(
    card.getByRole("heading", { name: "First edit", exact: true }),
  ).toBeVisible();
  await page.clock.runFor(650);
  await expect(card.getByRole("status")).toContainText("Not synced");
  await expect(card.getByLabel("Bot name", { exact: true })).toHaveValue(
    "Final edit",
  );
  await expect(card.getByLabel("Welcome message", { exact: true })).toHaveValue(
    "Latest welcome",
  );
  await card.getByRole("button", { name: "Try again", exact: true }).click();
  await page.clock.runFor(650);
  await expect(card.getByRole("status")).toHaveText("Synced");
  expect(saved).toMatchObject({
    name: "Final edit",
    welcomeMessage: "Latest welcome",
    accentColor: "#2457c5",
  });
  expect(maximumActive).toBe(1);
  await card
    .getByLabel("Choose logo", { exact: true })
    .setInputFiles({ name: "logo.png", mimeType: "image/png", buffer: png });
  await page.clock.runFor(650);
  await expect(card.getByRole("status")).toContainText("Not synced");
  expect(
    await card
      .getByLabel("Choose logo", { exact: true })
      .evaluate((input: HTMLInputElement) => input.files?.[0]?.name),
  ).toBe("logo.png");
  await card.getByRole("button", { name: "Try again", exact: true }).click();
  await page.clock.runFor(650);
  await expect(card.getByRole("status")).toHaveText("Synced");
  expect(logos).toBe(2);
  expect(patches).toBe(3);
  await card.getByLabel("Bot name", { exact: true }).fill(" ");
  await page.clock.runFor(650);
  await expect(card.getByRole("alert")).toContainText(
    "Enter an assistant name",
  );
  expect(patches).toBe(3);
  await card
    .getByRole("button", { name: "Discard pending changes", exact: true })
    .click();
  await expect(card.getByLabel("Bot name", { exact: true })).toHaveValue(
    "Final edit",
  );
  await card.getByRole("tab", { name: "API", exact: true }).click();
  await expect(
    card.getByRole("heading", { name: "External API", exact: true }),
  ).toBeVisible();
  await page.keyboard.press("Home");
  await expect(
    card.getByRole("tab", { name: "Settings", exact: true }),
  ).toBeFocused();
  for (const [width, height] of [
    [320, 568],
    [390, 844],
    [768, 1024],
    [1366, 768],
    [1920, 1080],
    [844, 390],
  ]) {
    await page.setViewportSize({ width, height });
    await page.clock.runFor(50);
    expect(
      await page.evaluate(
        () => document.documentElement.scrollHeight <= innerHeight,
      ),
    ).toBe(true);
    expect(
      await page.evaluate(
        () => document.documentElement.scrollWidth <= innerWidth,
      ),
    ).toBe(true);
    expect(
      (
        await new AxeBuilder({ page })
          .withTags(["wcag2a", "wcag2aa", "wcag22aa"])
          .analyze()
      ).violations,
    ).toEqual([]);
    await page.screenshot({
      path: `test-results/autosync-${width}.png`,
      fullPage: true,
    });
  }
  await page.setViewportSize({ width: 320, height: 568 });
  await page.clock.runFor(50);
  await page
    .getByRole("button", { name: "Previous column", exact: true })
    .click();
  await expect(page.locator(".assistant-create-card")).toBeInViewport({
    ratio: 0.9,
  });
  await page.getByRole("button", { name: "Next column", exact: true }).click();
  await expect(card).toBeInViewport({ ratio: 0.9 });
  expect(
    await card
      .locator(".assistant-card-body")
      .evaluate((node) => node.scrollHeight > node.clientHeight),
  ).toBe(true);
  await card
    .getByRole("combobox", { name: "Accent color", exact: true })
    .focus();
  await expect(
    card.getByRole("combobox", { name: "Accent color", exact: true }),
  ).toBeInViewport();
  expect(
    await page.evaluate(
      () => document.documentElement.scrollHeight <= innerHeight,
    ),
  ).toBe(true);
});
