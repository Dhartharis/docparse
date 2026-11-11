import { expect, test } from "@playwright/test";
import { readFileSync } from "node:fs";

test("public AI entry is plain text, links resolve without login and app advertises it", async ({ request }) => {
  const index = await request.get("/llms.txt");
  expect(index.status()).toBe(200);
  expect(index.headers()["content-type"]).toContain("text/plain");
  const content = await index.text();
  expect(content).toMatch(/^# DocParse\n/);
  expect(content).not.toContain("<html");
  const localLinks = [...content.matchAll(/\]\(https:\/\/docparse\.fyr-studio\.com([^)]*)\)/g)];
  expect(localLinks.length).toBeGreaterThan(0);
  for (const [, path] of localLinks) {
    const response = await request.get(path);
    expect(response.status()).toBe(200);
    expect(await response.text()).toMatch(/^# DocParse\n/);
  }
  const html = await (await request.get("/")).text();
  expect(html).toContain('rel="describedby" href="/llms.txt"');
  expect(html).toContain('type="text/markdown" href="/index.md"');
  expect(html).toContain('content="noindex, nofollow"');
});

test("deployment keeps public text routes out of the SPA fallback", () => {
  const config = JSON.parse(readFileSync(new URL("../public/staticwebapp.config.json", import.meta.url), "utf8"));
  expect(config.navigationFallback.exclude).toEqual(expect.arrayContaining(["/llms.txt", "/index.md", "/api/*"]));
  expect(config.mimeTypes[".md"]).toContain("text/markdown");
});
