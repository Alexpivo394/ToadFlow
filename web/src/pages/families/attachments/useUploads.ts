import { useCallback, useEffect, useRef, useState } from 'react';
import { apiFetch } from '../../../api/client';
import { kindOf, maxFileBytes, type FileKind } from './kinds';

export interface UploadItem {
  key: string;
  name: string;
  size: number;
  kind: FileKind;
  /** Id «ничейной» загрузки на сервере; null — ещё грузится или не удалось. */
  id: string | null;
  /** Местная миниатюра картинки — до ответа сервера. */
  previewUrl: string | null;
  error: string | null;
}

interface Uploaded {
  id: string;
  fileName: string;
  size: number;
  contentType: string;
}

let counter = 0;

/**
 * Вложения до отправки: каждый файл сразу грузится «ничейным» на сервер (миниатюра видна сразу, из самого файла), id готовых — в
 * <c>ids</c> для подачи заявки или сообщения. Файл больше 25 МБ не грузится — ошибка на плитке.
 */
export function useUploads() {
  const [items, setItems] = useState<UploadItem[]>([]);
  const urls = useRef<string[]>([]);

  useEffect(() => () => urls.current.forEach((url) => URL.revokeObjectURL(url)), []);

  const patch = (key: string, value: Partial<UploadItem>) => setItems((list) => list.map((i) => (i.key === key ? { ...i, ...value } : i)));

  const add = useCallback((files: File[]) => {
    for (const file of files) {
      const key = `u${++counter}`;
      const kind = kindOf(file.type, file.name);
      const previewUrl = kind === 'image' ? URL.createObjectURL(file) : null;
      if (previewUrl) urls.current.push(previewUrl);
      const tooBig = file.size > maxFileBytes;
      setItems((list) => [...list, { key, name: file.name || 'вставка.png', size: file.size, kind, id: null, previewUrl,
        error: tooBig ? 'Файл — до 25 МБ' : null }]);
      if (tooBig) continue;
      const form = new FormData();
      form.append('file', file, file.name || 'вставка.png');
      apiFetch<Uploaded>('/api/families/requests/uploads', { method: 'POST', body: form })
        .then((uploaded) => patch(key, { id: uploaded.id, kind: kindOf(uploaded.contentType, uploaded.fileName) }))
        .catch((error: Error) => patch(key, { error: error.message }));
    }
  }, []);

  const remove = useCallback((key: string) => setItems((list) => list.filter((i) => i.key !== key)), []);
  const clear = useCallback(() => setItems([]), []);
  const ids = items.filter((i) => i.id !== null).map((i) => i.id!);
  const busy = items.some((i) => i.id === null && i.error === null);
  return { items, add, remove, clear, ids, busy };
}

/** Ctrl+V файлов (скриншотов) где угодно на странице. Текст из буфера не трогается — вставка текста работает как обычно. */
export function usePasteFiles(onFiles: (files: File[]) => void, enabled = true) {
  useEffect(() => {
    if (!enabled) return;
    const handler = (event: Event) => {
      const data = (event as ClipboardEvent).clipboardData;
      const files = data ? Array.from(data.files ?? []) : [];
      if (files.length === 0) return;
      event.preventDefault();
      onFiles(files);
    };
    document.addEventListener('paste', handler);
    return () => document.removeEventListener('paste', handler);
  }, [onFiles, enabled]);
}
