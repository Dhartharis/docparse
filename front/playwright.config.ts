import { defineConfig } from "@playwright/test";

export default defineConfig({
  testDir: "./tests",
  use: { baseURL: "http://127.0.0.1:45173", browserName: "chromium" },
  webServer: {
    command: "npm run dev -- --host 127.0.0.1 --port 45173 --strictPort",
    url: "http://127.0.0.1:45173",
    reuseExistingServer: false,
  },
});
