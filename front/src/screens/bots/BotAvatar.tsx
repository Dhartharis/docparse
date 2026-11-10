import { useEffect, useState } from "react";
import { bots } from "../../services/bots";
import type { Bot } from "../../services/api";

export default function BotAvatar({
  organizationId,
  bot,
  size = 48,
  publicSource,
}: {
  organizationId: string;
  bot: Bot;
  size?: number;
  publicSource?: string;
}) {
  const [source, setSource] = useState<{ key: string; url: string } | null>(
    null,
  );
  const key = `${organizationId}/${bot.id}/${bot.logoVersion}`;
  useEffect(() => {
    const controller = new AbortController();
    let url: string | undefined;
    if (bot.logoVersion && !publicSource)
      void bots
        .logo(organizationId, bot.id, controller.signal)
        .then((blob) => {
          if (controller.signal.aborted) return;
          url = URL.createObjectURL(blob);
          setSource({ key, url });
        })
        .catch(() => {
          /* Optional branding falls back to the bundled mark when inaccessible/offline. */
        });
    return () => {
      controller.abort();
      if (url) URL.revokeObjectURL(url);
    };
  }, [organizationId, bot.id, bot.logoVersion, key, publicSource]);
  return (
    <img
      className="bot-avatar"
      src={
        publicSource ??
        (source?.key === key ? source.url : "/assets/docparse-mark.svg")
      }
      width={size}
      height={size}
      alt=""
      onError={(event) => {
        if (
          event.currentTarget.getAttribute("src") !==
          "/assets/docparse-mark.svg"
        )
          event.currentTarget.src = "/assets/docparse-mark.svg";
      }}
    />
  );
}
