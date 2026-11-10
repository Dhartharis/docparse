import { test, expect } from "@playwright/test";

test("follow-up context excludes failed turns and stays scoped to each assistant", async ({
  page,
}) => {
  let failed = false;
  let releasePending: (() => void) | undefined;
  const calls: {
    path: string;
    question: string;
    history: { question: string; answer: string }[];
  }[] = [];
  await page.emulateMedia({ reducedMotion: "reduce" });
  await page.route("**/localization-api/**", (r) => r.abort());
  await page.route("**/api/**", (r) => {
    const path = new URL(r.request().url()).pathname;
    if (path.endsWith("/login"))
      return r.fulfill({
        json: { accessToken: "token", refreshToken: "refresh", expiresIn: 900 },
      });
    if (path.endsWith("/me"))
      return r.fulfill({ json: { email: "viewer@example.test" } });
    if (path === "/api/organizations/")
      return r.fulfill({ json: [{ id: "org", name: "Org", role: "Viewer" }] });
    if (path.endsWith("/bots/"))
      return r.fulfill({
        json: [
          { id: "a", name: "Alpha", welcomeMessage: "Hello" },
          { id: "b", name: "Beta", welcomeMessage: "Hello" },
        ],
      });
    if (path.endsWith("/messages")) {
      const body = r.request().postDataJSON();
      calls.push({ path, ...body });
      if (body.question === "Pending") {
        return new Promise<void>((resolve) => {
          releasePending = () => {
            void r
              .fulfill({ json: { answer: "Stale answer", sources: [] } })
              .catch(() => {})
              .finally(resolve);
          };
        });
      }
      if (body.question === "Expiry?" && !failed) {
        failed = true;
        return r.fulfill({ status: 502, json: {} });
      }
      return r.fulfill({
        json: { answer: "Reply: " + body.question, sources: [] },
      });
    }
    return r.fulfill({ status: 404, json: {} });
  });
  await page.goto("/");
  await page.getByLabel("Username or email", { exact: true }).fill("viewer");
  await page.getByLabel("Password", { exact: true }).fill("password");
  await page.getByRole("button", { name: "Sign in", exact: true }).click();
  async function ask(question: string) {
    await page.getByLabel("Your question", { exact: true }).fill(question);
    await page.getByLabel("Your question", { exact: true }).press("Enter");
  }
  await ask("Price?");
  await expect(page.getByText("Reply: Price?", { exact: true })).toBeVisible();
  expect(calls[0].history).toEqual([]);
  await ask("Expiry?");
  await page.getByRole("button", { name: "Try again", exact: true }).click();
  await expect(page.getByText("Reply: Expiry?", { exact: true })).toBeVisible();
  expect(calls[1].history).toEqual([
    { question: "Price?", answer: "Reply: Price?" },
  ]);
  expect(calls[2].history).toEqual(calls[1].history);
  await page.locator(".chat-heading select").selectOption("b");
  await ask("Beta question");
  await expect(
    page.getByText("Reply: Beta question", { exact: true }),
  ).toBeVisible();
  expect(calls[3].history).toEqual([]);
  await page.locator(".chat-heading select").selectOption("a");
  await ask("Follow-up");
  await expect(
    page.getByText("Reply: Follow-up", { exact: true }),
  ).toBeVisible();
  expect(calls[4].history).toEqual([
    { question: "Price?", answer: "Reply: Price?" },
    { question: "Expiry?", answer: "Reply: Expiry?" },
  ]);
  const clear = page.getByRole("button", {
    name: "Clear conversation and context",
    exact: true,
  });
  await clear.click();
  await expect(page.locator(".chat-turn")).toHaveCount(0);
  const composer = page.getByLabel("Your question", { exact: true });
  await expect(composer).toBeFocused();
  await expect(clear).toBeDisabled();
  await composer.press("Enter");
  expect(calls).toHaveLength(5);
  await composer.fill("New");
  await composer.press("Shift+Enter");
  await composer.pressSequentially("line");
  await expect(composer).toHaveValue("New\nline");
  expect(calls).toHaveLength(5);
  await composer.dispatchEvent("keydown", {
    key: "Enter",
    code: "Enter",
    isComposing: true,
  });
  expect(calls).toHaveLength(5);
  await composer.press("Enter");
  await expect(
    page.getByText("Reply: New\nline", { exact: true }),
  ).toBeVisible();
  expect(calls[5].history).toEqual([]);
  await page.locator(".chat-heading select").selectOption("b");
  await expect(
    page.getByText("Reply: Beta question", { exact: true }),
  ).toBeVisible();
  await page.locator(".chat-heading select").selectOption("a");
  await ask("Pending");
  await expect.poll(() => calls.length).toBe(7);
  await clear.click();
  await expect(composer).toBeEnabled();
  await expect(page.locator(".chat-turn")).toHaveCount(0);
  releasePending!();
  await ask("After cancellation");
  await expect(
    page.getByText("Reply: After cancellation", { exact: true }),
  ).toBeVisible();
  expect(calls[7].history).toEqual([]);
  await expect(page.getByText("Stale answer", { exact: true })).toHaveCount(0);
});
