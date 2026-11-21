import { useEffect, useMemo, useState } from 'react';
import { useNavigate } from 'react-router';
import { ActionIcon, Group, Kbd, Modal, ScrollArea, Stack, Text, TextInput, Tooltip, UnstyledButton } from '@mantine/core';
import { IconSearch } from '@tabler/icons-react';
import { useApi } from '../api/queries';
import { useAuth } from '../auth/AuthContext';
import { t } from '../i18n/ru';
import { useSpaces, visiblePages } from '../spaces/useSpaces';

interface SearchHit {
  kind: string;
  title: string;
  subtitle: string | null;
  link: string;
}

const openEvent = 'toadflow:search';

/** Открыть быстрый поиск из любого места (кнопка в полосе пространств). */
export const openSearch = () => window.dispatchEvent(new Event(openEvent));

/** Кнопка поиска для полосы пространств и мобильной шапки. */
export function SearchButton() {
  return (
    <Tooltip label={t.today.searchHint} position="right">
      <ActionIcon variant="subtle" size="lg" aria-label={t.today.search} onClick={openSearch}>
        <IconSearch size={20} stroke={1.6} />
      </ActionIcon>
    </Tooltip>
  );
}

/**
 * Быстрый поиск (Ctrl+K): страницы сервиса — сразу, без сервера; семейства, заявки (по номеру), расписания, проверки и пользователи —
 * с сервера, каждому по его правам. Стрелки выбирают, Enter открывает.
 */
export function CommandPalette() {
  const [opened, setOpened] = useState(false);
  const [q, setQ] = useState('');
  const [probe, setProbe] = useState('');
  const [active, setActive] = useState(0);
  const navigate = useNavigate();
  const auth = useAuth();
  const { spaces } = useSpaces();

  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === 'k') {
        e.preventDefault();
        setOpened(true);
      }
    };
    const onOpen = () => setOpened(true);
    document.addEventListener('keydown', onKey);
    window.addEventListener(openEvent, onOpen);
    return () => {
      document.removeEventListener('keydown', onKey);
      window.removeEventListener(openEvent, onOpen);
    };
  }, []);
  useEffect(() => {
    const timer = setTimeout(() => setProbe(q.trim()), 200);
    return () => clearTimeout(timer);
  }, [q]);

  const server = useApi<SearchHit[]>(['search', probe], opened && probe.length >= 1 ? `/api/search?q=${encodeURIComponent(probe)}` : null, { keepPrevious: true });
  const pages = useMemo(() => {
    const word = q.trim().toLowerCase();
    if (word.length < 2) return [];
    return spaces.flatMap((space) => visiblePages(space, auth).map((page) => ({
      kind: 'page', title: `${space.label} · ${page.label}`, subtitle: t.today.pages, link: page.to,
    }))).filter((p) => p.title.toLowerCase().includes(word)).slice(0, 5);
  }, [q, spaces, auth]);
  const hits: SearchHit[] = [...pages, ...(probe === q.trim() ? (server.data ?? []) : [])];

  const close = () => {
    setOpened(false);
    setQ('');
    setActive(0);
  };
  const go = (hit: SearchHit | undefined) => {
    if (!hit) return;
    close();
    navigate(hit.link);
  };
  const onKeyDown = (e: React.KeyboardEvent) => {
    if (e.key === 'ArrowDown') { e.preventDefault(); setActive((i) => Math.min(i + 1, hits.length - 1)); }
    if (e.key === 'ArrowUp') { e.preventDefault(); setActive((i) => Math.max(i - 1, 0)); }
    if (e.key === 'Enter') { e.preventDefault(); go(hits[Math.min(active, hits.length - 1)]); }
  };

  let lastKind = '';
  return (
    <Modal opened={opened} onClose={close} withCloseButton={false} size="lg" padding={0} yOffset="10vh">
      <TextInput size="lg" variant="unstyled" px="md" py={4} leftSection={<IconSearch size={18} />} placeholder={t.today.searchPlaceholder} value={q}
        onChange={(e) => { setQ(e.currentTarget.value); setActive(0); }} onKeyDown={onKeyDown} data-autofocus
        rightSection={<Kbd size="xs">Esc</Kbd>} rightSectionWidth={48} />
      {q.trim().length > 0 && (
        <ScrollArea.Autosize mah={420} style={{ borderTop: '1px solid var(--mantine-color-default-border)' }}>
          <Stack gap={0} p={6}>
            {hits.length === 0 && probe === q.trim() && !server.isFetching && <Text size="sm" c="dimmed" p="sm">{t.today.nothingFound}</Text>}
            {hits.map((hit, index) => {
              const header = hit.kind !== lastKind ? (hit.kind === 'page' ? t.today.pages : t.today.kinds[hit.kind] ?? hit.kind) : null;
              lastKind = hit.kind;
              return (
                <div key={`${hit.kind}:${hit.link}`}>
                  {header && <Text size="xs" c="dimmed" fw={600} tt="uppercase" px="sm" pt="xs">{header}</Text>}
                  <UnstyledButton w="100%" px="sm" py={8} onClick={() => go(hit)} onMouseEnter={() => setActive(index)}
                    style={{ borderRadius: 6, background: index === active ? 'var(--mantine-primary-color-light)' : undefined }}>
                    <Group justify="space-between" wrap="nowrap">
                      <Text size="sm" fw={500} truncate>{hit.title}</Text>
                      {hit.subtitle && hit.kind !== 'page' && <Text size="xs" c="dimmed" truncate>{hit.subtitle}</Text>}
                    </Group>
                  </UnstyledButton>
                </div>
              );
            })}
          </Stack>
        </ScrollArea.Autosize>
      )}
    </Modal>
  );
}
