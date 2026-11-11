import { expect, test } from "@playwright/test";
import AxeBuilder from "@axe-core/playwright";

test("Admin creates knowledge, validates uploads, reviews fields and downloads authenticated files", async ({
  page,
}) => {
  let bases: { id: string; name: string }[] = [];
  let documents: object[] = [];
  let uploads = 0;
  let detailFails = true;
  const summary = {
    id: "doc",
    fileName: "Employee policies.pdf",
    contentType: "application/pdf",
    documentType: "Contract",
    createdAt: "2026-11-04T03:00:00Z",
    hasOriginal: true,
  };
  await page.route("**/localization-api/**", (route) => route.abort());
  await page.route("**/api/**", async (route) => {
    const request = route.request();
    const path = new URL(request.url()).pathname;
    if (path === "/api/auth/login")
      return route.fulfill({
        json: {
          accessToken: "test-access",
          refreshToken: "test-refresh",
          expiresIn: 900,
        },
      });
    expect(request.headers().authorization).toBe("Bearer test-access");
    if (path === "/api/auth/me")
      return route.fulfill({ json: { email: "admin@example.test" } });
    if (path === "/api/organizations/")
      return route.fulfill({
        json: [{ id: "org", name: "Fyr Studio", role: "Admin" }],
      });
    if (path.endsWith("/bots/"))
      return route.fulfill({
        json: [
          { id: "bot", name: "People assistant", welcomeMessage: "Hello" },
        ],
      });
    if (path.endsWith("/bots/bot/knowledge-bases"))
      return route.fulfill(
        request.method() === "PUT"
          ? { status: 204 }
          : { json: { knowledgeBaseIds: [] } },
      );
    if (path.endsWith("/knowledge-bases/")) {
      if (request.method() === "POST") {
        expect(request.postDataJSON()).toEqual({ name: "People policies" });
        bases = [{ id: "base", name: "People policies" }];
        return route.fulfill({ status: 201, json: bases[0] });
      }
      return route.fulfill({ json: bases });
    }
    if (
      path.endsWith("/knowledge-bases/base") &&
      request.method() === "PATCH"
    ) {
      expect(request.postDataJSON()).toEqual({
        name: "People policies updated",
      });
      return route.fulfill({ status: 204 });
    }
    if (path.endsWith("/documents/all"))
      return route.fulfill({ json: documents });
    if (path.endsWith("/extract")) {
      uploads++;
      expect(request.headers()["content-type"]).toContain(
        "multipart/form-data; boundary=",
      );
      expect(request.postData()).toContain('filename="Employee policies.pdf"');
      expect(request.postData()).toContain("PUBLIC_TEST_CONTENT");
      documents = [summary];
      return route.fulfill({ json: { id: "doc" } });
    }
    if (path.endsWith("/documents/doc")) {
      if (detailFails) return route.fulfill({ status: 502, json: {} });
      return route.fulfill({
        json: {
          document: summary,
          fields: {
            parties: "Fyr Studio & employee",
            content: "<script>window.injection = true</script>",
            description: "A very long unbroken value " + "a".repeat(100),
          },
        },
      });
    }
    if (path.endsWith("/file"))
      return route.fulfill({
        contentType: "application/pdf",
        body: "original-file-bytes",
      });
    if (path.endsWith("/export/csv"))
      return route.fulfill({
        contentType: "text/csv",
        body: "Field,Value\nparties,Fyr Studio",
      });
    return route.fulfill({ status: 404, json: {} });
  });
  await page.goto("/");
  await page.getByLabel("Username or email", { exact: true }).fill("admin");
  await page.getByLabel("Password", { exact: true }).fill("test-password");
  await page.getByRole("button", { name: "Sign in", exact: true }).click();
  await page.getByRole("tab", { name: "Knowledge", exact: true }).click();
  await page.getByRole("button", { name: "New knowledge base" }).click();
  await expect(page.getByLabel("Knowledge base name")).toBeFocused();
  await page.getByLabel("Knowledge base name").fill("People policies");
  await page.getByRole("button", { name: "Create base", exact: true }).click();
  await expect(
    page.getByRole("heading", { name: "People policies" }),
  ).toBeVisible();
  await page
    .getByRole("button", { name: "Rename People policies", exact: true })
    .click();
  await expect(
    page.getByLabel("Knowledge base name", { exact: true }),
  ).toHaveValue("People policies");
  await page
    .getByLabel("Knowledge base name", { exact: true })
    .fill("Discarded name");
  await page.getByLabel("Knowledge base name", { exact: true }).press("Escape");
  await page
    .getByRole("button", { name: "Rename People policies", exact: true })
    .click();
  await page
    .getByLabel("Knowledge base name", { exact: true })
    .fill("People policies updated");
  await expect(
    page.getByRole("heading", { name: "People policies updated", exact: true }),
  ).toBeVisible();
  const file = page.getByLabel("Choose a document");
  await file.setInputFiles({
    name: "bad.exe",
    mimeType: "application/octet-stream",
    buffer: Buffer.from("BAD"),
  });
  await expect(page.getByRole("alert")).toContainText("Choose a non-empty PDF");
  await expect(
    page.getByRole("button", { name: "Upload document", exact: true }),
  ).toHaveCount(0);
  await file.setInputFiles({
    name: "empty.pdf",
    mimeType: "application/pdf",
    buffer: Buffer.alloc(0),
  });
  await expect(page.getByRole("alert")).toContainText("Choose a non-empty PDF");
  await file.setInputFiles({
    name: "large.pdf",
    mimeType: "application/pdf",
    buffer: Buffer.alloc(10 * 1024 * 1024 + 1),
  });
  await expect(page.getByRole("alert")).toContainText("at most 10 MiB");
  expect(uploads).toBe(0);
  const transfer = await page.evaluateHandle(() => {
    const data = new DataTransfer();
    for (let index = 0; index < 2; index++)
      data.items.add(
        new File(["PUBLIC_TEST_CONTENT"], "Employee policies.pdf", {
          type: "application/pdf",
        }),
      );
    return data;
  });
  await page
    .locator(".document-upload")
    .dispatchEvent("drop", { dataTransfer: transfer });
  await expect.poll(() => uploads).toBe(2);
  await expect(
    page.getByRole("status").filter({ hasText: "was received." }),
  ).toContainText("was received.");
  expect(uploads).toBe(2);
  await page.getByRole("button", { name: /Employee policies.pdf/ }).click();
  await expect(page.getByRole("alert")).toContainText("We couldn’t complete");
  detailFails = false;
  await page.getByRole("button", { name: "Try again", exact: true }).click();
  await expect(
    page.getByRole("heading", { name: summary.fileName }),
  ).toBeFocused();
  await expect(
    page.getByText("Fyr Studio & employee", { exact: true }),
  ).toBeVisible();
  expect(await page.evaluate(() => "injection" in window)).toBe(false);
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
    path: "test-results/knowledge-desktop.png",
    fullPage: true,
  });
  const original = page.waitForEvent("download");
  await page
    .getByRole("button", { name: "Download original", exact: true })
    .click();
  expect((await original).suggestedFilename()).toBe(summary.fileName);
  const csv = page.waitForEvent("download");
  await page
    .getByRole("button", { name: "Export fields as CSV", exact: true })
    .click();
  expect((await csv).suggestedFilename()).toBe("document_doc.csv");
  expect(
    await page.evaluate(() =>
      JSON.stringify({ ...localStorage, ...sessionStorage }),
    ),
  ).not.toContain("test-access");
});

test("Failed upload does not retry automatically, and changing bases discards stale detail", async ({
  page,
}) => {
  let uploads = 0;
  let requests: string[] = [];
  let releaseDetail: (() => void) | undefined;
  const delayed = new Promise<void>((resolve) => {
    releaseDetail = resolve;
  });
  await page.route("**/localization-api/**", (route) => route.abort());
  await page.route("**/api/**", async (route) => {
    const path = new URL(route.request().url()).pathname;
    requests.push(path);
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
        json: [{ id: "org", name: "Acme", role: "Admin" }],
      });
    if (path.endsWith("/bots/"))
      return route.fulfill({
        json: [
          { id: "bot", name: "People assistant", welcomeMessage: "Hello" },
        ],
      });
    if (path.endsWith("/bots/bot/knowledge-bases"))
      return route.fulfill({ json: { knowledgeBaseIds: ["a", "b"] } });
    if (path.endsWith("/knowledge-bases/"))
      return route.fulfill({
        json: [
          { id: "a", name: "Base A" },
          { id: "b", name: "Base B" },
        ],
      });
    if (path.includes("/a/documents/all"))
      return route.fulfill({
        json: [
          {
            id: "old",
            fileName: "Old.pdf",
            contentType: "application/pdf",
            documentType: "Unknown",
            createdAt: "2026-11-04T03:00:00Z",
            hasOriginal: true,
          },
        ],
      });
    if (path.includes("/b/documents/all")) return route.fulfill({ json: [] });
    if (path.endsWith("/documents/old")) {
      await delayed;
      return route
        .fulfill({
          json: {
            document: { fileName: "Old.pdf" },
            fields: { secret: "STALE_DETAIL" },
          },
        })
        .catch((error) => {
          // A base switch intentionally aborts this obsolete browser request.
          if (!/closed|intercept|aborted/i.test(String(error))) throw error;
        });
    }
    if (path.endsWith("/extract")) {
      uploads++;
      return route.fulfill({ status: 502, json: {} });
    }
    return route.fulfill({ status: 404, json: {} });
  });
  await page.goto("/");
  await page.getByLabel("Username or email", { exact: true }).fill("admin");
  await page.getByLabel("Password", { exact: true }).fill("test-password");
  await page.getByRole("button", { name: "Sign in", exact: true }).click();
  await page.getByRole("tab", { name: "Knowledge", exact: true }).click();
  await page.getByRole("button", { name: "Base A", exact: true }).click();
  await page.getByRole("button", { name: /Old.pdf/ }).click();
  await page.getByRole("button", { name: "Base B", exact: true }).click();
  releaseDetail!();
  await expect(
    page.getByRole("heading", { name: "No documents in this base yet." }),
  ).toBeVisible();
  await expect(page.getByText("STALE_DETAIL")).toHaveCount(0);
  const chooser = page.waitForEvent("filechooser");
  await page.locator(".subtle-file-picker").click();
  await (
    await chooser
  ).setFiles({
    name: "failed.pdf",
    mimeType: "application/pdf",
    buffer: Buffer.from("FAILED"),
  });
  await expect(page.getByRole("alert")).toContainText(
    "Refresh it before uploading",
  );
  expect(uploads).toBe(1);
  expect(
    requests.filter((path) => path.endsWith("/b/documents/all")).length,
  ).toBeGreaterThan(1);
});
test("base checkboxes only select; batch unlink preserves bases and retries through shared sync state", async ({
  page,
}) => {
  let ids = ["a", "b"];
  let writes = 0;
  let documentReads = 0;
  await page.emulateMedia({ reducedMotion: "reduce" });
  await page.route("**/localization-api/**", (route) => route.abort());
  await page.route("**/api/**", (route) => {
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
      return route.fulfill({ json: { email: "admin@example.test" } });
    if (path === "/api/organizations/")
      return route.fulfill({
        json: [{ id: "org", name: "Acme", role: "Admin" }],
      });
    if (path.endsWith("/bots/"))
      return route.fulfill({
        json: [{ id: "bot", name: "Assistant", welcomeMessage: "Hello" }],
      });
    if (path.endsWith("/bots/bot/knowledge-bases")) {
      if (route.request().method() === "PUT") {
        writes++;
        if (writes === 1) return route.fulfill({ status: 503, json: {} });
        ids = route.request().postDataJSON().knowledgeBaseIds;
        return route.fulfill({ status: 204 });
      }
      return route.fulfill({ json: { knowledgeBaseIds: ids } });
    }
    if (path.endsWith("/knowledge-bases/"))
      return route.fulfill({
        json: [
          { id: "a", name: "Base A" },
          { id: "b", name: "Base B" },
          { id: "c", name: "Shared base" },
        ],
      });
    if (path.endsWith("/documents/all")) {
      documentReads++;
      return route.fulfill({ json: [] });
    }
    throw new Error(
      `Unexpected mutation or route: ${route.request().method()} ${path}`,
    );
  });
  await page.goto("/");
  await page.getByLabel("Username or email", { exact: true }).fill("admin");
  await page.getByLabel("Password", { exact: true }).fill("password");
  await page.getByRole("button", { name: "Sign in", exact: true }).click();
  await page.getByRole("tab", { name: "Knowledge", exact: true }).click();
  const status = page.locator(".assistant-card-header [role=status]");
  await expect(status).toHaveText("Synced");
  await expect(
    page.getByRole("button", { name: "Remove selected (0)", exact: true }),
  ).toBeDisabled();
  await page
    .getByRole("checkbox", { name: "Select Base A", exact: true })
    .check();
  await page
    .getByRole("checkbox", { name: "Select Base B", exact: true })
    .check();
  expect(writes).toBe(0);
  expect(documentReads).toBe(0);
  await expect(
    page.getByRole("button", { name: "Base A", exact: true }),
  ).toHaveAttribute("aria-expanded", "false");
  await expect(
    page.getByRole("button", { name: "Save knowledge", exact: true }),
  ).toHaveCount(0);
  await page
    .getByRole("button", { name: "Remove selected (2)", exact: true })
    .click();
  await expect(page.getByRole("dialog")).toContainText(
    "Files and bases are preserved",
  );
  await page.getByRole("button", { name: "Remove links", exact: true }).click();
  await expect(status).toHaveText("Not synced");
  expect(ids).toEqual(["a", "b"]);
  await expect(
    page.getByRole("checkbox", { name: "Select Base A", exact: true }),
  ).toBeChecked();
  await page.getByRole("button", { name: "Try again", exact: true }).click();
  await expect(status).toHaveText("Synced");
  expect(ids).toEqual([]);
  await page
    .getByRole("combobox", { name: "Link an existing base", exact: true })
    .selectOption("a");
  await expect(
    page.getByRole("button", { name: "Base A", exact: true }),
  ).toBeVisible();
  expect(ids).toEqual(["a"]);
  expect(documentReads).toBe(0);
  await page.getByRole("button", { name: "Base A", exact: true }).click();
  await expect(
    page.getByLabel("Choose a document", { exact: true }),
  ).toBeAttached();
  await expect(status).toHaveText("Synced");
  expect(documentReads).toBe(1);
  await expect(
    page.getByRole("button", { name: "Upload document", exact: true }),
  ).toHaveCount(0);
});
