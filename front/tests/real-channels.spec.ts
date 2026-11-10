import { test, expect } from "@playwright/test";
import AxeBuilder from "@axe-core/playwright";
import type { Route } from "@playwright/test";
test.skip(
  !process.env.DOCPARSE_REAL_API,
  "Requires isolated local backend integration services.",
);
test("Admin manages keys and embeds the shared chat on an allowed site", async ({
  page,
}) => {
  await page.emulateMedia({ reducedMotion: "reduce" });
  await page.route("**/localization-api/**", (r) => r.abort());
  await page.goto("/");
  await page
    .getByLabel("Username or email", { exact: true })
    .fill(process.env.DOCPARSE_TEST_ADMIN_EMAIL!);
  await page
    .getByLabel("Password", { exact: true })
    .fill(process.env.DOCPARSE_TEST_ADMIN_PASSWORD!);
  await page.getByRole("button", { name: "Sign in", exact: true }).click();
  await page
    .getByRole("button", { name: "Create assistant", exact: false })
    .click();
  const create = page.getByRole("dialog");
  await create.getByLabel("Bot name", { exact: true }).fill("Channel fixture");
  await create
    .getByRole("button", { name: "Create assistant", exact: true })
    .click();
  const card = page.getByRole("article", {
    name: "Channel fixture",
    exact: true,
  });
  await card.getByRole("tab", { name: "API", exact: true }).click();
  const apiHelp = card.getByRole("button", { name: "About External API", exact: true });
  const apiTip = card.getByRole("tooltip").filter({ hasText: "Each key can only chat" });
  await expect(apiTip).toBeHidden();
  await apiHelp.hover();
  await expect(apiTip).toBeVisible();
  await card.getByRole("heading", { name: "External API", exact: true }).hover();
  await expect(apiTip).toBeHidden();
  await apiHelp.click();
  await card.getByRole("heading", { name: "External API", exact: true }).hover();
  await expect(apiTip).toBeVisible();
  await page.screenshot({ path: "test-results/api-help-desktop.png" });
  await card.getByRole("heading", { name: "External API", exact: true }).click();
  await expect(apiTip).toBeHidden();
  await apiHelp.focus();
  await expect(apiTip).toBeVisible();
  await page.keyboard.press("Escape");
  await expect(apiTip).toBeHidden();
  await page.setViewportSize({ width: 390, height: 844 });
  await card.getByRole("button", { name: "About Embedded chat", exact: true }).click();
  const widgetTip = card.getByRole("tooltip").filter({ hasText: "This does not configure external API clients" });
  await expect(widgetTip).toBeVisible();
  const bounds = await widgetTip.boundingBox();
  expect(bounds!.x).toBeGreaterThanOrEqual(0);
  expect(bounds!.x + bounds!.width).toBeLessThanOrEqual(390);
  expect((await new AxeBuilder({ page }).withTags(["wcag2a", "wcag2aa"]).analyze()).violations).toEqual([]);
  await page.screenshot({ path: "test-results/widget-help-mobile.png" });
  await card.getByRole("heading", { name: "Embedded chat", exact: true }).click();
  await expect(widgetTip).toBeHidden();
  await page.setViewportSize({ width: 1440, height: 1000 });
  await card.getByLabel("Key name", { exact: true }).fill("Website server");
  await card.getByRole("button", { name: "Create key", exact: true }).click();
  await expect(card.locator(".channel-secret code")).toHaveText(/^dp_/);
  const oldSecret = await card.locator(".channel-secret code").textContent();
  await card
    .locator(".channel-secret")
    .getByRole("button", { name: "Close", exact: true })
    .click();
  await expect(card.locator(".channel-secret")).toHaveCount(0);
  await card.getByRole("button", { name: "Rotate", exact: true }).click();
  await expect(card.locator(".channel-secret code")).not.toHaveText(oldSecret!);
  await card
    .locator(".channel-secret")
    .getByRole("button", { name: "Close", exact: true })
    .click();
  await card.getByRole("button", { name: "Revoke", exact: true }).click();
  await expect(
    card.getByRole("button", { name: "Revoke", exact: true }),
  ).toHaveCount(0);
  await card
    .getByLabel("Allowed site origins", { exact: false })
    .fill("http://127.0.0.1:45173");
  let held: Route | undefined;
  await page.route("**/channels/embed", (route) => {
    if (route.request().method() === "PUT") held = route;
    else return route.continue();
  });
  await expect(card.locator(".widget-origin-row.is-active")).toHaveCount(0);
  await card.getByRole("button", { name: "Activate site", exact: true }).click();
  await expect.poll(() => !!held).toBe(true);
  await expect(card.locator(".widget-origin-row.is-active")).toHaveCount(0);
  await expect(card.locator(".assistant-card-header [role=status]")).toHaveText("Syncing…");
  await held!.continue();
  await page.unroute("**/channels/embed");
  await expect(card.locator(".assistant-card-header [role=status]")).toHaveText(
    "Synced",
  );
  await expect(card.getByRole("button", { name: "Deactivate http://127.0.0.1:45173", exact: true })).toHaveAttribute("aria-pressed", "true");
  await expect(card.locator(".widget-origin-row.is-active")).toHaveCSS("border-top-color", "rgb(100, 194, 154)");
  await page.route("**/channels/embed", (route) => route.fulfill({status:503,json:{code:"server"}}), {times:1});
  await card.getByRole("button", { name: "Deactivate http://127.0.0.1:45173", exact: true }).click();
  await expect(card.getByRole("alert")).toContainText("previous saved configuration remains active");
  await expect(card.locator(".widget-origin-row.is-active")).toHaveCount(1);
  await card.getByRole("button", {name:"Try again",exact:true}).click();
  await expect(card.locator(".widget-origin-row.is-active")).toHaveCount(0);
  await expect(card.locator(".widget-snippet")).toHaveCount(0);
  await card.getByRole("button", {name:"Activate http://127.0.0.1:45173",exact:true}).click();
  await expect(card.locator(".widget-origin-row.is-active")).toHaveCount(1);
  await expect(card.locator(".assistant-card-header [role=status]")).toHaveText(
    "Synced",
  );
  const snippet = await card.locator(".widget-snippet code").textContent();
  const src = /src="([^"]+)"/.exec(snippet!)![1];
  expect(
    (await new AxeBuilder({ page }).withTags(["wcag2a", "wcag2aa"]).analyze())
      .violations,
  ).toEqual([]);
  await page.screenshot({ path: "test-results/channels-desktop.png" });
  await page.setViewportSize({width:390,height:844});
  await card.locator(".widget-origin-row.is-active").scrollIntoViewIfNeeded();
  expect((await new AxeBuilder({page}).withTags(["wcag2a","wcag2aa"]).analyze()).violations).toEqual([]);
  await page.screenshot({path:"test-results/widget-site-active-mobile.png"});
  await page.setViewportSize({width:1440,height:1000});
  const botId = /embed\/([^/]+)\/loader/.exec(src)![1];
  await page.goto("/widget-demo.html#bot=" + botId);
  await page.getByRole("button", { name: "DocParse", exact: true }).click();
  const widget = page.frameLocator("iframe");
  await expect(
    widget.getByRole("heading", { name: "Channel fixture", exact: true }),
  ).toBeVisible();
  await widget.getByLabel("Your question", { exact: true }).fill("Question");
  await widget.getByLabel("Your question", { exact: true }).press("Enter");
  await expect(widget.locator(".message-answer")).toBeVisible();
  await widget
    .getByRole("button", {
      name: "Clear conversation and context",
      exact: true,
    })
    .click();
  await expect(widget.locator(".chat-turn")).toHaveCount(0);
  await page.setViewportSize({ width: 390, height: 844 });
  await expect(
    widget.getByLabel("Your question", { exact: true }),
  ).toBeVisible();
  await page.screenshot({ path: "test-results/widget-mobile.png" });
});
