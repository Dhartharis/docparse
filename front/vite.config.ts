import react from "@vitejs/plugin-react";
import { defineConfig } from "vite";

// https://vite.dev/config/
export default defineConfig({
  plugins: [react()],
  server: {
    proxy: {
      "/localization-api": {
        target: "https://fyr-localization-production.up.railway.app",
        changeOrigin: true,
        rewrite: (path) => path.replace(/^\/localization-api/, ""),
      },
    },
  },
});
