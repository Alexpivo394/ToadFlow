/**
 * Копирует текст в буфер обмена. navigator.clipboard есть только в защищённом контексте (HTTPS или localhost), а ToadFlow
 * обычно открывают по http://сервер:8080 — тогда текст копируется через скрытое поле и execCommand('copy').
 * Поле кладётся в `container` (по умолчанию — body): внутри модального окна фокус не должен уходить за его пределы.
 */
export async function copyText(text: string, container: HTMLElement = document.body): Promise<boolean> {
  if (window.isSecureContext && navigator.clipboard) {
    try {
      await navigator.clipboard.writeText(text);
      return true;
    } catch {
      // нет разрешения — пробуем старым способом
    }
  }

  const field = document.createElement('textarea');
  field.value = text;
  field.setAttribute('readonly', '');
  field.style.position = 'fixed';
  field.style.top = '0';
  field.style.left = '0';
  field.style.opacity = '0';
  container.appendChild(field);
  try {
    field.focus();
    field.select();
    return document.execCommand('copy');
  } catch {
    return false;
  } finally {
    field.remove();
  }
}
