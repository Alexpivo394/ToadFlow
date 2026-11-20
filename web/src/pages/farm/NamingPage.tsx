import { useAuth } from '../../auth/AuthContext';
import { NamingEditor } from './naming/NamingEditor';

/**
 * Справочник именования моделей — общий для модулей: по нему LOI раскладывает аналитику, коллизии — разделы моделей, а
 * пользователи выбирают свои разделы. Правит администратор.
 */
export function NamingPage() {
  const { can } = useAuth();
  return <NamingEditor readOnly={!can('Admin')} />;
}
