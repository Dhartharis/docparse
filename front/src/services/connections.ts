import { api } from "./api";

export type ConnectionSettings = {
  name: string; priceMaxAgeSeconds: number; stockMaxAgeSeconds: number; clockSkewSeconds: number; syncIntervalSeconds: number | null;
};
export type SourceConnection = {
  id: string; sourceBusinessId: string; baseUrl: string | null; push: boolean; pullLive: boolean; pullScheduled: boolean;
  settings: ConnectionSettings; revision: string; disconnectedAt: string | null;
  pushKey: { keyId: string; expiresAt: string } | null; pullKeyId: string | null;
  state: "configured" | "needs_credentials" | "disconnected";
};
export type ConnectionDefinition = {
  sourceBusinessId: string; baseUrl: string | null; push: boolean; pullLive: boolean; pullScheduled: boolean; pushKeyDays: number; settings: ConnectionSettings;
};
export type ConnectionList = { connections: SourceConnection[]; providerOriginsConfigured: boolean };
export type ConnectionCreated = { connection: SourceConnection; pushSecretHex: string | null };
const root = (org: string) => `/organizations/${org}/connections`;
export const listConnections = (org: string, signal: AbortSignal) => api<ConnectionList>(root(org) + "/", undefined, signal);
export const createConnection = (org: string, requestId: string, definition: ConnectionDefinition, signal: AbortSignal) =>
  api<ConnectionCreated>(root(org) + "/", { requestId, definition }, signal);
export const updateConnection = (org: string, connection: SourceConnection, settings: ConnectionSettings, signal: AbortSignal) =>
  api<SourceConnection>(root(org) + "/" + connection.id, { revision: connection.revision, settings }, signal, "PUT");
export const rotatePushKey = (org: string, connection: SourceConnection, days: number, signal: AbortSignal) =>
  api<{ connection: SourceConnection; secretHex: string }>(root(org) + "/" + connection.id + "/push-key", { revision: connection.revision, days }, signal);
export const savePullKey = (org: string, connection: SourceConnection, keyId: string, secretHex: string, signal: AbortSignal) =>
  api<SourceConnection>(root(org) + "/" + connection.id + "/pull-key", { revision: connection.revision, keyId, secretHex }, signal, "PUT");
export const disconnectConnection = (org: string, connection: SourceConnection, signal: AbortSignal) =>
  api(root(org) + "/" + connection.id + "/disconnect", { revision: connection.revision }, signal);
