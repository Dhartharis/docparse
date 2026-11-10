import { expect, test } from "@playwright/test";
import AxeBuilder from "@axe-core/playwright";

test.skip(
  !process.env.DOCPARSE_REAL_API,
  "Run through tests/backend_integration.py --browser for an isolated real API/database.",
);
test("real Admin invitation, registration, confirmation, username login and recovery work together", async ({
  page,
  request,
}) => {
  await page.route("**/localization-api/**", (route) => route.abort());
  const email = `ui-${crypto.randomUUID()}@example.test`;
  const username = `ui-${crypto.randomUUID().slice(0, 8)}`;
  const password = `Ui-${crypto.randomUUID()}!aA1`;
  async function mail(subject: string) {
    let body = "";
    await expect
      .poll(async () => {
        const response = await request.get(
          `http://127.0.0.1:8025/api/v1/search?query=${encodeURIComponent(`to:${email}`)}`,
        );
        const results = await response.json();
        const message = results.messages.find(
          (entry: { Subject: string }) => entry.Subject === subject,
        );
        if (!message) return false;
        const result = await request.get(
          `http://127.0.0.1:8025/api/v1/message/${message.ID}`,
        );
        body = (await result.json()).HTML.replaceAll("&amp;", "&");
        return true;
      })
      .toBe(true);
    return body;
  }
  async function login(identifier: string, secret: string) {
    await page
      .getByLabel("Username or email", { exact: true })
      .fill(identifier);
    await page.getByLabel("Password", { exact: true }).fill(secret);
    await page.getByRole("button", { name: "Sign in", exact: true }).click();
  }
  async function logout() {
    const dialog = page.getByRole("dialog");
    if (await dialog.isVisible())
      await dialog.getByRole("button", { name: "Close", exact: true }).click();
    await page.locator(".account-menu > summary").click();
    await page.getByRole("button", { name: "Sign out", exact: true }).click();
  }
  await page.goto("/");
  await page.setViewportSize({ width: 1440, height: 1000 });
  await login(
    process.env.DOCPARSE_TEST_ADMIN_EMAIL!,
    process.env.DOCPARSE_TEST_ADMIN_PASSWORD!,
  );
  await page.locator(".account-menu > summary").click();
  await page.getByText("Manage team", { exact: true }).click();
  await expect(
    page
      .getByRole("listitem")
      .getByText(process.env.DOCPARSE_TEST_ADMIN_EMAIL!, { exact: true }),
  ).toBeVisible();
  const adminRow = page
    .getByRole("listitem")
    .filter({ hasText: process.env.DOCPARSE_TEST_ADMIN_EMAIL! });
  await adminRow
    .getByRole("button", { name: "Make Viewer", exact: true })
    .click();
  await page
    .getByRole("button", { name: "Confirm change", exact: true })
    .click();
  await expect(page.getByRole("alert")).toContainText(
    "keep at least one Admin",
  );
  await page.getByRole("button", { name: "Cancel", exact: true }).click();
  const viewerRow = page
    .getByRole("listitem")
    .filter({ hasText: process.env.DOCPARSE_TEST_VIEWER_EMAIL! });
  await viewerRow
    .getByRole("button", { name: "Make Admin", exact: true })
    .click();
  await page
    .getByRole("button", { name: "Confirm change", exact: true })
    .click();
  await expect(
    viewerRow.getByRole("button", { name: "Make Viewer", exact: true }),
  ).toBeVisible();
  await viewerRow
    .getByRole("button", { name: "Make Viewer", exact: true })
    .click();
  await page
    .getByRole("button", { name: "Confirm change", exact: true })
    .click();
  await expect(
    viewerRow.getByRole("button", { name: "Make Admin", exact: true }),
  ).toBeVisible();
  await page.getByLabel("Email address", { exact: true }).fill(email);
  await page
    .getByRole("button", { name: "Send invitation", exact: true })
    .click();
  await expect(page.getByRole("status")).toContainText("Invitation queued");
  expect(
    (
      await new AxeBuilder({ page })
        .withTags(["wcag2a", "wcag2aa", "wcag22aa"])
        .analyze()
    ).violations,
  ).toEqual([]);
  await page.screenshot({
    path: "test-results/real-team-desktop.png",
    fullPage: true,
  });
  await logout();
  const invitationMail = await mail("DocParse organization invitation");
  const invitationToken = invitationMail.match(/#invite=([A-F0-9]{64})/)![1];
  await page.goto("/#invite=" + invitationToken);
  await expect(page.getByLabel("Email address", { exact: true })).toHaveValue(
    email,
  );
  await page.getByLabel("Username", { exact: true }).fill(username);
  await page.getByLabel("Password", { exact: true }).fill(password);
  await page.setViewportSize({ width: 390, height: 844 });
  expect(
    await page.evaluate(
      () => document.documentElement.scrollWidth <= innerWidth,
    ),
  ).toBe(true);
  await page.screenshot({
    path: "test-results/real-invitation-mobile.png",
    fullPage: true,
  });
  await page
    .getByRole("button", { name: "Create account", exact: true })
    .click();
  await expect(page.getByRole("status")).toContainText("Check your email");
  const confirmation = await mail("Confirm your DocParse email");
  const link = confirmation.match(/href="([^"]+)"/)![1];
  expect(
    link.startsWith(`${process.env.DOCPARSE_REAL_API}/api/auth/confirmEmail?`),
  ).toBe(true);
  expect((await request.get(link)).ok()).toBe(true);
  await login(username, password);
  await expect(page.getByText("DocParse Local", { exact: true })).toBeVisible();
  await expect(page.getByText("Manage team", { exact: true })).toHaveCount(0);
  await expect(page.getByText("Administrator", { exact: true })).toHaveCount(0);
  await expect(
    page.getByRole("heading", { name: "Your assistants will appear here." }),
  ).toBeVisible();
  expect(
    (
      await new AxeBuilder({ page })
        .withTags(["wcag2a", "wcag2aa", "wcag22aa"])
        .analyze()
    ).violations,
  ).toEqual([]);
  await page.screenshot({
    path: "test-results/real-viewer-mobile.png",
    fullPage: true,
  });
  await logout();
  await page.getByRole("button", { name: "Forgot password?" }).click();
  await page.getByLabel("Email address", { exact: true }).fill(email);
  await page.getByRole("button", { name: "Send reset code" }).click();
  await expect(page.getByLabel("Reset code", { exact: true })).toBeVisible();
  const recovery = await mail("Reset your DocParse password");
  await page
    .getByLabel("Reset code", { exact: true })
    .fill(recovery.match(/Reset code: ([^<\s]+)/)![1]);
  await page.getByLabel("Password", { exact: true }).fill(password + "New");
  await page
    .getByRole("button", { name: "Reset password", exact: true })
    .click();
  await expect(page.getByRole("status")).toContainText(
    "Your password has been reset",
  );
  await login(email, password + "New");
  await expect(page.getByText("DocParse Local", { exact: true })).toBeVisible();
  await logout();
  await login(
    process.env.DOCPARSE_TEST_ADMIN_EMAIL!,
    process.env.DOCPARSE_TEST_ADMIN_PASSWORD!,
  );
  await page.locator(".account-menu > summary").click();
  await page.getByText("Manage team", { exact: true }).click();
  const recipient = page.getByRole("listitem").filter({ hasText: email });
  await recipient
    .getByRole("button", { name: "Remove access", exact: true })
    .click();
  await page
    .getByRole("button", { name: "Confirm change", exact: true })
    .click();
  await expect(recipient).toHaveCount(0);
  await logout();
  await login(email, password + "New");
  await expect(
    page.getByRole("heading", { name: "A place for your knowledge." }),
  ).toBeVisible();
});
