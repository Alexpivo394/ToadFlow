import { Badge, Card, Center, Group, Image, Loader, Stack, Text } from '@mantine/core';
import { IconBox } from '@tabler/icons-react';
import { useImage } from '../../api/useImage';
import { YearBadge } from '../../components/ui';
import { t } from '../../i18n/ru';
import { previewPath, typesLabel, type FamilyHit } from './api';

/** Картинка семейства или заглушка, пока ферма её не сняла. */
export function FamilyPreview({ cardId, hash, unreviewed, size }: { cardId: string; hash: string | null; unreviewed: boolean; size: number }) {
  const image = useImage(previewPath(cardId, hash, unreviewed));
  if (image.url) return <Image src={image.url} alt="" h={size} w={size} fit="contain" />;
  return (
    <Center h={size} w={size} bg="var(--mantine-color-default-hover)" style={{ borderRadius: 'var(--mantine-radius-sm)' }} aria-label={t.families.noPreview}
      title={t.families.noPreview}>
      {image.isLoading ? <Loader size="sm" /> : <IconBox size={size / 3} opacity={0.4} />}
    </Center>
  );
}

/** Плитка выдачи каталога: картинка, имя, категория · раздел · типоразмеры, версия Revit, «на проверке». */
export function FamilyTile({ hit, onOpen }: { hit: FamilyHit; onOpen: () => void }) {
  const unreviewed = hit.state === 'InReview';
  const details = [hit.category, hit.section, typesLabel(hit.typeCount)].filter((part) => part.length > 0).join(' · ');
  return (
    <Card withBorder padding="sm" radius="md" component="button" type="button" onClick={onOpen} aria-label={hit.name}
      style={{ cursor: 'pointer', textAlign: 'left' }}>
      <Stack gap={6}>
        <Center>
          <FamilyPreview cardId={hit.id} hash={hit.previewHash} unreviewed={unreviewed} size={140} />
        </Center>
        <Text fw={600} size="sm" lineClamp={2} title={hit.name}>{hit.name}</Text>
        <Text size="xs" c="dimmed" lineClamp={2}>{details}</Text>
        <Group gap={4}>
          <YearBadge year={hit.revitYear} />
          {unreviewed && <Badge size="sm" color="orange" variant="light">{t.families.inReview}</Badge>}
          {hit.state === 'Archived' && <Badge size="sm" color="gray" variant="light">{t.families.archived}</Badge>}
          {hit.coordinatorsOnly && <Badge size="sm" color="orange" variant="light" title={t.families.iterations.locked}>🔒</Badge>}
          {hit.iterationCount > 0 && <Badge size="sm" color="blue" variant="light">{t.families.iterations.tab}: {hit.iterationCount}</Badge>}
        </Group>
      </Stack>
    </Card>
  );
}
