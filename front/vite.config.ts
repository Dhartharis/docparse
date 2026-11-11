import react from "@vitejs/plugin-react";
import { defineConfig } from "vite";

// https://vite.dev/config/
export default defineConfig({
  plugins: [react()],
  server: {
    watch: { usePolling: process.env.VITE_USE_POLLING === "true" },
    proxy: {
      "/localization-api": {
        target: process.env.VITE_LOCALIZATION_PROXY_TARGET ?? "https://fyr-localization-production.up.railway.app",
        changeOrigin: true,
        rewrite: (path) => path.replace(/^\/localization-api/, ""),
      },
      "/api": {
        target: process.env.VITE_API_PROXY_TARGET ?? "http://127.0.0.1:5128",
        changeOrigin: true,
      },
    },
  },
});
