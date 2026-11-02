import { Alert, Button, Collapse, Group, List, Stack, Table, Text, UnstyledButton } from '@mantine/core';
import { useState } from 'react';
import { useApi, useApiMutation } from '../../../api/queries';
import type { ApprovePreview } from '../../../api/types';
import { formatFullDateTime } from '../reports/ReportPage';

const n = (value: number) => value.toLocaleString('ru-RU');
const MaxPairs = 8;

/** «пара», «пары», «пар» по числу. */
export function pairsWord(count: number): string {
  const tens = count % 100;
  const ones = count % 10;
  if (tens >= 11 && tens <= 14) return 'пар';
  if (ones === 1) return 'пара';
  if (ones >= 2 && ones <= 4) return 'пары';
  return 'пар';
}

/**
 * «Если утвердить» по последнему прогону черновика: сколько коллизий уйдёт из-за правил, появится и останется — по парам
 * разделов, с «≈», потому что прогон мог разойтись с реальными выгрузками. Ниже — свёрнутый список отличий настройки.
 * `dirty` — в редакторе есть несохранённые правки: числа их не учитывают, «Прогнать черновик» сначала сохраняет.
 */
export function ApprovePreviewBlock({ partId, dirty = false, onSave }: { partId: string; dirty?: boolean; onSave?: () => Promise<void> }) {
  const preview = useApi<ApprovePreview>(['clash-parts', partId, 'approve-preview'], `/api/clash/parts/${partId}/approve/preview`);
  const rerun = useApiMutation<void>('POST', () => `/api/clash/parts/${partId}/draft/run`, { invalidate: ['clash-parts'] });
  const [changesOpen, setChangesOpen] = useState(false);
  const [saving, setSaving] = useState(false);
  const data = preview.data;
  const runDraft = async () => {
    if (dirty && onSave) {
      setSaving(true);
      try {
        await onSave();
      } catch {
        return; // сохранение само показало ошибку; прогон старой версии не нужен
      } finally {
        setSaving(false);
      }
    }
    rerun.mutate();
  };
  if (!data) return null;

  const pairs = [...data.pairs].sort((a, b) => b.gone + b.new - (a.gone + a.new));
  const shown = pairs.slice(0, MaxPairs);
  const rest = pairs.length - shown.length;
  const total = pairs.reduce((s, p) => ({ gone: s.gone + p.gone, new: s.new + p.new, same: s.same + p.same }), { gone: 0, new: 0, same: 0 });

  return (
    <Stack gap="xs">
      <Text fw={700} size="sm">Если утвердить</Text>
      {data.neverRun ? (
        <Text size="sm" c="dimmed">Черновик ещё не прогонялся</Text>
      ) : (
        <>
          {(dirty || data.stale) && (
            <Alert color="yellow" p="xs">
              <Group justify="space-between" gap="xs">
                <Text size="sm">{dirty ? 'Есть несохранённые правки — числа по прогону до них' : 'Черновик изменён после прогона — числа устарели'}</Text>
                <Button size="compact-sm" variant="light" loading={saving || rerun.isPending} onClick={() => void runDraft()}>Прогнать черновик</Button>
              </Group>
            </Alert>
          )}
          {data.firstApproval ? (
            <Text size="sm">Появятся ≈ {n(total.new)}</Text>
          ) : (
            <Table fz="xs" withTableBorder verticalSpacing={2}>
              <Table.Thead>
                <Table.Tr>
                  <Table.Th>Пара разделов</Table.Th>
                  <Table.Th ta="right">Уйдут из-за правил</Table.Th>
                  <Table.Th ta="right">Появятся новые</Table.Th>
                  <Table.Th ta="right">Без изменений</Table.Th>
                </Table.Tr>
              </Table.Thead>
              <Table.Tbody>
                {shown.map((p) => (
                  <Table.Tr key={p.pair} data-testid="preview-row">
                    <Table.Td>{p.pair}</Table.Td>
                    <Table.Td ta="right">≈ {n(p.gone)}</Table.Td>
                    <Table.Td ta="right">≈ {n(p.new)}</Table.Td>
                    <Table.Td ta="right">{n(p.same)}</Table.Td>
                  </Table.Tr>
                ))}
                {rest > 0 && (
                  <Table.Tr>
                    <Table.Td colSpan={4}><Text size="xs" c="dimmed">ещё {n(rest)} {pairsWord(rest)}</Text></Table.Td>
                  </Table.Tr>
                )}
                <Table.Tr data-testid="preview-total" fw={700}>
                  <Table.Td>Итого</Table.Td>
                  <Table.Td ta="right">≈ {n(total.gone)}</Table.Td>
                  <Table.Td ta="right">≈ {n(total.new)}</Table.Td>
                  <Table.Td ta="right">{n(total.same)}</Table.Td>
                </Table.Tr>
              </Table.Tbody>
            </Table>
          )}
          <Text size="xs" c="dimmed">≈ по прогону черновика от {formatFullDateTime(data.draftRunAt)}</Text>
        </>
      )}
      {data.changes.length > 0 && (
        <>
          <UnstyledButton onClick={() => setChangesOpen((o) => !o)}>
            <Text size="sm" c="blue">{changesOpen ? '▾' : '▸'} Изменения в настройке: {n(data.changes.length)}</Text>
          </UnstyledButton>
          <Collapse expanded={changesOpen}>
            <List size="xs">{data.changes.map((c, i) => <List.Item key={i}>{c.text}</List.Item>)}</List>
          </Collapse>
        </>
      )}
    </Stack>
  );
}
