import { PageTitle } from '../../../components/ui';
import { t } from '../../../i18n/ru';

/** «Мои коллизии» — коллизии разделов профиля по всем проверкам. */
export function MyPage() {
  return <PageTitle>{t.clash.myTitle}</PageTitle>;
}
