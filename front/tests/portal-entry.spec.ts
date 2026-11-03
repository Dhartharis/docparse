import { expect, test } from "@playwright/test";

test.beforeEach(async ({ page }) => {
  await page.route("**/localization-api/**", (route) => route.abort());
  await page.clock.install({ time: new Date("2026-11-04T00:00:00Z") });
  await page.clock.pauseAt(new Date("2026-11-04T00:00:00Z"));
});

test("continues the yellow portal frame into the logo and preserves language without replay on reload", async ({
  page,
}) => {
  await page.setViewportSize({ width: 1440, height: 960 });
  await page.goto("/?entry=fyr-portal&lang=es&campaign=studio#login");
  await expect(page.locator(".portal-entry")).toHaveCount(1);
  await expect(page.locator("html")).toHaveAttribute("lang", "es");
  expect(new URL(page.url()).searchParams.get("entry")).toBeNull();
  expect(new URL(page.url()).searchParams.get("campaign")).toBe("studio");
  expect(new URL(page.url()).hash).toBe("#login");
  const color = await page
    .locator<HTMLCanvasElement>(".portal-entry")
    .evaluate((canvas) =>
      Array.from(canvas.getContext("2d")!.getImageData(0, 0, 1, 1).data),
    );
  expect(color).toEqual([255, 219, 56, 255]);
  await expect(page.locator(".brand-art img")).toHaveCSS(
    "visibility",
    "hidden",
  );
  await page.clock.runFor(1500);
  await expect(page.locator(".portal-entry")).toHaveCount(0);
  await expect(page.locator(".brand-art img")).toBeVisible();
  await expect(
    page.getByRole("button", { name: "Iniciar sesión" }),
  ).toBeEnabled();
  await page.reload();
  await expect(page.locator(".portal-entry")).toHaveCount(0);
  await expect(page.locator("html")).not.toHaveAttribute("data-docparse-entry");
});

test("opens directly without animation and respects reduced motion for portal arrivals", async ({
  page,
}) => {
  await page.goto("/");
  await expect(page.locator(".portal-entry")).toHaveCount(0);
  await page.emulateMedia({ reducedMotion: "reduce" });
  await page.goto("/?entry=fyr-portal&lang=vi");
  await expect(page.locator(".portal-entry")).toHaveCount(0);
  await expect(page.locator("html")).not.toHaveAttribute("data-docparse-entry");
  await expect(page.locator("html")).toHaveAttribute("lang", "vi");
  await expect(page.getByRole("button", { name: "Đăng nhập" })).toBeVisible();
});

test("uses the visible header logo on mobile and releases the overlay on resize or keyboard input", async ({
  page,
}) => {
  await page.setViewportSize({ width: 390, height: 844 });
  await page.goto("/?entry=fyr-portal");
  await expect(page.locator(".product-brand img")).toHaveCSS(
    "visibility",
    "hidden",
  );
  await page.keyboard.press("Tab");
  await expect(page.locator(".portal-entry")).toHaveCount(0);
  await expect(page.locator(".product-brand img")).toBeVisible();
  await expect(page.locator(".product-brand")).toBeFocused();
  await page.goto("/?entry=fyr-portal");
  await expect(page.locator(".portal-entry")).toHaveCount(1);
  await page.setViewportSize({ width: 768, height: 900 });
  await page.clock.runFor(20);
  await expect(page.locator(".portal-entry")).toHaveCount(0);
  await expect(page.locator("html")).not.toHaveAttribute("data-docparse-entry");
});

test("removes the cover when the logo cannot load", async ({ page }) => {
  await page.route("**/assets/docparse-mark.svg", (route) => route.abort());
  await page.goto("/?entry=fyr-portal");
  await expect(page.locator(".portal-entry")).toHaveCount(0);
  await expect(page.locator("html")).not.toHaveAttribute("data-docparse-entry");
  await expect(
    page.getByRole("button", { name: "Sign in", exact: true }),
  ).toBeVisible();
});
