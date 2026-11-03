import { StrictMode } from "react";
import { createRoot } from "react-dom/client";
import "./index.css";
import App from "./App.tsx";
import { consumePortalEntry } from "./portal-entry";

const shouldPlayEntry = consumePortalEntry();

createRoot(document.getElementById("root")!).render(
  <StrictMode>
    <App shouldPlayEntry={shouldPlayEntry} />
  </StrictMode>,
);
