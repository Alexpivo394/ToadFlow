import { useCallback, useEffect, useState } from 'react';
import { notifications } from '@mantine/notifications';
import { useQueryClient } from '@tanstack/react-query';
import { ApiError, apiFetch } from '../../../api/client';
import { useApi } from '../../../api/queries';
import type { DraftView, MergeConflict } from '../../../api/types';
import type { ClashConfig } from '../../../lib/clashConfig';
import { mergeDraft, takeTheirs } from '../../../lib/draftMerge';

interface State {
  /** Версия с сервера, от которой идут мои правки. */
  base: ClashConfig;
  /** Мои правки поверх неё. */
  local: ClashConfig;
  version: number;
}

export interface Conflicts {
  items: MergeConflict[];
  current: DraftView;
}

export interface Draft {
  view: DraftView | undefined;
  local: ClashConfig | null;
  dirty: boolean;
  problems: string[];
  saving: boolean;
  conflicts: Conflicts | null;
  edit: (change: (config: ClashConfig) => ClashConfig) => void;
  save: () => Promise<void>;
  reset: () => void;
  /** Закрыть диалог конфликтов, ничего не сохраняя: мои правки остаются. */
  dismiss: () => void;
  /** Решение конфликтов: ключи, где берётся чужое; остальное — моё. */
  resolve: (takeTheirsKeys: string[]) => Promise<void>;
}

const same = (a: ClashConfig, b: ClashConfig) => JSON.stringify(a) === JSON.stringify(b);

/**
 * Черновик части с моими несохранёнными правками. Новая версия с сервера (чужое сохранение, живое обновление) вливается
 * под мои правки; сохранение отправляет базовую версию, и сервер сливает правки двоих. Конфликт — диалог выбора.
 */
export function useDraft(partId: string): Draft {
  const queryClient = useQueryClient();
  const draft = useApi<DraftView>(['clash-parts', partId, 'draft'], `/api/clash/parts/${partId}/draft`);
  const [state, setState] = useState<State | null>(null);
  const [problems, setProblems] = useState<string[]>([]);
  const [saving, setSaving] = useState(false);
  const [conflicts, setConflicts] = useState<Conflicts | null>(null);

  useEffect(() => {
    const view = draft.data;
    if (!view) return;
    setProblems(view.problems);
    setState((s) => {
      if (!s) return { base: view.configuration, local: view.configuration, version: view.version };
      if (s.version === view.version && same(s.base, view.configuration)) return s;
      return { base: view.configuration, local: mergeDraft(s.base, view.configuration, s.local), version: view.version };
    });
  }, [draft.data]);

  const apply = useCallback(
    (view: DraftView) => {
      setState({ base: view.configuration, local: view.configuration, version: view.version });
      setProblems(view.problems);
      queryClient.setQueryData(['clash-parts', partId, 'draft'], view);
    },
    [partId, queryClient],
  );

  const send = useCallback(
    async (target: State) => {
      setSaving(true);
      try {
        const saved = await apiFetch<DraftView>(`/api/clash/parts/${partId}/draft`, {
          method: 'PUT',
          json: { baseVersion: target.version, configuration: target.local },
        });
        apply(saved);
        setConflicts(null);
        notifications.show({ color: 'green', message: 'Черновик сохранён' });
        await queryClient.invalidateQueries({ queryKey: ['clash-parts', 'list'] });
      } catch (error) {
        if (error instanceof ApiError && error.status === 409) {
          const body = error.body as { conflicts: MergeConflict[]; current: DraftView };
          setConflicts({ items: body.conflicts, current: body.current });
        } else if (error instanceof ApiError && error.status === 400 && Array.isArray((error.body as { problems?: string[] })?.problems)) {
          setProblems((error.body as { problems: string[] }).problems);
        } else {
          notifications.show({ color: 'red', title: 'Не сохранилось', message: (error as Error).message });
        }
      } finally {
        setSaving(false);
      }
    },
    [apply, partId, queryClient],
  );

  return {
    view: draft.data,
    local: state?.local ?? null,
    dirty: !!state && !same(state.base, state.local),
    problems,
    saving,
    conflicts,
    edit: (change) => setState((s) => (s ? { ...s, local: change(s.local) } : s)),
    save: async () => {
      if (state) await send(state);
    },
    reset: () => setState((s) => (s ? { ...s, local: s.base } : s)),
    dismiss: () => setConflicts(null),
    resolve: async (theirs) => {
      if (!state || !conflicts) return;
      const current = conflicts.current;
      // Моё поверх нынешней версии, кроме выбранного «взять чужое»; база — нынешняя, поэтому сервер примет без слияния.
      const local = takeTheirs(mergeDraft(state.base, current.configuration, state.local), current.configuration, theirs);
      const next = { base: current.configuration, local, version: current.version };
      setState(next);
      setConflicts(null);
      await send(next);
    },
  };
}
