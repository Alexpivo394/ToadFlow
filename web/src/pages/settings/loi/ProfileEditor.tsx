import { useState } from 'react';
import { Alert, Button, Group, SimpleGrid, Stack, Text, TextInput } from '@mantine/core';
import { notifications } from '@mantine/notifications';
import { useQueryClient } from '@tanstack/react-query';
import { apiFetch } from '../../../api/client';
import type { CheckProfile, CheckRule, LoiProfileSummary } from '../../../api/types';
import { Panel } from '../../../components/ui';
import { t } from '../../../i18n/ru';
import { newRule } from '../../../lib/loi';
import { RuleEditor } from './RuleEditor';
import type { LoiCatalog } from './useLoiCatalog';

interface Props {
  /** null — новый профиль. */
  id: string | null;
  initial: CheckProfile;
  catalog: LoiCatalog;
  readOnly?: boolean;
  onDone: () => void;
}

/** Профиль проверки LOI: вид и ключевое слово РН по умолчанию и правила. Модели профилю не нужны — их задаёт расписание. */
export function ProfileEditor({ id, initial, catalog, readOnly, onDone }: Props) {
  const queryClient = useQueryClient();
  const [profile, setProfile] = useState<CheckProfile>(initial);
  const [error, setError] = useState<string | null>(null);
  const [saving, setSaving] = useState(false);

  const setRules = (rules: CheckRule[]) => setProfile({ ...profile, Rules: rules });
  const updateRule = (index: number, rule: CheckRule) => setRules(profile.Rules.map((r, i) => (i === index ? rule : r)));
  const moveRule = (index: number, offset: -1 | 1) => {
    const rules = [...profile.Rules];
    [rules[index], rules[index + offset]] = [rules[index + offset], rules[index]];
    setRules(rules);
  };

  async function save() {
    setSaving(true);
    setError(null);
    try {
      const saved = await apiFetch<LoiProfileSummary>(id ? `/api/loi/profiles/${id}` : '/api/loi/profiles', {
        method: id ? 'PUT' : 'POST',
        json: { profile },
      });
      notifications.show({ color: 'green', message: 'Профиль сохранён' });
      if (saved.problems.length > 0)
        notifications.show({ color: 'yellow', title: 'Профиль ещё не готов к запуску', message: saved.problems.join('; '), autoClose: 10_000 });
      await queryClient.invalidateQueries({ queryKey: ['loi-profiles'] });
      onDone();
    } catch (e) {
      setError((e as Error).message);
    } finally {
      setSaving(false);
    }
  }

  return (
    <Panel title={id ? `Профиль «${initial.Name}»` : 'Новый профиль'}>
      <Stack>
        {error && <Alert color="red">{error}</Alert>}
        <SimpleGrid cols={{ base: 1, md: 3 }}>
          <TextInput label="Название" value={profile.Name} disabled={readOnly} required onChange={(e) => setProfile({ ...profile, Name: e.currentTarget.value })} />
          <TextInput label="Вид по умолчанию" description="Пусто — проверяется вся модель" value={profile.ViewName} disabled={readOnly}
            onChange={(e) => setProfile({ ...profile, ViewName: e.currentTarget.value })} />
          <TextInput label="Не открывать РН, содержащие" description="Пусто — открываются все рабочие наборы" value={profile.WorksetKeyword}
            disabled={readOnly} onChange={(e) => setProfile({ ...profile, WorksetKeyword: e.currentTarget.value })} />
        </SimpleGrid>

        {!catalog.loaded && (
          <Text size="sm" c="dimmed">
            Подписей категорий и подсказок параметров пока нет — снимите каталог из Revit кнопкой «Обновить из Revit». Править правила можно и без него.
          </Text>
        )}

        {profile.Rules.map((rule, index) => (
          <RuleEditor
            key={index}
            rule={rule}
            index={index}
            count={profile.Rules.length}
            catalog={catalog}
            readOnly={readOnly}
            onChange={(r) => updateRule(index, r)}
            onMove={(offset) => moveRule(index, offset)}
            onDuplicate={() => setRules([...profile.Rules.slice(0, index + 1), structuredClone(rule), ...profile.Rules.slice(index + 1)])}
            onDelete={() => setRules(profile.Rules.filter((_, i) => i !== index))}
          />
        ))}

        {!readOnly && (
          <Group>
            <Button variant="default" onClick={() => setRules([...profile.Rules, newRule()])}>+ Правило</Button>
          </Group>
        )}

        <Group>
          {!readOnly && <Button loading={saving} onClick={save}>{t.common.save}</Button>}
          <Button variant="default" onClick={onDone}>{readOnly ? 'Закрыть' : t.common.cancel}</Button>
        </Group>
      </Stack>
    </Panel>
  );
}
