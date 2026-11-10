import { expect, test } from "@playwright/test";
import AxeBuilder from "@axe-core/playwright";
test.skip(
  !process.env.DOCPARSE_REAL_API,
  "Requires isolated local API and Mailpit.",
);

test("a business owner signs up without invitation and invites a scoped Viewer", async ({
  page,
  request,
}) => {
  await page.emulateMedia({ reducedMotion: "reduce" });
  await page.route("**/localization-api/**", (route) => route.abort());
  const suffix = crypto.randomUUID().slice(0, 8);
  const email = "owner-ui-" + suffix + "@example.test";
  const handle = "owner-ui-" + suffix;
  const guest = "guest-ui-" + suffix + "@example.test";
  const guestHandle = "guest-ui-" + suffix;
  const password = "Local-" + crypto.randomUUID() + "!aA1";
  const company = "Local business " + suffix;
  async function mail(recipient: string, subject: string) {
    let body = "";
    await expect
      .poll(async () => {
        const response = await request.get(
          "http://127.0.0.1:8025/api/v1/search?query=" +
            encodeURIComponent("to:" + recipient),
        );
        const row = (await response.json()).messages.find(
          (m: { Subject: string }) => m.Subject === subject,
        );
        if (!row) return false;
        body = (
          await (
            await request.get("http://127.0.0.1:8025/api/v1/message/" + row.ID)
          ).json()
        ).HTML.replaceAll("&amp;", "&");
        return true;
      })
      .toBe(true);
    return body;
  }
  async function logout() {
    await page.locator(".account-menu > summary").click();
    await page.getByRole("button", { name: "Sign out", exact: true }).click();
  }
  async function login(identifier: string) {
    await page
      .getByLabel("Username or email", { exact: true })
      .fill(identifier);
    await page.getByLabel("Password", { exact: true }).fill(password);
    await page.getByRole("button", { name: "Sign in", exact: true }).click();
  }
  await page.goto("/");
  await page
    .getByRole("button", { name: "Create business", exact: true })
    .click();
  await expect(page.getByLabel("Invitation code", { exact: true })).toHaveCount(
    0,
  );
  await page.getByLabel("Business name", { exact: true }).fill(company);
  await page.getByLabel("Email address", { exact: true }).fill(email);
  await page.getByLabel("Username", { exact: true }).fill(handle);
  await page.getByLabel("Password", { exact: true }).fill(password);
  await page.setViewportSize({ width: 390, height: 844 });
  expect(
    (
      await new AxeBuilder({ page })
        .withTags(["wcag2a", "wcag2aa", "wcag22aa"])
        .analyze()
    ).violations,
  ).toEqual([]);
  await page.screenshot({
    path: "test-results/owner-signup-mobile.png",
    fullPage: true,
  });
  await page
    .getByRole("button", { name: "Create my account", exact: true })
    .click();
  await expect(page.locator(".form-status")).toContainText("Check your email");
  const ownerMail = await mail(email, "Confirm your DocParse email");
  const ownerLink = ownerMail.match(/href="([^"]+)"/)![1];
  const invalid = new URL(ownerLink);
  invalid.searchParams.set("code", "invalid");
  await page.goto(invalid.toString());
  await expect(page.locator(".form-status")).toContainText(
    "could not be verified",
  );
  await page.goto(ownerLink);
  await expect(page.locator(".form-status")).toContainText("Email confirmed");
  await login(handle);
  await expect(page.getByText(company, { exact: true })).toBeVisible();
  await page.setViewportSize({ width: 1440, height: 1000 });
  await page
    .getByRole("button", { name: "Create assistant", exact: false })
    .click();
  await page
    .getByRole("dialog")
    .getByLabel("Bot name", { exact: true })
    .fill("Support");
  await page
    .getByRole("dialog")
    .getByRole("button", { name: "Create assistant", exact: true })
    .click();
  const card = page.getByRole("article", { name: "Support", exact: true });
  await card
    .getByRole("button", { name: "Move assistant to trash", exact: true })
    .click();
  await page
    .getByRole("dialog")
    .getByRole("button", { name: "Move to trash", exact: true })
    .click();
  await expect(card).toHaveCount(0);
  await page.locator(".account-menu > summary").click();
  await page.getByRole("button", { name: "Trash", exact: true }).click();
  const trash = page.getByRole("dialog");
  await trash.getByRole("button", { name: "Restore", exact: true }).click();
  await expect(trash.getByRole("status")).toHaveText("Restored.");
  await page.screenshot({ path: "test-results/trash-restored.png" });
  await trash.getByRole("button", { name: "Close", exact: true }).click();
  await expect(card).toBeVisible();
  await card.getByRole("tab", { name: "Access", exact: true }).click();
  await card.getByRole("button", { name: "Add email", exact: true }).click();
  await page
    .getByRole("dialog")
    .getByLabel("Email address", { exact: true })
    .fill(guest);
  await page
    .getByRole("dialog")
    .getByRole("button", { name: "Add access", exact: true })
    .click();
  await expect(card.getByText(guest, { exact: true })).toBeVisible();
  const guestMail = await mail(guest, "DocParse organization invitation");
  const invitation = guestMail.match(/#invite=([A-F0-9]{64})/)![1];
  await logout();
  await page.goto("/#invite=" + invitation);
  await expect(page.getByLabel("Email address", { exact: true })).toHaveValue(
    guest,
  );
  await expect(
    page.getByLabel("Email address", { exact: true }),
  ).not.toBeEditable();
  await page.getByLabel("Username", { exact: true }).fill(guestHandle);
  await page.getByLabel("Password", { exact: true }).fill(password);
  await page
    .getByRole("button", { name: "Create account", exact: true })
    .click();
  await expect(page.locator(".form-status")).toContainText("Check your email");
  const guestLink = (await mail(guest, "Confirm your DocParse email")).match(
    /href="([^"]+)"/,
  )![1];
  await page.goto(guestLink);
  await login(guestHandle);
  await expect(page.locator(".chat-heading h2")).toHaveText("Support");
  await expect(page.getByRole("tab")).toHaveCount(0);
  await expect(page.getByText("Manage team", { exact: true })).toHaveCount(0);
  await page
    .getByLabel("Your question", { exact: true })
    .fill("What do you know?");
  await page.getByLabel("Your question", { exact: true }).press("Enter");
  await expect(page.locator(".message-answer")).toBeVisible();
  await page
    .getByRole("button", {
      name: "Clear conversation and context",
      exact: true,
    })
    .click();
  await expect(page.locator(".chat-turn")).toHaveCount(0);
  await page.setViewportSize({ width: 390, height: 844 });
  expect(
    (
      await new AxeBuilder({ page })
        .withTags(["wcag2a", "wcag2aa", "wcag22aa"])
        .analyze()
    ).violations,
  ).toEqual([]);
  await page.screenshot({ path: "test-results/owner-invited-viewer.png" });
});
