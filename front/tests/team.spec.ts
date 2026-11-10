import { expect, test } from "@playwright/test";
import AxeBuilder from "@axe-core/playwright";

for (const selfChange of [false, true]) {
  test(
    selfChange
      ? "self demotion reloads trusted roles and removes administration"
      : "member removal confirms explicitly and the last Admin is protected",
    async ({ page }) => {
      let ownRole = "Admin";
      let removed = false;
      let mutations = 0;
      await page.route("**/localization-api/**", (route) => route.abort());
      await page.route("**/api/**", async (route) => {
        const request = route.request();
        const path = new URL(request.url()).pathname;
        if (path.endsWith("/login"))
          return route.fulfill({
            json: {
              accessToken: "access",
              refreshToken: "refresh",
              expiresIn: 900,
            },
          });
        if (path.endsWith("/me"))
          return route.fulfill({ json: { email: "admin@example.test" } });
        if (path === "/api/organizations/")
          return route.fulfill({
            json: [{ id: "org", name: "Acme", role: ownRole }],
          });
        if (path.endsWith("/bots/")) return route.fulfill({ json: [] });
        if (path.endsWith("/invitations")) return route.fulfill({ json: [] });
        if (path.endsWith("/members"))
          return route.fulfill({
            json: [
              { id: "admin", email: "admin@example.test", role: ownRole },
              ...(removed
                ? []
                : [
                    {
                      id: "other",
                      email: "other@example.test",
                      role: selfChange ? "Admin" : "Viewer",
                    },
                  ]),
            ],
          });
        if (path.includes("/members/")) {
          mutations++;
          if (path.endsWith("/admin")) {
            if (!selfChange)
              return route.fulfill({
                status: 409,
                json: { code: "last_admin" },
              });
            expect(request.postDataJSON()).toEqual({ role: "Viewer" });
            ownRole = "Viewer";
          } else {
            expect(request.method()).toBe("DELETE");
            removed = true;
          }
          return route.fulfill({ status: 204 });
        }
        return route.fulfill({ status: 404, json: {} });
      });
      await page.goto("/");
      await page.getByLabel("Username or email", { exact: true }).fill("admin");
      await page
        .getByLabel("Password", { exact: true })
        .fill("example-password");
      await page.getByRole("button", { name: "Sign in", exact: true }).click();
      await page.locator(".account-menu > summary").click();
      await page.getByText("Manage team", { exact: true }).click();
      const admin = page
        .getByRole("listitem")
        .filter({ hasText: "admin@example.test" });
      await admin
        .getByRole("button", { name: "Make Viewer", exact: true })
        .click();
      await expect(
        page.getByRole("button", { name: "Confirm change", exact: true }),
      ).toBeFocused();
      expect(mutations).toBe(0);
      await page
        .getByRole("button", { name: "Confirm change", exact: true })
        .click();
      if (selfChange) {
        await expect(
          page.getByText("Manage team", { exact: true }),
        ).toHaveCount(0);
        await expect(
          page.getByRole("button", { name: "Bots", exact: true }),
        ).toHaveCount(0);
        await expect(
          page.getByRole("heading", {
            name: "Your assistants will appear here.",
          }),
        ).toBeVisible();
      } else {
        await expect(page.getByRole("alert")).toContainText(
          "keep at least one Admin",
        );
        await page.getByRole("button", { name: "Cancel", exact: true }).click();
        await expect(
          admin.getByRole("button", { name: "Make Viewer", exact: true }),
        ).toBeFocused();
        const other = page
          .getByRole("listitem")
          .filter({ hasText: "other@example.test" });
        await other
          .getByRole("button", { name: "Remove access", exact: true })
          .click();
        await page.getByRole("button", { name: "Cancel", exact: true }).click();
        expect(mutations).toBe(1);
        await other
          .getByRole("button", { name: "Remove access", exact: true })
          .click();
        await page
          .getByRole("button", { name: "Confirm change", exact: true })
          .click();
        await expect(other).toHaveCount(0);
        expect(mutations).toBe(2);
        await page.setViewportSize({ width: 320, height: 1000 });
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
      }
    },
  );
}
