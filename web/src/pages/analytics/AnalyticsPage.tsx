import { Link, Route, Routes, useLocation, useNavigate } from 'react-router';
import { Alert, Anchor, Tabs } from '@mantine/core';
import { useApi } from '../../api/queries';
import type { AnalyticsSettings } from '../../api/types';
import { useAuth } from '../../auth/AuthContext';
import { PageTitle } from '../../components/ui';
import { t } from '../../i18n/ru';
import { ModelPage } from './ModelPage';
import { ModelsTab, ParametersTab } from './ModelsTab';
import { OverviewTab } from './OverviewTab';
import { AnalyticsFiltersBar } from './parts';

export function AnalyticsPage() {
  const location = useLocation();
  const navigate = useNavigate();
  const { can } = useAuth();
  const settings = useApi<AnalyticsSettings>(['loi-analytics', 'settings'], '/api/loi/analytics/settings');
  const tab = location.pathname.startsWith('/analytics/models') ? 'models' : location.pathname.startsWith('/analytics/parameters') ? 'parameters' : 'overview';
  const onModel = location.pathname.startsWith('/analytics/model') && !location.pathname.startsWith('/analytics/models');

  return (
    <>
      <PageTitle>{t.nav.analytics}</PageTitle>
      {settings.data && !settings.data.enabled && (
        <Alert color="yellow" mb="md">
          Аналитика выключена — новые проверки в неё не попадают.{' '}
          {can('Admin') && <Anchor component={Link} to="/settings/analytics">Включить в настройках</Anchor>}
        </Alert>
      )}
      {!onModel && (
        <>
          <Tabs value={tab} onChange={(v) => navigate({ pathname: v === 'overview' ? '/analytics' : `/analytics/${v}`, search: location.search })} mb="md">
            <Tabs.List>
              <Tabs.Tab value="overview">Обзор</Tabs.Tab>
              <Tabs.Tab value="models">Модели</Tabs.Tab>
              <Tabs.Tab value="parameters">Параметры</Tabs.Tab>
            </Tabs.List>
          </Tabs>
          <AnalyticsFiltersBar />
        </>
      )}
      <Routes>
        <Route index element={<OverviewTab />} />
        <Route path="models" element={<ModelsTab />} />
        <Route path="parameters" element={<ParametersTab />} />
        <Route path="model" element={<ModelPage />} />
      </Routes>
    </>
  );
}
