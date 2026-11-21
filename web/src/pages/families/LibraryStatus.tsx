import { Alert, Badge, Button, Group, List, Stack, Table, Text } from '@mantine/core';
import { useApi, useApiMutation } from '../../api/queries';
import { useAuth } from '../../auth/AuthContext';
import { Panel } from '../../components/ui';
import { t } from '../../i18n/ru';
import { familiesKey, type LibraryStatus as Status } from './api';

const dateTime = (iso: string) => new Date(iso).toLocaleString('ru-RU', { dateStyle: 'short', timeStyle: 'short' });

/** Состояние библиотеки: агент, последний обход, полный ли он, ошибки обхода, очередь чтения; «Обойти сейчас» — координатору. */
export function LibraryStatus() {
  const { can, isStaff } = useAuth();
  const status = useApi<Status>([familiesKey, 'status'], '/api/families/status', { refetchInterval: 30_000 });
  const scan = useApiMutation('POST', () => '/api/families/scan', { invalidate: [familiesKey], success: t.families.scanStarted });
  const s = status.data;
  if (!s) return null;

  const agent = s.agentName ? (
    <Group gap={6}>
      <Text size="sm">{s.agentName}</Text>
      {!s.agentOnline && <Badge color="red" variant="light">{t.families.agentOffline}</Badge>}
    </Group>
  ) : (
    <Text size="sm" c="red">{t.families.noAgent}</Text>
  );

  return (
    <Panel title={t.families.statusTitle}
      actions={isStaff && can('Operator') ? (
        <Button size="compact-sm" loading={scan.isPending} disabled={!s.configured} onClick={() => scan.mutate()}>{t.families.scanNow}</Button>
      ) : undefined}>
      {!s.configured ? (
        <Text size="sm" c="dimmed">{t.families.notConfigured}</Text>
      ) : (
        <Stack gap="sm">
          <Table withRowBorders={false} verticalSpacing={4}>
            <Table.Tbody>
              <Table.Tr>
                <Table.Th w={240}>{t.families.agent}</Table.Th>
                <Table.Td>{agent}</Table.Td>
              </Table.Tr>
              <Table.Tr>
                <Table.Th>{t.families.lastScan}</Table.Th>
                <Table.Td>
                  {s.lastScanAt ? (
                    <Group gap={6}>
                      <Text size="sm">{dateTime(s.lastScanAt)}</Text>
                      <Badge variant="light" color={s.lastScanComplete ? 'green' : 'orange'}>
                        {s.lastScanComplete ? t.families.complete : t.families.incomplete}
                      </Badge>
                    </Group>
                  ) : (
                    <Text size="sm" c="dimmed">{t.families.neverScanned}</Text>
                  )}
                </Table.Td>
              </Table.Tr>
              <Table.Tr>
                <Table.Th>{t.families.pendingReads}</Table.Th>
                <Table.Td>{s.pendingReads}</Table.Td>
              </Table.Tr>
              <Table.Tr>
                <Table.Th>{t.families.failedReads}</Table.Th>
                <Table.Td>{s.failedReads}</Table.Td>
              </Table.Tr>
            </Table.Tbody>
          </Table>
          {s.lastScanErrors.length > 0 && (
            <Alert color="orange" title={t.families.scanErrors}>
              <List size="sm">
                {s.lastScanErrors.map((error) => <List.Item key={error}>{error}</List.Item>)}
              </List>
            </Alert>
          )}
        </Stack>
      )}
    </Panel>
  );
}
