/** Что браузер покажет сам: картинка, PDF или ничего (скачать). */
export type FileKind = 'image' | 'pdf' | 'other';

export const maxFileBytes = 25 * 1024 * 1024;

export function kindOf(contentType: string | undefined, name: string): FileKind {
  if (contentType?.startsWith('image/') && contentType !== 'image/svg+xml') return 'image';
  if (contentType === 'application/pdf') return 'pdf';
  if (!contentType || contentType === 'application/octet-stream') {
    if (/\.(png|jpe?g|gif|webp)$/i.test(name)) return 'image';
    if (/\.pdf$/i.test(name)) return 'pdf';
  }
  return 'other';
}

export const fileSize = (bytes: number) =>
  bytes >= 1024 * 1024 ? `${(bytes / 1024 / 1024).toFixed(1)} МБ` : `${Math.max(1, Math.round(bytes / 1024))} КБ`;
