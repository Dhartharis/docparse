import { api } from "./api";
import type { Bot } from "./api";
import { knowledge } from "./knowledge";
const root = (org: string) => `/organizations/${org}/bots`;
export type BotAccess = { email: string; pending: boolean };
export const bots = {
  access: (org: string, id: string, signal: AbortSignal) =>
    api<BotAccess[]>(`${root(org)}/${id}/access/`, undefined, signal),
  addAccess: (
    org: string,
    id: string,
    email: string,
    allBots: boolean,
    signal: AbortSignal,
    locale = "en",
  ) =>
    api<{ pending: boolean; affectedBots: number }>(
      `${root(org)}/${id}/access/`,
      { email, allBots, locale },
      signal,
    ),
  removeAccess: (org: string, id: string, email: string, signal: AbortSignal) =>
    api(
      `${root(org)}/${id}/access/?email=${encodeURIComponent(email)}`,
      undefined,
      signal,
      "DELETE",
    ),
  list: (org: string, signal: AbortSignal) =>
    api<Bot[]>(`${root(org)}/`, undefined, signal),
  create: (
    org: string,
    name: string,
    welcomeMessage: string,
    signal: AbortSignal,
    accentColor?: string,
  ) => api<Bot>(`${root(org)}/`, { name, welcomeMessage, accentColor }, signal),
  update: (
    org: string,
    id: string,
    name: string,
    welcomeMessage: string,
    signal: AbortSignal,
    accentColor?: string,
  ) =>
    api(
      `${root(org)}/${id}`,
      { name, welcomeMessage, accentColor },
      signal,
      "PATCH",
    ),
  logo: (org: string, id: string, signal: AbortSignal) =>
    api<Blob>(`${root(org)}/${id}/logo`, undefined, signal, "GET", "blob"),
  uploadLogo: (org: string, id: string, file: File, signal: AbortSignal) => {
    const data = new FormData();
    data.append("file", file);
    return api<{ logoVersion: string }>(
      `${root(org)}/${id}/logo`,
      data,
      signal,
    );
  },
  configuration: async (org: string, id: string, signal: AbortSignal) => {
    const [bases, members, linked, assigned] = await Promise.all([
      knowledge.list(org, signal),
      api<{ id: string; email: string; role: string }[]>(
        `/organizations/${org}/members`,
        undefined,
        signal,
      ),
      api<{ knowledgeBaseIds: string[] }>(
        `${root(org)}/${id}/knowledge-bases`,
        undefined,
        signal,
      ),
      api<{ userIds: string[] }>(
        `${root(org)}/${id}/viewers`,
        undefined,
        signal,
      ),
    ]);
    const viewers = members.filter((member) => member.role === "Viewer");
    return {
      bases,
      viewers,
      linked: linked.knowledgeBaseIds,
      assigned: assigned.userIds.filter((id) =>
        viewers.some((member) => member.id === id),
      ),
    };
  },
  link: (
    org: string,
    id: string,
    knowledgeBaseIds: string[],
    signal: AbortSignal,
  ) =>
    api(
      `${root(org)}/${id}/knowledge-bases`,
      { knowledgeBaseIds },
      signal,
      "PUT",
    ),
  assign: (org: string, id: string, userIds: string[], signal: AbortSignal) =>
    api(`${root(org)}/${id}/viewers`, { userIds }, signal, "PUT"),
};
