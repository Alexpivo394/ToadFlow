import { PageTitle } from '../../../components/ui';
import { t } from '../../../i18n/ru';

/** «Коллизии → Сводка»: аналитика по разделам, проектам и проверкам. */
export function SummaryPage() {
  return <PageTitle>{t.nav.clashSummary}</PageTitle>;
}
