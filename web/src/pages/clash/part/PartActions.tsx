import { useState } from 'react';
import { Button, Group, Menu, Modal, Stack, Text, TextInput } from '@mantine/core';
import { modals } from '@mantine/modals';
import { notifications } from '@mantine/notifications';
import { useQueryClient } from '@tanstack/react-query';
import { apiFetch } from '../../../api/client';
import type { ClashPartView } from '../../../api/types';

/** Название, проект и часть здания: правка в окне, пустое название не сохраняется. */
function RenameDialog({ part, onClose }: { part: ClashPartView; onClose: () => void }) {
  const queryClient = useQueryClient();
  const [name, setName] = useState(part.name);
  const [object, setObject] = useState(part.object ?? '');
  const [building, setBuilding] = useState(part.part ?? '');
  const [saving, setSaving] = useState(false);

  async function save() {
    setSaving(true);
    try {
      await apiFetch(`/api/clash/parts/${part.id}`, { method: 'PUT', json: { name, object, part: building } });
      notifications.show({ color: 'green', message: 'Часть сохранена' });
      await queryClient.invalidateQueries({ queryKey: ['clash-parts'] });
      onClose();
    } catch (error) {
      notifications.show({ color: 'red', title: 'Не получилось', message: (error as Error).message });
    } finally {
      setSaving(false);
    }
  }

  return (
    <Modal opened onClose={onClose} title="Название и проект части">
      <Stack gap="sm">
        <TextInput label="Название" required data-autofocus value={name} onChange={(e) => setName(e.currentTarget.value)} />
        <TextInput label="Проект" placeholder="Без проекта" value={object} onChange={(e) => setObject(e.currentTarget.value)} />
        <TextInput label="Часть здания" placeholder="Не задана" value={building} onChange={(e) => setBuilding(e.currentTarget.value)} />
        <Group justify="flex-end">
          <Button variant="default" onClick={onClose}>Отмена</Button>
          <Button disabled={!name.trim()} loading={saving} onClick={save}>Сохранить</Button>
        </Group>
      </Stack>
    </Modal>
  );
}

/**
 * Меню части в шапке: переименовать и удалить. Удаление необратимо — с частью уходят черновик, версии, прогоны, коллизии и их
 * история; модели и выгрузки остаются.
 */
export function PartActions({ part, approved, onDeleted }: { part: ClashPartView; approved: boolean; onDeleted: () => void }) {
  const queryClient = useQueryClient();
  const [renaming, setRenaming] = useState(false);

  function askDelete() {
    modals.openConfirmModal({
      title: `Удалить часть «${part.name}»?`,
      children: (
        <Text size="sm">
          {approved
            ? 'Вместе с частью удалятся черновик, утверждённые версии, все прогоны, коллизии с их статусами, примечаниями и историей. Часть уйдёт из расписаний. '
            : 'Вместе с частью удалятся черновик и его прогоны. '}
          Модели и выгрузки останутся. Отменить удаление нельзя.
        </Text>
      ),
      labels: { confirm: 'Удалить', cancel: 'Отмена' },
      confirmProps: { color: 'red' },
      onConfirm: async () => {
        try {
          await apiFetch(`/api/clash/parts/${part.id}`, { method: 'DELETE' });
          notifications.show({ color: 'green', message: `Часть «${part.name}» удалена` });
          onDeleted();
          await queryClient.invalidateQueries({ queryKey: ['clash-parts'] });
        } catch (error) {
          notifications.show({ color: 'red', title: 'Не получилось', message: (error as Error).message });
        }
      },
    });
  }

  return (
    <>
      <Menu>
        <Menu.Target><Button size="xs" variant="default">Часть ▾</Button></Menu.Target>
        <Menu.Dropdown>
          <Menu.Item onClick={() => setRenaming(true)}>Переименовать…</Menu.Item>
          <Menu.Divider />
          <Menu.Item color="red" onClick={askDelete}>Удалить часть…</Menu.Item>
        </Menu.Dropdown>
      </Menu>
      {renaming && <RenameDialog part={part} onClose={() => setRenaming(false)} />}
    </>
  );
}
