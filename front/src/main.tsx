import { StrictMode } from "react";
import { createRoot } from "react-dom/client";
import "./index.css";
import App from "./App.tsx";
import EmbeddedChatScreen from "./screens/chat/EmbeddedChatScreen";
import type { WidgetBootstrap } from "./screens/chat/EmbeddedChatScreen";
declare global {
  interface Window {
    __DOCPARSE_EMBED__?: WidgetBootstrap;
  }
}
import { consumePortalEntry } from "./portal-entry";

const shouldPlayEntry = consumePortalEntry();

createRoot(document.getElementById("root")!).render(
  <StrictMode>
    {window.__DOCPARSE_EMBED__ ? (
      <EmbeddedChatScreen bootstrap={window.__DOCPARSE_EMBED__} />
    ) : (
      <App shouldPlayEntry={shouldPlayEntry} />
    )}
  </StrictMode>,
);
