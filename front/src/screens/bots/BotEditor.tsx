import { useCallback, useState } from "react";
import TrashAction from "../../components/TrashAction";
import type { SyncState } from "./BotSyncStatus";
import BotSettings from "./BotSettings";
import BotChannelsPanel from "./BotChannelsPanel";
import BotKnowledgePanel from "./BotKnowledgePanel";
import BotAccessPanel from "./BotAccessPanel";
import type { Bot } from "../../services/api";
import type { Language, Translate } from "../../i18n";
export type AssistantPane = "settings" | "knowledge" | "access" | "api";
export default function BotEditor({
  organizationId,
  bot,
  pane,
  t,
  language,
  disabled,
  onBusyChange,
  onUpdated,
  onDeleted,
  onSyncStatusChange,
}: {
  organizationId: string;
  bot: Bot;
  pane: AssistantPane;
  t: Translate;
  language: Language;
  disabled: boolean;
  onBusyChange: (value: boolean) => void;
  onUpdated: (value: Bot) => void;
  onDeleted: () => void;
  onSyncStatusChange: (state: SyncState) => void;
}) {
  const [settingsBusy, setSettingsBusy] = useState(false);
  const changeSettingsBusy = useCallback(
    (v: boolean) => {
      setSettingsBusy(v);
      onBusyChange(v);
    },
    [onBusyChange],
  );
  if (pane === "api")
    return (
      <BotChannelsPanel
        organizationId={organizationId}
        botId={bot.id}
        t={t}
        disabled={disabled}
        onBusyChange={onBusyChange}
        onSyncStatusChange={onSyncStatusChange}
      />
    );
  if (pane === "knowledge")
    return (
      <BotKnowledgePanel
        organizationId={organizationId}
        botId={bot.id}
        botName={bot.name}
        t={t}
        language={language}
        disabled={disabled}
        onBusyChange={onBusyChange}
        onSyncStatusChange={onSyncStatusChange}
      />
    );
  if (pane === "access")
    return (
      <BotAccessPanel
        language={language}
        organizationId={organizationId}
        botId={bot.id}
        t={t}
        disabled={disabled}
        onBusyChange={onBusyChange}
        onSyncStatusChange={onSyncStatusChange}
      />
    );
  return (
    <>
      <BotSettings
        organizationId={organizationId}
        bot={bot}
        t={t}
        disabled={disabled}
        onBusyChange={changeSettingsBusy}
        onUpdated={onUpdated}
        onSyncStatusChange={onSyncStatusChange}
      />
      <TrashAction
        path={`/organizations/${organizationId}/bots/${bot.id}`}
        name={bot.name}
        label="trash.move_bot"
        help="trash.bot_help"
        t={t}
        disabled={disabled || settingsBusy}
        onBusyChange={onBusyChange}
        onMoved={onDeleted}
      />
    </>
  );
}
