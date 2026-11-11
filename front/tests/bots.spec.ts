import { expect, test } from "@playwright/test";
import AxeBuilder from "@axe-core/playwright";

test("assistant cards, scoped email access modal, search, pending email and revocation", async ({
  page,
}) => {
  let entries: { email: string; pending: boolean }[] = [];
  let grants = 0;
  let created = false;
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
      return route.fulfill({
        json: { email: "admin@example.test", userName: "dhartharis" },
      });
    if (path === "/api/organizations/")
      return route.fulfill({
        json: [{ id: "org", name: "Company", role: "Admin" }],
      });
    if (path.endsWith("/bots/")) {
      if (request.method() === "POST") {
        expect(request.postDataJSON()).toEqual({
          name: "People assistant",
          welcomeMessage: "Hello",
          accentColor: "#7441ec",
        });
        created = true;
        return route.fulfill({
          status: 201,
          json: {
            id: "bot",
            name: "People assistant",
            welcomeMessage: "Hello",
          },
        });
      }
      return route.fulfill({
        json: created
          ? [{ id: "bot", name: "People assistant", welcomeMessage: "Hello" }]
          : [],
      });
    }
    if (path.endsWith("/access/")) {
      if (request.method() === "GET") return route.fulfill({ json: entries });
      if (request.method() === "DELETE") {
        expect(new URL(request.url()).searchParams.get("email")).toBe(
          "member@example.test",
        );
        entries = entries.filter(
          (entry) => entry.email !== "member@example.test",
        );
        return route.fulfill({ status: 204 });
      }
      grants++;
      const body = request.postDataJSON();
      if (grants === 1) return route.fulfill({ status: 409, json: {} });
      expect(body.allBots).toBe(grants === 2);
      entries = [
        ...entries,
        { email: body.email, pending: body.email === "pending@example.test" },
      ];
      return route.fulfill({
        json: {
          pending: body.email === "pending@example.test",
          affectedBots: 1,
        },
      });
    }
    return route.fulfill({ status: 404, json: {} });
  });
  await page.goto("/");
  await page.getByLabel("Username or email", { exact: true }).fill("admin");
  await page.getByLabel("Password", { exact: true }).fill("example-password");
  await page.getByRole("button", { name: "Sign in", exact: true }).click();
  await expect(
    page.getByRole("heading", { name: "dhartharis", exact: true }),
  ).toBeVisible();
  await expect(
    page.getByRole("heading", { name: "Your first assistant starts here." }),
  ).toBeVisible();
  await expect(page.locator(".portal-entry")).toHaveCount(1);
  await page.keyboard.press("Escape");
  await expect(page.locator(".portal-entry")).toHaveCount(0);
  await page
    .getByRole("button", { name: "Create assistant", exact: false })
    .click();
  let dialog = page.getByRole("dialog");
  await expect(dialog.getByLabel("Bot name", { exact: true })).toBeFocused();
  await dialog.getByLabel("Bot name", { exact: true }).fill("People assistant");
  await dialog.getByLabel("Welcome message", { exact: true }).fill("Hello");
  await dialog
    .getByRole("button", { name: "Create assistant", exact: true })
    .click();
  await expect(dialog).toHaveCount(0);
  await page.getByRole("tab", { name: "Access", exact: true }).click();
  await page.getByRole("button", { name: "Add email", exact: true }).click();
  dialog = page.getByRole("dialog");
  await expect(
    dialog.getByLabel("Email address", { exact: true }),
  ).toBeFocused();
  await dialog
    .getByLabel("Email address", { exact: true })
    .fill("member@example.test");
  await dialog
    .getByRole("checkbox", {
      name: "Give access to all current assistants",
      exact: true,
    })
    .check();
  await dialog.getByRole("button", { name: "Add access", exact: true }).click();
  await expect(dialog.getByRole("alert")).toContainText(
    "Check the information",
  );
  await expect(dialog.getByLabel("Email address", { exact: true })).toHaveValue(
    "member@example.test",
  );
  await dialog.getByRole("button", { name: "Add access", exact: true }).click();
  await expect(dialog).toHaveCount(0);
  await expect(
    page.getByText("member@example.test", { exact: true }),
  ).toBeVisible();
  await page.getByRole("button", { name: "Add email", exact: true }).click();
  dialog = page.getByRole("dialog");
  await dialog
    .getByLabel("Email address", { exact: true })
    .fill("pending@example.test");
  await dialog.getByRole("button", { name: "Add access", exact: true }).click();
  await expect(
    page.getByText("Pending membership", { exact: true }),
  ).toBeVisible();
  await page
    .getByRole("searchbox", { name: "Search by email", exact: true })
    .fill("member@");
  await expect(
    page.getByText("pending@example.test", { exact: true }),
  ).toHaveCount(0);
  await page
    .getByRole("button", {
      name: "Remove access for member@example.test",
      exact: true,
    })
    .click();
  await expect(
    page.getByText("member@example.test", { exact: true }),
  ).toHaveCount(0);
  await page
    .getByRole("searchbox", { name: "Search by email", exact: true })
    .fill("");
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
  }
  await page.screenshot({
    path: "test-results/assistant-cards.png",
    fullPage: true,
  });
});
