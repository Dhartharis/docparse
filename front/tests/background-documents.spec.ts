import AxeBuilder from "@axe-core/playwright";
import { test, expect } from "@playwright/test";

test("accepted documents process without changing sync or blocking editing and survive tab changes", async ({
  page,
}) => {
  await page.emulateMedia({ reducedMotion: "reduce" });
  let state = "processing";
  let uploaded = false;
  let welcome = "Hello";
  await page.route("**/localization-api/**", (r) => r.abort());
  await page.route("**/api/**", (r) => {
    const request = r.request();
    const path = new URL(request.url()).pathname;
    if (path.endsWith("/login"))
      return r.fulfill({
        json: { accessToken: "token", refreshToken: "refresh", expiresIn: 900 },
      });
    if (path.endsWith("/me"))
      return r.fulfill({ json: { email: "admin@example.test" } });
    if (path === "/api/organizations/")
      return r.fulfill({
        json: [{ id: "org", name: "Company", role: "Admin" }],
      });
    if (path.endsWith("/bots/"))
      return r.fulfill({
        json: [{ id: "bot", name: "Assistant", welcomeMessage: welcome }],
      });
    if (path.endsWith("/bots/bot") && request.method() === "PATCH") {
      welcome = request.postDataJSON().welcomeMessage;
      return r.fulfill({ status: 204 });
    }
    if (path.endsWith("/knowledge-bases/"))
      return r.fulfill({ json: [{ id: "base", name: "Shared base" }] });
    if (path.endsWith("/bots/bot/knowledge-bases"))
      return r.fulfill({ json: { knowledgeBaseIds: ["base"] } });
    if (path.endsWith("/documents/all"))
      return r.fulfill({
        json: uploaded
          ? [
              {
                id: "doc",
                fileName: "Background.pdf",
                contentType: "application/pdf",
                documentType: "Document",
                createdAt: "2026-11-05T00:00:00Z",
                hasOriginal: true,
                processingStatus: state,
              },
            ]
          : [],
      });
    if (path.endsWith("/extract")) {
      uploaded = true;
      return r.fulfill({
        status: 202,
        json: { id: "doc", processingStatus: "queued" },
      });
    }
    if (path.endsWith("/documents/doc"))
      return r.fulfill({
        json: {
          document: {
            id: "doc",
            fileName: "Background.pdf",
            documentType: "Document",
            contentType: "application/pdf",
            createdAt: "2026-11-05T00:00:00Z",
            hasOriginal: true,
            processingStatus: "ready",
          },
          fields: { text: "Ready content" },
        },
      });
    return r.fulfill({ status: 404, json: {} });
  });
  await page.goto("/");
  await page.getByLabel("Username or email", { exact: true }).fill("admin");
  await page.getByLabel("Password", { exact: true }).fill("password");
  await page.getByRole("button", { name: "Sign in", exact: true }).click();
  const card = page.getByRole("article");
  const sync = card.locator(".assistant-card-header [role=status]");
  await card.getByRole("tab", { name: "Knowledge", exact: true }).click();
  await card.getByRole("button", { name: "Shared base", exact: true }).click();
  await card.getByLabel("Choose a document", { exact: true }).setInputFiles({
    name: "Background.pdf",
    mimeType: "application/pdf",
    buffer: Buffer.from("fixture"),
  });
  await expect(
    card.getByRole("button", { name: /Background.pdf/ }),
  ).toBeDisabled();
  await expect(card.locator(".document-processing-state")).toContainText(
    "Processing",
  );
  await expect(sync).toHaveText("Synced");
  for (const width of [320, 1366]) {
    await page.setViewportSize({ width, height: 768 });
    expect(
      (
        await new AxeBuilder({ page })
          .withTags(["wcag2a", "wcag2aa", "wcag22aa"])
          .analyze()
      ).violations,
    ).toEqual([]);
    expect(
      await page.evaluate(
        () =>
          document.documentElement.scrollWidth <= innerWidth &&
          document.documentElement.scrollHeight <= innerHeight,
      ),
    ).toBe(true);
    await page.screenshot({
      path: `test-results/background-processing-${width}.png`,
      fullPage: true,
    });
  }
  await card.getByRole("tab", { name: "Settings", exact: true }).click();
  await card
    .getByLabel("Welcome message", { exact: true })
    .fill("Editable while processing");
  await expect.poll(() => welcome).toBe("Editable while processing");
  await expect(sync).toHaveText("Synced");
  await card.getByRole("tab", { name: "Knowledge", exact: true }).click();
  await card.getByRole("button", { name: "Shared base", exact: true }).click();
  await expect(card.locator(".document-processing-state")).toContainText(
    "Processing",
  );
  state = "ready";
  await expect(
    card.getByRole("button", { name: /Background.pdf/ }),
  ).toBeEnabled({ timeout: 7000 });
  await expect(sync).toHaveText("Synced");
  await card.getByRole("button", { name: /Background.pdf/ }).click();
  await expect(card.getByText("Ready content", { exact: true })).toBeVisible();
});
