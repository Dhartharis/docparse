import { expect, test } from "@playwright/test";
import AxeBuilder from "@axe-core/playwright";
import { readFile } from "node:fs/promises";

test.skip(
  !process.env.DOCPARSE_REAL_API,
  "Run through tests/backend_integration.py --browser with isolated local services.",
);
test("assistant cards connect knowledge, private branding, email grants, Viewer chat and revocation", async ({
  page,
}) => {
  await page.route("**/localization-api/**", (route) => route.abort());
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
    await page.getByText("Account", { exact: true }).click();
    await page.getByRole("button", { name: "Sign out", exact: true }).click();
  }
  await page.goto("/");
  await page.setViewportSize({ width: 1440, height: 1000 });
  await login(
    process.env.DOCPARSE_TEST_ADMIN_EMAIL!,
    process.env.DOCPARSE_TEST_ADMIN_PASSWORD!,
  );
  await page
    .getByRole("button", { name: "Create assistant", exact: false })
    .click();
  const create = page.getByRole("dialog");
  await create
    .getByLabel("Bot name", { exact: true })
    .fill("Company assistant");
  await create
    .getByLabel("Welcome message", { exact: true })
    .fill("Ask about company policies.");
  await create
    .getByRole("button", { name: "Create assistant", exact: true })
    .click();
  const card = page.getByRole("article", {
    name: "Company assistant",
    exact: true,
  });
  await expect(card).toBeVisible();
  await card.getByRole("tab", { name: "Knowledge", exact: true }).click();
  await card
    .getByRole("button", { name: "New knowledge base", exact: true })
    .click();
  await card
    .getByLabel("Knowledge base name", { exact: true })
    .fill("Company policies");
  await card.getByRole("button", { name: "Create base", exact: true }).click();
  await expect(
    card.getByRole("heading", { name: "Company policies", exact: true }),
  ).toBeVisible();
  const content =
    'UI_REAL_DOCUMENT_CONTENT\nLine two, comma and a quote: "hello"';
  await card.getByLabel("Choose a document", { exact: true }).setInputFiles({
    name: "Company policies.pdf",
    mimeType: "application/pdf",
    buffer: Buffer.from(content),
  });

  await expect(
    card.getByRole("status").filter({ hasText: "was received." }),
  ).toBeVisible();
  await card.getByRole("button", { name: /Company policies.pdf/ }).click();
  await expect(card.getByText(content, { exact: true })).toBeVisible();
  const originalEvent = page.waitForEvent("download");
  await card
    .getByRole("button", { name: "Download original", exact: true })
    .click();
  expect(await readFile((await (await originalEvent).path())!, "utf8")).toBe(
    content,
  );
  const csvEvent = page.waitForEvent("download");
  await card
    .getByRole("button", { name: "Export fields as CSV", exact: true })
    .click();
  expect(await readFile((await (await csvEvent).path())!, "utf8")).toContain(
    "'=SUM",
  );
  await card.getByRole("button", { name: "Move document to trash", exact: true }).click();
  await page.getByRole("dialog").getByRole("button", { name: "Move to trash", exact: true }).click();
  await expect(card.getByRole("button", { name: /Company policies.pdf/ })).toHaveCount(0);
  await page.locator(".account-menu > summary").click();
  await page.getByRole("button", { name: "Trash", exact: true }).click();
  const trash = page.getByRole("dialog");
  await trash.getByRole("button", { name: "Restore", exact: true }).click();
  await expect(trash.getByRole("status")).toHaveText("Restored.");
  await trash.getByRole("button", { name: "Close", exact: true }).click();
  await card.getByRole("tab", { name: "Knowledge", exact: true }).click();
  const baseToggle = card.getByRole("button", { name: "Company policies", exact: true });
  if (await baseToggle.getAttribute("aria-expanded") === "false") await baseToggle.click();
  await expect(card.getByRole("button", { name: /Company policies.pdf/ })).toBeVisible();
  await card
    .getByRole("button", { name: "Rename Company policies", exact: true })
    .click();
  await card
    .getByLabel("Knowledge base name", { exact: true })
    .fill("Company handbook");
  await card.getByLabel("Knowledge base name", { exact: true }).press("Enter");
  await expect(
    card.getByRole("heading", { name: "Company handbook", exact: true }),
  ).toBeVisible();
  await card.getByRole("tab", { name: "Settings", exact: true }).click();
  await card
    .getByRole("combobox", { name: "Accent color", exact: true })
    .selectOption("#2457c5");
  await expect(card.getByRole("status")).toHaveText("Synced");
  const png = Buffer.from(
    "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+/mwoAAAAASUVORK5CYII=",
    "base64",
  );
  await card
    .getByLabel("Choose logo", { exact: true })
    .setInputFiles({ name: "logo.png", mimeType: "image/png", buffer: png });
  await expect(card.getByRole("status")).toHaveText("Synced");
  await expect
    .poll(() =>
      card
        .locator(".bot-avatar")
        .evaluate((element: HTMLImageElement) => element.naturalWidth),
    )
    .toBe(1);
  await card.getByRole("tab", { name: "Access", exact: true }).click();
  await card.getByRole("button", { name: "Add email", exact: true }).click();
  const access = page.getByRole("dialog");
  await access
    .getByLabel("Email address", { exact: true })
    .fill(process.env.DOCPARSE_TEST_VIEWER_EMAIL!);
  await access.getByRole("button", { name: "Add access", exact: true }).click();
  await expect(access).toHaveCount(0);
  await expect(
    card.getByText(process.env.DOCPARSE_TEST_VIEWER_EMAIL!, { exact: true }),
  ).toBeVisible();
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
    if (width === 320)
      await page.screenshot({
        path: "test-results/assistant-mobile.png",
        fullPage: true,
      });
  }
  await page.screenshot({
    path: "test-results/assistant-desktop.png",
    fullPage: true,
  });
  await logout();
  await login(
    process.env.DOCPARSE_TEST_ADMIN_EMAIL!,
    process.env.DOCPARSE_TEST_ADMIN_PASSWORD!,
  );
  await card.getByRole("tab", { name: "Knowledge", exact: true }).click();
  await expect(
    card.getByRole("checkbox", {
      name: "Select Company handbook",
      exact: true,
    }),
  ).not.toBeChecked();
  await card
    .getByRole("button", { name: "Company handbook", exact: true })
    .click();
  await expect(
    card.getByRole("button", { name: /Company policies.pdf/ }),
  ).toBeVisible();
  await logout();
  await login(
    process.env.DOCPARSE_TEST_VIEWER_EMAIL!,
    process.env.DOCPARSE_TEST_VIEWER_PASSWORD!,
  );
  await page
    .getByRole("combobox", { name: "Organization", exact: true })
    .selectOption({ label: "DocParse Local" });
  await expect(
    page.getByRole("heading", { name: "Company assistant", exact: true }),
  ).toBeVisible();
  await expect(page.locator(".chat-panel")).toHaveCSS(
    "border-top-color",
    "rgb(36, 87, 197)",
  );
  await expect
    .poll(() =>
      page
        .locator(".bot-avatar")
        .evaluate((element: HTMLImageElement) => element.naturalWidth),
    )
    .toBe(1);
  await page
    .getByLabel("Your question", { exact: true })
    .fill("What are the policies?");
  await page.getByRole("button", { name: "Send", exact: true }).click();
  await expect(page.locator(".message-answer")).toContainText(
    "UI_REAL_DOCUMENT_CONTENT",
  );
  await page.getByText("Sources (1)", { exact: true }).click();
  await expect(
    page.getByText("Company policies.pdf", { exact: true }),
  ).toBeVisible();
  await logout();
  await login(
    process.env.DOCPARSE_TEST_ADMIN_EMAIL!,
    process.env.DOCPARSE_TEST_ADMIN_PASSWORD!,
  );
  await card.getByRole("tab", { name: "Access", exact: true }).click();
  await card
    .getByRole("button", {
      name: `Remove access for ${process.env.DOCPARSE_TEST_VIEWER_EMAIL!}`,
      exact: true,
    })
    .click();
  await expect(
    card.getByText(process.env.DOCPARSE_TEST_VIEWER_EMAIL!, { exact: true }),
  ).toHaveCount(0);
  await logout();
  await login(
    process.env.DOCPARSE_TEST_VIEWER_EMAIL!,
    process.env.DOCPARSE_TEST_VIEWER_PASSWORD!,
  );
  await page
    .getByRole("combobox", { name: "Organization", exact: true })
    .selectOption({ label: "DocParse Local" });
  await expect(
    page.getByRole("heading", { name: "Your assistants will appear here." }),
  ).toBeVisible();
});
