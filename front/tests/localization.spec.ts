import { readFileSync } from "node:fs";
import { expect, test } from "@playwright/test";
import type { Page } from "@playwright/test";

const read = (path: string) =>
  JSON.parse(
    readFileSync(new URL(`../localization/${path}`, import.meta.url), "utf8"),
  );
const docparse = read("docparse/locales/en.json");
const portal = read("fyr-portal/en.json");

test('does not replace a newer validated cache with an older advertised version', async ({ page }) => {
  await page.addInitScript(pack => {
    localStorage.setItem('fyr.docparse.pack.docparse.en', JSON.stringify(pack));
  }, { ...docparse, version: docparse.version + 1, strings: { ...docparse.strings, 'login.greeting': 'Newer cached welcome' } });
  await mockService(page);
  await page.goto('/');
  await expect(page.locator('html')).toHaveAttribute('data-login-localization', 'bundled-or-cached');
  await expect(page.getByRole('heading', {name: 'Newer cached welcome'})).toBeVisible();
  expect(await page.evaluate(() => JSON.parse(localStorage.getItem('fyr.docparse.pack.docparse.en')!).version)).toBe(docparse.version + 1);
});

async function mockService(
  page: Page,
  transform: (product: string, pack: typeof docparse) => unknown = (_, pack) =>
    pack,
) {
  await page.route("**/localization-api/**", async (route) => {
    const segments = new URL(route.request().url()).pathname.split("/");
    const product = segments[4];
    const locale = segments[6];
    if (!locale) {
      await route.fulfill({
        json: {
          schemaVersion: 1,
          locales: ["en", "es", "vi"].map((code) => ({ code, version: (product === "docparse" ? docparse : portal).version })),
        },
      });
      return;
    }
    const pack = read(
      product === "docparse"
        ? `docparse/locales/${locale}.json`
        : `fyr-portal/${locale}.json`,
    );
    await route.fulfill({ json: transform(product, pack) });
  });
}

test("loads login text and the exact portal slogan from separate service catalogs, then uses cache offline", async ({
  page,
}) => {
  await mockService(page, (product, pack) => ({
    ...pack,
    strings: {
      ...pack.strings,
      ...(product === "docparse"
        ? { "login.greeting": "Remote welcome" }
        : { "doc.description": "One shared portal slogan." }),
    },
  }));
  await page.goto("/");
  await expect(
    page.getByRole("heading", { name: "Remote welcome" }),
  ).toBeVisible();
  await expect(page.getByRole("heading", { level: 1 })).toHaveText(
    "One shared portal slogan.",
  );
  await expect
    .poll(() =>
      page.evaluate(
        () =>
          Object.keys(localStorage).filter((key) =>
            key.startsWith("fyr.docparse.pack."),
          ).length,
      ),
    )
    .toBe(2);
  await page.unrouteAll();
  await page.route("**/localization-api/**", (route) => route.abort());
  await page.reload();
  await expect(
    page.getByRole("heading", { name: "Remote welcome" }),
  ).toBeVisible();
  await expect(page.getByRole("heading", { level: 1 })).toHaveText(
    "One shared portal slogan.",
  );
});

test("rejects incomplete packs, wrong versions, interpolation changes and corrupt cache", async ({
  page,
}) => {
  await page.addInitScript(() =>
    localStorage.setItem("fyr.docparse.pack.docparse.en", "{invalid-json"),
  );
  const completed: string[] = [];
  await mockService(page, (product, pack) => {
    completed.push(product);
    return product === "docparse"
      ? { ...pack, version: pack.version + 1 }
      : {
          ...pack,
          strings: {
            ...pack.strings,
            "footer.copyright": "© {wrong} Fyr Studio",
          },
        };
  });
  await page.goto("/");
  await expect.poll(() => completed.length).toBe(2);
  await expect(page.getByRole("heading", { level: 1 })).toHaveText(
    portal.strings["doc.description"],
  );
  await expect(
    page.getByRole("heading", { name: docparse.strings["login.greeting"] }),
  ).toBeVisible();
  await mockService(page, (_, pack) => ({ ...pack, strings: {} }));
  await page.reload();
  await expect(
    page.getByRole("heading", { name: docparse.strings["login.greeting"] }),
  ).toBeVisible();
});

test("preserves the portal language on entry and an explicit change after reload", async ({
  page,
}) => {
  await page.route("**/localization-api/**", (route) => route.abort());
  await page.addInitScript(() => {
    if (!localStorage.getItem("seeded")) {
      localStorage.setItem("docparse.language", "en");
      localStorage.setItem("seeded", "true");
    }
  });
  await page.goto("/?entry=fyr-portal&lang=vi");
  await expect(page.locator("html")).toHaveAttribute("lang", "vi");
  await page.locator("select").selectOption("es");
  await page.reload();
  await expect(page.locator("html")).toHaveAttribute("lang", "es");
  expect(new URL(page.url()).searchParams.get("lang")).toBe("es");
});

test("keeps bundled translations usable when browser storage and the service are unavailable", async ({
  page,
}) => {
  await page.route("**/localization-api/**", (route) => route.abort());
  await page.addInitScript(() => {
    Object.defineProperty(window, "localStorage", {
      get() {
        throw new Error("Storage blocked");
      },
    });
  });
  await page.goto("/");
  await page.locator("select").selectOption("es");
  await expect(
    page.getByRole("button", { name: "Iniciar sesión" }),
  ).toBeVisible();
  await expect(page.getByRole("heading", { level: 1 })).toHaveText(
    read("fyr-portal/es.json").strings["doc.description"],
  );
});
