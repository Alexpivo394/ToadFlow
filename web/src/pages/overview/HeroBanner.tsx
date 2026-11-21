import { useEffect, useState } from 'react';
import type { AttentionItem, OverviewPulse, OverviewTile } from '../../api/types';
import { daySummary, greeting, headline, noErrorsShare } from './greeting';

/** Часы шапки: обновляются раз в 10 с — минуты не отстают больше чем на 10 с. */
function useNow(): Date {
  const [now, setNow] = useState(() => new Date());
  useEffect(() => {
    const timer = setInterval(() => setNow(new Date()), 10_000);
    return () => clearInterval(timer);
  }, []);
  return now;
}

/**
 * Шапка «Обзора»: приветствие и как дела у системы целиком, одна фраза о сутках из фраз модулей, четыре числа и часы.
 * Есть сбои — шапка красная.
 */
export function HeroBanner({ pulse, attention, tiles, userName }: { pulse: OverviewPulse; attention: AttentionItem[]; tiles: OverviewTile[]; userName: string }) {
  const now = useNow();
  const problems = attention.filter((a) => a.level === 'Problem').length;
  const todos = attention.length - problems;
  const highlights = tiles.map((tile) => tile.highlight).filter((h): h is string => !!h);
  const time = now.toLocaleTimeString('ru-RU', { hour: '2-digit', minute: '2-digit' });
  const date = now.toLocaleDateString('ru-RU', { weekday: 'long', day: 'numeric', month: 'long' });

  return (
    <section className="ov-hero" aria-label="Сводка" data-tone={problems > 0 ? 'problem' : 'calm'}>
      <svg className="ov-hero-frog" width="150" height="150" viewBox="0 0 100 100" aria-hidden>
        <circle cx="30" cy="30" r="16" fill="#fff" />
        <circle cx="70" cy="30" r="16" fill="#fff" />
        <ellipse cx="50" cy="62" rx="44" ry="30" fill="#fff" />
      </svg>
      <div className="ov-hero-title">{`${greeting(now.getHours())}, ${userName}. ${headline(problems, todos)}`}</div>
      <div className="ov-hero-sub">{daySummary(highlights)}</div>
      <div className="ov-hero-kpis">
        <Kpi value={`${pulse.revitBusy} / ${pulse.revitTotal}`} label="Revit занято сейчас" />
        <Kpi value={String(pulse.doneDay + pulse.failedDay)} label="заданий за сутки" />
        <Kpi value={noErrorsShare(pulse.doneDay, pulse.failedDay)} label="без ошибок" />
        <Kpi value={String(pulse.queued)} label="в очереди" />
      </div>
      <div className="ov-hero-clock">
        <b>{time}</b>
        {date}
      </div>
    </section>
  );
}

function Kpi({ value, label }: { value: string; label: string }) {
  return (
    <div className="ov-hero-kpi">
      <b>{value}</b>
      <span>{label}</span>
    </div>
  );
}
