import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { renderUi } from '../../../test/renderApp';
import { theme } from '../../../theme';
import { AttachmentGrid } from './AttachmentGrid';
import { DropArea } from './DropArea';
import { FileViewer, type ViewerFile } from './FileViewer';
import { Linkify } from './Linkify';
import { useUploads } from './useUploads';

vi.setConfig({ testTimeout: 15_000 });

beforeEach(() => {
  vi.stubGlobal('URL', Object.assign(URL, { createObjectURL: () => 'blob:preview', revokeObjectURL: () => undefined }));
});

function Harness({ onIds }: { onIds?: (ids: string[]) => void }) {
  const uploads = useUploads();
  onIds?.(uploads.ids);
  return (
    <>
      <DropArea onFiles={uploads.add} />
      <AttachmentGrid items={uploads.items} onRemove={uploads.remove} />
    </>
  );
}

const png = (name = 'скрин.png', size = 10) => new File([new Uint8Array(size)], name, { type: 'image/png' });

describe('attachments', () => {
  it('a pasted screenshot is uploaded at once and shown as a tile', async () => {
    let posted = 0;
    let ids: string[] = [];
    renderUi(<Harness onIds={(value) => { ids = value; }} />, {
      'POST /api/families/requests/uploads': () => { posted++; return { id: 'f1', fileName: 'скрин.png', size: 10, contentType: 'image/png' }; },
    });
    await screen.findByText(/Ctrl\+V/);

    const paste = new Event('paste', { bubbles: true }) as Event & { clipboardData: unknown };
    paste.clipboardData = { files: [png()], items: [] };
    document.dispatchEvent(paste);

    expect(await screen.findByText('скрин.png')).toBeTruthy();
    await waitFor(() => expect(ids).toEqual(['f1']));
    expect(posted).toBe(1);
  });

  it('a file over 25 MB is refused without uploading', async () => {
    let posted = 0;
    renderUi(<Harness />, { 'POST /api/families/requests/uploads': () => { posted++; return {}; } });
    const input = (await screen.findByTestId('drop-input')) as HTMLInputElement;

    fireEvent.change(input, { target: { files: [png('огромный.png', 26 * 1024 * 1024)] } });

    expect(await screen.findByText('Файл — до 25 МБ')).toBeTruthy();
    expect(posted).toBe(0);
  });

  it('links in text are clickable, scripts are not', () => {
    render(<MantineProvider theme={theme}><Linkify text={'каталог: https://example.ru/a?b=1 и javascript:alert(1)'} /></MantineProvider>);

    const links = screen.getAllByRole('link');
    expect(links).toHaveLength(1);
    expect(links[0].getAttribute('href')).toBe('https://example.ru/a?b=1');
    expect(links[0].getAttribute('rel')).toContain('noopener');
  });

  it('the viewer pages through images', async () => {
    const files: ViewerFile[] = [
      { key: 'a', name: 'первый.png', kind: 'image', url: 'blob:a' },
      { key: 'b', name: 'второй.png', kind: 'image', url: 'blob:b' },
    ];
    const onIndex = vi.fn();
    render(<MantineProvider theme={theme}><FileViewer files={files} index={0} onIndex={onIndex} onClose={() => undefined} /></MantineProvider>);
    expect(await screen.findByText('первый.png')).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: 'Следующий' }));
    expect(onIndex).toHaveBeenCalledWith(1);
  });
});
