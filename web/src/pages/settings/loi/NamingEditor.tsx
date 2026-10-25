import { useEffect, useState } from 'react';
import {
  ActionIcon, Alert, Badge, Button, Group, Popover, Select, SimpleGrid, Stack, Table, Tabs, Text, Textarea, TextInput, Tooltip,
} from '@mantine/core';
import { notifications } from '@mantine/notifications';
import { useQueryClient } from '@tanstack/react-query';
import { IconTrash } from '@tabler/icons-react';
import { apiFetch } from '../../../api/client';
import { useApi } from '../../../api/queries';
import type { KnownModel, NamingConfig, NamingEntry, NamingPreviewRow } from '../../../api/types';
import { Panel } from '../../../components/ui';
import { builtInFields, emptyNaming, fieldLabel, fieldsOf } from './naming';

/** Добавить нераспознанный кусок в справочник прямо из предпросмотра. */
function AddToken({ token, fields, onAdd }: { token: string; fields: string[]; onAdd: (entry: NamingEntry) => void }) {
  const [opened, setOpened] = useState(false);
  const [field, setField] = useState<string>('ignore');
  const [value, setValue] = useState('');
  const [customer, setCustomer] = useState('');

  return (
    <Popover opened={opened} onChange={setOpened} width={280} withArrow>
      <Popover.Target>
        <Badge color="orange" variant="light" style={{ cursor: 'pointer' }} onClick={() => setOpened((o) => !o)}>
          {token}
        </Badge>
      </Popover.Target>
      <Popover.Dropdown>
        <Stack gap="xs">
          <Text size="sm">Кусок «{token}» — это:</Text>
          <Select data={fields.map((f) => ({ value: f, label: fieldLabel(f) }))} value={field} allowDeselect={false} onChange={(v) => v && setField(v)} />
          {field !== 'ignore' && <TextInput placeholder="Значение, например «Полковая»" value={value} onChange={(e) => setValue(e.currentTarget.value)} />}
          {field === 'object' && <TextInput placeholder="Заказчик (необязательно)" value={customer} onChange={(e) => setCustomer(e.currentTarget.value)} />}
          <Button size="xs" disabled={field !== 'ignore' && !value.trim()}
            onClick={() => {
              onAdd({ field, tokens: token, value: value.trim(), customer: customer.trim() || null });
              setOpened(false);
            }}>
            Добавить в справочник
          </Button>
        </Stack>
      </Popover.Dropdown>
    </Popover>
  );
}

/**
 * Справочник именования: куски имени модели → объект, раздел, часть здания или свои поля. Предпросмотр показывает разбор
 * настоящих имён по ещё не сохранённым настройкам; нераспознанные куски добавляются в справочник щелчком.
 */
export function NamingEditor({ readOnly }: { readOnly: boolean }) {
  const queryClient = useQueryClient();
  const saved = useApi<NamingConfig>(['loi-analytics', 'naming'], '/api/loi/naming');
  const [draft, setDraft] = useState<NamingConfig | null>(null);
  const [names, setNames] = useState('');
  const [preview, setPreview] = useState<NamingPreviewRow[] | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [filter, setFilter] = useState<string>('object');
  const config = draft ?? saved.data ?? emptyNaming();
  const fields = fieldsOf(config);

  useEffect(() => {
    if (saved.data && !draft) setDraft(saved.data);
  }, [saved.data, draft]);

  const set = (patch: Partial<NamingConfig>) => setDraft({ ...config, ...patch });
  const updateEntry = (index: number, patch: Partial<NamingEntry>) => set({ entries: config.entries.map((e, i) => (i === index ? { ...e, ...patch } : e)) });

  async function runPreview(next: NamingConfig = config) {
    const list = names.split(/\r?\n/).map((n) => n.trim()).filter(Boolean);
    if (list.length === 0) return;
    try {
      setPreview(await apiFetch<NamingPreviewRow[]>('/api/loi/naming/preview', { method: 'POST', json: { config: next, names: list } }));
      setError(null);
    } catch (e) {
      setError((e as Error).message);
    }
  }

  async function loadKnown() {
    const models = await apiFetch<KnownModel[]>('/api/loi/naming/models');
    setNames(models.map((m) => m.name).join('\n'));
    if (models.length === 0) notifications.show({ color: 'yellow', message: 'Сервис ещё не знает ни одной модели — вставьте имена вручную' });
  }

  async function save() {
    try {
      const result = await apiFetch<NamingConfig>('/api/loi/naming', { method: 'PUT', json: config });
      setDraft(result);
      setError(null);
      notifications.show({ color: 'green', message: 'Справочник сохранён. Чтобы применить его к уже записанным проверкам, нажмите «Пересчитать аналитику»' });
      await queryClient.invalidateQueries({ queryKey: ['loi-analytics'] });
    } catch (e) {
      setError((e as Error).message);
    }
  }

  function addEntry(entry: NamingEntry) {
    const next = { ...config, entries: [...config.entries, entry] };
    setDraft(next);
    void runPreview(next);
  }

  const visible = config.entries.map((entry, index) => ({ entry, index })).filter(({ entry }) => entry.field === filter);

  return (
    <Panel title="Разбор имён моделей">
      <Stack>
        <Text size="sm" c="dimmed">
          Имя модели режется на куски по разделителям, и каждый кусок ищется в справочнике — где бы он ни стоял. Регистр, пробелы и русские буквы,
          похожие на латинские (К и K), не важны. Несколько кусков через «+» (например <code>DON+BNK-DOO</code>) задают значение вместе и важнее
          одиночных. Корпуса, секции и версию Revit распознают шаблоны.
        </Text>
        {error && <Alert color="red">{error}</Alert>}

        <TextInput label="Разделители" description="Символы между кусками имени" w={200} value={config.separators} disabled={readOnly}
          onChange={(e) => set({ separators: e.currentTarget.value })} />

        <Tabs value={filter} onChange={(v) => v && setFilter(v)}>
          <Tabs.List>
            {fields.map((f) => (
              <Tabs.Tab key={f} value={f}>
                {fieldLabel(f)} <Text span c="dimmed" size="xs">{config.entries.filter((e) => e.field === f).length}</Text>
              </Tabs.Tab>
            ))}
          </Tabs.List>
        </Tabs>
        <Table>
          <Table.Thead>
            <Table.Tr>
              <Table.Th>Куски имени</Table.Th>
              {filter !== 'ignore' && <Table.Th>Значение</Table.Th>}
              {filter === 'object' && <Table.Th>Заказчик</Table.Th>}
              <Table.Th w={40} />
            </Table.Tr>
          </Table.Thead>
          <Table.Tbody>
            {visible.map(({ entry, index }) => (
              <Table.Tr key={index}>
                <Table.Td><TextInput size="xs" value={entry.tokens} disabled={readOnly} onChange={(e) => updateEntry(index, { tokens: e.currentTarget.value })} /></Table.Td>
                {filter !== 'ignore' && (
                  <Table.Td><TextInput size="xs" value={entry.value} disabled={readOnly} onChange={(e) => updateEntry(index, { value: e.currentTarget.value })} /></Table.Td>
                )}
                {filter === 'object' && (
                  <Table.Td><TextInput size="xs" value={entry.customer ?? ''} disabled={readOnly} onChange={(e) => updateEntry(index, { customer: e.currentTarget.value })} /></Table.Td>
                )}
                <Table.Td>
                  {!readOnly && (
                    <ActionIcon variant="subtle" color="red" onClick={() => set({ entries: config.entries.filter((_, i) => i !== index) })} aria-label="Удалить запись">
                      <IconTrash size={16} />
                    </ActionIcon>
                  )}
                </Table.Td>
              </Table.Tr>
            ))}
          </Table.Tbody>
        </Table>
        {!readOnly && (
          <Group gap="xs">
            <Button size="xs" variant="default" onClick={() => set({ entries: [...config.entries, { field: filter, tokens: '', value: '', customer: null }] })}>
              + Запись «{fieldLabel(filter)}»
            </Button>
            <TextInput size="xs" placeholder="Своё поле, например «Стадия»" w={220}
              onKeyDown={(e) => {
                const value = e.currentTarget.value.trim();
                if (e.key === 'Enter' && value && !(value in builtInFields)) {
                  set({ entries: [...config.entries, { field: value, tokens: '', value: '', customer: null }] });
                  setFilter(value);
                  e.currentTarget.value = '';
                }
              }} />
          </Group>
        )}

        <Text size="sm" fw={500}>Шаблоны кусков</Text>
        <Text size="xs" c="dimmed">Регулярное выражение на один кусок целиком, латиницей и заглавными (кусок уже приведён к такому виду). В значении $1, $2 — группы.</Text>
        {config.patterns.map((pattern, index) => (
          <Group key={index} gap="xs" wrap="nowrap">
            <Select size="xs" w={150} data={fields.map((f) => ({ value: f, label: fieldLabel(f) }))} value={pattern.field} disabled={readOnly} allowDeselect={false}
              onChange={(v) => v && set({ patterns: config.patterns.map((p, i) => (i === index ? { ...p, field: v } : p)) })} />
            <TextInput size="xs" style={{ flex: 1 }} value={pattern.pattern} disabled={readOnly}
              onChange={(e) => set({ patterns: config.patterns.map((p, i) => (i === index ? { ...p, pattern: e.currentTarget.value } : p)) })} />
            <TextInput size="xs" style={{ flex: 1 }} placeholder="Значение" value={pattern.value} disabled={readOnly || pattern.field === 'ignore'}
              onChange={(e) => set({ patterns: config.patterns.map((p, i) => (i === index ? { ...p, value: e.currentTarget.value } : p)) })} />
            {!readOnly && (
              <ActionIcon variant="subtle" color="red" onClick={() => set({ patterns: config.patterns.filter((_, i) => i !== index) })} aria-label="Удалить шаблон">
                <IconTrash size={16} />
              </ActionIcon>
            )}
          </Group>
        ))}
        {!readOnly && (
          <Group>
            <Button size="xs" variant="default" onClick={() => set({ patterns: [...config.patterns, { field: 'part', pattern: '', value: '' }] })}>+ Шаблон</Button>
          </Group>
        )}

        <Textarea
          label="Регулярные выражения на имя целиком"
          description="Для строгих стандартов: группы object, discipline, part, customer. По одному на строку; срабатывает первое подошедшее"
          autosize
          minRows={1}
          value={config.nameExpressions.join('\n')}
          disabled={readOnly}
          onChange={(e) => set({ nameExpressions: e.currentTarget.value.split('\n').filter((line) => line.trim()) })}
        />

        {!readOnly && (
          <Group>
            <Button onClick={save}>Сохранить справочник</Button>
            <Button variant="default" onClick={() => saved.data && setDraft(saved.data)}>Отменить правки</Button>
          </Group>
        )}

        <Text size="sm" fw={500} mt="md">Предпросмотр</Text>
        <SimpleGrid cols={{ base: 1, md: 3 }}>
          <Stack gap="xs">
            <Textarea placeholder="Имена моделей, по одному на строку" autosize minRows={6} maxRows={16} value={names} onChange={(e) => setNames(e.currentTarget.value)} />
            <Group gap="xs">
              <Button size="xs" variant="default" onClick={loadKnown}>Модели сервиса</Button>
              <Button size="xs" onClick={() => runPreview()}>Разобрать</Button>
            </Group>
          </Stack>
          <div style={{ gridColumn: 'span 2', overflowX: 'auto' }}>
            {preview && (
              <Table fz="xs">
                <Table.Thead>
                  <Table.Tr>
                    <Table.Th>Модель</Table.Th>
                    <Table.Th>Заказчик</Table.Th>
                    <Table.Th>Объект</Table.Th>
                    <Table.Th>Раздел</Table.Th>
                    <Table.Th>Часть</Table.Th>
                    <Table.Th>Не распознано</Table.Th>
                  </Table.Tr>
                </Table.Thead>
                <Table.Tbody>
                  {preview.map((row) => (
                    <Table.Tr key={row.name}>
                      <Table.Td>{row.name}</Table.Td>
                      <Table.Td>{row.result.customer ?? <Text span c="dimmed">—</Text>}</Table.Td>
                      <Table.Td>{row.result.object ?? <Text span c="red">—</Text>}</Table.Td>
                      <Table.Td>{row.result.discipline ?? <Text span c="red">—</Text>}</Table.Td>
                      <Table.Td>{row.result.part ?? ''}</Table.Td>
                      <Table.Td>
                        <Group gap={4}>
                          {row.result.unrecognized.map((token) =>
                            readOnly ? <Badge key={token} color="orange" variant="light">{token}</Badge> : (
                              <Tooltip key={token} label="Щёлкните, чтобы добавить в справочник">
                                <span><AddToken token={token} fields={fields} onAdd={addEntry} /></span>
                              </Tooltip>
                            ))}
                        </Group>
                      </Table.Td>
                    </Table.Tr>
                  ))}
                </Table.Tbody>
              </Table>
            )}
          </div>
        </SimpleGrid>
      </Stack>
    </Panel>
  );
}
