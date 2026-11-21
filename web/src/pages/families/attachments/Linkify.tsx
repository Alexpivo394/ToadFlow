import { Anchor, Text, type TextProps } from '@mantine/core';

const url = /(https?:\/\/[^\s<>"«»]+)/g;
const trailing = /[.,;:!?)\]]+$/;

/** Текст с кликабельными ссылками http(s); переносы строк сохраняются. Другие схемы (javascript: и т.п.) остаются текстом. */
export function Linkify({ text, ...props }: { text: string } & TextProps) {
  const parts = text.split(url);
  return (
    <Text size="sm" style={{ whiteSpace: 'pre-wrap', overflowWrap: 'anywhere' }} {...props}>
      {parts.map((part, i) => {
        if (i % 2 === 0) return part;
        const tail = part.match(trailing)?.[0] ?? '';
        const href = tail ? part.slice(0, -tail.length) : part;
        return (
          <span key={i}>
            <Anchor href={href} target="_blank" rel="noopener noreferrer">{href}</Anchor>
            {tail}
          </span>
        );
      })}
    </Text>
  );
}
