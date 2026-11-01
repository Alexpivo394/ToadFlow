import { Paper, SimpleGrid, Spoiler, Stack, Table, Text, UnstyledButton } from '@mantine/core';
import type { ReportView } from '../../../api/types';
import { Panel } from '../../../components/ui';
import { formatFullDateTime } from './ReportPage';

const n = (value: number) => value.toLocaleString('ru-RU');

function Tile({ label, value, color }: { label: string; value: number; color?: string }) {
  return (
    <Paper withBorder p="xs" radius="md">
      <Text fw={700} size="xl" c={color}>{n(value)}</Text>
      <Text size="xs" c="dimmed">{label}</Text>
    </Paper>
  );
}

/** «Сводка» отчёта: модели (свёрнуто), плитки и таблица пар в порядке справочника; щелчок по паре выбирает её. */
export function ReportSummary({ view, onPair }: { view: ReportView; onPair: (pair: string) => void }) {
  const s = view.summary;
  return (
    <Stack gap="sm">
      <Panel>
        <Spoiler maxHeight={0} showLabel={`Модели (${view.models.length})`} hideLabel="Скрыть модели">
          <Stack gap={2}>
            {view.models.map((m) => (
              <Text key={m.name} size="sm" c={m.unchecked ? 'red' : undefined}>
                {m.name} · {m.discipline} · выгружена {formatFullDateTime(m.uploadedAt)}{m.unchecked ? ' · не выгрузилась, не проверена' : ''}
              </Text>
            ))}
          </Stack>
        </Spoiler>
        <SimpleGrid cols={{ base: 2, sm: 4, lg: 7 }} mt="sm" data-testid="report-tiles">
          <Tile label="Новых" value={s.new} color="red" />
          <Tile label="Вернулись" value={s.returned} color="grape" />
          <Tile label="Активных" value={s.active} color="orange" />
          <Tile label="Исправлено" value={s.fixed} color="green" />
          <Tile label="Ушли из-за правил" value={s.goneByRules} />
          <Tile label="Не проверено" value={s.unchecked} color="yellow.8" />
          <Tile label="Фиктивных" value={s.fake} />
        </SimpleGrid>
      </Panel>

      {view.pairs.length > 0 && (
        <Panel>
          <Table highlightOnHover>
            <Table.Thead>
              <Table.Tr>
                <Table.Th>Пара разделов</Table.Th>
                <Table.Th ta="right">Открытых сейчас</Table.Th>
                <Table.Th ta="right">Новых</Table.Th>
                <Table.Th ta="right">Вернулись</Table.Th>
                <Table.Th ta="right">Исправлено</Table.Th>
                <Table.Th ta="right">Фиктивных</Table.Th>
              </Table.Tr>
            </Table.Thead>
            <Table.Tbody data-testid="report-pairs">
              {view.pairs.map((p) => (
                <Table.Tr key={p.pair}>
                  <Table.Td>
                    <UnstyledButton onClick={() => onPair(p.pair)}><Text size="sm" c="blue">{p.label}</Text></UnstyledButton>
                  </Table.Td>
                  <Table.Td ta="right">{n(p.open)}</Table.Td>
                  <Table.Td ta="right">{n(p.new)}</Table.Td>
                  <Table.Td ta="right">{n(p.returned)}</Table.Td>
                  <Table.Td ta="right">{n(p.fixed)}</Table.Td>
                  <Table.Td ta="right">{n(p.fake)}</Table.Td>
                </Table.Tr>
              ))}
            </Table.Tbody>
          </Table>
        </Panel>
      )}
    </Stack>
  );
}
