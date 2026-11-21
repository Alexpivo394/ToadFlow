import { Button, Tooltip } from '@mantine/core';
import { IconBell, IconBellRinging } from '@tabler/icons-react';
import { useQueryClient } from '@tanstack/react-query';
import { apiFetch } from '../../api/client';
import { useApi } from '../../api/queries';
import { t } from '../../i18n/ru';

/** «Следить» за расписанием: уведомления об ошибках его запусков. Создатель следит с создания. */
export function FollowButton({ id }: { id: string }) {
  const queryClient = useQueryClient();
  const following = useApi<string[]>(['schedules', 'following'], '/api/schedules/following');
  const on = (following.data ?? []).includes(id);
  const toggle = async () => {
    await apiFetch(`/api/schedules/${id}/follow`, { method: on ? 'DELETE' : 'POST' });
    void queryClient.invalidateQueries({ queryKey: ['schedules', 'following'] });
  };
  return (
    <Tooltip label={t.insights.followHint}>
      <Button size="xs" variant={on ? 'light' : 'default'} leftSection={on ? <IconBellRinging size={14} /> : <IconBell size={14} />}
        disabled={following.isLoading} onClick={() => void toggle()}>
        {on ? t.insights.following : t.insights.follow}
      </Button>
    </Tooltip>
  );
}
