import { Link } from 'react-router';
import { Group, Loader, Tooltip, UnstyledButton } from '@mantine/core';
import { IconCheck, IconCircleCheck } from '@tabler/icons-react';
import { useApiMutation } from '../../api/queries';
import type { AttentionItem } from '../../api/types';
import { useAuth } from '../../auth/AuthContext';
import { t } from '../../i18n/ru';

/**
 * «Требует внимания» — плашки под шапкой: не показатели, а что сделать или починить; каждая ведёт туда, где это делается. Сбои
 * красные и выше дел. Ничего нет — одна плашка «Всё в порядке». Ошибки модуля Координатор может отметить решёнными — кнопка
 * вплотную справа от плашки (внутрь ссылки кнопку не вложить).
 */
export function AttentionBlock({ items }: { items: AttentionItem[] | undefined }) {
  const { can } = useAuth();
  const resolve = useApiMutation<string>('POST', (key) => `/api/overview/attention/${encodeURIComponent(key)}/resolve`, {
    invalidate: ['overview'],
    success: t.attention.resolved,
  });
  const sorted = [...(items ?? [])].sort((a, b) => (a.level === b.level ? 0 : a.level === 'Problem' ? -1 : 1));

  return (
    <Group component="section" aria-label="Требует внимания" gap={10} wrap="wrap">
      {items === undefined ? (
        <Loader size="sm" />
      ) : sorted.length === 0 ? (
        <span className="ov-chip" data-level="ok">
          <IconCircleCheck size={18} />
          Всё в порядке
        </span>
      ) : (
        sorted.map((item) => {
          const chip = (
            <Link key={item.text + item.link} to={item.link} className="ov-chip" data-level={item.level}>
              <span className="ov-dot" data-tone={item.level === 'Problem' ? 'bad' : 'todo'} />
              {item.text}
            </Link>
          );
          if (!item.resolveKey || !can('Operator')) return chip;
          const key = item.resolveKey;
          return (
            <span key={item.text + item.link} className="ov-chip-pair">
              {chip}
              <Tooltip label={t.attention.resolveHint} withArrow multiline w={260}>
                <UnstyledButton className="ov-chip-resolve" data-level={item.level} disabled={resolve.isPending} onClick={() => resolve.mutate(key)}>
                  <IconCheck size={16} />
                  {t.attention.resolve}
                </UnstyledButton>
              </Tooltip>
            </span>
          );
        })
      )}
    </Group>
  );
}
