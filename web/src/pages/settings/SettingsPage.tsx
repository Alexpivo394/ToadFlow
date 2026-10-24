import { Navigate, useParams } from 'react-router';
import { useAuth } from '../../auth/AuthContext';
import { PageTitle } from '../../components/ui';
import { t } from '../../i18n/ru';
import { AgentsSettings } from './AgentsSettings';
import { GeneralSettingsForm } from './GeneralSettingsForm';
import { LoiSettings } from './loi/LoiSettings';
import { PresetsSettings } from './PresetsSettings';
import { SourcesSettings } from './SourcesSettings';
import { UsersSettings } from './UsersSettings';

export function SettingsPage() {
  const { section } = useParams();
  const { can } = useAuth();

  const content = {
    agents: <AgentsSettings />,
    sources: <SourcesSettings />,
    presets: <PresetsSettings />,
    loi: <LoiSettings />,
    users: can('Admin') ? <UsersSettings /> : null,
    general: <GeneralSettingsForm />,
  }[section ?? ''];

  if (!content) return <Navigate to="/settings/agents" replace />;

  return (
    <>
      <PageTitle>{t.nav.settings}</PageTitle>
      {content}
    </>
  );
}
