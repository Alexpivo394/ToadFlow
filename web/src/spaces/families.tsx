import { lazy } from 'react';
import { Navigate, Route } from 'react-router';
import { IconBooks } from '@tabler/icons-react';
import { t } from '../i18n/ru';
import { RoleOnly, Titled } from './guards';
import type { Space } from './types';

// Каталог — в главном чанке: с него начинают проектировщики. Остальное грузится, когда открыли.
import { CatalogPage } from '../pages/families/CatalogPage';

const ReviewPage = lazy(() => import('../pages/families/ReviewPage').then((m) => ({ default: m.ReviewPage })));
const IssuesPage = lazy(() => import('../pages/families/IssuesPage').then((m) => ({ default: m.IssuesPage })));
const SettingsPage = lazy(() => import('../pages/families/SettingsPage').then((m) => ({ default: m.SettingsPage })));
const RequestsPage = lazy(() => import('../pages/families/RequestsPage').then((m) => ({ default: m.RequestsPage })));
const RequestNewPage = lazy(() => import('../pages/families/RequestNewPage').then((m) => ({ default: m.RequestNewPage })));
const RequestPage = lazy(() => import('../pages/families/RequestPage').then((m) => ({ default: m.RequestPage })));
const MissesPage = lazy(() => import('../pages/families/MissesPage').then((m) => ({ default: m.MissesPage })));
const AnalyticsPage = lazy(() => import('../pages/families/AnalyticsPage').then((m) => ({ default: m.AnalyticsPage })));

/** Библиотека семейств Revit: каталог и заявки для всех, проверка и спорные случаи — координатору, параметры — администратору. */
export const familiesSpace: Space = {
  id: 'families',
  module: 'families',
  label: t.nav.families,
  short: t.nav.shortFamilies,
  icon: IconBooks,
  pages: [
    { to: '/families/catalog', label: t.nav.familiesCatalog, designer: true, section: 'work' },
    { to: '/families/requests', label: t.nav.familiesRequests, designer: true, section: 'work' },
    { to: '/families/review', label: t.nav.familiesReview, role: 'Operator', section: 'work' },
    { to: '/families/issues', label: t.nav.familiesIssues, role: 'Operator', section: 'work' },
    { to: '/families/misses', label: t.insights.misses, role: 'Operator', section: 'work' },
    { to: '/families/analytics', label: t.insights.analytics, role: 'Operator', section: 'work' },
    { to: '/families/settings', label: t.nav.familiesSettings, section: 'setup' },
  ],
  routes: () => (
    <>
      <Route index element={<Navigate to="/families/catalog" replace />} />
      <Route path="catalog" element={<Titled title={t.nav.families}><CatalogPage /></Titled>} />
      <Route path="requests" element={<Titled title={t.requests.title}><RequestsPage /></Titled>} />
      <Route path="requests/new" element={<Titled title={t.requests.new}><RequestNewPage /></Titled>} />
      <Route path="requests/:id" element={<Titled title={t.requests.title}><RequestPage /></Titled>} />
      <Route path="review" element={<RoleOnly role="Operator"><Titled title={t.nav.familiesReview}><ReviewPage /></Titled></RoleOnly>} />
      <Route path="issues" element={<RoleOnly role="Operator"><Titled title={t.nav.familiesIssues}><IssuesPage /></Titled></RoleOnly>} />
      <Route path="misses" element={<RoleOnly role="Operator"><Titled title={t.insights.missesTitle}><MissesPage /></Titled></RoleOnly>} />
      <Route path="analytics" element={<RoleOnly role="Operator"><Titled title={t.insights.analyticsTitle}><AnalyticsPage /></Titled></RoleOnly>} />
      <Route path="settings" element={<RoleOnly role="Viewer"><Titled title={t.nav.familiesSettings}><SettingsPage /></Titled></RoleOnly>} />
    </>
  ),
};
