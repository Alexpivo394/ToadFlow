import { Link, useSearchParams } from 'react-router';
import { Alert, Anchor, Badge, Group, Loader, Paper, SimpleGrid, Stack, Table, Text, Title } from '@mantine/core';
import { LineChart } from '@mantine/charts';
import { useApi } from '../../api/queries';
import type { ModelHistory } from '../../api/types';
import { Panel } from '../../components/ui';
import { formatDateTime } from '../../lib/format';
import { toPercent } from './filters';
import { Delta, orNone, Share, Stat } from './parts';

const percent = (value: number) => `${value.toString().replace('.', ',')} %`;

/** История одной модели: как менялась заполненность, что с правилами и параметрами в последней проверке. */
export function ModelPage() {
  const [params] = useSearchParams();
  const key = params.get('key') ?? '';
  const history = useApi<ModelHistory>(['loi-analytics', 'model', key], key ? `/api/loi/analytics/model?key=${encodeURIComponent(key)}` : null);

  if (!key) return <Alert color="yellow">Не указана модель — выберите её на вкладке «Модели».</Alert>;
  if (history.error) return <Alert color="red">{history.error.message}</Alert>;
  const data = history.data;
  if (!data) return <Loader />;

  const model = data.model;
  const latestId = data.checks[data.checks.length - 1]?.id;
  const rules = data.rules.filter((r) => r.checkId === latestId);
  const fingerprints = new Set(data.checks.map((c) => c.profileFingerprint));

  return (
    <Stack>
      <Anchor component={Link} to="/analytics/models" size="sm">← К моделям</Anchor>
      {/* Имя модели — заголовком как есть: подпись панели набрана капслоком, а в именах моделей регистр значим. */}
      <Paper withBorder p="md" radius="md">
        <Title order={3}>{model.modelName}</Title>
        <Group gap="lg" mt={4}>
          <Text size="sm">{[model.customer, orNone(model.object), orNone(model.discipline), model.part].filter(Boolean).join(' · ')}</Text>
          <Text size="sm" c="dimmed">{model.modelPath}</Text>
        </Group>
      </Paper>
      <SimpleGrid cols={{ base: 2, md: 4 }}>
        <Stat title="Заполненность" value={<Share value={model.fillRate} size="xl" />} />
        <Stat title="Полнота" value={<Share value={model.completeness} size="xl" />} />
        <Stat title="С прошлой проверки" value={<Delta now={model.fillRate} before={model.previousFillRate} profileChanged={model.profileChanged} />} />
        <Stat title="Проверок" value={data.checks.length} hint={`последняя ${formatDateTime(model.checkedAt)}`} />
      </SimpleGrid>
      {fingerprints.size > 1 && (
        <Alert color="yellow">За историю модели правила профиля менялись {fingerprints.size - 1} раз(а) — на графике это места, где доли можно сравнивать с оговоркой.</Alert>
      )}
      <Panel title="История">
        <LineChart h={240}
          data={data.checks.map((c) => ({ at: formatDateTime(c.checkedAt), Заполненность: toPercent(c.fillRate), Полнота: toPercent(c.completeness) }))}
          dataKey="at" yAxisProps={{ domain: [0, 100] }} valueFormatter={percent} connectNulls withLegend
          series={[{ name: 'Заполненность', color: 'blue.6' }, { name: 'Полнота', color: 'teal.5' }]} />
      </Panel>
      <SimpleGrid cols={{ base: 1, md: 2 }}>
        <Panel title="Правила в последней проверке">
          <Table fz="sm">
            <Table.Thead>
              <Table.Tr><Table.Th>Правило</Table.Th><Table.Th>Элементов</Table.Th><Table.Th>Заполненность</Table.Th><Table.Th>Полнота</Table.Th></Table.Tr>
            </Table.Thead>
            <Table.Tbody>
              {rules.map((r) => (
                <Table.Tr key={r.position}>
                  <Table.Td>{r.title || `Правило ${r.position + 1}`}</Table.Td>
                  <Table.Td>{r.elements === 0 ? 'нет элементов' : r.elements}</Table.Td>
                  <Table.Td><Share value={r.fillRate} /></Table.Td>
                  <Table.Td><Share value={r.completeness} /></Table.Td>
                </Table.Tr>
              ))}
            </Table.Tbody>
          </Table>
        </Panel>
        <Panel title="Параметры в последней проверке">
          <Table fz="sm">
            <Table.Thead>
              <Table.Tr><Table.Th>Параметр</Table.Th><Table.Th>Правило</Table.Th><Table.Th>Заполненность</Table.Th><Table.Th>Пусто / нет</Table.Th></Table.Tr>
            </Table.Thead>
            <Table.Tbody>
              {data.parameters.map((p, i) => (
                <Table.Tr key={i}>
                  <Table.Td fw={500}>{p.name}</Table.Td>
                  <Table.Td c="dimmed">{p.rule}</Table.Td>
                  <Table.Td><Share value={p.fillRate} /></Table.Td>
                  <Table.Td>{p.empty} / <Text span c={p.missing > 0 ? 'red.7' : undefined}>{p.missing}</Text></Table.Td>
                </Table.Tr>
              ))}
            </Table.Tbody>
          </Table>
        </Panel>
      </SimpleGrid>
      <Panel title="Проверки">
        <Table fz="sm">
          <Table.Tbody>
            {[...data.checks].reverse().map((c) => (
              <Table.Tr key={c.id}>
                <Table.Td>{formatDateTime(c.checkedAt)}</Table.Td>
                <Table.Td>{c.source === 'Manual' ? <Badge size="xs" variant="light" color="grape">вручную · {c.userName}</Badge> : 'по расписанию'}</Table.Td>
                <Table.Td c="dimmed">профиль «{c.profileName}»</Table.Td>
                <Table.Td>элементов {c.elements}</Table.Td>
                <Table.Td><Share value={c.fillRate} /></Table.Td>
              </Table.Tr>
            ))}
          </Table.Tbody>
        </Table>
      </Panel>
    </Stack>
  );
}
