import { Loader, Stack } from '@mantine/core';
import { useApi } from '../../api/queries';
import type { ServerStats } from '../../api/types';
import { Panel } from '../../components/ui';
import { ServerFacts } from '../overview/ServerBlock';
import { GeneralSettingsForm } from './GeneralSettingsForm';

/** Сервер: те же сведения, что на «Обзоре», и общие настройки (правит администратор). */
export function ServerPage() {
  const server = useApi<ServerStats>(['server'], '/api/server', { refetchInterval: 60_000 });

  return (
    <Stack>
      <Panel title="Состояние">{server.data ? <ServerFacts stats={server.data} /> : <Loader size="sm" />}</Panel>
      <GeneralSettingsForm />
    </Stack>
  );
}
