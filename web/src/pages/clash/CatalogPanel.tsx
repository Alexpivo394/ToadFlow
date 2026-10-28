import { useMemo, useState } from 'react';
import { Badge, Group, MultiSelect, Stack, Table, Tabs, Text, TextInput } from '@mantine/core';
import { useApi } from '../../api/queries';
import type { ClashModelView, ModelCatalog } from '../../api/types';
import { Empty, Panel } from '../../components/ui';

const number = (value: number) => value.toLocaleString('ru-RU');

function ValueList({ values }: { values: string[] }) {
  if (values.length === 0) return <Empty>Нет значений</Empty>;
  return (
    <Group gap={6}>
      {values.map((v) => (
        <Badge key={v} variant="light" color="gray" tt="none">{v}</Badge>
      ))}
    </Group>
  );
}

/** Что встречается в последних выгрузках выбранных моделей — подсказки для будущих наборов проверок. */
export function CatalogPanel({ models }: { models: ClashModelView[] }) {
  const [selected, setSelected] = useState<string[]>([]);
  const [search, setSearch] = useState('');
  const query = selected.map((id) => `modelId=${encodeURIComponent(id)}`).join('&');
  const catalog = useApi<ModelCatalog>(['clash-models', 'catalog', ...selected], `/api/clash/catalog${query ? `?${query}` : ''}`);

  const parameters = useMemo(() => {
    const needle = search.trim().toLowerCase();
    const all = catalog.data?.parameters ?? [];
    return needle ? all.filter((p) => p.name.toLowerCase().includes(needle)) : all;
  }, [catalog.data, search]);

  const data = catalog.data;
  return (
    <Panel title="Каталог категорий и параметров">
      <Stack gap="sm">
        <MultiSelect label="Модели" placeholder={selected.length ? undefined : 'Все модели'} searchable clearable maw={640}
          data={models.filter((m) => m.latest).map((m) => ({ value: m.id, label: m.name }))} value={selected} onChange={setSelected} />
        {!data ? null : (
          <Tabs defaultValue="categories" keepMounted={false}>
            <Tabs.List mb="sm">
              <Tabs.Tab value="categories">Категории ({data.categories.length})</Tabs.Tab>
              <Tabs.Tab value="parameters">Параметры</Tabs.Tab>
              <Tabs.Tab value="values">Семейства, типы, уровни, рабочие наборы</Tabs.Tab>
            </Tabs.List>
            <Tabs.Panel value="categories">
              {data.categories.length === 0 ? (
                <Empty>Выгрузок ещё нет</Empty>
              ) : (
                <Table>
                  <Table.Thead>
                    <Table.Tr>
                      <Table.Th>Категория</Table.Th>
                      <Table.Th ta="right">Элементов</Table.Th>
                    </Table.Tr>
                  </Table.Thead>
                  <Table.Tbody>
                    {data.categories.map((c) => (
                      <Table.Tr key={c.key}>
                        <Table.Td>{c.key}</Table.Td>
                        <Table.Td ta="right">{number(c.count)}</Table.Td>
                      </Table.Tr>
                    ))}
                  </Table.Tbody>
                </Table>
              )}
            </Tabs.Panel>
            <Tabs.Panel value="parameters">
              <Stack gap="sm">
                <TextInput placeholder="Поиск параметра" maw={360} value={search} onChange={(e) => setSearch(e.currentTarget.value)} />
                <Table>
                  <Table.Thead>
                    <Table.Tr>
                      <Table.Th>Параметр</Table.Th>
                      <Table.Th>Значения</Table.Th>
                      <Table.Th>Категории</Table.Th>
                      <Table.Th ta="right">Элементов</Table.Th>
                    </Table.Tr>
                  </Table.Thead>
                  <Table.Tbody>
                    {parameters.map((p) => (
                      <Table.Tr key={p.sharedGuid ?? p.name}>
                        <Table.Td>
                          <Text size="sm">{p.name}</Text>
                          {p.sharedGuid && <Text size="xs" c="dimmed">{p.sharedGuid}</Text>}
                        </Table.Td>
                        <Table.Td>{p.isNumeric ? 'число' : 'текст'}</Table.Td>
                        <Table.Td><Text size="xs">{p.categories.join(', ')}</Text></Table.Td>
                        <Table.Td ta="right">{number(p.count)}</Table.Td>
                      </Table.Tr>
                    ))}
                  </Table.Tbody>
                </Table>
                {parameters.length === 0 && <Empty>Ничего не нашлось</Empty>}
              </Stack>
            </Tabs.Panel>
            <Tabs.Panel value="values">
              <Stack gap="md">
                {([['Семейства', data.families], ['Типы', data.types], ['Уровни', data.levels], ['Рабочие наборы', data.worksets]] as const).map(([title, values]) => (
                  <Stack key={title} gap={6}>
                    <Text fw={600} size="sm">{title}</Text>
                    <ValueList values={[...values]} />
                  </Stack>
                ))}
              </Stack>
            </Tabs.Panel>
          </Tabs>
        )}
      </Stack>
    </Panel>
  );
}
