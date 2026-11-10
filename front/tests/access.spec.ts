import { test, expect } from "@playwright/test";
import AxeBuilder from "@axe-core/playwright";

test.beforeEach(async ({ page }) => {
  await page.route("**/localization-api/**", (route) => route.abort());
});

test("a Viewer enters only assigned chat and loses it when the server revokes access", async ({
  page,
}) => {
  let assigned = true;
  await page.route("**/api/**", async (route) => {
    const path = new URL(route.request().url()).pathname;
    if (path === "/api/auth/login")
      return route.fulfill({
        json: {
          accessToken: "test-access",
          refreshToken: "test-refresh",
          expiresIn: 900,
        },
      });
    if (path === "/api/auth/me")
      return route.fulfill({ json: { email: "viewer@example.test" } });
    if (path === "/api/organizations/")
      return route.fulfill({
        json: [{ id: "org", name: "Acme", role: "Viewer" }],
      });
    if (path.endsWith("/bots/"))
      return route.fulfill({
        json: assigned
          ? [
              {
                id: "bot",
                name: "People assistant",
                welcomeMessage: "Ask about onboarding.",
              },
            ]
          : [],
      });
    if (path.endsWith("/messages")) {
      if (!assigned) return route.fulfill({ status: 404, json: {} });
      return route.fulfill({
        json: {
          answer: "Contact the People team before your first day.",
          sources: [{ documentId: "source-id", title: "Onboarding.pdf" }],
        },
      });
    }
    return route.fulfill({ status: 404, json: {} });
  });
  await page.goto("/");
  await page
    .getByLabel(/^(Email address|Username or email)$/, { exact: true })
    .fill("viewer@example.test");
  await page.getByLabel("Password", { exact: true }).fill("example-password");
  await page.getByRole("button", { name: "Sign in", exact: true }).click();
  await expect(
    page.getByRole("heading", { name: "People assistant" }),
  ).toBeVisible();
  await expect(page.getByText("Administrator", { exact: true })).toHaveCount(0);
  await page
    .getByLabel("Your question", { exact: true })
    .fill("How do I get ready?");
  await page.getByRole("button", { name: "Send", exact: true }).click();
  await expect(
    page.getByText("Contact the People team before your first day.", {
      exact: true,
    }),
  ).toBeVisible();
  await page.getByText("Sources (1)", { exact: true }).click();
  await expect(page.getByText("Onboarding.pdf", { exact: true })).toBeVisible();
  for (const width of [320, 1440]) {
    await page.setViewportSize({ width, height: 900 });
    expect(
      await page.evaluate(
        () => document.documentElement.scrollWidth <= innerWidth,
      ),
    ).toBe(true);
    expect(
      (
        await new AxeBuilder({ page })
          .withTags(["wcag2a", "wcag2aa", "wcag21aa", "wcag22aa"])
          .analyze()
      ).violations,
    ).toEqual([]);
  }
  await page.screenshot({
    path: "test-results/chat-desktop.png",
    fullPage: true,
  });
  const storage = await page.evaluate(() =>
    JSON.stringify({
      local: { ...localStorage },
      session: { ...sessionStorage },
    }),
  );
  expect(storage).not.toContain("example-password");
  expect(storage).not.toContain("test-access");
  expect(storage).not.toContain("test-refresh");
  assigned = false;
  await page
    .getByLabel("Your question", { exact: true })
    .fill("Another question");
  await page.getByRole("button", { name: "Send", exact: true }).click();
  await expect(
    page.getByRole("heading", { name: "Your assistants will appear here." }),
  ).toBeVisible();
  await expect(
    page.getByRole("heading", { name: "People assistant" }),
  ).toHaveCount(0);
});

test("registration, recovery and network errors retain useful feedback", async ({
  page,
}) => {
  await page.route("**/api/invitations/preview", (route) =>
    route.fulfill({
      json: {
        email: "member@example.test",
        organization: "Company",
        organizationId: "org",
        existingAccount: false,
      },
    }),
  );
  await page.route("**/api/invitations/register", (route) =>
    route.fulfill({ status: 200, body: "" }),
  );
  await page.route("**/api/auth/forgotPassword", (route) =>
    route.fulfill({ status: 200, body: "" }),
  );
  await page.route("**/api/auth/resetPassword", (route) =>
    route.fulfill({ status: 200, body: "" }),
  );
  await page.route("**/api/auth/login", (route) => route.abort());
  await page.goto("/");
  await expect(
    page.getByRole("button", { name: "I have an invitation", exact: true }),
  ).toBeVisible();
  await page.goto("/#invite=" + "A".repeat(64));
  await expect(page.getByLabel("Email address", { exact: true })).toHaveValue(
    "member@example.test",
  );
  await page.getByLabel("Username", { exact: true }).fill("new-member");
  await page.getByLabel("Password", { exact: true }).fill("A-real-password!42");
  await page
    .getByRole("button", { name: "Create account", exact: true })
    .click();
  await expect(page.getByRole("status")).toContainText("Check your email");
  await expect(page.getByLabel("Password", { exact: true })).toHaveValue("");
  await page.getByRole("button", { name: "Forgot password?" }).click();
  await page.getByRole("button", { name: "Send reset code" }).click();
  await expect(page.getByLabel("Reset code", { exact: true })).toBeVisible();
  await page.getByLabel("Reset code", { exact: true }).fill("sample-code");
  await page.getByLabel("Password", { exact: true }).fill("A-new-password!42");
  await page
    .getByRole("button", { name: "Reset password", exact: true })
    .click();
  await expect(page.getByRole("status")).toContainText(
    "Your password has been reset",
  );
  await page.getByLabel("Password", { exact: true }).fill("A-new-password!42");
  await page.getByRole("button", { name: "Sign in", exact: true }).click();
  await expect(page.getByRole("alert")).toContainText("We couldn’t connect.");
  await expect(
    page.getByRole("button", { name: "Sign in", exact: true }),
  ).toBeEnabled();
});
