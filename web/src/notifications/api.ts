export const notificationsKey = 'notifications';

export interface NotificationItem {
  id: number;
  kind: string;
  title: string;
  text: string;
  link: string | null;
  count: number;
  at: string;
  read: boolean;
}

export interface NotificationCount {
  unread: number;
}

export interface NotificationKind {
  kind: string;
  group: string;
  title: string;
  muted: boolean;
}

export interface NotificationSettings {
  kinds: NotificationKind[];
}
