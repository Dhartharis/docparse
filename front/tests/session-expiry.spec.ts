import { test, expect } from "@playwright/test";
import AxeBuilder from "@axe-core/playwright";

for (const condition of ["revoked-refresh", "transport-reload"] as const) {
  test(`expired session returns to one branded login notice after ${condition}`, async ({
    page,
  }) => {
    await page.emulateMedia({ reducedMotion: "reduce" });
    let expire = false;
    let refreshes = 0;
    await page.route("**/localization-api/**", (route) => route.abort());
    await page.route("**/api/**", (route) => {
      const path = new URL(route.request().url()).pathname;
      if (path.endsWith("/login"))
        return route.fulfill({
          json: {
            accessToken: "private-access",
            refreshToken: "private-refresh",
            expiresIn: 900,
          },
        });
      if (path.endsWith("/refresh")) {
        refreshes++;
        return route.fulfill({ status: 403, json: {} });
      }
      if (expire) return route.fulfill({ status: 401, json: {} });
      if (path.endsWith("/me"))
        return route.fulfill({
          json: { email: "admin@example.test", userName: "Admin" },
        });
      if (path === "/api/organizations/")
        return route.fulfill({
          json: [{ id: "org", name: "Workspace", role: "Admin" }],
        });
      if (path.endsWith("/bots/"))
        return route.fulfill({
          json: [{ id: "bot", name: "Assistant", welcomeMessage: "Hello" }],
        });
      if (path.endsWith("/access/")) return route.fulfill({ json: [] });
      return route.fulfill({ status: 404, json: {} });
    });
    async function login() {
      await page.getByLabel("Username or email", { exact: true }).fill("admin");
      await page
        .getByLabel("Password", { exact: true })
        .fill("secret-password");
      await page.getByRole("button", { name: "Sign in", exact: true }).click();
      await expect(
        page.getByRole("heading", { name: "Admin", exact: true }),
      ).toBeVisible();
      await expect(
        page.locator(".assistant-card-header [role=status]"),
      ).toHaveText("Synced");
    }
    await page.goto("/");
    await login();
    if (condition === "revoked-refresh") {
      expire = true;
      await page.getByRole("tab", { name: "Access", exact: true }).click();
    } else {
      // A fresh transport module has no tokens and must reach the App's existing listener.
      await page.evaluate(async () => {
        const path = "/src/services/api.ts?session-regression";
        const transport = await import(/* @vite-ignore */ path);
        await transport.api("/auth/me").catch(() => undefined);
      });
    }
    await expect(page.locator(".workspace-shell")).toHaveCount(0);
    await expect(page.locator(".session-notice")).toHaveCount(1);
    await expect(page.locator(".session-notice")).toHaveText(
      "Your session has ended.",
    );
    await expect(
      page.getByRole("heading", { name: "Welcome back.", exact: true }),
    ).toBeVisible();
    await expect(
      page.getByRole("button", { name: "Try again", exact: true }),
    ).toHaveCount(0);
    await expect(
      page.getByLabel("Username or email", { exact: true }),
    ).toBeFocused();
    await expect(page.getByLabel("Password", { exact: true })).toHaveValue("");
    for (const width of [320, 768, 1440]) {
      await page.setViewportSize({ width, height: 1000 });
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
      if (condition === "revoked-refresh")
        await page.screenshot({
          path: `test-results/session-expired-${width}.png`,
          fullPage: true,
        });
    }
    expire = false;
    await login();
    await expect(page.locator(".session-notice")).toHaveCount(0);
    expect(refreshes).toBe(condition === "revoked-refresh" ? 1 : 0);
    const storage = await page.evaluate(() =>
      JSON.stringify({ ...localStorage, ...sessionStorage }),
    );
    expect(storage).not.toContain("private-access");
    expect(storage).not.toContain("private-refresh");
  });
}
