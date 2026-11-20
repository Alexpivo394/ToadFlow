import { Center, Loader } from '@mantine/core';
import { Navigate, Route, useLocation, useParams } from 'react-router';
import { useApi } from '../api/queries';
import type { ScheduleView } from '../api/types';
import { useAuth } from '../auth/AuthContext';
import { moduleInfo, type ModuleName } from '../spaces/modules';
import { useSpaces, visiblePages } from '../spaces/useSpaces';

/** Переход на новый адрес с той же строкой запроса и якорем: закладка с фильтрами открывает тот же вид. */
function Moved({ to }: { to: string }) {
  const { search, hash } = useLocation();
  return <Navigate to={{ pathname: to, search, hash }} replace />;
}

/** Хвост старого адреса после префикса: /analytics/models → /models. */
function MovedTail({ from, to }: { from: string; to: string }) {
  const { pathname } = useLocation();
  return <Moved to={to + pathname.slice(from.length)} />;
}

/** /schedules — первая доступная страница расписаний: выгрузки, LOI, коллизий. */
function OldSchedules() {
  const auth = useAuth();
  const { spaces } = useSpaces();
  const pages = spaces.flatMap((space) => visiblePages(space, auth).map((page) => page.to));
  const target = (['export', 'loi', 'clash'] as ModuleName[]).map((m) => moduleInfo[m].schedules).find((to) => pages.includes(to));
  return <Moved to={target ?? '/'} />;
}

/** /schedules/:id — в расписания модуля этого расписания; не найдено (или модуль выключен) — на главную. */
function OldSchedule() {
  const { id } = useParams();
  const schedule = useApi<ScheduleView>(['schedules', 'one', id], `/api/schedules/${id}`);
  if (schedule.isLoading) return <Center py="xl"><Loader /></Center>;
  const module = schedule.data?.module as ModuleName | undefined;
  return <Moved to={module && module in moduleInfo ? `${moduleInfo[module].schedules}/${id}` : '/'} />;
}

/**
 * Прежние адреса (до пространств) — в закладках и пересланных ссылках. Ведут на новые места; если там модуль выключен, адрес
 * не найдётся и приложение уведёт на главную. Адреса /clash/... не менялись — они стоят в отчётах HTML и письмах.
 */
export function oldRoutes() {
  return (
    <>
      <Route path="schedules" element={<OldSchedules />} />
      <Route path="schedules/:id" element={<OldSchedule />} />
      <Route path="history" element={<Moved to="/farm/queue" />} />
      <Route path="history/models" element={<Moved to="/farm/queue/models" />} />
      <Route path="history/events" element={<Moved to="/admin/events" />} />
      <Route path="history/*" element={<MovedTail from="/history" to="/farm/queue" />} />
      <Route path="analytics/*" element={<MovedTail from="/analytics" to="/loi/analytics" />} />
      <Route path="settings" element={<Moved to="/farm/agents" />} />
      <Route path="settings/agents" element={<Moved to="/farm/agents" />} />
      <Route path="settings/sources" element={<Moved to="/farm/sources" />} />
      <Route path="settings/presets" element={<Moved to="/export/presets" />} />
      <Route path="settings/loi" element={<Moved to="/loi/profiles" />} />
      <Route path="settings/analytics" element={<Moved to="/loi/analytics-settings" />} />
      <Route path="settings/clash" element={<Moved to="/clash/setup/settings" />} />
      <Route path="settings/users" element={<Moved to="/admin/users" />} />
      <Route path="settings/general" element={<Moved to="/admin/server" />} />
    </>
  );
}
