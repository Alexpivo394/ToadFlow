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

/** Библиотека семейств Revit: каталог для всех, проверка и спорные случаи — координатору, параметры — администратору. */
export const familiesSpace: Space = {
  id: 'families',
  module: 'families',
  label: t.nav.families,
  short: t.nav.shortFamilies,
  icon: IconBooks,
  pages: [
    { to: '/families/catalog', label: t.nav.familiesCatalog, designer: true, section: 'work' },
    { to: '/families/review', label: t.nav.familiesReview, role: 'Operator', section: 'work' },
    { to: '/families/issues', label: t.nav.familiesIssues, role: 'Operator', section: 'work' },
    { to: '/families/settings', label: t.nav.familiesSettings, section: 'setup' },
  ],
  routes: () => (
    <>
      <Route index element={<Navigate to="/families/catalog" replace />} />
      <Route path="catalog" element={<Titled title={t.nav.families}><CatalogPage /></Titled>} />
      <Route path="review" element={<RoleOnly role="Operator"><Titled title={t.nav.familiesReview}><ReviewPage /></Titled></RoleOnly>} />
      <Route path="issues" element={<RoleOnly role="Operator"><Titled title={t.nav.familiesIssues}><IssuesPage /></Titled></RoleOnly>} />
      <Route path="settings" element={<RoleOnly role="Viewer"><Titled title={t.nav.familiesSettings}><SettingsPage /></Titled></RoleOnly>} />
    </>
  ),
};
