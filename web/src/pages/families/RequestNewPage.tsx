import { useEffect, useState } from 'react';
import { useNavigate, useSearchParams } from 'react-router';
import { Alert, Button, Card, Group, Select, SimpleGrid, Stack, Text, Textarea, TextInput, Title } from '@mantine/core';
import { useMutation } from '@tanstack/react-query';
import { apiFetch } from '../../api/client';
import { useApi } from '../../api/queries';
import { t } from '../../i18n/ru';
import { familiesKey, requestsKey, type FamilyFacets, type NewRequest, type RequestUrgency, type SimilarResult } from './api';
import { FamilyCardDrawer } from './FamilyCardDrawer';
import { FamilyTile } from './FamilyTile';
import { RequestStateBadge } from './RequestsPage';

const similarDelayMs = 400;

/**
 * Новая заявка. Пока человек пишет, под формой — похожие семейства каталога (может, нужное уже есть) и похожие открытые заявки
 * (можно присоединиться, а не подавать такую же). Текст поиска из панели Revit приходит в адресе: <c>?q=</c>.
 */
export function RequestNewPage() {
  const navigate = useNavigate();
  const [params] = useSearchParams();
  const [form, setForm] = useState<NewRequest>({ title: params.get('q') ?? '', text: '', section: '', urgency: 'Normal', project: '' });
  const [probe, setProbe] = useState(form.title);
  const [card, setCard] = useState<string | null>(null);
  const facets = useApi<FamilyFacets>([familiesKey, 'facets', ''], '/api/families/facets');

  useEffect(() => {
    const timer = setTimeout(() => setProbe(`${form.title} ${form.text}`.trim()), similarDelayMs);
    return () => clearTimeout(timer);
  }, [form.title, form.text]);
  const similar = useApi<SimilarResult>([requestsKey, 'similar', probe], probe.length >= 3 ? `/api/families/requests/similar?q=${encodeURIComponent(probe)}` : null);

  const create = useMutation({
    mutationFn: (body: NewRequest) => apiFetch<{ id: string }>('/api/families/requests', { method: 'POST', json: body }),
    onSuccess: (created) => navigate(`/families/requests/${created.id}`, { replace: true }),
  });

  const sections = (facets.data?.sections ?? []).map((s) => s.value);
  const found = similar.data;

  return (
    <Stack gap="md" maw={1100}>
      <Title order={3}>{t.requests.new}</Title>
      <Stack gap="sm" maw={720}>
        <TextInput label={t.requests.what} description={t.requests.whatHint} required value={form.title} maxLength={200}
          onChange={(e) => setForm({ ...form, title: e.currentTarget.value })} data-autofocus />
        <Textarea label={t.requests.details} description={t.requests.detailsHint} autosize minRows={3} maxLength={4000} value={form.text}
          onChange={(e) => setForm({ ...form, text: e.currentTarget.value })} />
        <Group grow>
          <Select label={t.families.section} data={sections} searchable clearable value={form.section || null}
            onChange={(value) => setForm({ ...form, section: value ?? '' })} />
          <TextInput label={t.requests.project} maxLength={200} value={form.project} onChange={(e) => setForm({ ...form, project: e.currentTarget.value })} />
          <Select label={t.requests.urgency} allowDeselect={false} value={form.urgency}
            data={[{ value: 'Normal', label: t.requests.normal }, { value: 'Urgent', label: t.requests.urgent }]}
            onChange={(value) => setForm({ ...form, urgency: (value ?? 'Normal') as RequestUrgency })} />
        </Group>
        <Text size="xs" c="dimmed">{t.requests.filesLater}</Text>
        {create.error && <Alert color="red">{create.error.message}</Alert>}
        <Group>
          <Button disabled={form.title.trim() === ''} loading={create.isPending} onClick={() => create.mutate(form)}>{t.requests.submit}</Button>
          <Button variant="default" onClick={() => navigate('/families/requests')}>{t.common.cancel}</Button>
        </Group>
      </Stack>

      {found && found.families.length > 0 && (
        <Stack gap="xs">
          <Title order={5}>{t.requests.similarFamilies}</Title>
          <SimpleGrid cols={{ base: 2, sm: 3, md: 5 }} spacing="sm">
            {found.families.map((hit) => <FamilyTile key={hit.id} hit={hit} onOpen={() => setCard(hit.id)} />)}
          </SimpleGrid>
        </Stack>
      )}
      {found && found.requests.length > 0 && (
        <Stack gap="xs">
          <Title order={5}>{t.requests.similarRequests}</Title>
          {found.requests.map((r) => (
            <Card key={r.id} withBorder padding="xs" style={{ cursor: 'pointer' }} onClick={() => navigate(`/families/requests/${r.id}`)}>
              <Group justify="space-between">
                <Text size="sm">№ {r.number} · {r.title}</Text>
                <Group gap={6}>
                  <Text size="xs" c="dimmed">{r.authorName}{r.members > 1 ? ` + ${r.members - 1}` : ''}</Text>
                  <RequestStateBadge state={r.state} />
                </Group>
              </Group>
            </Card>
          ))}
          <Text size="xs" c="dimmed">{t.requests.joinHint}</Text>
        </Stack>
      )}
      <FamilyCardDrawer cardId={card} unreviewed={false} onClose={() => setCard(null)} />
    </Stack>
  );
}
