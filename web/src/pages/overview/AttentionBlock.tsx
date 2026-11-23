import { Link } from 'react-router';
import { Group, Loader } from '@mantine/core';
import { IconCircleCheck } from '@tabler/icons-react';
import type { AttentionItem } from '../../api/types';
import { useAuth } from '../../auth/AuthContext';
import { ResolveButton } from './ResolveButton';

/**
 * «Требует внимания» — плашки под шапкой: не показатели, а что сделать или починить; каждая ведёт туда, где это делается. Сбои
 * красные и выше дел. Ничего нет — одна плашка «Всё в порядке». Ошибки модуля Координатор может отметить решёнными — кнопка
 * вплотную справа от плашки (внутрь ссылки кнопку не вложить).
 */
export function AttentionBlock({ items }: { items: AttentionItem[] | undefined }) {
  const { can } = useAuth();
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
          return (
            <span key={item.text + item.link} className="ov-chip-pair">
              {chip}
              <ResolveButton resolveKey={item.resolveKey} title={item.text} variant="chip" level={item.level} />
            </span>
          );
        })
      )}
    </Group>
  );
}
