import { useMemo } from "react";
import ChatScreen from "./ChatScreen";
import type { EmbeddedChat } from "./ChatScreen";
import type { Bot, Answer } from "../../services/api";
import { ApiError } from "../../services/api";
import { bundledTranslator, resolveLanguage } from "../../i18n";
export type WidgetBootstrap = {
  organizationId: string;
  bot: Bot;
  token: string;
  logoDataUrl?: string;
};
export default function EmbeddedChatScreen({
  bootstrap,
}: {
  bootstrap: WidgetBootstrap;
}) {
  const language = resolveLanguage(null, [navigator.language]);
  const t = bundledTranslator(language);
  const transport = useMemo<EmbeddedChat>(
    () => ({
      bot: bootstrap.bot,
      logoUrl: bootstrap.logoDataUrl,
      async send(question, history, signal) {
        const response = await fetch(
          `/api/embed/${bootstrap.bot.id}/messages`,
          {
            method: "POST",
            credentials: "omit",
            headers: {
              "Content-Type": "application/json",
              "X-DocParse-Session": bootstrap.token,
            },
            body: JSON.stringify({ question, history }),
            signal: AbortSignal.any([signal, AbortSignal.timeout(120000)]),
          },
        );
        if (!response.ok) {
          const problem = await response
            .clone()
            .json()
            .catch(() => ({}));
          if (problem.code === "usage_limit") throw new ApiError("usage_limit");
          if (problem.code === "usage_unavailable")
            throw new ApiError("usage_unavailable");
          throw new ApiError(
            response.status === 429
              ? "rate"
              : response.status === 401
                ? "session"
                : response.status === 503
                  ? "processing"
                  : "server",
          );
        }
        return (await response.json()) as Answer;
      },
    }),
    [bootstrap],
  );
  return (
    <main className="embedded-chat">
      <ChatScreen
        organization={{
          id: bootstrap.organizationId,
          name: "",
          role: "Viewer",
        }}
        embedded={transport}
        t={t}
      />
    </main>
  );
}
