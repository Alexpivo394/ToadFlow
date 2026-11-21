import { useEffect, useMemo, useState } from 'react';
import { useNavigate, useSearchParams } from 'react-router';
import { Alert, Autocomplete, Badge, Button, Card, Checkbox, Grid, Group, Paper, Select, SimpleGrid, Stack, Text, Textarea, TextInput, Title } from '@mantine/core';
import { DateInput } from '@mantine/dates';
import { IconArrowRight, IconSend, IconUsers } from '@tabler/icons-react';
import { useMutation } from '@tanstack/react-query';
import { apiFetch } from '../../api/client';
import { useApi } from '../../api/queries';
import { useAuth } from '../../auth/AuthContext';
import { t } from '../../i18n/ru';
import { AttachmentGrid } from './attachments/AttachmentGrid';
import { DropArea } from './attachments/DropArea';
import { useUploads } from './attachments/useUploads';
import { familiesKey, requestsKey, type Direction, type FamilyFacets, type NewRequest, type SimilarResult } from './api';
import { FamilyCardDrawer } from './FamilyCardDrawer';
import { FamilyTile } from './FamilyTile';
import { RequestStateBadge } from './RequestsPage';

const similarDelayMs = 400;
const revitYears = ['2020', '2021', '2022', '2023', '2024', '2025', '2026', '2027'];

/** Направление по разделу библиотеки; иначе — по разделам профиля, если они дают ровно одно. Сервер решает так же. */
export function guessDirection(directions: Direction[], section: string, disciplines: string[]): string | null {
  const lower = (s: string) => s.toLowerCase();
  if (section) {
    const bySection = directions.find((d) => d.sections.some((s) => lower(s) === lower(section)));
    if (bySection) return bySection.id;
  }
  const byProfile = directions.filter((d) => d.disciplines.some((x) => disciplines.some((y) => lower(x) === lower(y))));
  return byProfile.length === 1 ? byProfile[0].id : null;
}

/**
 * Новая заявка. Слева — что нужно, вложения (вставить скриншот Ctrl+V, перетащить, выбрать), подробности с подсказкой направления,
 * проект, срок. Справа — что уже есть в каталоге и похожие открытые заявки. Контекст из панели Revit приходит в адресе: <c>q</c>,
 * <c>project</c>, <c>section</c>, <c>revit</c>; проблема в семействе каталога — <c>kind=fix&card=</c>.
 */
export function RequestNewPage() {
  const navigate = useNavigate();
  const auth = useAuth();
  const [params] = useSearchParams();
  const fix = params.get('kind') === 'fix' && params.get('card') !== null;
  const cardId = fix ? params.get('card') : null;
  const [form, setForm] = useState({
    title: params.get('q') ?? '', text: '', section: params.get('section') ?? '', project: params.get('project') ?? '',
    neededBy: null as string | null, blocksIssue: false, revitYear: params.get('revit') ?? '',
  });
  const [directionId, setDirectionId] = useState<string | null | undefined>(undefined);
  const [probe, setProbe] = useState(form.title);
  const [card, setCard] = useState<string | null>(null);
  const uploads = useUploads();

  const facets = useApi<FamilyFacets>([familiesKey, 'facets', ''], '/api/families/facets');
  const directions = useApi<Direction[]>([requestsKey, 'directions'], '/api/families/directions');
  const target = useApi<{ id: string; name: string; section: string }>([familiesKey, 'card', cardId], cardId ? `/api/families/${cardId}` : null);
  const projects = useApi<string[]>([requestsKey, 'projects', form.project], `/api/families/requests/projects?q=${encodeURIComponent(form.project)}`, { keepPrevious: true });

  useEffect(() => {
    const timer = setTimeout(() => setProbe(`${form.title} ${form.text}`.trim()), similarDelayMs);
    return () => clearTimeout(timer);
  }, [form.title, form.text]);
  const similar = useApi<SimilarResult>([requestsKey, 'similar', probe], !fix && probe.length >= 3 ? `/api/families/requests/similar?q=${encodeURIComponent(probe)}` : null);

  const list = useMemo(() => directions.data ?? [], [directions.data]);
  const section = form.section || target.data?.section || '';
  const guessed = useMemo(() => guessDirection(list, section, auth.disciplines), [list, section, auth.disciplines]);
  const chosen = directionId === undefined ? guessed : directionId;
  const direction = list.find((d) => d.id === chosen) ?? null;

  const create = useMutation({
    mutationFn: (body: NewRequest) => apiFetch<{ id: string }>('/api/families/requests', { method: 'POST', json: body }),
    onSuccess: (created) => navigate(`/families/requests/${created.id}`, { replace: true }),
  });
  const join = useMutation({
    mutationFn: (id: string) => apiFetch(`/api/families/requests/${id}/join`, { method: 'POST' }).then(() => id),
    onSuccess: (id) => navigate(`/families/requests/${id}`),
  });

  const submit = () => create.mutate({
    title: form.title.trim(), text: form.text.trim(), section: form.section, project: form.project.trim(), kind: fix ? 'Fix' : 'New',
    directionId: chosen ?? null, neededBy: form.neededBy, blocksIssue: form.blocksIssue, revitYear: form.revitYear ? Number(form.revitYear) : null,
    targetCardId: cardId, fileIds: uploads.ids,
  });

  const sections = (facets.data?.sections ?? []).map((s) => s.value);
  const found = similar.data;
  const recipients = direction ? (direction.coordinators.length > 0 ? direction.coordinators.map((c) => c.name).join(', ') : t.requests.willGoAll) : t.requests.willGoAll;

  return (
    <Stack gap="md" maw={1280}>
      <Stack gap={2}>
        <Title order={2}>{fix ? t.requests.newFix : t.requests.new}</Title>
        {fix && target.data && <Text c="dimmed">{t.requests.fixOf} «{target.data.name}»</Text>}
      </Stack>
      <Grid gap="xl">
        <Grid.Col span={{ base: 12, md: 7 }}>
          <Stack gap="md">
            <TextInput size="md" label={fix ? t.requests.whatFix : t.requests.what} description={fix ? t.requests.whatFixHint : t.requests.whatHint} required
              value={form.title} maxLength={200} data-autofocus onChange={(e) => setForm({ ...form, title: e.currentTarget.value })} />
            <DropArea onFiles={uploads.add} />
            <AttachmentGrid items={uploads.items} onRemove={uploads.remove} />
            <Textarea label={t.requests.details} placeholder={direction?.hint || t.requests.detailsPlaceholder} autosize minRows={4} maxLength={8000}
              value={form.text} onChange={(e) => setForm({ ...form, text: e.currentTarget.value })} />
            <SimpleGrid cols={{ base: 1, sm: 2 }}>
              {list.length > 0 && (
                <Select label={t.requests.direction} allowDeselect={false} value={chosen ?? ''}
                  data={[...list.map((d) => ({ value: d.id, label: d.name })), { value: '', label: t.requests.directionCommon }]}
                  onChange={(value) => setDirectionId(value ? value : null)} />
              )}
              {!fix && (
                <Select label={t.families.section} data={sections} searchable clearable value={form.section || null}
                  onChange={(value) => setForm({ ...form, section: value ?? '' })} />
              )}
              <Autocomplete label={t.requests.project} placeholder={t.requests.projectHint} data={projects.data ?? []} maxLength={200} value={form.project}
                onChange={(value) => setForm({ ...form, project: value })} />
              <Select label={t.requests.revit} data={revitYears} clearable value={form.revitYear || null}
                onChange={(value) => setForm({ ...form, revitYear: value ?? '' })} />
              <DateInput label={t.requests.neededBy} description={t.requests.neededByHint} clearable valueFormat="DD.MM.YYYY" minDate={new Date()}
                value={form.neededBy} onChange={(value) => setForm({ ...form, neededBy: value ? String(value).slice(0, 10) : null })} />
              <Checkbox mt={{ sm: 32 }} label={t.requests.blocksIssue} description={t.requests.blocksIssueHint} checked={form.blocksIssue}
                onChange={(e) => setForm({ ...form, blocksIssue: e.currentTarget.checked })} />
            </SimpleGrid>
            <Paper withBorder p="sm" radius="md" bg="var(--mantine-color-default-hover)">
              <Group gap="xs">
                <IconUsers size={18} />
                <Text size="sm">{t.requests.willGo}: <b>{direction ? `${direction.name} — ${recipients}` : `${t.requests.directionCommon} — ${recipients}`}</b></Text>
              </Group>
            </Paper>
            {create.error && <Alert color="red">{create.error.message}</Alert>}
            <Group>
              <Button size="md" leftSection={<IconSend size={18} />} disabled={form.title.trim() === '' || uploads.busy} loading={create.isPending} onClick={submit}>
                {t.requests.submit}
              </Button>
              <Button size="md" variant="default" onClick={() => navigate('/families/requests')}>{t.common.cancel}</Button>
              {uploads.busy && <Text size="sm" c="dimmed">{t.requests.uploading}</Text>}
            </Group>
          </Stack>
        </Grid.Col>

          <Grid.Col span={{ base: 12, md: 5 }} display={fix ? 'none' : undefined}>
            <Stack gap="sm" pos="sticky" top={16}>
              <Title order={4}>{t.requests.similarFamilies}</Title>
              {!found && <Text size="sm" c="dimmed">{t.requests.similarHint}</Text>}
              {found && found.families.length > 0 && (
                <SimpleGrid cols={{ base: 2, lg: 3 }} spacing="xs">
                  {found.families.map((hit) => <FamilyTile key={hit.id} hit={hit} onOpen={() => setCard(hit.id)} />)}
                </SimpleGrid>
              )}
              {found && found.requests.length > 0 && (
                <Stack gap="xs" mt="sm">
                  <Text fw={600}>{t.requests.similarRequests}</Text>
                  {found.requests.map((r) => (
                    <Card key={r.id} withBorder padding="sm" radius="md">
                      <Stack gap={6}>
                        <Group justify="space-between" wrap="nowrap">
                          <Text size="sm" fw={500} lineClamp={2}>№ {r.number} · {r.title}</Text>
                          <RequestStateBadge state={r.state} />
                        </Group>
                        <Group justify="space-between">
                          <Badge variant="light" color="gray" leftSection={<IconUsers size={12} />}>{r.members}</Badge>
                          <Group gap={6}>
                            <Button size="compact-sm" variant="subtle" rightSection={<IconArrowRight size={14} />} onClick={() => navigate(`/families/requests/${r.id}`)}>
                              {t.common.open}
                            </Button>
                            {!r.isMember && (
                              <Button size="compact-sm" loading={join.isPending && join.variables === r.id} onClick={() => join.mutate(r.id)}>{t.requests.join}</Button>
                            )}
                          </Group>
                        </Group>
                      </Stack>
                    </Card>
                  ))}
                  <Text size="xs" c="dimmed">{t.requests.joinHint}</Text>
                </Stack>
              )}
            </Stack>
          </Grid.Col>
      </Grid>
      <FamilyCardDrawer cardId={card} unreviewed={false} onClose={() => setCard(null)} />
    </Stack>
  );
}
