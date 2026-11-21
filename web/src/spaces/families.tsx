import { Navigate, Route } from 'react-router';
import { IconBooks } from '@tabler/icons-react';
import { t } from '../i18n/ru';
import { Titled } from './guards';
import type { Space } from './types';

// Каталог — в главном чанке: с него начинают проектировщики.
import { CatalogPage } from '../pages/families/CatalogPage';

/** Библиотека семейств Revit: каталог для всех. */
export const familiesSpace: Space = {
  id: 'families',
  module: 'families',
  label: t.nav.families,
  short: t.nav.shortFamilies,
  icon: IconBooks,
  pages: [{ to: '/families/catalog', label: t.nav.familiesCatalog, designer: true, section: 'work' }],
  routes: () => (
    <>
      <Route index element={<Navigate to="/families/catalog" replace />} />
      <Route path="catalog" element={<Titled title={t.nav.families}><CatalogPage /></Titled>} />
    </>
  ),
};
