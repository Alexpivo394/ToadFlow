import { Box, Group, SegmentedControl, Select, Stack, Text, UnstyledButton } from '@mantine/core';
import type { ReportDisciplineView, ReportPairView, ReportView } from '../../../api/types';

/** Что выбрано в дереве: сводка, раздел или пара (ключ пары). */
export type ReportSelection = { kind: 'summary' } | { kind: 'discipline'; name: string } | { kind: 'pair'; pair: string };

const MineKey = 'clash.report.mine';

/** «Мои разделы» в дереве — запомненный выбор браузера; по умолчанию включено. */
export function readMine(): boolean {
  try {
    return localStorage.getItem(MineKey) !== '0';
  } catch {
    return true;
  }
}

export function writeMine(mine: boolean) {
  try {
    localStorage.setItem(MineKey, mine ? '1' : '0');
  } catch {
    // Хранилище браузера недоступно — выбор живёт до перезагрузки.
  }
}

type Counts = { open: number; new: number; returned: number; fixed: number };

/** Есть ли у узла коллизии в области отчёта. */
export const inScope = (c: Counts, scope: string) => (scope === 'open' ? c.open > 0 : c.new + c.returned + c.fixed > 0);

/** Счётчики области: в «Изменениях» — новых / вернулось / исправлено, в «Открытых» — открыто. */
function Count({ c, scope }: { c: Counts; scope: string }) {
  const n = (v: number) => v.toLocaleString('ru-RU');
  return scope === 'open'
    ? <Text size="xs" c="dimmed" title="открыто">{n(c.open)}</Text>
    : <Text size="xs" c="dimmed" title="новых / вернулись / исправлено">{n(c.new)} / {n(c.returned)} / {n(c.fixed)}</Text>;
}

function Node({ label, aria, active, indent, onClick, children }: {
  label: string; aria?: string; active: boolean; indent: number; onClick: () => void; children?: React.ReactNode;
}) {
  return (
    <UnstyledButton aria-label={aria} onClick={onClick} pl={indent} pr={6} py={3}
      bg={active ? 'var(--mantine-primary-color-light)' : undefined} style={{ borderRadius: 4 }}>
      <Group justify="space-between" wrap="nowrap" gap={6}>
        <Text size="sm" fw={indent === 0 ? 600 : 400} truncate>{label}</Text>
        {children}
      </Group>
    </UnstyledButton>
  );
}

/** Разделы дерева: только с коллизиями в области и, если включено «Мои», — только разделы пользователя. */
export function visibleDisciplines(view: ReportView, scope: string, mine: string[] | null): ReportDisciplineView[] {
  return view.disciplines.filter((d) => inScope(d, scope) && (mine === null || mine.includes(d.name)));
}

/**
 * Дерево отчёта: «Сводка», разделы в порядке справочника, под каждым — его пары (пара двух разделов видна под обоими).
 * Наверху — «Мои разделы / Все», если у пользователя есть разделы.
 */
export function ReportTree({ view, scope, selection, myDisciplines, mine, onMine, onSelect }: {
  view: ReportView;
  scope: string;
  selection: ReportSelection;
  myDisciplines: string[];
  mine: boolean;
  onMine: (mine: boolean) => void;
  onSelect: (selection: ReportSelection) => void;
}) {
  const pairs = new Map(view.pairs.map((p) => [p.pair, p]));
  const disciplines = visibleDisciplines(view, scope, myDisciplines.length > 0 && mine ? myDisciplines : null);
  return (
    <Stack gap={2} data-testid="report-tree">
      {myDisciplines.length > 0 && (
        <SegmentedControl size="xs" mb={4} fullWidth value={mine ? 'mine' : 'all'} onChange={(v) => onMine(v === 'mine')}
          data={[{ value: 'mine', label: 'Мои разделы' }, { value: 'all', label: 'Все' }]} />
      )}
      <Node label="Сводка" aria="Сводка" indent={0} active={selection.kind === 'summary'} onClick={() => onSelect({ kind: 'summary' })} />
      {disciplines.length > 0 && (
        <Group justify="space-between" wrap="nowrap" gap={6} pl={0} pr={6} pt={4} data-testid="tree-columns">
          <Text size="xs" c="dimmed">Раздел, пара</Text>
          <Text size="xs" c="dimmed">{scope === 'open' ? 'открыто' : 'новых / вернулись / исправлено'}</Text>
        </Group>
      )}
      {disciplines.map((d) => (
        <Box key={d.name} data-testid="tree-discipline" data-name={d.name}>
          <Stack gap={0}>
            <Node label={d.name} aria={`Раздел ${d.name}`} indent={0}
              active={selection.kind === 'discipline' && selection.name === d.name} onClick={() => onSelect({ kind: 'discipline', name: d.name })}>
              <Count c={d} scope={scope} />
            </Node>
            {d.pairs.map((key) => pairs.get(key)).filter((p): p is ReportPairView => !!p && inScope(p, scope)).map((p) => (
              <Node key={p.pair} label={p.label} aria={`Пара ${p.label}`} indent={16}
                active={selection.kind === 'pair' && selection.pair === p.pair} onClick={() => onSelect({ kind: 'pair', pair: p.pair })}>
                <Count c={p} scope={scope} />
              </Node>
            ))}
          </Stack>
        </Box>
      ))}
    </Stack>
  );
}

const selectionValue = (s: ReportSelection) => (s.kind === 'summary' ? 'summary' : s.kind === 'discipline' ? `d:${s.name}` : `p:${s.pair}`);

/** Дерево на узком экране — выпадающий список: сводка, разделы, пары. */
export function ReportTreeSelect({ view, scope, selection, onSelect }: {
  view: ReportView;
  scope: string;
  selection: ReportSelection;
  onSelect: (selection: ReportSelection) => void;
}) {
  const data = [
    { group: 'Отчёт', items: [{ value: 'summary', label: 'Сводка' }] },
    { group: 'Разделы', items: visibleDisciplines(view, scope, null).map((d) => ({ value: `d:${d.name}`, label: d.name })) },
    { group: 'Пары', items: view.pairs.filter((p) => inScope(p, scope)).map((p) => ({ value: `p:${p.pair}`, label: p.label })) },
  ];
  return (
    <Select size="xs" aria-label="Раздел или пара" data={data} value={selectionValue(selection)} allowDeselect={false}
      onChange={(v) => {
        if (!v || v === 'summary') onSelect({ kind: 'summary' });
        else if (v.startsWith('d:')) onSelect({ kind: 'discipline', name: v.slice(2) });
        else onSelect({ kind: 'pair', pair: v.slice(2) });
      }} />
  );
}
